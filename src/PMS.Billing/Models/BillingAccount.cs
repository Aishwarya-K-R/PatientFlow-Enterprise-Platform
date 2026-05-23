using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PatientFlow.Billing.Models;

public class BillingAccount
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required(ErrorMessage = "PatientId is required")]
    public int PatientId { get; set; }

    public string AccountId { get; set; } = string.Empty;

    public string Status { get; set; } = "INACTIVE";
}
