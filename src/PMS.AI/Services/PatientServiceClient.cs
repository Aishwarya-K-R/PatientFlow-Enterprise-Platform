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

    /// <summary>
    /// Fetch a single patient by id. Used by the embedding pipeline to
    /// build a richer source text than what the Kafka event carries
    /// (events only have Id, Name, Email - not address / DOB).
    /// Returns null on 404 so callers can decide whether that's fatal.
    /// </summary>
    public async Task<PatientDto?> GetPatientByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync($"/api/patient/{id}", cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException(
                    $"Failed to fetch patient {id}: {response.StatusCode} - {error}");
            }

            return await response.Content.ReadFromJsonAsync<PatientDto>(cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching patient {PatientId} from Patient Service", id);
            throw;
        }
    }
}
