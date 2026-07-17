using System.Text.Json;
using ModelContextProtocol.Server;
using PatientFlow.Mcp.Data;

namespace PatientFlow.Mcp.Resources;

/// <summary>
/// MCP <b>Resources</b> for patient data.
///
/// Difference from tools:
///  - <b>Tools</b> are verbs — the model chooses to invoke them ("search for X").
///  - <b>Resources</b> are nouns — named, addressable, cacheable state
///    the client can browse or subscribe to.
///
/// Both are auto-discovered by <c>WithResourcesFromAssembly()</c> the same way
/// tools are discovered. Method parameters bind to placeholders in the
/// <c>UriTemplate</c>, so <c>patientflow://patients/{id}</c> with an <c>int id</c>
/// parameter gives us a dynamic resource without extra plumbing.
/// </summary>
[McpServerResourceType]
public sealed class PatientResources
{
    private readonly McpReadRepository _repo;

    public PatientResources(McpReadRepository repo) => _repo = repo;

    [McpServerResource(
        UriTemplate = "patientflow://patients/summary",
        Name = "patients_summary",
        MimeType = "application/json")]
    [System.ComponentModel.Description(
        "Aggregate patient statistics: total count, registrations today, this week, this month.")]
    public async Task<string> GetPatientsSummaryAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // ISO-style week: Monday as the first day. DayOfWeek.Sunday == 0, so
        // (int)Sunday - 1 = -1 which would push weekStart INTO the future;
        // treat Sunday as day 7 to keep the same-week semantics users expect.
        var dow = (int)today.DayOfWeek;
        if (dow == 0) dow = 7;
        var weekStart = today.AddDays(-(dow - 1));

        var monthStart = new DateOnly(today.Year, today.Month, 1);

        var counts = await _repo.GetPatientsSummaryAsync(today, weekStart, monthStart, ct);

        var payload = new
        {
            total = counts.Total,
            registeredToday = counts.RegisteredToday,
            registeredThisWeek = counts.RegisteredThisWeek,
            registeredThisMonth = counts.RegisteredThisMonth,
            asOf = DateTime.UtcNow
        };

        return JsonSerializer.Serialize(payload);
    }

    [McpServerResource(
        UriTemplate = "patientflow://patients/{id}",
        Name = "patient_by_id",
        MimeType = "application/json")]
    [System.ComponentModel.Description(
        "Single patient summary as a resource (Id, Name, Email, RegisteredDate, Age). " +
        "Returns a JSON error object with 'error' field if the id doesn't exist.")]
    public async Task<string> GetPatientAsync(int id, CancellationToken ct = default)
    {
        var patient = await _repo.GetPatientAsync(id, ct);
        return patient is null
            ? JsonSerializer.Serialize(new { error = $"No patient with id {id}" })
            : JsonSerializer.Serialize(patient);
    }
}
