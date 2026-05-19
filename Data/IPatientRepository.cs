using Patient_Management_System.Models;

namespace Patient_Management_System.Data
{
    /// <summary>
    /// Data-access contract for the Patient aggregate.
    /// Owns ALL EF Core interaction for Patients; callers (services) never
    /// touch DbContext directly. This is the seam for unit-test mocking and
    /// the boundary we'll keep when the patient service becomes its own
    /// microservice in Phase 1.
    /// </summary>
    public interface IPatientRepository
    {
        Task<List<Patient>> SearchAsync(
            string search, string sortCol, string sortDir, int pageNo, int pageSize);

        Task<Patient?> GetByIdAsync(int id);

        /// <summary>
        /// Find a patient by email. When <paramref name="excludingId"/> is supplied,
        /// the match with that id is ignored — useful for "is this email used by
        /// any OTHER patient" duplicate checks during update.
        /// </summary>
        Task<Patient?> GetByEmailAsync(string email, int? excludingId = null);

        Task<Patient> AddAsync(Patient patient);

        Task UpdateAsync(Patient patient);

        Task DeleteAsync(Patient patient);
    }
}
