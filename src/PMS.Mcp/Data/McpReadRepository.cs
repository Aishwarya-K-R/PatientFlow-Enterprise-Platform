using Microsoft.EntityFrameworkCore;
using PatientFlow.Mcp.Data;
using PatientFlow.Mcp.Models;
using StackExchange.Redis;

namespace PatientFlow.Mcp.Data;

/// <summary>
/// Single read facade over the patient DB, billing DB and Redis.
///
/// Why one class:
///  - MCP tools shouldn't know which store an answer comes from. If a
///    patient summary lives in Redis (cache-warmed by PMS.AI) we return that;
///    otherwise we fall back to Postgres. Callers get a domain model either way.
///  - Centralising the projections (EF entity -> record) means schema changes
///    in Patient/Billing land in exactly one place.
///
/// All EF calls go through <see cref="IDbContextFactory{TContext}"/>. MCP tool
/// calls are inherently concurrent (multiple SSE clients, multiple tool
/// invocations in flight), so per-request short-lived contexts are safer than
/// a scoped context that could be shared across parallel tool handlers.
/// </summary>
public sealed class McpReadRepository
{
    private readonly IDbContextFactory<McpPatientDbContext> _patientFactory;
    private readonly IDbContextFactory<McpBillingDbContext> _billingFactory;
    private readonly IConnectionMultiplexer _redis;

    public McpReadRepository(
        IDbContextFactory<McpPatientDbContext> patientFactory,
        IDbContextFactory<McpBillingDbContext> billingFactory,
        IConnectionMultiplexer redis)
    {
        _patientFactory = patientFactory;
        _billingFactory = billingFactory;
        _redis = redis;
    }

    // -----------------------------------------------------------------
    // Patients
    // -----------------------------------------------------------------

    public async Task<PatientSummary?> GetPatientAsync(int id, CancellationToken ct = default)
    {
        await using var db = await _patientFactory.CreateDbContextAsync(ct);
        var row = await db.Patients
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        return row is null ? null : ToSummary(row);
    }

    public async Task<IReadOnlyList<PatientSummary>> SearchPatientsAsync(
        string? nameContains,
        int take = 25,
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 100);

        await using var db = await _patientFactory.CreateDbContextAsync(ct);
        var query = db.Patients.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(nameContains))
        {
            // EF.Functions.ILike -> Postgres case-insensitive LIKE
            var pattern = $"%{nameContains.Trim()}%";
            query = query.Where(p =>
                EF.Functions.ILike(p.Name, pattern) ||
                EF.Functions.ILike(p.Email, pattern));
        }

        var rows = await query
            .OrderBy(p => p.Id)
            .Take(take)
            .ToListAsync(ct);

        return rows.Select(ToSummary).ToList();
    }

    /// <summary>
    /// Aggregate patient counts computed directly in Postgres — safe for any
    /// row count. Uses four CountAsync calls (single round-trip each) rather
    /// than pulling rows client-side, so it stays O(1) memory as the table grows.
    /// </summary>
    public async Task<PatientsSummaryCounts> GetPatientsSummaryAsync(
        DateOnly today,
        DateOnly weekStart,
        DateOnly monthStart,
        CancellationToken ct = default)
    {
        await using var db = await _patientFactory.CreateDbContextAsync(ct);
        var patients = db.Patients.AsNoTracking();

        var total = await patients.CountAsync(ct);
        var registeredToday = await patients.CountAsync(p => p.RegisteredDate == today, ct);
        var registeredThisWeek = await patients.CountAsync(p => p.RegisteredDate >= weekStart, ct);
        var registeredThisMonth = await patients.CountAsync(p => p.RegisteredDate >= monthStart, ct);

        return new PatientsSummaryCounts(total, registeredToday, registeredThisWeek, registeredThisMonth);
    }

    // -----------------------------------------------------------------
    // Billing
    // -----------------------------------------------------------------

    public async Task<BillingSummary?> GetBillingForPatientAsync(int patientId, CancellationToken ct = default)
    {
        await using var db = await _billingFactory.CreateDbContextAsync(ct);
        var row = await db.BillingAccounts
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.PatientId == patientId, ct);

        return row is null
            ? null
            : new BillingSummary(row.PatientId, row.AccountId, row.Status);
    }

    // -----------------------------------------------------------------
    // Recent events (outbox)
    // -----------------------------------------------------------------

    public async Task<IReadOnlyList<RecentEvent>> GetRecentEventsAsync(int take = 20, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 100);

        await using var db = await _patientFactory.CreateDbContextAsync(ct);
        var rows = await db.OutboxMessages
            .AsNoTracking()
            .OrderByDescending(o => o.CreatedAt)
            .Take(take)
            .ToListAsync(ct);

        return rows.Select(o => new RecentEvent(
            o.Id,
            o.Topic,
            o.CreatedAt,
            o.IsPublished,
            Preview(o.Payload, 160),
            o.Payload)).ToList();
    }

    // -----------------------------------------------------------------
    // Redis (AI-warmed context)
    // -----------------------------------------------------------------

    /// <summary>
    /// Returns the pseudonymised, PHI-redacted patient context that
    /// AiCacheWarmupService pushes to Redis. Null if the entry isn't warmed
    /// (cold patient or Redis unavailable) — the caller should fall back
    /// to the DB path in that case.
    /// </summary>
    public async Task<string?> TryGetWarmedPatientContextAsync(int patientId, CancellationToken ct = default)
    {
        _ = ct;
        var db = _redis.GetDatabase();
        // Same key convention used by PMS.AI's AiCacheWarmupService.
        var value = await db.StringGetAsync($"ai:patient:{patientId}:context");
        return value.IsNullOrEmpty ? null : value.ToString();
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static PatientSummary ToSummary(PatientRow row) =>
        new(row.Id, row.Name, row.Email, row.RegisteredDate, CalculateAge(row.DateOfBirth));

    private static int CalculateAge(DateOnly dob)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var age = today.Year - dob.Year;
        if (dob > today.AddYears(-age)) age--;
        return Math.Max(0, age);
    }

    private static string Preview(string s, int max) =>
        string.IsNullOrEmpty(s) ? string.Empty
            : s.Length <= max ? s
            : s[..max] + "…";
}
