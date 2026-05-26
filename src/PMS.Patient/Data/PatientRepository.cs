using Microsoft.EntityFrameworkCore;
using PatientFlow.Common.Exceptions;

namespace PatientFlow.Patient.Data;

public class PatientRepository(PatientDbContext db) : IPatientRepository
{
    private readonly PatientDbContext _db = db;

    public async Task<List<Models.Patient>> SearchAsync(
        string search, string sortCol, string sortDir, int pageNo, int pageSize)
    {
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

    public async Task<Models.Patient?> GetByIdAsync(int id)
    {
        return await _db.Patients.FindAsync(id);
    }

    public async Task<Models.Patient?> GetByEmailAsync(string email, int? excludingId = null)
    {
        var normalized = email.ToLower();
        return await _db.Patients.FirstOrDefaultAsync(p =>
            p.Email.ToLower() == normalized &&
            (excludingId == null || p.Id != excludingId));
    }

    public async Task<Models.Patient> AddAsync(Models.Patient patient)
    {
        _db.Patients.Add(patient);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("IX_Patients_Email_Unique") == true)
        {
            throw new DuplicateEmailException(patient.Email);
        }
        return patient;
    }

    public async Task UpdateAsync(Models.Patient patient)
    {
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("IX_Patients_Email_Unique") == true)
        {
            throw new DuplicateEmailException(patient.Email);
        }
    }

    public async Task DeleteAsync(Models.Patient patient)
    {
        _db.Patients.Remove(patient);
        await _db.SaveChangesAsync();
    }
}
