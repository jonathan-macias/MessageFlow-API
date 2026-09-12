using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Exceptions;
using MessageFlow.Domain.Flows;

namespace MessageFlow.Tests;

public class FlowExecutionTests
{
    private static FlowExecution NewPendingExecution(long totalRecords = 0)
        => FlowExecution.Start(
            Guid.CreateVersion7(),
            ExecutionType.Immediate,
            TriggerSource.Manual,
            $"manual:{Guid.NewGuid():N}",
            DateTimeOffset.UtcNow,
            totalRecords);

    [Fact]
    public void Fail_desde_pending_o_running_marca_failed_y_guarda_error()
    {
        var execution = NewPendingExecution();
        var at = DateTimeOffset.UtcNow;

        execution.Fail(at, "No hay proveedor para el canal.");

        Assert.Equal(ExecutionStatus.Failed, execution.Status);
        Assert.Equal(at, execution.CompletedAtUtc);
        Assert.Equal("No hay proveedor para el canal.", execution.ErrorMessage);
    }

    [Fact]
    public void Fail_trunca_el_mensaje_al_maximo_permitido()
    {
        var execution = NewPendingExecution();
        execution.Begin(DateTimeOffset.UtcNow);

        var longMessage = new string('x', FlowExecution.ErrorMessageMaxLength + 50);

        execution.Fail(DateTimeOffset.UtcNow, longMessage);

        Assert.Equal(FlowExecution.ErrorMessageMaxLength, execution.ErrorMessage!.Length);
    }

    [Fact]
    public void Fail_con_mensaje_vacio_es_rechazado()
    {
        var execution = NewPendingExecution();

        Assert.Throws<DomainException>(() => execution.Fail(DateTimeOffset.UtcNow, "   "));
    }

    [Theory]
    [InlineData(ExecutionStatus.Completed)]
    [InlineData(ExecutionStatus.Cancelled)]
    [InlineData(ExecutionStatus.Failed)]
    public void Fail_tras_cerrar_lanza_transicion_invalida(ExecutionStatus closedStatus)
    {
        var execution = NewPendingExecution();
        execution.Begin(DateTimeOffset.UtcNow);

        switch (closedStatus)
        {
            case ExecutionStatus.Completed:
                execution.Complete(DateTimeOffset.UtcNow);
                break;
            case ExecutionStatus.Cancelled:
                execution.Cancel(DateTimeOffset.UtcNow, "prueba");
                break;
            default:
                execution.Fail(DateTimeOffset.UtcNow, "primer fallo");
                break;
        }

        Assert.Throws<InvalidExecutionTransitionException>(
            () => execution.Fail(DateTimeOffset.UtcNow, "segundo cierre"));
    }
}
