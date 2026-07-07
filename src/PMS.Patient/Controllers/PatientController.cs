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
    /// Request DTO for the internal embedding upsert endpoint.
    /// Kept as a nested type to keep the wire contract co-located with the endpoint.
    /// </summary>
    public sealed class UpsertEmbeddingRequest
    {
        public string SourceText { get; set; } = string.Empty;
        public float[] Vector { get; set; } = Array.Empty<float>();
        public string Model { get; set; } = string.Empty;
    }
}
