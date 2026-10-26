using MagicOnion;
using MessagePack;
using Queryeables.Abstraction;
using System.Linq.Expressions;
using System.Reflection;

namespace Queryeables.Onion;

/// <summary>
/// <see cref="IEntityStore"/> côté client, au-dessus du service MagicOnion. Permet d'utiliser
/// <see cref="EntityCache{TEntity, TKey}"/> et les transactions à distance comme en local.
/// <para>
/// <b>Prédicats :</b> comparaisons, booléens, <c>liste.Contains(e.Prop)</c>, StartsWith/EndsWith/Contains sur du texte,
/// combinés tous en ET ou tous en OU. Le reste lève <see cref="NotSupportedException"/> avant tout appel.
/// </para>
/// <para>
/// <b>Transactions :</b> pendant une transaction, les écritures sont mises en attente et envoyées en un seul appel
/// au <see cref="IEntityTransaction.CommitAsync"/>, exécuté dans une transaction côté serveur : tout est appliqué,
/// ou rien. En attendant, UpdateAsync et DeleteAsync renvoient true, et une insertion ne reçoit sa clé qu'au Commit ;
/// les lectures ne voient pas encore les écritures en attente.
/// </para>
/// <para><b>Erreurs :</b> <see cref="EntityStoreException"/>, avec la nature de l'erreur (droits, validation, conflit...).</para>
/// </summary>
/// <example>
/// <code>
/// var layer = MagicOnionClient.Create&lt;IMagicOnionLayer&gt;(channel);
/// var cache = new EntityCache&lt;Client, long&gt;(
///     () => new MagicOnionEntityStore(layer, session), c => c.Id, monitor, RecordKey.Parse&lt;long&gt;);
/// </code>
/// </example>
public sealed class MagicOnionEntityStore(
    IMagicOnionLayer layer,
    IDbSession? session = null,
    MessagePackSerializerOptions? options = null) : IEntityStore
{
    private RemoteTransaction? _transaction;

    // ---------- Lecture ----------

    public async Task<IReadOnlyList<TEntity>> GetAllAsync<TEntity>(CancellationToken ct = default) where TEntity : class
        => DeserializeMany<TEntity>(await CallAsync(l => l.GetAllAsync(Name<TEntity>()), ct), ct);

    public async Task<IReadOnlyList<TEntity>> SelectAsync<TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
    {
        var (conditions, any) = Filters.FromPredicate(predicate);
        return DeserializeMany<TEntity>(await CallAsync(l => l.SelectAsync(Name<TEntity>(), conditions, any), ct), ct);
    }

    public async Task<TEntity?> FirstOrDefaultAsync<TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
        => (await SelectPagedAsync(predicate, 1, 1, ct)).FirstOrDefault();

    public async Task<IReadOnlyList<TEntity>> SelectPagedAsync<TEntity>(
        Expression<Func<TEntity, bool>> predicate, int pageNumber, int pageSize,
        CancellationToken ct = default) where TEntity : class
    {
        Paging.Validate(pageNumber, pageSize);
        var (conditions, any) = Filters.FromPredicate(predicate);
        return DeserializeMany<TEntity>(
            await CallAsync(l => l.SelectPagedAsync(Name<TEntity>(), conditions, any, pageNumber, pageSize), ct), ct);
    }

    // ---------- Écriture ----------

    public async Task InsertAsync<TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (Enqueue(BatchOperationKind.Insert, entity, ct))
            return;

        var data = await CallAsync(l => l.InsertAsync(Name<TEntity>(), Serialize(entity, ct), ConnectionId<TEntity>()), ct);
        ApplyResult(data, entity, ct); // clé générée et version
    }

    public async Task<bool> UpdateAsync<TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (Enqueue(BatchOperationKind.Update, entity, ct))
            return true;

        var data = await CallAsync(l => l.UpdateAsync(Name<TEntity>(), Serialize(entity, ct), ConnectionId<TEntity>()), ct);
        return ApplyResult(data, entity, ct); // données vides : ligne introuvable
    }

    public async Task<bool> DeleteAsync<TEntity>(TEntity entity, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (Enqueue(BatchOperationKind.Delete, entity, ct))
            return true;

        var data = await CallAsync(l => l.DeleteAsync(Name<TEntity>(), Serialize(entity, ct), ConnectionId<TEntity>()), ct);
        return data is { Length: > 0 } && MessagePackSerializer.Deserialize<bool>(data, options, ct);
    }

    // ---------- Transactions ----------

    public Task<IEntityTransaction> BeginTransactionAsync(CancellationToken ct = default)
    {
        if (_transaction is not null)
            throw new InvalidOperationException("Une transaction est déjà en cours sur ce store.");

        _transaction = new RemoteTransaction(this);
        return Task.FromResult<IEntityTransaction>(_transaction);
    }

    public ValueTask DisposeAsync()
    {
        _transaction = null; // écritures en attente abandonnées
        return ValueTask.CompletedTask;
    }

    /// <summary>Met l'écriture en attente si une transaction est en cours.</summary>
    private bool Enqueue<TEntity>(BatchOperationKind kind, TEntity entity, CancellationToken ct) where TEntity : class
    {
        if (_transaction is null)
            return false;

        var operation = new BatchOperation(Name<TEntity>(), kind, Serialize(entity, ct), ConnectionId<TEntity>());
        Action<byte[]> applyResult = kind == BatchOperationKind.Delete
            ? _ => { }
            : data => ApplyResult(data, entity, CancellationToken.None);
        _transaction.Add(operation, applyResult);
        return true;
    }

    private async Task CommitAsync(RemoteTransaction transaction, CancellationToken ct)
    {
        if (!ReferenceEquals(_transaction, transaction))
            throw new InvalidOperationException("Cette transaction n'est plus active.");
        _transaction = null;

        if (transaction.Operations.Count == 0)
            return;

        var operations = transaction.Operations.Select(o => o.Operation).ToArray();
        var data = await CallAsync(l => l.ExecuteBatchAsync(operations), ct);
        var results = data is { Length: > 0 }
            ? MessagePackSerializer.Deserialize<byte[][]>(data, options, ct) ?? []
            : [];

        // Clés générées et nouvelles versions reportées dans les entités d'origine.
        for (var i = 0; i < Math.Min(results.Length, transaction.Operations.Count); i++)
            transaction.Operations[i].ApplyResult(results[i]);
    }

    private void Abandon(RemoteTransaction transaction)
    {
        if (ReferenceEquals(_transaction, transaction))
            _transaction = null;
    }

    private sealed class RemoteTransaction(MagicOnionEntityStore store) : IEntityTransaction
    {
        public List<(BatchOperation Operation, Action<byte[]> ApplyResult)> Operations { get; } = [];
        private bool _done;

        public void Add(BatchOperation operation, Action<byte[]> applyResult) => Operations.Add((operation, applyResult));

        public async Task CommitAsync(CancellationToken ct = default)
        {
            if (_done) throw new InvalidOperationException("La transaction est déjà terminée.");
            _done = true;
            await store.CommitAsync(this, ct);
        }

        public Task RollbackAsync(CancellationToken ct = default)
        {
            _done = true;
            store.Abandon(this); // rien n'a été envoyé : il suffit d'oublier les écritures
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (!_done)
                store.Abandon(this);
            _done = true;
            return ValueTask.CompletedTask;
        }
    }

    // ---------- Interne ----------

    private async Task<byte[]?> CallAsync(Func<IMagicOnionLayer, UnaryResult<DBResult>> call, CancellationToken ct)
        => (await call(layer.WithCancellationToken(ct))).GetDataOrThrow();

    /// <summary>Nom sous lequel le serveur enregistre le type (voir <see cref="EntityName"/>).</summary>
    private static string Name<TEntity>() => EntityName.Of<TEntity>();

    private string? ConnectionId<TEntity>()
        => session is not null && session.TryGetSession(typeof(TEntity), out var current) ? current.session : null;

    private byte[] Serialize<TEntity>(TEntity entity, CancellationToken ct)
        => MessagePackSerializer.Serialize(entity, options, ct);

    private IReadOnlyList<TEntity> DeserializeMany<TEntity>(byte[]? data, CancellationToken ct)
        => data is { Length: > 0 }
            ? MessagePackSerializer.Deserialize<TEntity[]>(data, options, ct) ?? []
            : [];

    /// <summary>Recopie l'entité renvoyée par le serveur dans celle de l'appelant. Faux si le serveur n'a rien renvoyé.</summary>
    private bool ApplyResult<TEntity>(byte[]? data, TEntity target, CancellationToken ct) where TEntity : class
    {
        if (data is not { Length: > 0 })
            return false;

        var source = MessagePackSerializer.Deserialize<TEntity>(data, options, ct);
        if (source is null)
            return false;

        foreach (var property in typeof(TEntity).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
                property.SetValue(target, property.GetValue(source));
        }
        return true;
    }
}
