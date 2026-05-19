using System.ComponentModel.DataAnnotations;

namespace Patient_Management_System.Models
{
    // ------------------------------------------------------------------
    // Auth DTOs
    // ------------------------------------------------------------------

    /// <summary>
    /// Returned by POST /auth/login. JSON shape is camelCase by default
    /// (ASP.NET Core JsonSerializer): { "accessToken": "...", "expiresAt": "..." }.
    /// </summary>
    public record LoginResponse(string AccessToken, DateTime ExpiresAt);

    // ------------------------------------------------------------------
    // AI DTOs
    // ------------------------------------------------------------------

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
}
