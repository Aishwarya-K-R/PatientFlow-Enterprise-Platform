using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PatientFlow.Patient.Services;
using PatientFlow.Contracts.Dtos;

namespace PatientFlow.Patient.Controllers;

[ApiController]
[Route("api")]
public class PatientController(
    PatientService patientService,
    ILogger<PatientController> logger) : ControllerBase
{
    private readonly PatientService _patientService = patientService;
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
            RegisteredDate = p.RegisteredDate
        }).ToList();

        _logger.LogInformation("Returning {Count} patients", dtos.Count);
        return Ok(dtos);
    }
}
