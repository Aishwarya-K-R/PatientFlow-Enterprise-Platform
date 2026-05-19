using Microsoft.EntityFrameworkCore;
using Patient_Management_System.Models;

namespace Patient_Management_System.Data
{
    public class PatientRepository(AppDbContext db) : IPatientRepository
    {
        private readonly AppDbContext _db = db;

        public async Task<List<Patient>> SearchAsync(
            string search, string sortCol, string sortDir, int pageNo, int pageSize)
        {
            // Defensive clamping lives HERE — closer to the database — so no caller
            // can accidentally request a million rows or a negative offset.
            pageNo = Math.Max(1, pageNo);
            pageSize = Math.Clamp(pageSize, 1, 100);

            var query = _db.Patients.AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var pattern = $"%{search}%";
                query = query.Where(p =>
                    EF.Functions.ILike(p.Name, pattern) ||
                    EF.Functions.ILike(p.Email, pattern));
            }

            // Allow-list of sortable columns. Anything outside falls through to Id —
            var isDesc = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);
            query = sortCol switch
            {
                "Name"           => isDesc ? query.OrderByDescending(p => p.Name)           : query.OrderBy(p => p.Name),
                "Email"          => isDesc ? query.OrderByDescending(p => p.Email)          : query.OrderBy(p => p.Email),
                "RegisteredDate" => isDesc ? query.OrderByDescending(p => p.RegisteredDate) : query.OrderBy(p => p.RegisteredDate),
                "DateOfBirth"    => isDesc ? query.OrderByDescending(p => p.DateOfBirth)    : query.OrderBy(p => p.DateOfBirth),
                _                => isDesc ? query.OrderByDescending(p => p.Id)             : query.OrderBy(p => p.Id),
            };

            return await query
                .Skip((pageNo - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();
        }

        public async Task<Patient?> GetByIdAsync(int id)
        {
            return await _db.Patients.FindAsync(id);
        }

        public async Task<Patient?> GetByEmailAsync(string email, int? excludingId = null)
        {
            var normalized = email.ToLower();
            return await _db.Patients.FirstOrDefaultAsync(p =>
                p.Email.ToLower() == normalized &&
                (excludingId == null || p.Id != excludingId));
        }

        public async Task<Patient> AddAsync(Patient patient)
        {
            _db.Patients.Add(patient);
            await _db.SaveChangesAsync();
            return patient;
        }

        public async Task UpdateAsync(Patient patient)
        {
            // The caller is expected to have fetched this patient via GetByIdAsync
            // (which returns a tracked entity) and mutated its properties.
            // SaveChanges flushes the tracked changes.
            await _db.SaveChangesAsync();
        }

        public async Task DeleteAsync(Patient patient)
        {
            _db.Patients.Remove(patient);
            await _db.SaveChangesAsync();
        }
    }
}
