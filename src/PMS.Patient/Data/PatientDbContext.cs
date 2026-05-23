using Microsoft.EntityFrameworkCore;

namespace PatientFlow.Patient.Data;

public class PatientDbContext(DbContextOptions<PatientDbContext> options) : DbContext(options)
{
    public DbSet<Models.Patient> Patients { get; set; }
}
