using Microsoft.EntityFrameworkCore;

namespace PatientFlow.Mcp.Data;

internal sealed class BillingAccountRow
{
    public int Id { get; set; }
    public int PatientId { get; set; }
    public string AccountId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

/// <summary>
/// Read-only EF Core context for the billing database.
/// See <see cref="McpPatientDbContext"/> for the design rationale — same
/// pattern: table-shaped mirror entities, no migrations, no writes.
/// </summary>
public sealed class McpBillingDbContext : DbContext
{
    public McpBillingDbContext(DbContextOptions<McpBillingDbContext> options) : base(options)
    {
        ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        ChangeTracker.AutoDetectChangesEnabled = false;
    }

    internal DbSet<BillingAccountRow> BillingAccounts => Set<BillingAccountRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BillingAccountRow>(e =>
        {
            e.ToTable("BillingAccounts");
            e.HasKey(b => b.Id);
        });
    }

    public override int SaveChanges() =>
        throw new InvalidOperationException("McpBillingDbContext is read-only.");

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("McpBillingDbContext is read-only.");
}
