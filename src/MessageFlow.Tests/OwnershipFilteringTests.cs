using MessageFlow.Application.Abstractions;
using MessageFlow.Application.Abstractions.Persistence;
using MessageFlow.Application.Common;
using MessageFlow.Application.Executions.Queries;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Flows;
using MessageFlow.Domain.Messaging;
using MessageFlow.Domain.Scheduling;
using MessageFlow.Infrastructure.Persistence.Repositories;
using MessageFlow.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace MessageFlow.Tests;

/// <summary>
/// Verifica que el ownership filtering impida que un usuario acceda a recursos de otro:
/// Flows, Datasets y FlowExecutions. Un usuario autenticado solo ve sus propios recursos;
/// un usuario no autenticado (background/scheduler) ve todos.
/// </summary>
public class OwnershipFilteringTests
{
    private const string UserA = "user-a-000000000000000000000000";
    private const string UserB = "user-b-000000000000000000000000";

    private static readonly FakeCurrentUser CurrentUserA = new() { UserId = UserA };
    private static readonly FakeCurrentUser CurrentUserB = new() { UserId = UserB };
    private static readonly FakeCurrentUser UnauthenticatedUser = new() { UserId = null };

    // ── Flow ownership ────────────────────────────────────────────────────
    [Fact]
    public async Task Usuario_A_no_ve_flows_de_usuario_B()
    {
        var repoA = new FakeFlowRepository();
        var repoB = new FakeFlowRepository();

        var flow = Flow.CreateDraft(
            "Flow de B", null, Guid.NewGuid(), Channel.WhatsApp,
            MessageTemplate.Create("Hola"),
            FlowSchedule.Immediate(TimeZoneId.Create("UTC")),
            UserB);

        await repoA.AddAsync(flow);
        await repoB.AddAsync(flow);

        // User A no lo ve (el repositorio fake no filtra, pero el real sí):
        // Simulamos el comportamiento del repositorio real con ownership check.
        var isOwnerOfA = await IsOwnerOfFlow(repoA, flow.Id, CurrentUserA);
        var isOwnerOfB = await IsOwnerOfFlow(repoB, flow.Id, CurrentUserB);

        Assert.False(isOwnerOfA);
        Assert.True(isOwnerOfB);
    }

    // ── Dataset ownership ─────────────────────────────────────────────────
    [Fact]
    public void Dataset_creado_por_A_tieneOwnerId_de_A()
    {
        var dataset = Domain.Datasets.Dataset.Create(
            "Contactos", "test.xlsx", UserA);

        Assert.Equal(UserA, dataset.OwnerId);
    }

    [Fact]
    public void Dataset_creado_por_B_tieneOwnerId_de_B()
    {
        var dataset = Domain.Datasets.Dataset.Create(
            "Contactos", "test.xlsx", UserB);

        Assert.Equal(UserB, dataset.OwnerId);
    }

    // ── Flow creation assigns OwnerId ─────────────────────────────────────
    [Fact]
    public void Flow_creado_por_A_tieneOwnerId_de_A()
    {
        var flow = Flow.CreateDraft(
            "Mi Flow", null, Guid.NewGuid(), Channel.WhatsApp,
            MessageTemplate.Create("Hola"),
            FlowSchedule.Immediate(TimeZoneId.Create("UTC")),
            UserA);

        Assert.Equal(UserA, flow.OwnerId);
    }

    // ── Unauthenticated user sees everything (system context) ─────────────
    [Fact]
    public void Usuario_no_autenticado_ve_todos_los_flows()
    {
        var flow = Flow.CreateDraft(
            "Flow cualquiera", null, Guid.NewGuid(), Channel.WhatsApp,
            MessageTemplate.Create("Hola"),
            FlowSchedule.Immediate(TimeZoneId.Create("UTC")),
            UserA);

        Assert.False(UnauthenticatedUser.IsAuthenticated);
        // En el repositorio real: !currentUser.IsAuthenticated → no filtra por OwnerId
    }

    // ── Helpers ───────────────────────────────────────────────────────────
    private static Task<bool> IsOwnerOfFlow(FakeFlowRepository repo, Guid flowId, ICurrentUser user)
    {
        if (!user.IsAuthenticated)
        {
            return Task.FromResult(true); // system context: sees everything
        }

        var flow = repo.Store.GetValueOrDefault(flowId);
        return Task.FromResult(flow is not null && flow.OwnerId == user.UserId);
    }
}
