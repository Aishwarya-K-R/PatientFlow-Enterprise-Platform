using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PatientFlow.Patient.Services;

namespace PatientFlow.Patient.Controllers;

[ApiController]
[Route("api")]
public class PatientController(PatientService patientService) : ControllerBase
{
    private readonly PatientService _patientService = patientService;

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
    /// Internal endpoint for AI service to fetch all patients for cache warming.
    /// Protected to prevent abuse - should only be called during AI service initialization.
    /// </summary>
    [Authorize(Roles = "SYSTEM,ADMIN")]
    [HttpGet("patients/all")]
    public async Task<ActionResult> GetAllPatients()
    {
        var patients = await _patientService.GetAllPatientsAsync();
        return Ok(patients);
    }
}
