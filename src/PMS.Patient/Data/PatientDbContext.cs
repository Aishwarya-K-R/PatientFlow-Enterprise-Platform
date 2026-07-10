using Microsoft.EntityFrameworkCore;

namespace PatientFlow.Patient.Data;

public class PatientDbContext(DbContextOptions<PatientDbContext> options) : DbContext(options)
{
    public DbSet<Models.Patient> Patients { get; set; }
    public DbSet<Models.OutboxMessage> OutboxMessages { get; set; }
    public DbSet<Models.PatientEmbedding> PatientEmbeddings { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Enable the pgvector extension. EF Core will emit `CREATE EXTENSION IF NOT EXISTS vector`
        // in the migration, so Postgres has the `vector` type available before any table uses it.
        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.Entity<Models.Patient>()
            .HasIndex(p => p.Email)
            .IsUnique()
            .HasDatabaseName("IX_Patients_Email_Unique");

        modelBuilder.Entity<Models.OutboxMessage>()
            .HasIndex(o => new { o.IsPublished, o.CreatedAt })
            .HasDatabaseName("IX_OutboxMessages_IsPublished_CreatedAt");

        // 1:1 between Patient and PatientEmbedding, with cascade delete so
        // removing a patient also removes its stored vector (no orphan rows).
        modelBuilder.Entity<Models.PatientEmbedding>()
            .HasOne(e => e.Patient)
            .WithOne()
            .HasForeignKey<Models.PatientEmbedding>(e => e.PatientId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

