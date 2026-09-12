namespace MessageFlow.Domain.Exceptions;

public sealed class InvalidFlowTransitionException(Enums.FlowStatus from, Enums.FlowStatus to)
    : DomainException($"Transición de estado no permitida para el Flow: de '{from}' a '{to}'.")
{
    public Enums.FlowStatus From { get; } = from;

    public Enums.FlowStatus To { get; } = to;
}

public sealed class FlowNotExecutableException(string flowName, Enums.FlowStatus status)
    : DomainException($"El Flow '{flowName}' no puede ejecutarse porque su estado es '{status}'. Solo los Flows en estado 'Active' pueden ejecutarse.")
{
    public string FlowName { get; } = flowName;

    public Enums.FlowStatus Status { get; } = status;
}

public sealed class FlowNotModifiableException(string flowName, Enums.FlowStatus status)
    : DomainException($"El Flow '{flowName}' no puede modificarse porque su estado es '{status}'.")
{
    public string FlowName { get; } = flowName;

    public Enums.FlowStatus Status { get; } = status;
}
