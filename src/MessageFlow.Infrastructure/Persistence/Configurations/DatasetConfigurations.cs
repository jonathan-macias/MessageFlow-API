using MessageFlow.Domain.Datasets;
using MessageFlow.Infrastructure.Persistence.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MessageFlow.Infrastructure.Persistence.Configurations;

internal sealed class DatasetConfiguration : IEntityTypeConfiguration<Dataset>
{
    public void Configure(EntityTypeBuilder<Dataset> builder)
    {
        builder.ToTable("datasets");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(d => d.Name).HasColumnName("name").HasMaxLength(Dataset.NameMaxLength).IsRequired();
        builder.Property(d => d.SourceFileName).HasColumnName("source_file_name").HasMaxLength(260).IsRequired();
        builder.Property(d => d.OwnerId).HasColumnName("owner_id").HasMaxLength(128).IsRequired();

        // Columna telefónica seleccionada por el usuario: obligatoria para todo dataset.
        builder.Property(d => d.PhoneColumn)
            .HasColumnName("phone_column")
            .HasMaxLength(DatasetColumn.NameMaxLength)
            .IsRequired();

        builder.Property(d => d.StoragePath).HasColumnName("storage_path").HasMaxLength(1024);
        builder.Property(d => d.RowCount).HasColumnName("row_count");

        ConfigureAudit(builder);

        builder.HasMany(d => d.Columns)
            .WithOne()
            .HasForeignKey(c => c.DatasetId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(d => d.Columns).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(d => d.Name).HasDatabaseName("ix_datasets_name");
        builder.HasIndex(d => new { d.CreatedAtUtc }).HasDatabaseName("ix_datasets_created_at_utc");
        builder.HasIndex(d => d.OwnerId).HasDatabaseName("ix_datasets_owner_id");
    }

    internal static void ConfigureAudit<TEntity>(EntityTypeBuilder<TEntity> builder)
        where TEntity : class, Domain.Common.IAuditable
    {
        builder.Property(e => e.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(e => e.UpdatedAtUtc).HasColumnName("updated_at_utc");
        builder.Property(e => e.CreatedBy).HasColumnName("created_by").HasMaxLength(100).IsRequired().HasDefaultValue("system");
        builder.Property(e => e.UpdatedBy).HasColumnName("updated_by").HasMaxLength(100);
    }
}

internal sealed class DatasetColumnConfiguration : IEntityTypeConfiguration<DatasetColumn>
{
    public void Configure(EntityTypeBuilder<DatasetColumn> builder)
    {
        builder.ToTable("dataset_columns");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(c => c.DatasetId).HasColumnName("dataset_id");
        builder.Property(c => c.Name).HasColumnName("name").HasMaxLength(DatasetColumn.NameMaxLength).IsRequired();
        builder.Property(c => c.DataType).HasColumnName("data_type");
        builder.Property(c => c.Ordinal).HasColumnName("ordinal");

        // Unicidad por dataset: nombre exacto (el dominio ya normaliza y compara case-insensitive en proceso).
        builder.HasIndex(c => new { c.DatasetId, c.Name }).IsUnique().HasDatabaseName("ux_dataset_columns_dataset_name");
        builder.HasIndex(c => new { c.DatasetId, c.Ordinal }).IsUnique().HasDatabaseName("ux_dataset_columns_dataset_ordinal");
    }
}

internal sealed class DatasetRowConfiguration : IEntityTypeConfiguration<DatasetRow>
{
    public void Configure(EntityTypeBuilder<DatasetRow> builder)
    {
        builder.ToTable("dataset_rows");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(r => r.DatasetId).HasColumnName("dataset_id");
        builder.Property(r => r.RowNumber).HasColumnName("row_number");

        // Valores dinámicos como jsonb (decisión aprobada). Clave = nombre de columna normalizado.
        builder.Property(r => r.Values)
            .HasField("_values")
            .HasConversion(JsonbConverter<IReadOnlyDictionary<string, string?>>.Instance)
            .HasColumnType("jsonb")
            .IsRequired();

        builder.HasOne<Dataset>()
            .WithMany()
            .HasForeignKey(r => r.DatasetId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(r => new { r.DatasetId, r.RowNumber }).IsUnique().HasDatabaseName("ux_dataset_rows_dataset_row_number");
    }
}
