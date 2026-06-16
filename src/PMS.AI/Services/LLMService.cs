using Microsoft.Extensions.Options;
using PatientFlow.Contracts.Config;
using PatientFlow.Common.Metrics;
using PatientFlow.Common.Resilience;
using Polly;
using System.Diagnostics;
using System.Text.Json;

namespace PatientFlow.AI.Services;

public class LLMService
{
    private readonly HttpClient _http;
    private readonly AISettings _settings;
    private readonly ILogger<LLMService> _logger;
    private readonly ResiliencePipeline<string> _resiliencePipeline;

    public LLMService(HttpClient http, IOptions<AISettings> options, ILogger<LLMService> logger)
    {
        _http = http;
        _settings = options.Value;
        _logger = logger;

        // Initialize Polly resilience pipeline (timeout → retry → circuit breaker)
        _resiliencePipeline = ResiliencePolicies.GetCombinedPolicy<string>(
            logger,
            timeout: TimeSpan.FromSeconds(30)); // LLMs can be slow
    }

    public async Task<string> AskAsync(string prompt)
    {
        if (string.IsNullOrWhiteSpace(_settings.Model) || string.IsNullOrWhiteSpace(_settings.Endpoint))
        {
            return "LLM Error: Model or Endpoint not configured properly.";
        }

        _logger.LogInformation("Calling LLM with resilience policies: {Model} at {Endpoint}", 
            _settings.Model, _settings.Endpoint);

        // Time the entire call (including resilience retries) so the histogram
        // reflects user-visible latency, not just one HTTP attempt.
        var stopwatch = Stopwatch.StartNew();
        try
        {
            // Execute HTTP call with Polly resilience pipeline
            return await _resiliencePipeline.ExecuteAsync(async cancellationToken =>
            {
                var response = await _http.PostAsJsonAsync(
                    _settings.Endpoint,
                    new
                    {
                        model = _settings.Model,
                        prompt = prompt,
                        stream = false
                    }, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new HttpRequestException($"LLM returned {response.StatusCode}: {error}");
                }

                var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);

                if (json.TryGetProperty("response", out var result))
                {
                    return result.GetString() ?? "No response from AI";
                }

                return "No response from AI";
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LLM call failed after retries: {Message}", ex.Message);
            return $"LLM Error: {ex.Message}";
        }
        finally
        {
            stopwatch.Stop();
            AppMetrics.LlmRequestDuration.Observe(stopwatch.Elapsed.TotalSeconds);
        }
    }
}
