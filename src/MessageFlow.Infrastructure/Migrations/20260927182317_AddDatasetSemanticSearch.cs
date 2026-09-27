using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace MessageFlow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDatasetSemanticSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "dataset_embedding_states",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    dataset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    total_rows = table.Column<long>(type: "bigint", nullable: false),
                    embedded_rows = table.Column<long>(type: "bigint", nullable: false),
                    last_embedded_row_number = table.Column<long>(type: "bigint", nullable: false),
                    dimensions = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dataset_embedding_states", x => x.id);
                    table.ForeignKey(
                        name: "FK_dataset_embedding_states_datasets_dataset_id",
                        column: x => x.dataset_id,
                        principalTable: "datasets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "dataset_row_embeddings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    dataset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    dataset_row_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_number = table.Column<long>(type: "bigint", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    embedding = table.Column<Vector>(type: "vector(768)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dataset_row_embeddings", x => x.id);
                    table.ForeignKey(
                        name: "FK_dataset_row_embeddings_dataset_rows_dataset_row_id",
                        column: x => x.dataset_row_id,
                        principalTable: "dataset_rows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_dataset_row_embeddings_datasets_dataset_id",
                        column: x => x.dataset_id,
                        principalTable: "datasets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_dataset_embedding_states_status",
                table: "dataset_embedding_states",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ux_dataset_embedding_states_dataset_id",
                table: "dataset_embedding_states",
                column: "dataset_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_dataset_row_embeddings_dataset_id",
                table: "dataset_row_embeddings",
                column: "dataset_id");

            migrationBuilder.CreateIndex(
                name: "IX_dataset_row_embeddings_dataset_row_id",
                table: "dataset_row_embeddings",
                column: "dataset_row_id");

            migrationBuilder.CreateIndex(
                name: "ux_dataset_row_embeddings_dataset_row_number",
                table: "dataset_row_embeddings",
                columns: new[] { "dataset_id", "row_number" },
                unique: true);

            // La búsqueda vectorial se traduce al operador '<=>' con un ORDER BY sobre la
            // expresión escalar, que es la única forma que PostgreSQL puede resolver con un
            // índice ANN. Con b-tree la consulta ordenaría las 768 dimensiones de todas las
            // filas del dataset en memoria: mismo resultado, coste O(n log n) por consulta.
            //
            // vector_cosine_ops porque la distancia del ORDER BY es coseno, no L2. Usar el
            // opclass equivocado no da error: el índice simplemente no se usa.
            //
            // El índice es global, no por dataset. HNSW no admite una columna vectorial
            // precedida por dataset_id, así que el filtro por dataset se resuelve después;
            // por eso la conexión debe llevar 'hnsw.iterative_scan = strict_order' para que
            // PostgreSQL siga bajando por el grafo hasta reunir TopK filas del dataset pedido
            // en vez de devolver menos resultados.
            migrationBuilder.Sql(
                """
                CREATE INDEX ix_dataset_row_embeddings_embedding_hnsw
                    ON dataset_row_embeddings
                    USING hnsw (embedding vector_cosine_ops)
                    WITH (m = 16, ef_construction = 64);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_dataset_row_embeddings_embedding_hnsw;");

            migrationBuilder.DropTable(
                name: "dataset_embedding_states");

            migrationBuilder.DropTable(
                name: "dataset_row_embeddings");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");
        }
    }
}
