using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PatientFlow.Common.Metrics;
using PatientFlow.Common.Resilience;
using PatientFlow.Contracts.Config;
using Polly;

namespace PatientFlow.AI.Services;

/// <summary>
/// Talks to Ollama's /api/embeddings endpoint and returns the numeric vector
/// representation of a piece of text.
///
/// This is intentionally kept separate from LLMService because embeddings and
/// generative LLM calls have very different characteristics:
///   - Embeddings are fast (~50ms) and cheap; text generation is slow and expensive
///   - Embeddings use a different model (e.g. nomic-embed-text) than chat (llama3.2)
///   - Embeddings return a fixed-length float array; chat returns a string
///
/// The output dimension must match the pgvector column dimension in
/// PatientEmbeddings (currently vector(768) - matching nomic-embed-text).
/// </summary>
public class EmbeddingService
{
    private readonly HttpClient _http;
    private readonly AISettings _settings;
    private readonly ILogger<EmbeddingService> _logger;
    private readonly ResiliencePipeline<float[]> _resiliencePipeline;

    public EmbeddingService(HttpClient http, IOptions<AISettings> options, ILogger<EmbeddingService> logger)
    {
        _http = http;
        _settings = options.Value;
        _logger = logger;

        // Embeddings are quick once the model is loaded (~50-200ms), but the
        // FIRST call after Ollama starts (or after the model has been idle-evicted)
        // triggers a cold model load which can take 15-45s on CPU-only hosts.
        // Keeping the timeout at 60s absorbs that cold-start latency; warm calls
        // still return in a fraction of a second.
        _resiliencePipeline = ResiliencePolicies.GetCombinedPolicy<float[]>(
            logger,
            timeout: TimeSpan.FromSeconds(60));
    }

    /// <summary>
    /// Generates an embedding vector for the given text.
    /// Returns an empty array if the model or endpoint is not configured -
    /// callers should check length before persisting.
    /// </summary>
    public async Task<float[]> GenerateAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.EmbeddingModel) || string.IsNullOrWhiteSpace(_settings.EmbeddingEndpoint))
        {
            _logger.LogWarning("Embedding model or endpoint not configured; returning empty vector");
            return Array.Empty<float>();
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogWarning("GenerateAsync called with empty text; returning empty vector");
            return Array.Empty<float>();
        }

        _logger.LogDebug("Generating embedding via {Endpoint} with model {Model} for {Length} chars",
            _settings.EmbeddingEndpoint, _settings.EmbeddingModel, text.Length);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await _resiliencePipeline.ExecuteAsync(async ct =>
            {
                var response = await _http.PostAsJsonAsync(
                    _settings.EmbeddingEndpoint,
                    new
                    {
                        model = _settings.EmbeddingModel,
                        prompt = text
                    },
                    ct);

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync(ct);
                    throw new HttpRequestException($"Embedding API returned {response.StatusCode}: {error}");
                }

                var payload = await response.Content.ReadFromJsonAsync<OllamaEmbeddingResponse>(cancellationToken: ct);
                var vector = payload?.Embedding ?? Array.Empty<float>();

                if (vector.Length == 0)
                {
                    _logger.LogWarning("Embedding API returned empty vector for text of length {Length}", text.Length);
                }

                return vector;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Embedding call failed after retries: {Message}", ex.Message);
            return Array.Empty<float>();
        }
        finally
        {
            stopwatch.Stop();
            // Reuses the LLM histogram bucket - embeddings are much faster so they
            // will sit in the lower buckets and the p95 for chat will not be affected.
            AppMetrics.LlmRequestDuration.Observe(stopwatch.Elapsed.TotalSeconds);
        }
    }

    /// <summary>
    /// Ollama /api/embeddings response shape.
    /// Kept private since only this class needs to deserialize it.
    /// </summary>
    private sealed class OllamaEmbeddingResponse
    {
        [JsonPropertyName("embedding")]
        public float[] Embedding { get; set; } = Array.Empty<float>();
    }
}
