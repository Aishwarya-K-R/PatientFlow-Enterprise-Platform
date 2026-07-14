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
        // For the aggregate we scan a large search page rather than adding a
        // count-specific repo method; still O(1) round-trips, and the search
        // pipeline already applies AsNoTracking + the same read boundary.
        var all = await _repo.SearchPatientsAsync(null, take: 100, ct);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var weekStart = today.AddDays(-(int)today.DayOfWeek);
        var monthStart = new DateOnly(today.Year, today.Month, 1);

        var payload = new
        {
            total = all.Count,
            registeredToday = all.Count(p => p.RegisteredDate == today),
            registeredThisWeek = all.Count(p => p.RegisteredDate >= weekStart),
            registeredThisMonth = all.Count(p => p.RegisteredDate >= monthStart),
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
