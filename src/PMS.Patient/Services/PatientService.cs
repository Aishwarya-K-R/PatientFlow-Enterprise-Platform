using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using PatientFlow.Patient.Data;
using PatientFlow.Patient.Models;
using PatientFlow.Common.Exceptions;
using PatientFlow.Common.Kafka;
using PatientFlow.Contracts.Events;

namespace PatientFlow.Patient.Services;

public class PatientService(
    IPatientRepository repo,
    PatientDbContext db,
    IMemoryCache memoryCache,
    IDistributedCache redisCache,
    KafkaProducer kafkaProducer,
    BillingGrpcClient billingGrpcClient,
    ILogger<PatientService> logger,
    IConfiguration config)
{
    private readonly IPatientRepository _repo = repo;
    private readonly PatientDbContext _db = db;
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

        // Saga: patient row + Billing gRPC call + outbox event must all succeed
        // or all roll back. We open an explicit transaction so we can call gRPC
        // AFTER the patient has an Id (post-SaveChanges) but BEFORE we commit.
        using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            // 1. Save patient — populates newPatient.Id from the database.
            _db.Patients.Add(newPatient);
            await _db.SaveChangesAsync();

            // 2. Synchronous gRPC call to Billing. If this fails, the whole
            //    transaction rolls back — no orphan patient row, no outbox event.
            _logger.LogInformation("Creating billing account via gRPC for Patient {PatientId}", newPatient.Id);
            await _billingGrpcClient.CreateBillingAccountAsync(newPatient.Id);

            // 3. Now that the Id is real and gRPC succeeded, write the outbox event.
            var eventEnvelope = new EventEnvelope
            {
                EventId = Guid.NewGuid().ToString(),
                EventType = "PatientCreated",
                Version = "v1",
                OccurredAt = DateTime.UtcNow,
                Source = "PatientService",
                Payload = new PatientCreatedEvent
                {
                    PatientId = newPatient.Id,
                    Email = newPatient.Email,
                    Name = newPatient.Name
                }
            };

            _db.OutboxMessages.Add(new OutboxMessage
            {
                Topic = _config["Kafka:PatientCreatedTopic"]!,
                Payload = JsonSerializer.Serialize(eventEnvelope),
                CreatedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();

            await tx.CommitAsync();
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

        // Create outbox message for Kafka event
        var eventEnvelope = new EventEnvelope
        {
            EventId = Guid.NewGuid().ToString(),
            EventType = "PatientUpdated",
            Version = "v1",
            OccurredAt = DateTime.UtcNow,
            Source = "PatientService",
            Payload = new PatientUpdatedEvent
            {
                PatientId = id,
                Email = existing.Email,
                Name = existing.Name
            }
        };

        var outboxMessage = new OutboxMessage
        {
            Topic = _config["Kafka:PatientUpdatedTopic"]!,
            Payload = JsonSerializer.Serialize(eventEnvelope),
            CreatedAt = DateTime.UtcNow
        };

        await _repo.UpdateAsync(existing, outboxMessage);

        InvalidateCaches(id);
        return existing;
    }

    public async Task DeletePatientAsync(int id)
    {
        var existing = await _repo.GetByIdAsync(id) ?? throw new PatientNotFoundException(id);

        // Create outbox message for Kafka event
        var eventEnvelope = new EventEnvelope
        {
            EventId = Guid.NewGuid().ToString(),
            EventType = "PatientDeleted",
            Version = "v1",
            OccurredAt = DateTime.UtcNow,
            Source = "PatientService",
            Payload = new PatientDeletedEvent
            {
                PatientId = id
            }
        };

        var outboxMessage = new OutboxMessage
        {
            Topic = _config["Kafka:PatientDeletedTopic"]!,
            Payload = JsonSerializer.Serialize(eventEnvelope),
            CreatedAt = DateTime.UtcNow
        };

        await _repo.DeleteAsync(existing, outboxMessage);

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

    /// <summary>
    /// Get all patients for AI service cache warming.
    /// Returns lightweight DTOs (no full entity graph).
    /// </summary>
    public async Task<List<PatientSnapshotDto>> GetAllPatientsAsync()
    {
        _logger.LogInformation("Fetching all patients for cache snapshot");

        var patients = await _db.Patients
            .AsNoTracking()  // Read-only query
            .Select(p => new PatientSnapshotDto
            {
                PatientId = p.Id,
                Name = p.Name,
                Email = p.Email,
                DateOfBirth = p.DateOfBirth,
                Address = p.Address
            })
            .ToListAsync();

        _logger.LogInformation("Fetched {Count} patients for snapshot", patients.Count);
        return patients;
    }
}

/// <summary>
/// Lightweight DTO for AI service cache warming.
/// Contains only fields needed for AI context.
/// </summary>
public record PatientSnapshotDto
{
    public int PatientId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public DateOnly DateOfBirth { get; init; }
    public string Address { get; init; } = string.Empty;
}
