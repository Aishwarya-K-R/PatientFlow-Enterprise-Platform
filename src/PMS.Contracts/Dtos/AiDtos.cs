namespace PatientFlow.Contracts.Dtos;

/// <summary>
/// Body of POST /ai/ask. Replaces the awkward [FromBody] string contract
/// where clients had to send a raw JSON-quoted string.
/// </summary>
public class AskRequest
{
    /// <summary>
    /// Hard upper bound enforced at the validator. Kept as a compile-time
    /// constant on the DTO so the shared Contracts assembly doesn't have to
    /// take a runtime dependency on the AI service's IOptions&lt;AISettings&gt;.
    /// Per-deployment tuning (e.g. dev sandbox = 200) still happens via
    /// AI:MaxQuestionLength in appsettings — that value is read by
    /// PromptSanitizer as a second, defence-in-depth check. Any real change
    /// must move both this constant and the appsettings value together.
    /// </summary>
    public const int MaxQuestionLength = 1000;

    public string Question { get; set; } = string.Empty;
}
