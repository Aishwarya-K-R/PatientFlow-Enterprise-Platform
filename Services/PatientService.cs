using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Patient_Management_System.Data;
using Patient_Management_System.Exceptions;
using Patient_Management_System.Kafka;
using Patient_Management_System.Models;

namespace Patient_Management_System.Services
{
    public class PatientService(
        IPatientRepository repo,
        IMemoryCache memoryCache,
        IDistributedCache redisCache,
        KafkaProducer kafkaProducer,
        ILogger<PatientService> logger,
        IConfiguration config,
        RedisService redis,
        ContextService contextService)
    {
        private readonly IPatientRepository _repo = repo;
        private readonly IMemoryCache _memoryCache = memoryCache;
        private readonly IDistributedCache _redisCache = redisCache;
        private readonly KafkaProducer _kafkaProducer = kafkaProducer;
        private readonly IConfiguration _config = config;
        private readonly ILogger<PatientService> _logger = logger;
        private readonly RedisService _redis = redis;
        private readonly ContextService _contextService = contextService;

        public Task<List<Patient>> GetPatientsAsync(
            string search, string sortCol, string sortDir, int pageNo, int pageSize)
        {
            return _repo.SearchAsync(search, sortCol, sortDir, pageNo, pageSize);
        }

        public async Task<Patient> GetPatientByIdAsync(int id)
        {
            string cacheKey = $"Patient_{id}";

            // Tier 1: in-process memory cache
            _logger.LogInformation($"Trying to get patient with ID {id} from Memory Cache...");
            if (_memoryCache.TryGetValue(cacheKey, out Patient cachedPatient))
            {
                _logger.LogInformation($"Patient with ID {id} found in Memory Cache");
                return cachedPatient;
            }

            // Tier 2: Redis
            _logger.LogInformation($"Memory Cache miss. Trying to get patient with ID {id} from Redis Cache...");
            var patientJson = await _redisCache.GetStringAsync(cacheKey);
            if (patientJson != null)
            {
                _logger.LogInformation($"Patient with ID {id} found in Redis Cache");
                var patientObj = JsonSerializer.Deserialize<Patient>(patientJson);
                _memoryCache.Set(
                    cacheKey,
                    patientObj,
                    new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
                        SlidingExpiration = TimeSpan.FromMinutes(2)
                    });
                _logger.LogInformation($"Patient with ID {id} stored in Memory Cache.");
                return patientObj;
            }

            // Tier 3: database, via the repository (no direct DbContext here)
            _logger.LogInformation($"Redis Cache miss. Fetching patient with ID {id} from Database...");
            var patient = await _repo.GetByIdAsync(id) ?? throw new PatientNotFoundException(id);

            _logger.LogInformation($"Found patient with ID {id} in Database. Caching now...");
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

            _logger.LogInformation($"Patient with ID {id} stored in both Redis and Memory Cache.");
            return patient;
        }

        public async Task<Patient> CreatePatientAsync(Patient patient)
        {
            Validate(patient);

            if (await _repo.GetByEmailAsync(patient.Email) != null)
            {
                throw new DuplicateEmailException(patient.Email);
            }

            var newPatient = new Patient
            {
                Name = patient.Name,
                Email = patient.Email,
                Address = patient.Address,
                DateOfBirth = patient.DateOfBirth,
                RegisteredDate = patient.RegisteredDate
            };

            await _repo.AddAsync(newPatient);
            await _kafkaProducer.PublishAsync(_config["Kafka:PatientCreatedTopic"], new { PatientId = newPatient.Id });

            return newPatient;
        }

        public async Task<Patient> UpdatePatientAsync(int id, Patient patient)
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
            await _kafkaProducer.PublishAsync(_config["Kafka:PatientUpdatedTopic"], new { PatientId = id });

            InvalidateCaches(id);
            return existing;
        }

        public async Task DeletePatientAsync(int id)
        {
            var existing = await _repo.GetByIdAsync(id) ?? throw new PatientNotFoundException(id);

            await _repo.DeleteAsync(existing);
            await _kafkaProducer.PublishAsync(_config["Kafka:PatientDeletedTopic"], new { PatientId = id });

            InvalidateCaches(id);
        }

        // -------------------------------------------------------------------
        // Private helpers — business rules and cache management.
        // No DbContext / EF code here. That's the repository's job.
        // -------------------------------------------------------------------

        private static void Validate(Patient patient)
        {
            if (patient == null
                || string.IsNullOrWhiteSpace(patient.Name)
                || string.IsNullOrWhiteSpace(patient.Address)
                || patient.DateOfBirth == default
                || patient.DateOfBirth >= DateOnly.FromDateTime(DateTime.Today)
                || patient.RegisteredDate == default
                || patient.RegisteredDate < patient.DateOfBirth)
            {
                throw new ArgumentException("Invalid patient details!!!");
            }
        }

        private void InvalidateCaches(int id)
        {
            _memoryCache.Remove($"Patient_{id}");
            _ = _redisCache.RemoveAsync($"Patient_{id}");
        }
    }
}
