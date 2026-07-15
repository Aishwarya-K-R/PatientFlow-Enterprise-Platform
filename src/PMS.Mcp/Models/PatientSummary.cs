namespace PatientFlow.Mcp.Models;

/// <summary>
/// Lightweight, MCP-tool-facing view of a patient. Deliberately does not
/// expose <c>MedicalHistory</c> or <c>Address</c> at the summary level —
/// tools that need clinical detail should request a distinct, narrower
/// read model so PHI exposure is opt-in per-tool.
/// </summary>
public sealed record PatientSummary(
    int Id,
    string Name,
    string Email,
    DateOnly RegisteredDate,
    int Age);
