namespace MessageFlow.Domain.Exceptions;

public sealed class InvalidFlowTransitionException(Enums.FlowStatus from, Enums.FlowStatus to)
    : DomainException($"Flow status transition not allowed: from '{from}' to '{to}'.")
{
    public Enums.FlowStatus From { get; } = from;

    public Enums.FlowStatus To { get; } = to;
}

public sealed class FlowNotExecutableException(string flowName, Enums.FlowStatus status)
    : DomainException($"The Flow '{flowName}' cannot be executed because its status is '{status}'. Only Flows with status 'Active' can be executed.")
{
    public string FlowName { get; } = flowName;

    public Enums.FlowStatus Status { get; } = status;
}

public sealed class FlowNotModifiableException(string flowName, Enums.FlowStatus status)
    : DomainException($"The Flow '{flowName}' cannot be modified because its status is '{status}'.")
{
    public string FlowName { get; } = flowName;

    public Enums.FlowStatus Status { get; } = status;
}
