using Microsoft.EntityFrameworkCore;

namespace PatientFlow.Patient.Data;

public class PatientDbContext(DbContextOptions<PatientDbContext> options) : DbContext(options)
{
    public DbSet<Models.Patient> Patients { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Models.Patient>()
            .HasIndex(p => p.Email)
            .IsUnique()
            .HasDatabaseName("IX_Patients_Email_Unique");
    }
}
