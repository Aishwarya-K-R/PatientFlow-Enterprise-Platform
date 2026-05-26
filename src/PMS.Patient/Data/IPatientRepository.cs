namespace PatientFlow.Patient.Data;

public interface IPatientRepository
{
    Task<List<Models.Patient>> SearchAsync(string search, string sortCol, string sortDir, int pageNo, int pageSize);
    Task<Models.Patient?> GetByIdAsync(int id);
    Task<Models.Patient?> GetByEmailAsync(string email, int? excludingId = null);
    Task<Models.Patient> AddAsync(Models.Patient patient, Models.OutboxMessage? outboxMessage = null);
    Task UpdateAsync(Models.Patient patient, Models.OutboxMessage? outboxMessage = null);
    Task DeleteAsync(Models.Patient patient, Models.OutboxMessage? outboxMessage = null);
}
