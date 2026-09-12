using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Flows;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MessageFlow.Infrastructure.Persistence.Configurations;

internal sealed class FlowExecutionConfiguration : IEntityTypeConfiguration<FlowExecution>
{
    public void Configure(EntityTypeBuilder<FlowExecution> builder)
    {
        builder.ToTable("flow_executions");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(e => e.FlowId).HasColumnName("flow_id");
        builder.Property(e => e.ExecutionType).HasColumnName("execution_type");
        builder.Property(e => e.TriggerSource).HasColumnName("trigger_source");

        // Idempotencia (decisión aprobada): clave única global.
        builder.Property(e => e.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .HasMaxLength(FlowExecution.IdempotencyKeyMaxLength)
            .IsRequired();

        builder.HasIndex(e => e.IdempotencyKey).IsUnique().HasDatabaseName("ux_flow_executions_idempotency_key");

        builder.Property(e => e.Status).HasColumnName("status");
        builder.Property(e => e.StartedAtUtc).HasColumnName("started_at_utc").IsRequired();
        builder.Property(e => e.CompletedAtUtc).HasColumnName("completed_at_utc");
        builder.Property(e => e.ScheduledForUtc).HasColumnName("scheduled_for_utc");
        builder.Property(e => e.TotalRecords).HasColumnName("total_records");
        builder.Property(e => e.ProcessedRecords).HasColumnName("processed_records");
        builder.Property(e => e.SuccessfulRecords).HasColumnName("successful_records");
        builder.Property(e => e.FailedRecords).HasColumnName("failed_records");
        builder.Property(e => e.ErrorMessage).HasColumnName("error_message").HasMaxLength(FlowExecution.ErrorMessageMaxLength);

        DatasetConfiguration.ConfigureAudit(builder);

        builder.HasOne<Flow>()
            .WithMany()
            .HasForeignKey(e => e.FlowId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Navigation(e => e.Items).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(e => e.Items)
            .WithOne()
            .HasForeignKey(i => i.FlowExecutionId)
            .OnDelete(DeleteBehavior.Cascade);

        // Consultas de scheduler y listado por Flow.
        builder.HasIndex(e => new { e.FlowId, e.Status }).HasDatabaseName("ix_flow_executions_flow_status");
        builder.HasIndex(e => new { e.FlowId, e.StartedAtUtc }).HasDatabaseName("ix_flow_executions_flow_started_at_utc");
        builder.HasIndex(e => new { e.Status, e.StartedAtUtc }).HasDatabaseName("ix_flow_executions_status_started_at_utc");

        // Ancla del productor: última ocurrencia programada por Flow (MAX).
        builder.HasIndex(e => new { e.FlowId, e.TriggerSource, e.ScheduledForUtc })
            .HasDatabaseName("ix_flow_executions_flow_trigger_scheduled_for");
    }
}

internal sealed class FlowExecutionItemConfiguration : IEntityTypeConfiguration<FlowExecutionItem>
{
    public void Configure(EntityTypeBuilder<FlowExecutionItem> builder)
    {
        builder.ToTable("flow_execution_items");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(i => i.FlowExecutionId).HasColumnName("flow_execution_id");
        builder.Property(i => i.DatasetRowId).HasColumnName("dataset_row_id");
        builder.Property(i => i.Status).HasColumnName("status");
        builder.Property(i => i.ErrorMessage).HasColumnName("error_message").HasMaxLength(FlowExecutionItem.ErrorMessageMaxLength);
        builder.Property(i => i.ExecutedAtUtc).HasColumnName("executed_at_utc").IsRequired();

        builder.HasOne<DatasetRow>()
            .WithMany()
            .HasForeignKey(i => i.DatasetRowId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(i => new { i.FlowExecutionId, i.DatasetRowId })
            .IsUnique()
            .HasDatabaseName("ux_flow_execution_items_execution_row");
        builder.HasIndex(i => i.DatasetRowId).HasDatabaseName("ix_flow_execution_items_dataset_row_id");
    }
}
