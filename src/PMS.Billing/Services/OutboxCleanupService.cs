using Microsoft.EntityFrameworkCore;
using PatientFlow.Billing.Data;

namespace PatientFlow.Billing.Services;

/// <summary>
/// Periodically purges old, successfully-published outbox rows so the
/// table doesn't grow unbounded. Runs once per hour, deletes rows where
/// IsPublished=true AND PublishedAt is older than the retention window
/// (default 7 days). Unpublished or recently-published rows are left alone.
/// </summary>
public class OutboxCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<OutboxCleanupService> _logger;
    private readonly TimeSpan _runInterval = TimeSpan.FromHours(1);
    private readonly TimeSpan _retentionWindow = TimeSpan.FromDays(7);

    public OutboxCleanupService(
        IServiceProvider serviceProvider,
        ILogger<OutboxCleanupService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "OutboxCleanup service started. Retention: {Retention} days, run interval: {Interval} hours.",
            _retentionWindow.TotalDays, _runInterval.TotalHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PurgeOldMessagesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during outbox cleanup");
            }

            await Task.Delay(_runInterval, stoppingToken);
        }

        _logger.LogInformation("OutboxCleanup service stopped");
    }

    private async Task PurgeOldMessagesAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BillingDbContext>();

        var cutoff = DateTime.UtcNow - _retentionWindow;

        // ExecuteDeleteAsync runs a single DELETE statement on the server —
        // no rows pulled into memory. Available in EF Core 7+.
        var deleted = await context.OutboxMessages
            .Where(m => m.IsPublished && m.PublishedAt < cutoff)
            .ExecuteDeleteAsync(stoppingToken);

        if (deleted > 0)
        {
            _logger.LogInformation(
                "Outbox cleanup: deleted {Count} published messages older than {Cutoff:O}.",
                deleted, cutoff);
        }
    }
}
