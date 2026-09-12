namespace MessageFlow.Domain.Enums;

public enum ExecutionStatus
{
    Pending = 1,
    Running = 2,
    Completed = 3,
    PartiallyCompleted = 4,
    Failed = 5,
    Cancelled = 6,
}
