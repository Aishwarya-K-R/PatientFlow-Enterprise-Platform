using PatientFlow.Contracts.Dtos;

namespace PatientFlow.AI.Services;

/// <summary>
/// Typed HttpClient for Patient Service API.
/// Handles service-to-service communication for cache warming.
/// </summary>
public class PatientServiceClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<PatientServiceClient> _logger;

    public PatientServiceClient(HttpClient httpClient, ILogger<PatientServiceClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// Fetch all patients from Patient Service for cache warming.
    /// Uses dedicated /patients/all endpoint that returns lightweight DTOs.
    /// </summary>
    public async Task<List<PatientDto>> GetAllPatientsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Fetching all patients from Patient Service");

        try
        {
            var response = await _httpClient.GetAsync("/api/patients/all", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException(
                    $"Failed to fetch patients: {response.StatusCode} - {error}");
            }

            var patients = await response.Content.ReadFromJsonAsync<List<PatientDto>>(
                cancellationToken: cancellationToken);

            _logger.LogInformation("Fetched {Count} patients from Patient Service", 
                patients?.Count ?? 0);

            return patients ?? new List<PatientDto>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching patients from Patient Service");
            throw;
        }
    }
}
