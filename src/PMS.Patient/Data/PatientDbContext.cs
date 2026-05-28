using Microsoft.EntityFrameworkCore;

namespace PatientFlow.Patient.Data;

public class PatientDbContext(DbContextOptions<PatientDbContext> options) : DbContext(options)
{
    public DbSet<Models.Patient> Patients { get; set; }
    public DbSet<Models.OutboxMessage> OutboxMessages { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Models.Patient>()
            .HasIndex(p => p.Email)
            .IsUnique()
            .HasDatabaseName("IX_Patients_Email_Unique");

        modelBuilder.Entity<Models.OutboxMessage>()
            .HasIndex(o => new { o.IsPublished, o.CreatedAt })
            .HasDatabaseName("IX_OutboxMessages_IsPublished_CreatedAt");
    }
}
