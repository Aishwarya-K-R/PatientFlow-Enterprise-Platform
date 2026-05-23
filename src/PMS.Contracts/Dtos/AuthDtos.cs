using System.ComponentModel.DataAnnotations;

namespace PatientFlow.Contracts.Dtos;

/// <summary>
/// Returned by POST /auth/login. JSON shape is camelCase by default
/// (ASP.NET Core JsonSerializer): { "accessToken": "...", "expiresAt": "..." }.
/// </summary>
public record LoginResponse(string AccessToken, DateTime ExpiresAt);

/// <summary>
/// Request for user signup
/// </summary>
public class SignupRequest
{
    [Required]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// Request for user login
/// </summary>
public class LoginRequest
{
    [Required]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;
}
