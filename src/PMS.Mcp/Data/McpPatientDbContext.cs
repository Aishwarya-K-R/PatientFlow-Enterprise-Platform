using Microsoft.EntityFrameworkCore;

namespace PatientFlow.Mcp.Data;

/// <summary>
/// Read-only mirror of the columns PMS.Mcp cares about on the patient DB.
/// Deliberately does NOT reference PMS.Patient's real entity — that would
/// couple us to its migrations and its write-side services (validators,
/// repositories, event publisher). This shape maps to the "Patients" table
/// by convention; only the fields we actually project are declared.
/// </summary>
internal sealed class PatientRow
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public DateOnly DateOfBirth { get; set; }
    public DateOnly RegisteredDate { get; set; }
    public string MedicalHistory { get; set; } = string.Empty;
}

internal sealed class OutboxRow
{
    public long Id { get; set; }
    public string Topic { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
    public bool IsPublished { get; set; }
}

/// <summary>
/// Read-only EF Core context for the patient database.
///
/// Design notes:
///  - No migrations live here; the schema is owned by PMS.Patient. This context
///    only reads.
/// - Registered via <c>AddDbContextFactory</c> so each MCP tool invocation
///    can pull a fresh short-lived context (concurrent-safe).
///  - <see cref="ChangeTracker"/> QueryTrackingBehavior is forced to
///    <c>NoTracking</c> so every query is automatically no-tracking — a
///    safety net in case a repository call forgets <c>.AsNoTracking()</c>.
/// </summary>
public sealed class McpPatientDbContext : DbContext
{
    public McpPatientDbContext(DbContextOptions<McpPatientDbContext> options) : base(options)
    {
        ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        ChangeTracker.AutoDetectChangesEnabled = false;
    }

    internal DbSet<PatientRow> Patients => Set<PatientRow>();
    internal DbSet<OutboxRow> OutboxMessages => Set<OutboxRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PatientRow>(e =>
        {
            e.ToTable("Patients");
            e.HasKey(p => p.Id);
        });

        modelBuilder.Entity<OutboxRow>(e =>
        {
            e.ToTable("OutboxMessages");
            e.HasKey(o => o.Id);
        });
    }

    /// <summary>
    /// Hard block on writes. Even if a bug tries to SaveChanges, we throw
    /// rather than mutate the patient DB from the MCP process.
    /// </summary>
    public override int SaveChanges() =>
        throw new InvalidOperationException("McpPatientDbContext is read-only.");

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("McpPatientDbContext is read-only.");
}
