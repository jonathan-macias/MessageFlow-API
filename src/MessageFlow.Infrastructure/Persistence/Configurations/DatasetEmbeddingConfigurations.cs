using MessageFlow.Domain.Datasets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MessageFlow.Infrastructure.Persistence.Configurations;

internal sealed class DatasetRowEmbeddingConfiguration : IEntityTypeConfiguration<DatasetRowEmbedding>
{
    public void Configure(EntityTypeBuilder<DatasetRowEmbedding> builder)
    {
        builder.ToTable("dataset_row_embeddings");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(e => e.DatasetId).HasColumnName("dataset_id");
        builder.Property(e => e.DatasetRowId).HasColumnName("dataset_row_id");
        builder.Property(e => e.RowNumber).HasColumnName("row_number");
        builder.Property(e => e.Content).HasColumnName("content").IsRequired();

        // El dominio guarda el vector como float[] para no depender de Pgvector; la
        // conversión a Pgvector.Vector ocurre solo en Infrastructure. El comparador es
        // explícito porque los arrays se comparan por referencia y EF no puede detectar
        // cambios en el contenido.
        var embedding = builder.Property(e => e.Embedding)
            .HasColumnName("embedding")
            .HasColumnType($"vector({DatasetRowEmbedding.VectorDimensions})")
            .HasConversion(
                vector => new Pgvector.Vector(vector),
                vector => vector.ToArray())
            .IsRequired();

        // Los arrays se comparan por referencia, así que sin esto EF no detectaría
        // cambios en el contenido del vector.
        embedding.Metadata.SetValueComparer(new ValueComparer<float[]>(
            (left, right) => ReferenceEquals(left, right),
            vector => 0,
            vector => vector));

        builder.HasOne<Dataset>()
            .WithMany()
            .HasForeignKey(e => e.DatasetId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<DatasetRow>()
            .WithMany()
            .HasForeignKey(e => e.DatasetRowId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.DatasetId, e.RowNumber })
            .IsUnique()
            .HasDatabaseName("ux_dataset_row_embeddings_dataset_row_number");

        // Índice b-tree por dataset: toda búsqueda arranca acotada a un dataset, así que
        // este es el índice que realmente se usa para descartar el resto.
        builder.HasIndex(e => e.DatasetId).HasDatabaseName("ix_dataset_row_embeddings_dataset_id");
    }
}

internal sealed class DatasetEmbeddingStateConfiguration : IEntityTypeConfiguration<DatasetEmbeddingState>
{
    public void Configure(EntityTypeBuilder<DatasetEmbeddingState> builder)
    {
        builder.ToTable("dataset_embedding_states");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(s => s.DatasetId).HasColumnName("dataset_id");
        builder.Property(s => s.Status).HasColumnName("status");
        builder.Property(s => s.TotalRows).HasColumnName("total_rows");
        builder.Property(s => s.EmbeddedRows).HasColumnName("embedded_rows");
        builder.Property(s => s.LastEmbeddedRowNumber).HasColumnName("last_embedded_row_number");
        builder.Property(s => s.Dimensions).HasColumnName("dimensions");
        builder.Property(s => s.LastError)
            .HasColumnName("last_error")
            .HasMaxLength(DatasetEmbeddingState.LastErrorMaxLength);

        builder.HasOne<Dataset>()
            .WithMany()
            .HasForeignKey(s => s.DatasetId)
            .OnDelete(DeleteBehavior.Cascade);

        // Un estado por dataset: el worker lo usa como cola de indexado.
        builder.HasIndex(s => s.DatasetId).IsUnique().HasDatabaseName("ux_dataset_embedding_states_dataset_id");
        builder.HasIndex(s => s.Status).HasDatabaseName("ix_dataset_embedding_states_status");
    }
}
