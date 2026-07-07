using Microsoft.EntityFrameworkCore;
using PatientFlow.Patient.Data;
using PatientFlow.Patient.Models;
using Pgvector;
using Pgvector.EntityFrameworkCore;

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
    /// Return the ids of patients that do NOT yet have an embedding row.
    /// Used by AI service startup backfill: patients imported before
    /// pgvector was introduced (or before the AI service was up) have
    /// no vector and are therefore invisible to semantic search until
    /// this list is drained.
    /// Bounded by <paramref name="limit"/> so a huge legacy corpus is
    /// processed across successive restarts rather than in one giant batch.
    /// </summary>
    public async Task<List<int>> GetPatientIdsMissingEmbeddingAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return new List<int>();
        }

        // LEFT JOIN Patients -> PatientEmbeddings, filter to those with no
        // matching embedding row. Ordered by Id so multiple runs process the
        // same subset deterministically.
        return await _db.Patients
            .AsNoTracking()
            .Where(p => !_db.PatientEmbeddings.Any(e => e.PatientId == p.Id))
            .OrderBy(p => p.Id)
            .Select(p => p.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

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

    /// <summary>
    /// Cosine-distance nearest-neighbour search against PatientEmbeddings.
    /// Returns the top-K patient ids ordered by distance ascending
    /// (closest match first). The distance is included so the caller can
    /// log / debug relevance and, if desired, drop matches beyond a
    /// similarity threshold.
    ///
    /// Cosine distance = 1 - cosine similarity, so 0.0 is identical and
    /// 1.0 is orthogonal. In practice we see 0.3-0.6 for solid matches
    /// on the nomic-embed-text model.
    ///
    /// Uses the &lt;=&gt; operator via Pgvector.EntityFrameworkCore's
    /// EF.Functions.CosineDistance so the ORDER BY translates to a pure
    /// SQL sort - no rows are pulled into memory before ranking.
    /// </summary>
    public async Task<List<(int PatientId, double Distance)>> SearchNearestAsync(
        float[] queryVector,
        int topK,
        CancellationToken cancellationToken = default)
    {
        if (queryVector.Length == 0 || topK <= 0)
        {
            return new List<(int, double)>();
        }

        var query = new Vector(queryVector);

        var raw = await _db.PatientEmbeddings
            .AsNoTracking()
            .OrderBy(e => e.Embedding.CosineDistance(query))
            .Take(topK)
            .Select(e => new
            {
                e.PatientId,
                Distance = e.Embedding.CosineDistance(query)
            })
            .ToListAsync(cancellationToken);

        return raw.Select(r => (r.PatientId, r.Distance)).ToList();
    }
}
