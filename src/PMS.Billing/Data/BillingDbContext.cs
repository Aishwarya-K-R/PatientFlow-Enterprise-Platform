using Microsoft.EntityFrameworkCore;
using PatientFlow.Billing.Models;

namespace PatientFlow.Billing.Data;

public class BillingDbContext(DbContextOptions<BillingDbContext> options) : DbContext(options)
{
    public DbSet<BillingAccount> BillingAccounts { get; set; }
}
