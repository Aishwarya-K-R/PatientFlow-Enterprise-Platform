using System.Text;
using PatientFlow.Contracts.Dtos;

namespace PatientFlow.AI.Services;

/// <summary>
/// Single source of truth for turning a PatientDto into a PHI-safe string
/// suitable for storing in caches, embeddings, and LLM prompts.
///
/// PHI protection strategy (Phase 5 lite - full column encryption is still
/// tracked in ROADMAP.md Phase 5):
///   - Name    -> dropped entirely, replaced with pseudonymous "P-{id:D5}"
///   - Email   -> dropped entirely (never needed for clinical reasoning)
///   - Address -> reduced to city only (loses street/house identifier)
///   - DOB     -> bucketed into 10-year age band (loses precise identifier)
///   - Medical -> kept verbatim; this is the clinically-useful signal and it
///                only makes sense against the pseudonymous ID above
///
/// Why one class for both cache context and embedding text? Two reasons:
///   1. Consistency - both paths must apply the same rules; a future dev
///      updating "the redaction rules" should only need to edit ONE file.
///   2. Auditability - security reviewers can point at PhiRedactor and see
///      the full policy in ~50 lines, no need to trace call graphs.
/// </summary>
public class PhiRedactor
{
    /// <summary>
    /// Short human-readable context used by the LLM chat prompt.
    /// Example: "Patient P-00042, Age band 30-40, City Bangalore. Notes: Diabetic on Metformin"
    /// Kept compact because it goes into a size-limited prompt.
    /// </summary>
    public string BuildContext(PatientDto patient)
    {
        var sb = new StringBuilder();
        sb.Append("Patient ").Append(Pseudonym(patient.Id));
        sb.Append(", Age band ").Append(AgeBand(patient.DateOfBirth));

        var city = ExtractCity(patient.Address);
        if (!string.IsNullOrWhiteSpace(city))
        {
            sb.Append(", City ").Append(city);
        }

        if (!string.IsNullOrWhiteSpace(patient.MedicalHistory))
        {
            sb.Append(". Notes: ").Append(patient.MedicalHistory.Trim());
        }

        return sb.ToString();
    }

    /// <summary>
    /// Longer, embedding-friendly text. Same rules as BuildContext but the
    /// format is a set of labelled sentences that embedding models handle
    /// better than a single crammed line. Output goes to Ollama /api/embeddings
    /// AND to the PatientEmbeddings.SourceText column.
    /// </summary>
    public string BuildEmbeddingText(PatientDto patient)
    {
        var pseudonym = Pseudonym(patient.Id);
        var ageBand = AgeBand(patient.DateOfBirth);
        var city = ExtractCity(patient.Address);

        var sb = new StringBuilder();
        sb.Append("Patient ").Append(pseudonym).Append('.');
        sb.Append(" Age band: ").Append(ageBand).Append('.');
        if (!string.IsNullOrWhiteSpace(city))
        {
            sb.Append(" City: ").Append(city).Append('.');
        }
        if (!string.IsNullOrWhiteSpace(patient.MedicalHistory))
        {
            sb.Append(" Medical history: ").Append(patient.MedicalHistory.Trim());
            if (!patient.MedicalHistory.TrimEnd().EndsWith('.'))
            {
                sb.Append('.');
            }
        }

        return sb.ToString();
    }

    /// <summary>Public so consumers with only a patient id (no DTO) can log/reference the pseudonym.</summary>
    public static string Pseudonym(int patientId) => $"P-{patientId:D5}";

    /// <summary>
    /// Bucket DOB into decade bands. "30-40" means the patient's age at
    /// pseudonymisation time falls in [30, 40). Ambiguity is deliberate -
    /// two patients in the same band cannot be distinguished by age.
    /// </summary>
    public static string AgeBand(DateOnly dateOfBirth)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var age = today.Year - dateOfBirth.Year;
        if (dateOfBirth > today.AddYears(-age)) age--;

        if (age < 0) return "unknown";
        if (age >= 90) return "90+";

        var lower = (age / 10) * 10;
        return $"{lower}-{lower + 10}";
    }

    /// <summary>
    /// Pull just the city from a free-form address. Assumes comma-separated
    /// segments with the city usually second-to-last (India-style: "12 MG Road,
    /// Indiranagar, Bangalore, 560038"). Falls back to the last segment.
    /// Returns empty when no comma present so callers can decide what to render.
    /// </summary>
    public static string ExtractCity(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return string.Empty;

        var parts = address.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return string.Empty;
        if (parts.Length == 1) return string.Empty; // single-segment address is treated as too identifying to keep

        // Prefer second-to-last (city, then pincode/country). If only 2 parts, take the last.
        var candidate = parts.Length >= 3 ? parts[^2] : parts[^1];

        // Strip trailing pincodes / house numbers.
        candidate = candidate.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', ' ', '-');
        return candidate;
    }
}
