using MessageFlow.Application.Abstractions;
using MessageFlow.Application.Abstractions.Persistence;
using MessageFlow.Application.Auth;
using MessageFlow.Domain.Datasets;
using MessageFlow.Domain.Enums;
using MessageFlow.Domain.Flows;
using MessageFlow.Domain.Messaging;
using System.Runtime.CompilerServices;
using GroundedFilter = MessageFlow.Domain.AI.GroundedFilter;
using GroundedRow = MessageFlow.Domain.AI.GroundedRow;

namespace MessageFlow.Tests.TestSupport;

/// <summary>ICurrentUser de prueba con UserId configurable.</summary>
public sealed class FakeCurrentUser : ICurrentUser
{
    public string? UserId { get; set; } = "00000000-0000-0000-0000-000000000001";
    public string? Email => UserId is null ? null : "test@example.com";
    public bool IsAuthenticated => UserId is not null;
}

/// <summary>
/// Dobles de prueba compartidos por las suites del motor y del scheduler:
/// repositorios en memoria con la semántica observable de las implementaciones EF
/// (claim atómico simulado, keyset paging, cola de pendientes).
/// </summary>
public sealed class FakeUnitOfWork : IUnitOfWork
{
    public int SaveCount { get; private set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveCount++;
        return Task.FromResult(1);
    }

    public void ClearTrackedEntities()
    {
    }
}

public sealed class FakeFlowRepository : IFlowRepository
{
    public Dictionary<Guid, Flow> Store { get; } = [];

    public Task<Flow?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(Store.GetValueOrDefault(id));

    public Task AddAsync(Flow flow, CancellationToken cancellationToken = default)
    {
        Store[flow.Id] = flow;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Flow flow, CancellationToken cancellationToken = default)
    {
        Store.Remove(flow.Id);
        return Task.CompletedTask;
    }

    public Task<(IReadOnlyList<Flow> Items, int TotalCount)> ListAsync(int page, int pageSize, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    /// <summary>Misma semántica que el EF real: solo Flows Active, por creación ascendente.</summary>
    public Task<IReadOnlyList<Flow>> ListActiveAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Flow>>(
            [.. Store.Values.Where(f => f.Status == FlowStatus.Active).OrderBy(f => f.CreatedAtUtc)]);
}

public sealed class FakeDatasetRepository : IDatasetRepository
{
    private IReadOnlyList<ColumnDefinition> _columns = [];
    private List<DatasetRow> _rows = [];
    private readonly Dictionary<Guid, Dataset> _datasets = [];

    /// <summary>Datasets persistidos por el handler bajo prueba.</summary>
    public Dictionary<Guid, Dataset> Store => _datasets;

    /// <summary>Valor que devuelve GetPhoneColumnAsync (null = dataset sin configurar).</summary>
    public string? PhoneColumnValue { get; set; }

    public void Load(IReadOnlyList<ColumnDefinition> columns, IReadOnlyList<DatasetRow> rows)
        => (_columns, _rows) = (columns, [.. rows]);

    public void Load(Dataset dataset) => _datasets[dataset.Id] = dataset;

    public Task<Dataset?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(_datasets.GetValueOrDefault(id));

    public Task AddAsync(Dataset dataset, CancellationToken cancellationToken = default)
    {
        _datasets[dataset.Id] = dataset;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ColumnDefinition>> GetColumnDefinitionsAsync(Guid datasetId, CancellationToken cancellationToken = default)
        => Task.FromResult(_columns);

    public Task<string?> GetPhoneColumnAsync(Guid datasetId, CancellationToken cancellationToken = default)
        => Task.FromResult(PhoneColumnValue);

    public Task<IReadOnlyList<(Guid Id, string Name)>> ListSummariesAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<int> CountDatasetsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_datasets.Count);

    public Task<long> CountRowsAsync(Guid datasetId, CancellationToken cancellationToken = default)
        => Task.FromResult((long)_rows.Count);

    public Task<DatasetRow?> FindRowAsync(Guid datasetId, Guid rowId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<DatasetRow>> GetRowsByIdsAsync(
        Guid datasetId,
        IReadOnlyCollection<Guid> rowIds,
        CancellationToken cancellationToken = default)
    {
        var wanted = rowIds.ToHashSet();
        return Task.FromResult<IReadOnlyList<DatasetRow>>(
            _rows.Where(r => r.DatasetId == datasetId && wanted.Contains(r.Id)).ToList());
    }

    public Task AddRowsAsync(IEnumerable<DatasetRow> rows, CancellationToken cancellationToken = default)
    {
        _rows.AddRange(rows);
        return Task.CompletedTask;
    }

    public Task<(IReadOnlyList<DatasetRow> Items, long TotalCount)> ListRowsAsync(Guid datasetId, int page, int pageSize, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public async IAsyncEnumerable<DatasetRow> StreamRowsAsync(
        Guid datasetId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var row in _rows.Where(r => r.DatasetId == datasetId))
        {
            yield return row;
            await Task.Yield();
        }
    }

    public Task<IReadOnlyList<DatasetRow>> GetRowsAfterAsync(Guid datasetId, long afterRowNumber, int limit, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<DatasetRow>>(
            [.. _rows
                .Where(r => r.DatasetId == datasetId && r.RowNumber > afterRowNumber)
                .OrderBy(r => r.RowNumber)
                .Take(limit)]);
}

public sealed class FakeFlowExecutionRepository : IFlowExecutionRepository
{
    public Dictionary<Guid, FlowExecution> Store { get; } = [];

    /// <summary>Cuando está activo simula que otra instancia ganó el reclamo atómico.</summary>
    public bool BlockClaim { get; set; }

    public int BeginAttempts { get; private set; }

    public Task<FlowExecution?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(Store.GetValueOrDefault(id));

    public Task AddAsync(FlowExecution execution, CancellationToken cancellationToken = default)
    {
        Store[execution.Id] = execution;
        return Task.CompletedTask;
    }

    public Task<(IReadOnlyList<FlowExecution> Items, int TotalCount)> ListByFlowAsync(Guid flowId, int page, int pageSize, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<bool> ExistsByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken = default)
        => Task.FromResult(Store.Values.Any(e => e.IdempotencyKey == idempotencyKey));

    public Task<bool> TryBeginProcessingAsync(Guid flowExecutionId, DateTimeOffset startedAtUtc, CancellationToken cancellationToken = default)
    {
        BeginAttempts++;

        if (BlockClaim)
        {
            BlockClaim = false;
            return Task.FromResult(false);
        }

        var execution = Store.GetValueOrDefault(flowExecutionId);
        if (execution is null || execution.Status != ExecutionStatus.Pending)
        {
            return Task.FromResult(false);
        }

        typeof(FlowExecution)
            .GetProperty(nameof(FlowExecution.Status))!
            .SetValue(execution, ExecutionStatus.Running);

        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<Guid>> ListPendingIdsAsync(int maxCount, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Guid>>(
            [.. Store.Values
                .Where(e => e.Status == ExecutionStatus.Pending)
                .OrderBy(e => e.StartedAtUtc).ThenBy(e => e.Id)
                .Select(e => e.Id)
                .Take(maxCount)]);

    public Task<DateTimeOffset?> GetLatestScheduledOccurrenceUtcAsync(Guid flowId, CancellationToken cancellationToken = default)
    {
        DateTimeOffset? latest = Store.Values
            .Where(e => e.FlowId == flowId
                        && e.TriggerSource == TriggerSource.Scheduled
                        && e.ScheduledForUtc.HasValue)
            .Max(e => e.ScheduledForUtc);

        return Task.FromResult(latest);
    }
}

/// <summary>Proveedor determinista: registra envíos y puede fallar destinos configurados.</summary>
public sealed class StubMessageProvider(HashSet<string>? failingDestinations = null) : IMessageProvider
{
    public List<(Guid DatasetRowId, string Destination, string Body)> Sent { get; } = [];

    public Channel Channel => Channel.WhatsApp;

    public Task<DeliveryResult> SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Sent.Add((message.Recipient.DatasetRowId, message.Recipient.Destination, message.Body));

        return Task.FromResult(failingDestinations is not null && failingDestinations.Contains(message.Recipient.Destination)
            ? DeliveryResult.Failure($"El número {message.Recipient.Destination} no está registrado en el proveedor.")
            : DeliveryResult.Success("stub-1"));
    }
}

/// <summary>TimeProvider mutable para controlar "ahora" en pruebas de scheduling.</summary>
public sealed class MutableTimeProvider(DateTimeOffset initial) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = initial;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>IAccountService de prueba: almacena usuarios en memoria.</summary>
public sealed class FakeAccountService : IAccountService
{
    private readonly Dictionary<string, (Guid UserId, string PasswordHash)> _users = [];

    public bool ThrowOnRegister { get; set; }

    public Task<(Guid UserId, string Email)> RegisterAsync(string email, string password, CancellationToken ct = default)
    {
        if (ThrowOnRegister)
        {
            throw new Domain.Exceptions.DomainException("Error al registrar usuario: El email ya está en uso.");
        }

        if (_users.ContainsKey(email.ToLowerInvariant()))
        {
            throw new Domain.Exceptions.DomainException("Error al registrar usuario: El email ya está en uso.");
        }

        var userId = Guid.CreateVersion7();
        _users[email.ToLowerInvariant()] = (userId, $"hash_{password}");
        return Task.FromResult((userId, email));
    }

    public Task<(Guid UserId, string Email)?> ValidateCredentialsAsync(string email, string password, CancellationToken ct = default)
    {
        if (_users.TryGetValue(email.ToLowerInvariant(), out var user) && user.PasswordHash == $"hash_{password}")
        {
            return Task.FromResult<(Guid UserId, string Email)?>((user.UserId, email));
        }

        return Task.FromResult<(Guid UserId, string Email)?>(null);
    }

    public Task<(Guid UserId, string Email, bool IsNewUser)> FindOrCreateExternalAsync(
        string provider, string externalId, string email, string? displayName, CancellationToken ct = default)
    {
        if (_users.TryGetValue(email.ToLowerInvariant(), out var existing))
        {
            return Task.FromResult((existing.UserId, email, false));
        }

        var userId = Guid.CreateVersion7();
        _users[email.ToLowerInvariant()] = (userId, $"external_{externalId}");
        return Task.FromResult((userId, email, true));
    }
}

/// <summary>ITokenService de prueba: genera JWT fake con el userId y email.</summary>
public sealed class FakeTokenService : ITokenService
{
    public (string AccessToken, DateTimeOffset ExpiresAt) GenerateToken(Guid userId, string email, IReadOnlyList<string>? roles = null)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
        var token = $"fake-jwt-{userId:N}";
        return (token, expiresAt);
    }
}

/// <summary>IGoogleTokenValidator de prueba: siempre valida o siempre falla.</summary>
public sealed class FakeGoogleTokenValidator : IGoogleTokenValidator
{
    public GoogleUserInfo? ValidUser { get; set; } = new("google@test.com", "google-sub-123", "Test Google User");

    public bool ShouldFail { get; set; }

    public Task<GoogleUserInfo> ValidateAsync(string idToken, CancellationToken ct = default)
    {
        if (ShouldFail || ValidUser is null)
        {
            throw new Domain.Exceptions.DomainException("Token de Google inválido.");
        }

        return Task.FromResult(ValidUser);
    }
}

/// <summary>
/// IDatasetEmbeddingRepository de prueba. Reproduce la búsqueda como el repositorio real
/// (acotada al dataset, ordenada por similitud) para que los tests del handler no
/// dependan de PostgreSQL.
/// </summary>
public sealed class FakeEmbeddingRepository : IDatasetEmbeddingRepository
{
    private readonly List<DatasetRowEmbedding> _embeddings = [];
    private readonly Dictionary<Guid, DatasetEmbeddingState> _states = [];

    /// <summary>Candidatos que devuelve la búsqueda, si se fija explícitamente.</summary>
    public List<ScoredDatasetRow> SearchResults { get; set; } = [];

    public List<DatasetRowEmbedding> Embeddings => _embeddings;

    /// <summary>
    /// Estado del dataset, creado si no existe. Permite que el test lleve la máquina de
    /// estados al punto de partida que necesita (fallido, en curso) sin cablear el
    /// repositorio entero.
    /// </summary>
    public DatasetEmbeddingState StateFor(Guid datasetId)
    {
        if (!_states.TryGetValue(datasetId, out var state))
        {
            state = new DatasetEmbeddingState(datasetId, DatasetRowEmbedding.VectorDimensions);
            _states[datasetId] = state;
        }

        return state;
    }

    public Task<DatasetEmbeddingState?> FindStateAsync(Guid datasetId, CancellationToken cancellationToken = default)
        => Task.FromResult(_states.GetValueOrDefault(datasetId));

    public Task AddStateAsync(DatasetEmbeddingState state, CancellationToken cancellationToken = default)
    {
        _states[state.DatasetId] = state;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Guid>> ListDatasetsPendingIndexingAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Guid>>([.. _states.Values.Select(s => s.DatasetId)]);

    public Task AddRangeAsync(IReadOnlyList<DatasetRowEmbedding> embeddings, CancellationToken cancellationToken = default)
    {
        _embeddings.AddRange(embeddings);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ScoredDatasetRow>> SearchSimilarAsync(
        Guid datasetId,
        float[] queryVector,
        int limit,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ScoredDatasetRow>>([.. SearchResults.Take(limit)]);

    public Task<long> CountAsync(Guid datasetId, CancellationToken cancellationToken = default)
        => Task.FromResult((long)_embeddings.Count(e => e.DatasetId == datasetId));

    public Task DeleteByDatasetAsync(Guid datasetId, CancellationToken cancellationToken = default)
    {
        _embeddings.RemoveAll(e => e.DatasetId == datasetId);
        _states.Remove(datasetId);
        return Task.CompletedTask;
    }
}

/// <summary>
/// IEmbeddingService de prueba. Devuelve un vector con un 1 en la posición derivada del
/// texto, de modo que dos textos distintos producen vectores distintos y comparables.
/// </summary>
public sealed class FakeEmbeddingService : IEmbeddingService
{
    public int Dimensions { get; init; } = DatasetRowEmbedding.VectorDimensions;

    /// <summary>Textos vectorizados, en orden, para poder verificar el propósito usado.</summary>
    public List<(string Text, EmbeddingPurpose Purpose)> Calls { get; } = [];

    /// <summary>Si se fija, la siguiente llamada a GenerateEmbeddingAsync falla con este error.</summary>
    public Exception? QueryFailure { get; set; }

    public Task<float[]> GenerateEmbeddingAsync(
        string text,
        EmbeddingPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        Calls.Add((text, purpose));

        if (QueryFailure is not null)
        {
            throw QueryFailure;
        }

        return Task.FromResult(DeterministicVector(text));
    }

    public Task<IReadOnlyList<float[]>> GenerateBatchAsync(
        IReadOnlyList<string> texts,
        EmbeddingPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        foreach (var text in texts)
        {
            Calls.Add((text, purpose));
        }

        return Task.FromResult<IReadOnlyList<float[]>>([.. texts.Select(DeterministicVector)]);
    }

    private static float[] DeterministicVector(string text)
    {
        var vector = new float[DatasetRowEmbedding.VectorDimensions];
        var hash = (uint)StringComparer.Ordinal.GetHashCode(text);
        vector[hash % DatasetRowEmbedding.VectorDimensions] = 1f;
        return vector;
    }
}

/// <summary>
/// IGenerativeAIService de prueba. Graba la evidencia recibida para poder verificar que la
/// respuesta se redactó solo con las filas recuperadas.
/// </summary>
public sealed class FakeGenerativeAIService : IGenerativeAIService
{
    public string GroundedAnswer { get; set; } = "respuesta fundamentada";

    public List<GroundedRow> LastGroundedRows { get; } = [];

    public List<GroundedFilter> LastGroundedFilters { get; } = [];

    public string? LastGroundedQuestion { get; private set; }

    public int GroundedCallCount { get; private set; }

    public Task<GeneratedMessageResult> GenerateMessageAsync(
        string description,
        string tone,
        IReadOnlyList<ColumnDefinition> columns,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<Domain.AI.DatasetQuery> GenerateDatasetQueryAsync(
        string question,
        IReadOnlyList<ColumnDefinition> columns,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<string> GenerateResponseAsync(
        string originalQuestion,
        Domain.AI.DatasetQuery query,
        object? queryData,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<string> GenerateGroundedResponseAsync(
        string question,
        IReadOnlyList<GroundedRow> rows,
        IReadOnlyList<GroundedFilter> appliedFilters,
        CancellationToken cancellationToken = default)
    {
        LastGroundedQuestion = question;
        LastGroundedRows.Clear();
        LastGroundedRows.AddRange(rows);
        LastGroundedFilters.Clear();
        LastGroundedFilters.AddRange(appliedFilters);
        GroundedCallCount++;

        return Task.FromResult(GroundedAnswer);
    }
}
