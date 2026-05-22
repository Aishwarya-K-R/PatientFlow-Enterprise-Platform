using Microsoft.EntityFrameworkCore;
using PatientFlow.Billing.Models;

namespace PatientFlow.Billing.Data;

public class BillingDbContext(DbContextOptions<BillingDbContext> options) : DbContext(options)
{
    public DbSet<BillingAccount> BillingAccounts { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Ensure one billing account per patient (idempotency at DB level)
        modelBuilder.Entity<BillingAccount>()
            .HasIndex(b => b.PatientId)
            .IsUnique();
    }
}
