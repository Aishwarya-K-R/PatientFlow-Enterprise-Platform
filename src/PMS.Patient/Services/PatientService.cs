using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using PatientFlow.Patient.Data;
using PatientFlow.Common.Exceptions;
using PatientFlow.Common.Kafka;

namespace PatientFlow.Patient.Services;

public class PatientService(
    IPatientRepository repo,
    IMemoryCache memoryCache,
    IDistributedCache redisCache,
    KafkaProducer kafkaProducer,
    BillingGrpcClient billingGrpcClient,
    ILogger<PatientService> logger,
    IConfiguration config)
{
    private readonly IPatientRepository _repo = repo;
    private readonly IMemoryCache _memoryCache = memoryCache;
    private readonly IDistributedCache _redisCache = redisCache;
    private readonly KafkaProducer _kafkaProducer = kafkaProducer;
    private readonly BillingGrpcClient _billingGrpcClient = billingGrpcClient;
    private readonly IConfiguration _config = config;
    private readonly ILogger<PatientService> _logger = logger;

    public Task<List<Models.Patient>> GetPatientsAsync(
        string search, string sortCol, string sortDir, int pageNo, int pageSize)
    {
        return _repo.SearchAsync(search, sortCol, sortDir, pageNo, pageSize);
    }

    public async Task<Models.Patient> GetPatientByIdAsync(int id)
    {
        string cacheKey = $"Patient_{id}";

        _logger.LogInformation("Trying to get patient with ID {Id} from Memory Cache", id);
        if (_memoryCache.TryGetValue(cacheKey, out Models.Patient? cachedPatient) && cachedPatient != null)
        {
            _logger.LogInformation("Patient with ID {Id} found in Memory Cache", id);
            return cachedPatient;
        }

        _logger.LogInformation("Memory Cache miss. Trying Redis for patient ID {Id}", id);
        var patientJson = await _redisCache.GetStringAsync(cacheKey);
        if (patientJson != null)
        {
            _logger.LogInformation("Patient with ID {Id} found in Redis Cache", id);
            var patientObj = JsonSerializer.Deserialize<Models.Patient>(patientJson);
            if (patientObj != null)
            {
                _memoryCache.Set(
                    cacheKey,
                    patientObj,
                    new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
                        SlidingExpiration = TimeSpan.FromMinutes(2)
                    });
                return patientObj;
            }
        }

        _logger.LogInformation("Redis Cache miss. Fetching patient with ID {Id} from Database", id);
        var patient = await _repo.GetByIdAsync(id) ?? throw new PatientNotFoundException(id);

        _logger.LogInformation("Found patient with ID {Id} in Database. Caching now", id);
        _memoryCache.Set(
            cacheKey,
            patient,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
                SlidingExpiration = TimeSpan.FromMinutes(2)
            });
        await _redisCache.SetStringAsync(cacheKey, JsonSerializer.Serialize(patient),
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10),
                SlidingExpiration = TimeSpan.FromMinutes(5)
            });

        return patient;
    }

    public async Task<Models.Patient> CreatePatientAsync(Models.Patient patient)
    {
        Validate(patient);

        if (await _repo.GetByEmailAsync(patient.Email) != null)
        {
            throw new DuplicateEmailException(patient.Email);
        }

        var newPatient = new Models.Patient
        {
            Name = patient.Name,
            Email = patient.Email,
            Address = patient.Address,
            DateOfBirth = patient.DateOfBirth,
            RegisteredDate = patient.RegisteredDate
        };

        await _repo.AddAsync(newPatient);

        // Synchronous gRPC call to Billing service
        try
        {
            _logger.LogInformation("Creating billing account via gRPC for Patient {PatientId}", newPatient.Id);
            await _billingGrpcClient.CreateBillingAccountAsync(newPatient.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create billing account via gRPC for Patient {PatientId}", newPatient.Id);
            // Continue - Kafka consumer will retry if gRPC fails
        }

        // Publish Kafka event for reliability/audit
        await _kafkaProducer.PublishAsync(_config["Kafka:PatientCreatedTopic"]!, new { PatientId = newPatient.Id });

        return newPatient;
    }

    public async Task<Models.Patient> UpdatePatientAsync(int id, Models.Patient patient)
    {
        var existing = await _repo.GetByIdAsync(id) ?? throw new PatientNotFoundException(id);

        Validate(patient);

        if (await _repo.GetByEmailAsync(patient.Email, excludingId: id) != null)
        {
            throw new DuplicateEmailException(patient.Email);
        }

        existing.Name = patient.Name;
        existing.Email = patient.Email;
        existing.Address = patient.Address;
        existing.DateOfBirth = patient.DateOfBirth;
        existing.RegisteredDate = patient.RegisteredDate;

        await _repo.UpdateAsync(existing);
        await _kafkaProducer.PublishAsync(_config["Kafka:PatientUpdatedTopic"]!, new { PatientId = id });

        InvalidateCaches(id);
        return existing;
    }

    public async Task DeletePatientAsync(int id)
    {
        var existing = await _repo.GetByIdAsync(id) ?? throw new PatientNotFoundException(id);

        await _repo.DeleteAsync(existing);
        await _kafkaProducer.PublishAsync(_config["Kafka:PatientDeletedTopic"]!, new { PatientId = id });

        InvalidateCaches(id);
    }

    private static void Validate(Models.Patient patient)
    {
        if (patient == null
            || string.IsNullOrWhiteSpace(patient.Name)
            || string.IsNullOrWhiteSpace(patient.Address)
            || patient.DateOfBirth == default
            || patient.DateOfBirth >= DateOnly.FromDateTime(DateTime.Today)
            || patient.RegisteredDate == default
            || patient.RegisteredDate < patient.DateOfBirth)
        {
            throw new ArgumentException("Invalid patient details");
        }
    }

    private void InvalidateCaches(int id)
    {
        _memoryCache.Remove($"Patient_{id}");
        _ = _redisCache.RemoveAsync($"Patient_{id}");
    }
}
