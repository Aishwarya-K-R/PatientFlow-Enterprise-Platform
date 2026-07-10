using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PatientFlow.Patient.Models;

public class Patient
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public DateOnly DateOfBirth { get; set; }
    public DateOnly RegisteredDate { get; set; }

    // Free-text clinical narrative. Unlike Name/Email/Address (all PHI), this is
    // the field the RAG pipeline can actually search meaningfully - "who has
    // diabetes?", "which patients are on beta blockers?" etc. It IS still
    // sensitive medical information, so the PhiRedactor keeps the text as-is
    // but stores it against a pseudonymous ID rather than a real name.
    [Column(TypeName = "text")]
    public string MedicalHistory { get; set; } = string.Empty;
}
