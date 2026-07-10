using System.Net;

namespace PatientFlow.AI.Services;

/// <summary>
/// Persists patient embeddings by calling an internal endpoint on the Patient
/// service. The AI service does NOT talk to Postgres directly - that would
/// violate the microservice-owns-its-database rule and duplicate schema in two
/// projects. Instead, Patient service exposes an internal write endpoint and
/// the AI service posts to it over HTTP.
///
/// Communication flow:
///     Patient event on Kafka
///       -> AI service EmbeddingService.GenerateAsync() (Ollama call)
///       -> AI service PatientEmbeddingStore.UpsertAsync() (HTTP to Patient)
///       -> Patient service writes to PatientEmbeddings table (pgvector)
/// </summary>
public class PatientEmbeddingStore
{
    private readonly HttpClient _http;
    private readonly ILogger<PatientEmbeddingStore> _logger;

    public PatientEmbeddingStore(HttpClient http, ILogger<PatientEmbeddingStore> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>
    /// Insert or update the embedding row for a patient.
    /// Returns true on success, false on any error (caller decides retry policy).
    /// </summary>
    public async Task<bool> UpsertAsync(
        int patientId,
        string sourceText,
        float[] vector,
        string model,
        CancellationToken cancellationToken = default)
    {
        if (vector.Length == 0)
        {
            _logger.LogWarning("Refusing to upsert empty vector for patient {PatientId}", patientId);
            return false;
        }

        try
        {
            var response = await _http.PutAsJsonAsync(
                $"/api/patient/{patientId}/embedding",
                new UpsertEmbeddingRequest
                {
                    SourceText = sourceText,
                    Vector = vector,
                    Model = model
                },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "Embedding upsert for patient {PatientId} failed: {Status} {Error}",
                    patientId, response.StatusCode, error);
                return false;
            }

            _logger.LogInformation(
                "Upserted embedding for patient {PatientId} ({Dims} dims, model {Model})",
                patientId, vector.Length, model);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error upserting embedding for patient {PatientId}", patientId);
            return false;
        }
    }

    /// <summary>
    /// Delete the embedding row for a patient. Not strictly required because the
    /// cascade FK on PatientEmbeddings removes it when the patient row is deleted,
    /// but exposing this lets callers force cleanup independent of the parent row.
    /// A 404 response is treated as success (already gone).
    /// </summary>
    public async Task<bool> DeleteAsync(int patientId, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _http.DeleteAsync($"/api/patient/{patientId}/embedding", cancellationToken);

            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogInformation("Deleted embedding for patient {PatientId} (status {Status})",
                    patientId, response.StatusCode);
                return true;
            }

            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogWarning(
                "Embedding delete for patient {PatientId} failed: {Status} {Error}",
                patientId, response.StatusCode, error);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting embedding for patient {PatientId}", patientId);
            return false;
        }
    }

    /// <summary>
    /// Request body shape for PUT /api/patient/{id}/embedding.
    /// Kept as a class so callers can express it via object initialisers.
    /// </summary>
    public sealed class UpsertEmbeddingRequest
    {
        public string SourceText { get; set; } = string.Empty;
        public float[] Vector { get; set; } = Array.Empty<float>();
        public string Model { get; set; } = string.Empty;
    }
}
