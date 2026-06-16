using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using PatientFlow.Patient.Data;
using PatientFlow.Patient.Models;
using PatientFlow.Common.Exceptions;
using PatientFlow.Common.Metrics;
using PatientFlow.Contracts.Events;

namespace PatientFlow.Patient.Services;

public class PatientService(
    IPatientRepository repo,
    PatientDbContext db,
    IMemoryCache memoryCache,
    IDistributedCache redisCache,
    BillingGrpcClient billingGrpcClient,
    ILogger<PatientService> logger,
    IConfiguration config)
{
    // Cache key prefix + TTLs centralised so they're not magic numbers sprinkled inline.
    private const string CacheKeyPrefix = "Patient_";
    private static readonly TimeSpan MemoryCacheAbsoluteTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MemoryCacheSlidingTtl  = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RedisCacheAbsoluteTtl  = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RedisCacheSlidingTtl   = TimeSpan.FromMinutes(5);

    private readonly IPatientRepository _repo = repo;
    private readonly PatientDbContext _db = db;
    private readonly IMemoryCache _memoryCache = memoryCache;
    private readonly IDistributedCache _redisCache = redisCache;
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
        var cacheKey = CacheKey(id);

        _logger.LogInformation("Trying to get patient with ID {Id} from Memory Cache", id);
        if (_memoryCache.TryGetValue(cacheKey, out Models.Patient? cachedPatient) && cachedPatient != null)
        {
            _logger.LogInformation("Patient with ID {Id} found in Memory Cache", id);
            AppMetrics.RedisCacheHits.WithLabels("memory").Inc();
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
                _memoryCache.Set(cacheKey, patientObj, MemoryCacheOptions());
                AppMetrics.RedisCacheHits.WithLabels("redis").Inc();
                return patientObj;
            }
        }

        _logger.LogInformation("Redis Cache miss. Fetching patient with ID {Id} from Database", id);
        AppMetrics.RedisCacheMisses.Inc();
        var patient = await _repo.GetByIdAsync(id) ?? throw new PatientNotFoundException(id);

        _logger.LogInformation("Found patient with ID {Id} in Database. Caching now", id);
        _memoryCache.Set(cacheKey, patient, MemoryCacheOptions());
        await _redisCache.SetStringAsync(cacheKey, JsonSerializer.Serialize(patient), RedisCacheOptions());

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

        // Saga: patient row + Billing gRPC call + outbox event must all succeed
        // or all roll back. Explicit transaction lets us call gRPC AFTER the
        // patient has an Id (post-SaveChanges) but BEFORE we commit.
        using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            _db.Patients.Add(newPatient);
            await _db.SaveChangesAsync();

            _logger.LogInformation("Creating billing account via gRPC for Patient {PatientId}", newPatient.Id);
            await _billingGrpcClient.CreateBillingAccountAsync(newPatient.Id);

            _db.OutboxMessages.Add(BuildOutboxMessage(
                topicKey: "Kafka:PatientCreatedTopic",
                eventType: EventTypes.PatientCreated,
                payload: new PatientCreatedEvent
                {
                    PatientId = newPatient.Id,
                    Email = newPatient.Email,
                    Name = newPatient.Name
                }));
            await _db.SaveChangesAsync();

            await tx.CommitAsync();
            AppMetrics.PatientsCreated.Inc();
            return newPatient;
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("IX_Patients_Email_Unique") == true)
        {
            await tx.RollbackAsync();
            throw new DuplicateEmailException(patient.Email);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            _logger.LogError(ex, "Patient creation saga failed — rolled back. Patient {Email} not created.", patient.Email);
            throw;
        }
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

        var outboxMessage = BuildOutboxMessage(
            topicKey: "Kafka:PatientUpdatedTopic",
            eventType: EventTypes.PatientUpdated,
            payload: new PatientUpdatedEvent
            {
                PatientId = id,
                Email = existing.Email,
                Name = existing.Name
            });

        await _repo.UpdateAsync(existing, outboxMessage);

        InvalidateCaches(id);
        AppMetrics.PatientsUpdated.Inc();
        return existing;
    }

    public async Task DeletePatientAsync(int id)
    {
        var existing = await _repo.GetByIdAsync(id) ?? throw new PatientNotFoundException(id);

        var outboxMessage = BuildOutboxMessage(
            topicKey: "Kafka:PatientDeletedTopic",
            eventType: EventTypes.PatientDeleted,
            payload: new PatientDeletedEvent { PatientId = id });

        await _repo.DeleteAsync(existing, outboxMessage);

        InvalidateCaches(id);
        AppMetrics.PatientsDeleted.Inc();
    }

    // -------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------

    private OutboxMessage BuildOutboxMessage(string topicKey, string eventType, object payload)
    {
        var envelope = EventEnvelope.Create(
            eventType: eventType,
            source: EventSources.PatientService,
            payload: payload);

        return new OutboxMessage
        {
            Topic = _config[topicKey]!,
            Payload = JsonSerializer.Serialize(envelope),
            CreatedAt = DateTime.UtcNow
        };
    }

    private static string CacheKey(int id) => $"{CacheKeyPrefix}{id}";

    private static MemoryCacheEntryOptions MemoryCacheOptions() => new()
    {
        AbsoluteExpirationRelativeToNow = MemoryCacheAbsoluteTtl,
        SlidingExpiration = MemoryCacheSlidingTtl
    };

    private static DistributedCacheEntryOptions RedisCacheOptions() => new()
    {
        AbsoluteExpirationRelativeToNow = RedisCacheAbsoluteTtl,
        SlidingExpiration = RedisCacheSlidingTtl
    };

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
        var key = CacheKey(id);
        _memoryCache.Remove(key);
        _ = _redisCache.RemoveAsync(key);
    }
}
