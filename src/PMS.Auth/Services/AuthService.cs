using Microsoft.AspNetCore.Identity;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.EntityFrameworkCore;
using PatientFlow.Auth.Data;
using PatientFlow.Auth.Models;
using PatientFlow.Contracts.Dtos;
using PatientFlow.Common.Exceptions;

namespace PatientFlow.Auth.Services;

public class AuthService(AuthDbContext context, IConfiguration config)
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(1);

    private readonly AuthDbContext _context = context;
    private readonly IConfiguration _config = config;

    private (string Token, DateTime ExpiresAtUtc) GenerateJwtToken(string role, string id)
    {
        var jwtSettings = _config.GetSection("Jwt");
        var claims = new[]
        {
            new Claim(ClaimTypes.Role, role),
            new Claim(ClaimTypes.NameIdentifier, id)
        };
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings["Key"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var expiresAtUtc = DateTime.UtcNow.Add(TokenLifetime);

        var token = new JwtSecurityToken(
            issuer: jwtSettings["Issuer"],
            audience: jwtSettings["Audience"],
            claims: claims,
            expires: expiresAtUtc,
            signingCredentials: creds);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAtUtc);
    }

    public async Task Signup(SignupRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            throw new ArgumentException("Invalid user details");
        }

        var userByEmail = await _context.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == request.Email.ToLower());
        if (userByEmail != null)
        {
            throw new DuplicateEmailException(request.Email);
        }

        var user = new User
        {
            Email = request.Email,
            Password = request.Password,
            Role = UserRole.USER
        };

        var passwordHasher = new PasswordHasher<User>();
        user.Password = passwordHasher.HashPassword(user, user.Password);

        _context.Users.Add(user);
        await _context.SaveChangesAsync();
    }

    public async Task<LoginResponse?> Login(LoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            throw new ArgumentException("Invalid user details");
        }

        var userByEmail = await _context.Users
            .FirstOrDefaultAsync(u => u.Email.ToLower() == request.Email.ToLower())
            ?? throw new UnauthorizedAccessException("Invalid User Email");

        var passwordHasher = new PasswordHasher<User>();
        var result = passwordHasher.VerifyHashedPassword(userByEmail, userByEmail.Password, request.Password);

        if (result == PasswordVerificationResult.Failed)
        {
            throw new UnauthorizedAccessException("Invalid User Password");
        }

        var (token, expiresAtUtc) = GenerateJwtToken(userByEmail.Role.ToString(), userByEmail.Id.ToString());
        return new LoginResponse(token, expiresAtUtc);
    }
}
