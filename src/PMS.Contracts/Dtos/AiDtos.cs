using System.ComponentModel.DataAnnotations;

namespace PatientFlow.Contracts.Dtos;

/// <summary>
/// Body of POST /ai/ask. Replaces the awkward [FromBody] string contract
/// where clients had to send a raw JSON-quoted string.
/// </summary>
public class AskRequest
{
    [Required(ErrorMessage = "Question is required")]
    [StringLength(1000, ErrorMessage = "Question cannot exceed 1000 characters")]
    public string Question { get; set; } = string.Empty;
}
