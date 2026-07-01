using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Pgvector;

namespace PatientFlow.Patient.Models;

/// <summary>
/// Stores a semantic vector representation of a patient record so we can do
/// similarity search ("find patients whose profile most resembles this text")
/// instead of dumping every row into an LLM prompt.
///
/// One row per patient. Regenerated whenever the patient's searchable fields
/// change (create / update). Deleted when the patient is deleted (FK cascade).
///
/// The Embedding column uses pgvector's `vector(N)` type. Dimension is 768,
/// matching Ollama's `nomic-embed-text` model. If we ever switch embedding
/// models, we'll need a migration to change the column dimension because
/// pgvector enforces fixed-size vectors per column.
/// </summary>
public class PatientEmbedding
{
    /// <summary>Same value as the Patient.Id it belongs to (1:1 relationship).</summary>
    [Key]
    public int PatientId { get; set; }

    /// <summary>
    /// The exact text that was fed to the embedding model. Kept for debugging
    /// ("why did this patient match?") and to detect when a re-embed is needed
    /// (if the source text hasn't changed, skip the LLM call).
    /// </summary>
    [Required]
    public string SourceText { get; set; } = string.Empty;

    /// <summary>
    /// The 768-dimensional embedding vector produced by the embedding model.
    /// Column type in Postgres is `vector(768)` (pgvector extension).
    /// </summary>
    [Required]
    [Column(TypeName = "vector(768)")]
    public Vector Embedding { get; set; } = null!;

    /// <summary>Name of the embedding model that produced this vector
    /// (e.g. "nomic-embed-text"). Lets us detect stale embeddings if we
    /// swap models later without dropping the whole table.</summary>
    [Required]
    [MaxLength(100)]
    public string Model { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Navigation property back to the owning Patient row.</summary>
    [ForeignKey(nameof(PatientId))]
    public Patient? Patient { get; set; }
}
