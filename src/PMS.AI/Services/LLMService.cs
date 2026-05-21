using Microsoft.Extensions.Options;
using PatientFlow.Contracts.Config;
using System.Text.Json;

namespace PatientFlow.AI.Services;

public class LLMService(HttpClient http, IOptions<AISettings> options)
{
    private readonly HttpClient _http = http;
    private readonly AISettings _settings = options.Value;

    public async Task<string> AskAsync(string prompt)
    {
        if (string.IsNullOrWhiteSpace(_settings.Model) || string.IsNullOrWhiteSpace(_settings.Endpoint))
        {
            return "LLM Error: Model or Endpoint not configured properly.";
        }

        var response = await _http.PostAsJsonAsync(
            _settings.Endpoint,
            new
            {
                model = _settings.Model,
                prompt = prompt,
                stream = false
            });

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            return $"LLM Error: {error}";
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        if (json.TryGetProperty("response", out var result))
        {
            return result.GetString() ?? "";
        }

        return "No response from AI";
    }
}
