using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PatientFlow.Auth.Services;
using PatientFlow.Contracts.Dtos;

namespace PatientFlow.Auth.Controllers;

[ApiController]
[Route("auth")]
public class AuthController(AuthService authService) : ControllerBase
{
    private readonly AuthService _authService = authService;

    [HttpPost("signup")]
    public async Task<ActionResult> Signup(SignupRequest request)
    {
        await _authService.Signup(request);
        return Ok(new { message = "User registered successfully" });
    }

    [EnableRateLimiting("loginLimiter")]
    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest request)
    {
        var response = await _authService.Login(request);
        if (response == null)
        {
            return Unauthorized();
        }
        return Ok(response);
    }
}
