using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PMS.Patient.Migrations
{
    /// <summary>
    /// Adds a pgvector HNSW index on <c>PatientEmbeddings.Embedding</c> using the
    /// cosine-distance operator class. Without this index, the ORDER BY
    /// <c>Embedding &lt;=&gt; @query</c> in <c>SearchNearestAsync</c> runs a
    /// sequential scan and re-computes cosine distance for every row - O(N)
    /// per query, which becomes the bottleneck once the corpus exceeds a
    /// few thousand rows.
    ///
    /// HNSW (Hierarchical Navigable Small World) builds a multi-layer graph
    /// of nearest neighbours and answers top-K queries in O(log N) with
    /// ~99% recall at these defaults. Chose HNSW over IVFFlat because:
    ///   - No training step required (IVFFlat needs representative rows for
    ///     centroids), so the index works on an empty or newly-seeded table.
    ///   - Higher recall at default settings.
    ///   - Better fit for a growing corpus where insert cost is not a concern.
    ///
    /// Parameters:
    ///   m = 16               - max connections per node (pgvector default)
    ///   ef_construction = 64 - candidate list size while building (pgvector default)
    ///
    /// Query-time recall is tunable per session via <c>SET hnsw.ef_search = N</c>;
    /// we keep the built-in default (40) for now.
    ///
    /// EF Core does not model pgvector index types natively, so the DDL is
    /// emitted as raw SQL. The model snapshot therefore does not reference
    /// this index - only <c>__EFMigrationsHistory</c> tracks its existence.
    /// </summary>
    public partial class AddHnswCosineIndexOnPatientEmbeddings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS ""IX_PatientEmbeddings_Embedding_HnswCosine""
                ON ""PatientEmbeddings""
                USING hnsw (""Embedding"" vector_cosine_ops)
                WITH (m = 16, ef_construction = 64);
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS ""IX_PatientEmbeddings_Embedding_HnswCosine"";
            ");
        }
    }
}

