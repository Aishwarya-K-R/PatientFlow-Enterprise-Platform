using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PatientFlow.Contracts.Config;

namespace PatientFlow.AI.Services;

/// <summary>
/// Result of running a user question through <see cref="PromptSanitizer"/>.
/// The controller uses <see cref="IsBlocked"/> to short-circuit with a safe
/// canned response, and <see cref="SanitizedQuestion"/> as the input to the
/// prompt whenever the question is allowed through.
/// </summary>
/// <param name="SanitizedQuestion">
/// The question with dangerous whitespace / control characters / obvious
/// injection markers scrubbed. Safe to concatenate into the LLM prompt.
/// </param>
/// <param name="IsBlocked">
/// True when the sanitizer decided the request is malicious enough to refuse
/// outright. Controller MUST NOT call the LLM when this is true.
/// </param>
/// <param name="Reason">
/// Machine-friendly reason code (e.g. "role-override", "too-long"). Used in
/// audit logs so we can spot attack trends without leaking the raw question.
/// </param>
public record SanitizationResult(string SanitizedQuestion, bool IsBlocked, string? Reason);

/// <summary>
/// Belt-and-braces defence against prompt injection in <c>/ai/ask</c>.
///
/// The <see cref="AskRequestValidator"/> already rejects obvious jailbreaks at
/// the HTTP boundary. This class runs a second, deeper pass AFTER validation:
/// it also scrubs control characters, collapses whitespace tricks used to hide
/// instructions, and re-checks the length ceiling against the runtime-tunable
/// <see cref="AISettings.MaxQuestionLength"/> (validator uses the compile-time
/// constant on the DTO; sanitizer uses the config value so ops can tighten
/// per-deployment without a rebuild).
///
/// Redacts a small set of PHI shapes (raw patient IDs like P-12345 and email
/// addresses) from the LLM's response too, so an attacker who does slip a
/// jailbreak past every other layer still can't exfiltrate identifiers we
/// deliberately never put in the prompt.
///
/// Why "detect and refuse" instead of "detect and rewrite"? A rewrite that
/// looks harmless to us might still carry semantic intent that the LLM will
/// happily follow. Refusing is the only defensible policy for a healthcare
/// workload.
/// </summary>
public class PromptSanitizer
{
    private readonly AISettings _settings;
    private readonly ILogger<PromptSanitizer> _logger;

    // Patterns that override or reassign the assistant's role. Kept broader
    // than the validator's list on purpose - the validator has already
    // rejected the loudest attempts, so this pass catches subtler paraphrases
    // and multi-line variants an attacker might use once they discover
    // requests get blocked at the boundary.
    private static readonly Regex[] RoleOverridePatterns =
    [
        new(@"\bignore\s+(all\s+|any\s+|the\s+)?(previous|prior|above|earlier)\s+(instructions?|rules?|prompts?|context)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bdisregard\s+(all\s+|any\s+|the\s+)?(previous|prior|above|earlier)\s+(instructions?|rules?)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\byou\s+are\s+(now|a|an)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bact\s+as\s+(a|an|the)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bpretend\s+(you\s+are|to\s+be)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bforget\s+(everything|all|previous|your\s+instructions)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^\s*system\s*[:>]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline),
        new(@"^\s*assistant\s*[:>]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline),
        new(@"###\s*(system|instruction|new\s+instructions?)\s*###",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"</?\s*(system|instruction|prompt)\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    // Post-response scrubbers. If the model somehow includes these shapes,
    // wipe them before the answer leaves the service.
    private static readonly Regex EmailPattern = new(
        @"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}",
        RegexOptions.Compiled);

    // Raw internal patient IDs (integer form) that might sneak into the LLM's
    // output. We only ever hand it pseudonyms like "P-00074", so any bare
    // 4-6 digit integer paired with "patient" or "id" is a leak we want gone.
    private static readonly Regex RawPatientIdPattern = new(
        @"\b(patient\s+id\s*[:=]?\s*|patient[_-]?id\s*[:=]?\s*|id\s*[:=]\s*)(\d{1,6})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Control chars (except tab/newline/CR) can be used to smuggle hidden
    // instructions past visual review. Strip them.
    private static readonly Regex ControlCharPattern = new(
        @"[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]",
        RegexOptions.Compiled);

    public PromptSanitizer(IOptions<AISettings> options, ILogger<PromptSanitizer> logger)
    {
        _settings = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Runs the injection checks against a user-supplied question.
    /// Returns a <see cref="SanitizationResult"/> the controller should
    /// inspect before doing anything else with the input.
    /// </summary>
    public SanitizationResult Sanitize(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return new SanitizationResult(string.Empty, IsBlocked: true, Reason: "empty");
        }

        // Second length check - the validator uses the compile-time DTO
        // constant, this uses the runtime config knob. If ops has tightened
        // AI:MaxQuestionLength to below the DTO ceiling, this catches it.
        if (question.Length > _settings.MaxQuestionLength)
        {
            _logger.LogWarning(
                "Prompt sanitizer rejected over-long question | length={Length} | limit={Limit}",
                question.Length, _settings.MaxQuestionLength);
            return new SanitizationResult(string.Empty, IsBlocked: true, Reason: "too-long");
        }

        // Strip control characters up-front so smuggled instructions can't
        // hide behind them for the subsequent regex checks.
        var scrubbed = ControlCharPattern.Replace(question, string.Empty);

        foreach (var pattern in RoleOverridePatterns)
        {
            if (pattern.IsMatch(scrubbed))
            {
                _logger.LogWarning(
                    "Prompt sanitizer blocked role-override attempt | pattern={Pattern}",
                    pattern);
                return new SanitizationResult(string.Empty, IsBlocked: true, Reason: "role-override");
            }
        }

        // Collapse runs of whitespace so an attacker can't pad a payload out
        // to visually break up matches. Tab / newline get normalised to a
        // single space; this is safe because clinical questions don't rely on
        // whitespace structure for meaning.
        var normalized = Regex.Replace(scrubbed, @"\s+", " ").Trim();

        return new SanitizationResult(normalized, IsBlocked: false, Reason: null);
    }

    /// <summary>
    /// Post-processes an LLM response to strip any raw PHI shapes that
    /// slipped through. This is a last-mile guard: the prompt itself only
    /// ever contains pseudonymised patients (see <see cref="PhiRedactor"/>),
    /// so a well-behaved model should never produce these strings. Wiping
    /// them here means even a compromised model or a jailbreak that got past
    /// the input filters still can't leak identifiers to the caller.
    /// </summary>
    /// <returns>Answer text safe to return to the HTTP caller.</returns>
    public string ScrubResponse(string answer)
    {
        if (string.IsNullOrEmpty(answer)) return answer;

        var emailScrubbed = EmailPattern.Replace(answer, "[REDACTED-EMAIL]");
        var idScrubbed = RawPatientIdPattern.Replace(
            emailScrubbed, m => $"{m.Groups[1].Value}[REDACTED-ID]");

        if (!ReferenceEquals(idScrubbed, answer) && idScrubbed != answer)
        {
            _logger.LogWarning(
                "PromptSanitizer scrubbed PHI shapes from LLM response | originalChars={Original} | scrubbedChars={Scrubbed}",
                answer.Length, idScrubbed.Length);
        }

        return idScrubbed;
    }
}
