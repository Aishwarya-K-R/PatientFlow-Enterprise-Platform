using Prometheus;

namespace PatientFlow.Common.Metrics;

/// <summary>
/// Central registry for custom Prometheus business metrics.
/// Infrastructure metrics (HTTP duration, GC, etc.) are emitted automatically
/// by prometheus-net's UseHttpMetrics() and the default collectors. This class
/// captures the things that matter for the domain: patient lifecycle, cache
/// effectiveness, billing reliability, LLM performance, and Kafka throughput.
///
/// Naming follows Prometheus conventions:
///   - snake_case
///   - _total suffix for monotonically increasing counters
///   - _seconds suffix for time-based histograms
///   - the unit appears at the end of the metric name
/// </summary>
public static class AppMetrics
{
    // -------------------------------------------------------------------
    // Patient lifecycle counters
    // -------------------------------------------------------------------
    public static readonly Counter PatientsCreated = Prometheus.Metrics.CreateCounter(
        "patients_created_total",
        "Total number of patients successfully created.");

    public static readonly Counter PatientsUpdated = Prometheus.Metrics.CreateCounter(
        "patients_updated_total",
        "Total number of patients successfully updated.");

    public static readonly Counter PatientsDeleted = Prometheus.Metrics.CreateCounter(
        "patients_deleted_total",
        "Total number of patients successfully deleted.");

    // -------------------------------------------------------------------
    // Billing counters
    // -------------------------------------------------------------------
    public static readonly Counter BillingAccountsCreated = Prometheus.Metrics.CreateCounter(
        "billing_accounts_created_total",
        "Total number of billing accounts successfully created.");

    public static readonly Counter BillingFailures = Prometheus.Metrics.CreateCounter(
        "billing_failures_total",
        "Total number of failed Billing gRPC calls from Patient service (post-resilience exhaustion).");

    // -------------------------------------------------------------------
    // LLM performance
    // -------------------------------------------------------------------
    public static readonly Histogram LlmRequestDuration = Prometheus.Metrics.CreateHistogram(
        "llm_request_duration_seconds",
        "Duration of LLM AskAsync calls in seconds (end-to-end including resilience retries).",
        new HistogramConfiguration
        {
            // Tuned for LLM latency: 100ms .. 60s. LLM calls are typically slow,
            // so the lower buckets matter less than the 1s..30s range.
            Buckets = new[] { 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 20, 30, 45, 60 }
        });

    // -------------------------------------------------------------------
    // Cache effectiveness (Patient service)
    // -------------------------------------------------------------------
    public static readonly Counter RedisCacheHits = Prometheus.Metrics.CreateCounter(
        "redis_cache_hits_total",
        "Total number of cache hits when reading patients (memory or Redis).",
        new CounterConfiguration
        {
            // Distinguish L1 (memory) hits from L2 (Redis) hits so we can measure
            // each tier's effectiveness independently.
            LabelNames = new[] { "tier" }
        });

    public static readonly Counter RedisCacheMisses = Prometheus.Metrics.CreateCounter(
        "redis_cache_misses_total",
        "Total number of cache misses when reading patients (fell through to database).");

    // -------------------------------------------------------------------
    // Kafka throughput
    // -------------------------------------------------------------------
    public static readonly Counter KafkaMessagesPublished = Prometheus.Metrics.CreateCounter(
        "kafka_messages_published_total",
        "Total number of Kafka messages successfully published.",
        new CounterConfiguration
        {
            LabelNames = new[] { "topic" }
        });

    public static readonly Counter KafkaMessagesFailed = Prometheus.Metrics.CreateCounter(
        "kafka_messages_failed_total",
        "Total number of Kafka publish attempts that threw before broker acknowledgement.",
        new CounterConfiguration
        {
            LabelNames = new[] { "topic" }
        });

    // -------------------------------------------------------------------
    // Outbox backlog
    // -------------------------------------------------------------------
    // Gauge (not counter) - it goes up AND down as messages get published.
    // Set by each service's OutboxPublisherService on every poll iteration.
    // Used by the OutboxBacklog alert to detect Kafka publish stalls.
    public static readonly Gauge OutboxPendingMessages = Prometheus.Metrics.CreateGauge(
        "outbox_pending_messages",
        "Number of unpublished outbox rows currently waiting to be sent to Kafka.",
        new GaugeConfiguration
        {
            LabelNames = new[] { "service" }
        });
}
