using Microsoft.AspNetCore.Identity;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.IdentityModel.Tokens.Jwt;
using Patient_Management_System.Data;
using Patient_Management_System.Models;
using Microsoft.EntityFrameworkCore;
using Patient_Management_System.Exceptions;

namespace Patient_Management_System.Services
{
    public class AuthService(AppDbContext context, IConfiguration config)
    {
        private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(1);

        private readonly AppDbContext _context = context;
        private readonly IConfiguration _config = config;

        private (string Token, DateTime ExpiresAtUtc) GenerateJwtToken(string role, string id)
        {
            var jwtSettings = _config.GetSection("Jwt");
            var claims = new[]
            {
                new Claim(ClaimTypes.Role, role),
                new Claim(ClaimTypes.NameIdentifier, id)
            };
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings["Key"]));
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

        public async Task Signup(User user)
        {
            if (user == null || string.IsNullOrWhiteSpace(user.Email) || string.IsNullOrWhiteSpace(user.Password))
            {
                throw new ArgumentException("Invalid user details !!!");
            }
            var userByEmail = await _context.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == user.Email.ToLower());
            if (userByEmail != null)
            {
                throw new DuplicateEmailException(user.Email);
            }

            var passwordHasher = new PasswordHasher<User>();
            user.Password = passwordHasher.HashPassword(user, user.Password);

            User addUser = new()
            {
                Email = user.Email,
                Password = user.Password,
                Role = UserRole.USER,
            };

            _context.Users.Add(addUser);
            await _context.SaveChangesAsync();
        }

        public async Task<LoginResponse?> Login(User user)
        {
            if (user == null || string.IsNullOrWhiteSpace(user.Email) || string.IsNullOrWhiteSpace(user.Password))
            {
                throw new ArgumentException("Invalid user details!!!");
            }

            var userByEmail = await _context.Users
                .FirstOrDefaultAsync(u => u.Email.ToLower() == user.Email.ToLower())
                ?? throw new UnauthorizedAccessException("Invalid User Email!!!");

            var passwordHasher = new PasswordHasher<User>();
            var result = passwordHasher.VerifyHashedPassword(userByEmail, userByEmail.Password, user.Password);

            if (result == PasswordVerificationResult.Failed)
            {
                throw new UnauthorizedAccessException("Invalid User Password!!!");
            }

            var (token, expiresAtUtc) = GenerateJwtToken(userByEmail.Role.ToString(), userByEmail.Id.ToString());
            return new LoginResponse(token, expiresAtUtc);
        }
    }
}
