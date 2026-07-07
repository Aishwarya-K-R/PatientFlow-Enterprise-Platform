using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PatientFlow.Contracts.Config;

namespace PatientFlow.AI.Services;

/// <summary>
/// Turns a natural-language question into a small set of the most semantically
/// relevant patients by:
///   1. Embedding the question via Ollama (EmbeddingService)
///   2. Asking the Patient service to run cosine-distance nearest-neighbour
///      search over pgvector and return the top-K patient ids
/// The AI service itself never touches Postgres - it owns the question-side
/// embedding and lets the Patient service own the vector store, preserving
/// the microservice-owns-its-database rule.
///
/// This is the "retrieval" half of Retrieval-Augmented Generation: the caller
/// (AIController) is responsible for pulling the pseudonymised context for
/// those ids out of Redis and feeding them into the LLM prompt.
/// </summary>
public class VectorSearchService
{
    private readonly EmbeddingService _embeddingService;
    private readonly HttpClient _http;
    private readonly AISettings _settings;
    private readonly ILogger<VectorSearchService> _logger;

    public VectorSearchService(
        EmbeddingService embeddingService,
        HttpClient http,
        IOptions<AISettings> options,
        ILogger<VectorSearchService> logger)
    {
        _embeddingService = embeddingService;
        _http = http;
        _settings = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Finds the top-K patients whose stored embedding is closest to the given
    /// question. Uses <see cref="AISettings.TopKResults"/> if the caller does
    /// not override <paramref name="topK"/>.
    /// Returns an empty list if the question cannot be embedded, the Patient
    /// service is unreachable, or no rows exist in pgvector yet - callers
    /// treat "no relevant patients found" as a normal outcome, not an error.
    /// </summary>
    public async Task<List<PatientMatch>> FindRelevantPatientsAsync(
        string question,
        int? topK = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return new List<PatientMatch>();
        }

        var effectiveTopK = topK ?? _settings.TopKResults;
        if (effectiveTopK <= 0) effectiveTopK = 5;

        // 1. Embed the question. If Ollama is down we can't do semantic search.
        var queryVector = await _embeddingService.GenerateAsync(question, cancellationToken);
        if (queryVector.Length == 0)
        {
            _logger.LogWarning(
                "Question embedding was empty; skipping vector search and returning no matches");
            return new List<PatientMatch>();
        }

        // 2. Ask Patient service for the nearest neighbours.
        try
        {
            var response = await _http.PostAsJsonAsync(
                "/api/patients/vector-search",
                new VectorSearchRequest
                {
                    QueryVector = queryVector,
                    TopK = effectiveTopK
                },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "Vector search returned {Status}: {Error}", response.StatusCode, error);
                return new List<PatientMatch>();
            }

            var payload = await response.Content.ReadFromJsonAsync<VectorSearchResponse>(
                cancellationToken: cancellationToken);

            var matches = payload?.Results ?? new List<PatientMatch>();

            _logger.LogInformation(
                "Vector search returned {Count} matches for question (top distance: {Top:F4})",
                matches.Count, matches.Count == 0 ? -1 : matches[0].Distance);

            return matches;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Vector search failed - returning no matches");
            return new List<PatientMatch>();
        }
    }

    // Wire-shape for POST /api/patients/vector-search.
    private sealed class VectorSearchRequest
    {
        [JsonPropertyName("queryVector")]
        public float[] QueryVector { get; set; } = Array.Empty<float>();

        [JsonPropertyName("topK")]
        public int TopK { get; set; }
    }

    private sealed class VectorSearchResponse
    {
        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("results")]
        public List<PatientMatch> Results { get; set; } = new();
    }
}

/// <summary>
/// Single row of a vector search result. Public so callers (AIController,
/// tests) can inspect the distance for logging or threshold filtering.
/// Distance is cosine distance: 0 = identical, 1 = orthogonal.
/// </summary>
public sealed class PatientMatch
{
    [JsonPropertyName("patientId")]
    public int PatientId { get; set; }

    [JsonPropertyName("distance")]
    public double Distance { get; set; }
}
