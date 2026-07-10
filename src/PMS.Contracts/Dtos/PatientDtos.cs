namespace PatientFlow.Contracts.Dtos;

/// <summary>
/// Lightweight DTO for patient snapshot.
/// Used by AI service cache warming and other cross-service communication.
/// </summary>
public record PatientDto
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public DateOnly DateOfBirth { get; init; }
    public string Address { get; init; } = string.Empty;
    public DateOnly RegisteredDate { get; init; }

    // Clinical narrative used by the RAG pipeline. Optional so older callers
    // that only care about demographics can ignore it.
    public string MedicalHistory { get; init; } = string.Empty;
}
