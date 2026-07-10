using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PatientFlow.Patient.Services;
using PatientFlow.Contracts.Dtos;

namespace PatientFlow.Patient.Controllers;

[ApiController]
[Route("api")]
public class PatientController(
    PatientService patientService,
    PatientEmbeddingService patientEmbeddingService,
    ILogger<PatientController> logger) : ControllerBase
{
    private readonly PatientService _patientService = patientService;
    private readonly PatientEmbeddingService _patientEmbeddingService = patientEmbeddingService;
    private readonly ILogger<PatientController> _logger = logger;

    [Authorize]
    [HttpGet("patients")]
    public async Task<ActionResult> GetPatients(string search = "", string sortCol = "Id", string sortDir = "asc", int pageNo = 1, int pageSize = 10)
    {
        var patients = await _patientService.GetPatientsAsync(search, sortCol, sortDir, pageNo, pageSize);
        if (patients == null || patients.Count == 0)
        {
            return Ok(new { message = "No patients found" });
        }
        return Ok(patients);
    }

    [Authorize]
    [HttpGet("patient/{id}")]
    public async Task<ActionResult> GetPatientById(int id)
    {
        var patient = await _patientService.GetPatientByIdAsync(id);
        return Ok(patient);
    }

    [Authorize(Roles = "ADMIN")]
    [HttpPost("patient")]
    public async Task<ActionResult> CreatePatient(Models.Patient patient)
    {
        var newPatient = await _patientService.CreatePatientAsync(patient);
        return CreatedAtAction(nameof(GetPatientById), new { id = newPatient.Id }, newPatient);
    }

    [Authorize(Roles = "ADMIN")]
    [HttpPut("patient/{id}")]
    public async Task<ActionResult> UpdatePatient(int id, Models.Patient patient)
    {
        var updatedPatient = await _patientService.UpdatePatientAsync(id, patient);
        return Ok(updatedPatient);
    }

    [Authorize(Roles = "ADMIN")]
    [HttpDelete("patient/{id}")]
    public async Task<ActionResult> DeletePatient(int id)
    {
        await _patientService.DeletePatientAsync(id);
        return Ok(new { message = "Patient deleted successfully" });
    }

    /// <summary>
    /// Bulk export endpoint - returns ALL patients as lightweight DTOs.
    /// Use cases: cache warming, data export, backup systems.
    /// WARNING: No pagination - use only for internal services or admin operations.
    /// Accepts either an ADMIN JWT or an X-Internal-Api-Key header (handled by
    /// InternalApiKeyMiddleware) for service-to-service callers like AI warmup.
    /// </summary>
    [Authorize(Roles = "ADMIN")]
    [HttpGet("patients/all")]
    public async Task<ActionResult> GetAllPatients()
    {
        _logger.LogInformation("Bulk patient export requested");

        var patients = await _patientService.GetPatientsAsync("", "Id", "asc", 1, int.MaxValue);

        // Map to lightweight DTO (reduces payload size)
        var dtos = patients.Select(p => new PatientDto
        {
            Id = p.Id,
            Name = p.Name,
            Email = p.Email,
            DateOfBirth = p.DateOfBirth,
            Address = p.Address,
            RegisteredDate = p.RegisteredDate,
            MedicalHistory = p.MedicalHistory
        }).ToList();

        _logger.LogInformation("Returning {Count} patients", dtos.Count);
        return Ok(dtos);
    }

    /// <summary>
    /// Internal endpoint used by the AI service startup backfill to discover
    /// patients that don't yet have a stored embedding. Returns just the ids
    /// (the AI service will call GET /api/patient/{id} for each to build the
    /// same PatientDto the Kafka handler uses, so a single code path owns the
    /// PhiRedactor -> Ollama -> pgvector flow).
    /// Bounded by <c>limit</c> so a huge legacy corpus is processed across
    /// several restarts rather than in one giant batch.
    /// Auth: ADMIN JWT or internal API key (same rule as the other embedding endpoints).
    /// </summary>
    [Authorize(Roles = "ADMIN")]
    [HttpGet("patients/missing-embeddings")]
    public async Task<ActionResult> GetPatientsMissingEmbeddings([FromQuery] int limit = 500)
    {
        // Cap the limit to avoid a caller asking for the whole table by
        // mistake - startup backfill is meant to be a controlled trickle.
        var effectiveLimit = Math.Clamp(limit, 1, 5000);

        var ids = await _patientEmbeddingService.GetPatientIdsMissingEmbeddingAsync(effectiveLimit);

        _logger.LogInformation(
            "Missing-embedding scan returned {Count} patient ids (limit={Limit})",
            ids.Count, effectiveLimit);

        return Ok(new { count = ids.Count, patientIds = ids });
    }

    /// <summary>
    /// Internal endpoint used by the AI service to persist an embedding vector
    /// for a patient. Auth is enforced via InternalApiKeyMiddleware (which
    /// promotes the caller to ADMIN when the shared key matches) plus the
    /// [Authorize(Roles = "ADMIN")] attribute below - which also permits
    /// human admins to trigger it manually for debugging.
    /// </summary>
    [Authorize(Roles = "ADMIN")]
    [HttpPut("patient/{id}/embedding")]
    public async Task<ActionResult> UpsertEmbedding(int id, [FromBody] UpsertEmbeddingRequest request)
    {
        if (request?.Vector == null || request.Vector.Length == 0)
        {
            return BadRequest(new { message = "Vector must not be empty" });
        }

        var written = await _patientEmbeddingService.UpsertAsync(
            id, request.SourceText ?? string.Empty, request.Vector, request.Model ?? string.Empty);

        if (!written)
        {
            return NotFound(new { message = $"Patient {id} not found" });
        }

        return Ok(new { message = "Embedding upserted", patientId = id, dimensions = request.Vector.Length });
    }

    /// <summary>
    /// Delete the embedding row for a patient. Idempotent - returns 200 whether
    /// the row existed or not. Included so callers can force removal without
    /// relying on the cascade FK from Patients.
    /// </summary>
    [Authorize(Roles = "ADMIN")]
    [HttpDelete("patient/{id}/embedding")]
    public async Task<ActionResult> DeleteEmbedding(int id)
    {
        var deleted = await _patientEmbeddingService.DeleteAsync(id);
        return Ok(new { message = deleted ? "Embedding deleted" : "No embedding to delete", patientId = id });
    }

    /// <summary>
    /// Cosine-similarity nearest-neighbour search over PatientEmbeddings.
    /// The AI service calls this after embedding a natural-language question
    /// so it can pull just the most relevant patients into an LLM prompt
    /// instead of dumping the whole corpus.
    /// Body carries the query vector (dimension must match the stored vectors,
    /// currently 768 for nomic-embed-text). Response is a list of
    /// {patientId, distance} ordered ascending by cosine distance (smaller
    /// distance = closer match, 0.0 = identical).
    /// Same dual-auth rule as the other embedding endpoints.
    /// </summary>
    [Authorize(Roles = "ADMIN")]
    [HttpPost("patients/vector-search")]
    public async Task<ActionResult> VectorSearch([FromBody] VectorSearchRequest request)
    {
        if (request?.QueryVector == null || request.QueryVector.Length == 0)
        {
            return BadRequest(new { message = "QueryVector must not be empty" });
        }

        // Cap topK so a caller can't accidentally scan the whole table.
        var topK = Math.Clamp(request.TopK <= 0 ? 5 : request.TopK, 1, 50);

        var matches = await _patientEmbeddingService.SearchNearestAsync(request.QueryVector, topK);

        _logger.LogInformation(
            "Vector search returned {Count} matches (topK={TopK}, dims={Dims})",
            matches.Count, topK, request.QueryVector.Length);

        return Ok(new
        {
            count = matches.Count,
            results = matches.Select(m => new { patientId = m.PatientId, distance = m.Distance })
        });
    }

    /// <summary>
    /// Request DTO for the internal embedding upsert endpoint.
    /// Kept as a nested type to keep the wire contract co-located with the endpoint.
    /// </summary>
    public sealed class UpsertEmbeddingRequest
    {
        public string SourceText { get; set; } = string.Empty;
        public float[] Vector { get; set; } = Array.Empty<float>();
        public string Model { get; set; } = string.Empty;
    }

    /// <summary>
    /// Request DTO for the vector-search endpoint. Kept as a nested type for
    /// the same co-location reason as UpsertEmbeddingRequest.
    /// </summary>
    public sealed class VectorSearchRequest
    {
        public float[] QueryVector { get; set; } = Array.Empty<float>();
        public int TopK { get; set; } = 5;
    }
}
