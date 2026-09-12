using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Filters;
using MessageFlow.Domain.Flows;
using MessageFlow.Domain.Messaging;
using MessageFlow.Domain.Scheduling;
using MessageFlow.Infrastructure.Persistence.Configurations;
using MessageFlow.Infrastructure.Persistence.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MessageFlow.Infrastructure.Persistence.Configurations;

internal sealed class FlowConfiguration : IEntityTypeConfiguration<Flow>
{
    public void Configure(EntityTypeBuilder<Flow> builder)
    {
        builder.ToTable("flows");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(f => f.Name).HasColumnName("name").HasMaxLength(Flow.NameMaxLength).IsRequired();
        builder.Property(f => f.Description).HasColumnName("description").HasMaxLength(Flow.DescriptionMaxLength);
        builder.Property(f => f.Status).HasColumnName("status");
        builder.Property(f => f.Channel).HasColumnName("channel");
        builder.Property(f => f.OwnerId).HasColumnName("owner_id").HasMaxLength(128).IsRequired();
        builder.Property(f => f.DatasetId).HasColumnName("dataset_id");
        builder.Property(f => f.RecipientColumnId).HasColumnName("recipient_column_id");
        builder.Property(f => f.MessageTemplateText).HasColumnName("message_template_text").HasMaxLength(MessageTemplate.MaxLength).IsRequired();

        builder.Property(f => f.Schedule)
            .HasConversion(JsonbConverter<FlowSchedule>.Instance)
            .HasColumnType("jsonb")
            .HasColumnName("schedule")
            .IsRequired();

        // Propiedades opcionales: EF nunca invoca el convertidor con null; la anotación difiere solo a nivel de tipos.
#pragma warning disable CS8620
        builder.Property(f => f.RootFilter)
            .HasConversion(JsonbConverter<FilterGroup>.Instance)
            .HasColumnType("jsonb")
            .HasColumnName("root_filter");

        builder.Property(f => f.DataTrigger)
            .HasConversion(JsonbConverter<DataTriggerConfig>.Instance)
            .HasColumnType("jsonb")
            .HasColumnName("data_trigger");
#pragma warning restore CS8620

        DatasetConfiguration.ConfigureAudit(builder);

        // Concurrencia optimista con xmin (decisión aprobada).
        builder.Property<uint>("xmin").IsRowVersion();

        builder.HasOne<Dataset>()
            .WithMany()
            .HasForeignKey(f => f.DatasetId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<DatasetColumn>()
            .WithMany()
            .HasForeignKey(f => f.RecipientColumnId)
            .OnDelete(DeleteBehavior.SetNull);

        // Índices para listados y scheduling (§26). La próxima ejecución se calcula
        // desde Schedule (jsonb); una columna derivada NextRunAtUtc llegará en la fase de scheduling.
        builder.HasIndex(f => f.Status).HasDatabaseName("ix_flows_status");
        builder.HasIndex(f => f.DatasetId).HasDatabaseName("ix_flows_dataset_id");
        builder.HasIndex(f => f.OwnerId).HasDatabaseName("ix_flows_owner_id");
    }
}
