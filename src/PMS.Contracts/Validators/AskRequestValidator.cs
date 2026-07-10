using System.Text.RegularExpressions;
using FluentValidation;
using PatientFlow.Contracts.Dtos;

namespace PatientFlow.Contracts.Validators;

/// <summary>
/// First line of defence for POST /ai/ask. Rejects requests that are obviously
/// malformed or obviously hostile so the AI service never even runs vector
/// search / hits Ollama for garbage input. Deeper prompt-injection scrubbing
/// happens inside the AI service via PromptSanitizer (defence in depth).
/// </summary>
public class AskRequestValidator : AbstractValidator<AskRequest>
{
    // Case-insensitive markers for the most common jailbreak vectors seen in
    // the OWASP LLM01 catalogue. We reject rather than sanitise here because
    // a legitimate clinical question has no reason to contain any of these
    // strings verbatim. Sanitizer catches subtler variants inside the app.
    private static readonly Regex[] InjectionPatterns =
    [
        new(@"\bignore\s+(all\s+|any\s+|the\s+)?(previous|prior|above|earlier)\s+(instructions?|rules?|prompts?)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\byou\s+are\s+now\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bforget\s+(everything|all|previous)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"^\s*system\s*[:>]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline),
        new(@"###\s*(system|instruction)\s*###",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    public AskRequestValidator()
    {
        RuleFor(x => x.Question)
            .NotEmpty().WithMessage("Question is required")
            .MaximumLength(AskRequest.MaxQuestionLength)
                .WithMessage($"Question cannot exceed {AskRequest.MaxQuestionLength} characters")
            .Must(NotContainInjectionMarkers)
                .WithMessage("Question contains disallowed instructions. Rephrase and try again.");
    }

    private static bool NotContainInjectionMarkers(string? question)
    {
        if (string.IsNullOrEmpty(question)) return true;
        foreach (var pattern in InjectionPatterns)
        {
            if (pattern.IsMatch(question)) return false;
        }
        return true;
    }
}
