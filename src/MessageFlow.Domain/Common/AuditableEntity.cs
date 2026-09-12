namespace MessageFlow.Domain.Common;

public interface IAuditable
{
    DateTimeOffset CreatedAtUtc { get; }
    DateTimeOffset? UpdatedAtUtc { get; }
    string CreatedBy { get; }
    string? UpdatedBy { get; }
}

public abstract class AuditableEntity : Entity, IAuditable
{
    // Los valores se asignan en Infrastructure (SaveChangesInterceptor) usando ICurrentUser.
    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? UpdatedAtUtc { get; set; }

    public string CreatedBy { get; set; } = "system";

    public string? UpdatedBy { get; set; }
}
