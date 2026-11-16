using Queryeables.Abstraction;

namespace Queryeables.Remote;

/// <summary>
/// Server side: routes each call to the <see cref="EntityHandler"/> of the requested type. A transport (the MagicOnion
/// service, an HTTP endpoint...) only has to forward its calls here, with the caller (<see cref="EntityRequestContext"/>).
/// The calls are the reads of the replicas: every row, the rows by key, the changes since a date.
/// </summary>
/// <param name="registry">The exposed entity types.</param>
/// <param name="serializer">Writes the entities; the clients must use the same.</param>
public sealed class EntityRequestDispatcher(IEntityHandlerRegistry registry, IEntitySerializer serializer)
{
    private readonly IEntityHandlerRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly IEntitySerializer _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));

    public Task<TransportResult> GetAllAsync(string type, EntityRequestContext context, CancellationToken ct = default)
        => Dispatch(type, "get_all", h => h.GetAllAsync(_serializer, Reading(context), ct));

    /// <summary>The rows of one key (<c>Id = 7</c>, every column of a composite key).</summary>
    public Task<TransportResult> SelectAsync(string type, FilterCondition[] conditions, EntityRequestContext context, CancellationToken ct = default)
        => Dispatch(type, "select", h => h.SelectAsync(_serializer, conditions ?? [], Reading(context), ct));

    public Task<TransportResult> GetChangesAsync(string type, DateTime sinceUtc, EntityRequestContext context, CancellationToken ct = default)
        => Dispatch(type, "get_changes", h => h.GetChangesAsync(_serializer, sinceUtc, Reading(context), ct));

    /// <summary>A read: the handlers go with it, for the row filters that go through a relation toward another exposed type.</summary>
    private EntityRequestContext Reading(EntityRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context with { Handlers = _registry };
    }

    /// <summary>
    /// Send the message of an unexpected error (a database error...) to the client. Off by default: the client gets a
    /// generic message with a reference, and <see cref="UnexpectedError"/> receives the exception under that reference;
    /// database messages name tables, constraints and values. Turn it on in development only.
    /// </summary>
    public bool DetailedErrors { get; init; }

    /// <summary>Receives each unexpected error with the reference given to the client (to log it).</summary>
    public Action<Exception, string>? UnexpectedError { get; init; }

    /// <summary>
    /// This dispatcher as an <see cref="IEntityTransport"/>, called in-process with the given caller: a
    /// <see cref="RemoteEntityStore"/> on top of it behaves as a remote client, without any network. Useful for tests,
    /// and for an application that hosts its own server.
    /// </summary>
    public IEntityTransport AsTransport(EntityRequestContext? context = null) => new InProcessRemote(this, context ?? EntityRequestContext.Anonymous);

    private Task<TransportResult> Dispatch(string type, string operation, Func<EntityHandler, Task<TransportResult>> call)
        => !string.IsNullOrWhiteSpace(type) && _registry.TryGet(type, out var handler)
            ? SerializerProblem(handler) is { } problem
                ? Task.FromResult(problem)
                : RemoteDiagnostics.ServerAsync(operation, type, async () => Hide(await call(handler)))
            : Task.FromResult(UnknownType(type));

    /// <summary>
    /// Throws when an exposed type cannot be served with this serializer (a property hidden by an attribute it ignores):
    /// at startup, instead of at the first request for the type. <c>MapQueryeables</c> calls it.
    /// </summary>
    public void CheckConfiguration()
    {
        var problems = _registry.Handlers.Select(h => h.CheckSerializer(_serializer)).OfType<string>().ToList();
        if (problems.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, problems));
    }

    // Checked once per type: a property hidden by an attribute this serializer ignores would reach every client.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<EntityHandler, string?> _serializerProblems = new();

    private TransportResult? SerializerProblem(EntityHandler handler)
    {
        var problem = _serializerProblems.GetOrAdd(handler, h => h.CheckSerializer(_serializer));
        if (problem is null)
            return null;
        UnexpectedError?.Invoke(new InvalidOperationException(problem), "configuration");
        return TransportResult.Failure(EntityErrorCode.NotSupported, $"{handler.Type.Name} cannot be served: the server is misconfigured.");
    }

    /// <summary>An unexpected error leaves the server as a reference; the exception goes to <see cref="UnexpectedError"/>.</summary>
    private TransportResult Hide(TransportResult result)
    {
        if (result.ErrorCode != EntityErrorCode.Unknown || result.Successful || DetailedErrors)
            return result;

        var reference = Guid.NewGuid().ToString("N")[..12];
        System.Diagnostics.Activity.Current?.SetTag("queryeables.error.reference", reference);
        if (result.Cause is { } cause)
            UnexpectedError?.Invoke(cause, reference);
        return TransportResult.Failure(EntityErrorCode.Unknown, $"An unexpected error occurred on the server (reference {reference}).");
    }

    private static TransportResult UnknownType(string? type)
        => TransportResult.Failure(EntityErrorCode.UnknownType, $"Type not exposed by the server: {type}.");

    private sealed class InProcessRemote(EntityRequestDispatcher dispatcher, EntityRequestContext caller) : IEntityTransport
    {
        public Task<TransportResult> GetAllAsync(string type, CancellationToken ct = default)
            => dispatcher.GetAllAsync(type, caller, ct);

        public Task<TransportResult> SelectAsync(string type, FilterCondition[] conditions, CancellationToken ct = default)
            => dispatcher.SelectAsync(type, conditions, caller, ct);

        public Task<TransportResult> GetChangesAsync(string type, DateTime sinceUtc, CancellationToken ct = default)
            => dispatcher.GetChangesAsync(type, sinceUtc, caller, ct);
    }
}
