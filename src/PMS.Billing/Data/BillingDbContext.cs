using Microsoft.EntityFrameworkCore;
using PatientFlow.Billing.Models;

namespace PatientFlow.Billing.Data;

public class BillingDbContext(DbContextOptions<BillingDbContext> options) : DbContext(options)
{
    public DbSet<BillingAccount> BillingAccounts { get; set; }
    public DbSet<OutboxMessage> OutboxMessages { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<OutboxMessage>()
            .HasIndex(o => new { o.IsPublished, o.CreatedAt })
            .HasDatabaseName("IX_OutboxMessages_IsPublished_CreatedAt");
    }
}
