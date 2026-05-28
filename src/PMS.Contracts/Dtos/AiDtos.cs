namespace PatientFlow.Contracts.Dtos;

/// <summary>
/// Body of POST /ai/ask. Replaces the awkward [FromBody] string contract
/// where clients had to send a raw JSON-quoted string.
/// </summary>
public class AskRequest
{
    public string Question { get; set; } = string.Empty;
}
