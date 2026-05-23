using Microsoft.EntityFrameworkCore;
using PatientFlow.Auth.Models;

namespace PatientFlow.Auth.Data;

public class AuthDbContext(DbContextOptions<AuthDbContext> options) : DbContext(options)
{
    public DbSet<User> Users { get; set; }
}
