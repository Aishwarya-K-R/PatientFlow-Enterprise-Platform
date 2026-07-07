using Microsoft.EntityFrameworkCore;
using PatientFlow.Patient.Data;
using PatientFlow.Patient.Models;
using Pgvector;

namespace PatientFlow.Patient.Services;

/// <summary>
/// Owns the write side of the pgvector-backed PatientEmbeddings table.
/// Called only by internal service-to-service traffic (currently the AI
/// service after it generates an embedding via Ollama). Not exposed to
/// end users - the API surface is protected by InternalApiKeyMiddleware
/// plus [Authorize(Roles = "ADMIN")] on the controller.
/// </summary>
public class PatientEmbeddingService(
    PatientDbContext db,
    ILogger<PatientEmbeddingService> logger)
{
    private readonly PatientDbContext _db = db;
    private readonly ILogger<PatientEmbeddingService> _logger = logger;

    /// <summary>
    /// Insert-or-update the embedding row for a patient. If the patient does
    /// not exist we skip - trying to insert a child row with no parent would
    /// hit the FK constraint anyway. Returns true if a row was written.
    /// </summary>
    public async Task<bool> UpsertAsync(
        int patientId,
        string sourceText,
        float[] vector,
        string model,
        CancellationToken cancellationToken = default)
    {
        var patientExists = await _db.Patients
            .AsNoTracking()
            .AnyAsync(p => p.Id == patientId, cancellationToken);

        if (!patientExists)
        {
            _logger.LogWarning("Skipping embedding upsert; patient {PatientId} not found", patientId);
            return false;
        }

        var existing = await _db.PatientEmbeddings
            .FirstOrDefaultAsync(e => e.PatientId == patientId, cancellationToken);

        var pgVector = new Vector(vector);
        var now = DateTime.UtcNow;

        if (existing == null)
        {
            _db.PatientEmbeddings.Add(new PatientEmbedding
            {
                PatientId = patientId,
                SourceText = sourceText,
                Embedding = pgVector,
                Model = model,
                CreatedAt = now,
                UpdatedAt = now
            });
            _logger.LogInformation("Inserted embedding for patient {PatientId} ({Dims} dims)",
                patientId, vector.Length);
        }
        else
        {
            existing.SourceText = sourceText;
            existing.Embedding = pgVector;
            existing.Model = model;
            existing.UpdatedAt = now;
            _logger.LogInformation("Updated embedding for patient {PatientId} ({Dims} dims)",
                patientId, vector.Length);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Delete the embedding row for a patient. Returns true if a row was removed,
    /// false if none existed (caller can treat that as an idempotent 200/404).
    /// Note: the FK cascade already handles this when the Patient row is deleted;
    /// this method exists so callers can force cleanup independent of the parent.
    /// </summary>
    public async Task<bool> DeleteAsync(int patientId, CancellationToken cancellationToken = default)
    {
        var existing = await _db.PatientEmbeddings
            .FirstOrDefaultAsync(e => e.PatientId == patientId, cancellationToken);

        if (existing == null)
        {
            return false;
        }

        _db.PatientEmbeddings.Remove(existing);
        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Deleted embedding for patient {PatientId}", patientId);
        return true;
    }
}
