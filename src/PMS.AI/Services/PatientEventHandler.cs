using Microsoft.Extensions.Options;
using PatientFlow.Contracts.Config;
using PatientFlow.Contracts.Dtos;

namespace PatientFlow.AI.Services;

/// <summary>
/// Shared handler for patient-related domain events. The Kafka consumers are
/// intentionally thin (one topic each) and all delegate to this class so the
/// "what to do when a patient changes" logic lives in exactly one place.
///
/// Scope note: this type resolves scoped/singleton dependencies from the
/// container, so it must itself be registered as scoped and constructed
/// inside an IServiceScope by each consumer.
/// </summary>
public class PatientEventHandler(
    RedisService redis,
    EmbeddingService embeddingService,
    PatientEmbeddingStore embeddingStore,
    PatientServiceClient patientClient,
    PhiRedactor redactor,
    IOptions<AISettings> aiOptions,
    ILogger<PatientEventHandler> logger)
{
    private readonly RedisService _redis = redis;
    private readonly EmbeddingService _embeddingService = embeddingService;
    private readonly PatientEmbeddingStore _embeddingStore = embeddingStore;
    private readonly PatientServiceClient _patientClient = patientClient;
    private readonly PhiRedactor _redactor = redactor;
    private readonly AISettings _aiSettings = aiOptions.Value;
    private readonly ILogger<PatientEventHandler> _logger = logger;

    /// <summary>
    /// PatientCreated and PatientUpdated share the same downstream work:
    /// refresh the pseudonymised Redis context AND refresh the pgvector
    /// embedding. Fetches the full patient once so both writes see the
    /// same canonical snapshot.
    /// </summary>
    public async Task HandleChangedAsync(int patientId, string changeVerb, CancellationToken cancellationToken)
    {
        // The Kafka event carries only PatientId+Name+Email; that's not enough
        // to build a useful embedding. Pull the full record over HTTP.
        var patient = await _patientClient.GetPatientByIdAsync(patientId, cancellationToken);
        if (patient == null)
        {
            _logger.LogWarning("Patient {PatientId} not found while handling {Verb} event",
                patientId, changeVerb);
            return;
        }

        // 1. Refresh pseudonymised Redis context (used by the current LLM chat path).
        var context = _redactor.BuildContext(patient);
        await _redis.SetPatientContextAsync($"patient:{patientId}", context, TimeSpan.FromHours(24));
        _logger.LogInformation("Updated context for patient {Pseudonym} ({Verb})",
            PhiRedactor.Pseudonym(patientId), changeVerb);

        // 2. Refresh embedding (used by the RAG semantic-search path).
        await RefreshEmbeddingAsync(patient, cancellationToken);
    }

    /// <summary>
    /// PatientDeleted: only clears the Redis context. The pgvector row is
    /// removed automatically by the cascade FK on PatientEmbeddings when
    /// the parent Patient row is deleted upstream.
    /// </summary>
    public async Task HandleDeletedAsync(int patientId, CancellationToken cancellationToken)
    {
        await _redis.DeletePatientContextAsync($"patient:{patientId}");
        _logger.LogInformation("Removed context for deleted patient {Pseudonym}",
            PhiRedactor.Pseudonym(patientId));

        // No embedding delete call: the FK cascade already handled it.
        // We could belt-and-braces call embeddingStore.DeleteAsync here, but
        // that would issue an HTTP request every time for no real benefit.
        await Task.CompletedTask;
    }

    /// <summary>
    /// Generates an embedding for the patient via Ollama and hands the vector
    /// to the store. All text sent to Ollama is pre-pseudonymised by PhiRedactor
    /// so no real name/email/street ever leaves the AI service memory.
    /// Failures here are logged but do NOT throw - a missing embedding is a
    /// degraded state we recover from on the next update, not a reason to
    /// redeliver the whole Kafka event or abort the outer backfill loop.
    /// Returns true if a vector was persisted, false otherwise.
    /// </summary>
    public async Task<bool> RefreshEmbeddingAsync(PatientDto patient, CancellationToken cancellationToken)
    {
        try
        {
            var sourceText = _redactor.BuildEmbeddingText(patient);
            var vector = await _embeddingService.GenerateAsync(sourceText, cancellationToken);

            if (vector.Length == 0)
            {
                _logger.LogWarning("Empty embedding for patient {Pseudonym}; not persisting",
                    PhiRedactor.Pseudonym(patient.Id));
                return false;
            }

            return await _embeddingStore.UpsertAsync(
                patient.Id, sourceText, vector, _aiSettings.EmbeddingModel, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Embedding refresh failed for patient {Pseudonym}",
                PhiRedactor.Pseudonym(patient.Id));
            return false;
        }
    }
}
