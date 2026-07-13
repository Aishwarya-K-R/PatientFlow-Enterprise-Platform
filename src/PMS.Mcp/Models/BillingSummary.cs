namespace PatientFlow.Mcp.Models;

public sealed record BillingSummary(
    int PatientId,
    string AccountId,
    string Status);
