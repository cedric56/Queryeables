using Queryeables.Abstraction;
using System.Collections;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;

namespace Queryeables.Remote.Offline;

/// <summary>The local copy of one entity type, without its generic arguments.</summary>
internal abstract class OfflineEntityType
{
    public abstract Type Type { get; }

    /// <summary>The name of the type on the wire (<see cref="EntityName"/>).</summary>
    public abstract string Name { get; }

    /// <summary>The table its monitor watches: the [Table] name, otherwise the type name.</summary>
    public abstract string Table { get; }

    public abstract Task LoadAsync(IOfflineStorage storage, string prefix, CancellationToken ct);

    /// <summary>Saves what changed since the last save: the rows written, the rows removed, the list of keys when it changed.</summary>
    public abstract Task SaveAsync(IOfflineStorage storage, string prefix, CancellationToken ct);

    /// <summary>
    /// Brings the copy up to date with the server: only the rows changed since the last download when the type allows it
    /// ([LastModified] and [SoftDelete]), the whole scope otherwise. Throws when the server cannot be reached.
    /// </summary>
    public abstract Task DownloadAsync(IEntityRemote remote, CancellationToken ct);

    /// <summary>Rows changed on the server since the copy; null when the type has no [LastModified].</summary>
    public abstract Task<int?> CountServerChangesAsync(IEntityRemote remote, DateTime since, CancellationToken ct);

    /// <summary>The payload as the server expects it: temporary keys (parent and children) back to their default value.</summary>
    public abstract byte[] PrepareForSend(byte[] payload, BatchOperationKind kind);

    /// <summary>Sets the key of <paramref name="entity"/> back to its default value when it is a temporary one.</summary>
    public abstract void ResetIfTemporary(object entity);

    /// <summary>Sends a refused write again, adapted to the server's current row (its version, or an insert when it is gone).</summary>
    public abstract Task<RemoteResult> SendAgainAsync(IEntityRemote remote, OfflineConflict conflict, CancellationToken ct);

    /// <summary>The next download reads this row again from the server, whatever its modification date.</summary>
    public abstract void MarkLocal(string keyText);

    /// <summary>Copies of the rows: changing them does not change the copy.</summary>
    public abstract IEnumerable<object> CopyRows();

    /// <summary>Writes into the copy. An insert gives a default key a temporary one; false when an updated or deleted row is missing.</summary>
    public abstract bool Apply(BatchOperationKind kind, object entity);

    /// <summary>The formatted key of <paramref name="entity"/> (see <see cref="DbRecordKey"/>).</summary>
    public abstract string KeyTextOf(object entity);

    public abstract object Snapshot();

    public abstract void Restore(object snapshot);
}

/// <summary>
/// The local copy of <typeparamref name="TEntity"/>: rows by key, the keys given offline, and the rows written offline,
/// read again from the server at the next download. Stored row by row.
/// </summary>
internal sealed class OfflineEntityType<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity,
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TKey> : OfflineEntityType
    where TEntity : class
    where TKey : notnull
{
    /// <summary>Keys per request when rows are read again by key: well under the filter limit.</summary>
    private const int KeysPerRequest = 500;

    private static readonly Expression<Func<List<TKey>, TKey, bool>> ListContains = (list, key) => list.Contains(key);

    private readonly OfflineMode _mode;
    private readonly Expression<Func<TEntity, TKey>> _keyExpression;
    private readonly Func<TEntity, TKey> _key;
    private readonly Action<TEntity, TKey> _setKey;
    private readonly Expression<Func<TEntity, bool>>? _scope;
    private readonly Func<TEntity, bool> _inScope;
    private readonly bool _incremental;

    private State _state = new([], [], [], null, null);

    // What the storage holds, and what changed since the last save.
    private HashSet<string> _stored = [];
    private readonly HashSet<TKey> _changed = [];
    private bool _metaChanged;

    /// <param name="Rows">The copy.</param>
    /// <param name="Temporary">Keys given offline, replaced by the server's.</param>
    /// <param name="Local">Rows written offline: read again by key at the next download.</param>
    /// <param name="Watermark">The latest modification date received from the server: the next download asks from there.</param>
    /// <param name="Scope">The scope the copy was taken with: another one means a full download.</param>
    private sealed record State(Dictionary<TKey, TEntity> Rows, HashSet<TKey> Temporary, HashSet<TKey> Local,
        DateTime? Watermark, string? Scope);

    public OfflineEntityType(OfflineMode mode, Expression<Func<TEntity, TKey>> key, Expression<Func<TEntity, bool>>? scope)
    {
        _mode = mode;
        _keyExpression = key;
        _key = key.Compile();
        _setKey = Setter(key);
        _scope = scope;
        _inScope = scope?.Compile() ?? (static _ => true);
        _incremental = SyncMetadata.LastModified(typeof(TEntity)) is not null && SyncMetadata.SoftDelete(typeof(TEntity)) is not null;
        Name = EntityName.Of<TEntity>();
        Table = typeof(TEntity).GetCustomAttribute<TableAttribute>()?.Name ?? typeof(TEntity).Name;
    }

    public override Type Type => typeof(TEntity);

    public override string Name { get; }

    public override string Table { get; }

    // ---------- Rows ----------

    /// <summary>Copies, as a database returns them: changing them does not change the copy.</summary>
    public IEnumerable<TEntity> Rows => _state.Rows.Values.Select(Copy).ToList();

    public string KeyText(TEntity entity) => DbRecordKey.Format(_key(entity));

    /// <summary>Adds the row. A default key becomes a temporary one, written into <paramref name="entity"/> too.</summary>
    public void Insert(TEntity entity)
    {
        var key = _key(entity);
        if (IsDefault(key))
        {
            key = _mode.NewTemporaryKey<TKey>();
            _setKey(entity, key);
            _state.Temporary.Add(key);
        }
        else if (_state.Rows.ContainsKey(key))
        {
            throw new EntityStoreException(EntityErrorCode.Conflict, $"{typeof(TEntity).Name} {key} already exists.");
        }

        _state.Rows[key] = Copy(entity);
        Written(key);
    }

    public bool Update(TEntity entity)
    {
        var key = _key(entity);
        if (!_state.Rows.ContainsKey(key))
            return false;
        _state.Rows[key] = Copy(entity);
        Written(key);
        return true;
    }

    public bool Delete(TEntity entity)
    {
        var key = _key(entity);
        if (!_state.Rows.Remove(key))
            return false;
        _state.Temporary.Remove(key);
        Written(key);
        return true;
    }

    public override IEnumerable<object> CopyRows() => Rows;

    public override bool Apply(BatchOperationKind kind, object entity)
    {
        var typed = (TEntity)entity;
        switch (kind)
        {
            case BatchOperationKind.Insert:
                Insert(typed);
                return true;
            case BatchOperationKind.Update:
                return Update(typed);
            default:
                return Delete(typed);
        }
    }

    public override string KeyTextOf(object entity) => KeyText((TEntity)entity);

    public override void MarkLocal(string keyText)
    {
        _state.Local.Add(DbRecordKey.Parse<TKey>(keyText));
        _metaChanged = true;
    }

    // ---------- Storage ----------

    public override async Task LoadAsync(IOfflineStorage storage, string prefix, CancellationToken ct)
    {
        var rows = new Dictionary<TKey, TEntity>();
        var keys = OfflineCodec.ReadStrings(await storage.ReadAsync(IndexKey(prefix), ct));
        foreach (var keyText in keys)
        {
            if (await storage.ReadAsync(RowKey(prefix, keyText), ct) is { Length: > 0 } data
                && _mode.Serializer.Deserialize<TEntity>(data) is { } row)
                rows[_key(row)] = row;
        }

        var meta = OfflineCodec.ReadStrings(await storage.ReadAsync(MetaKey(prefix), ct));
        _state = new State(
            rows,
            ReadKeys(await storage.ReadAsync(TemporaryKey(prefix), ct)),
            ReadKeys(await storage.ReadAsync(LocalKey(prefix), ct)),
            meta.Count > 0 && long.TryParse(meta[0], CultureInfo.InvariantCulture, out var ticks) && ticks > 0
                ? new DateTime(ticks, DateTimeKind.Utc)
                : null,
            meta.Count > 1 ? meta[1] : null);
        _stored = rows.Keys.Select(k => DbRecordKey.Format(k)).ToHashSet();
        _changed.Clear();
        _metaChanged = false;
    }

    public override async Task SaveAsync(IOfflineStorage storage, string prefix, CancellationToken ct)
    {
        var indexChanged = false;
        foreach (var key in _changed)
        {
            var keyText = DbRecordKey.Format(key);
            if (_state.Rows.TryGetValue(key, out var row))
            {
                await storage.WriteAsync(RowKey(prefix, keyText), _mode.Serializer.Serialize(row), ct);
                indexChanged |= _stored.Add(keyText);
            }
            else if (_stored.Remove(keyText))
            {
                await storage.DeleteAsync(RowKey(prefix, keyText), ct);
                indexChanged = true;
            }
        }
        _changed.Clear();

        if (indexChanged)
            await storage.WriteAsync(IndexKey(prefix), OfflineCodec.WriteStrings(_stored), ct);

        if (_metaChanged)
        {
            await storage.WriteAsync(TemporaryKey(prefix), WriteKeys(_state.Temporary), ct);
            await storage.WriteAsync(LocalKey(prefix), WriteKeys(_state.Local), ct);
            await storage.WriteAsync(MetaKey(prefix), OfflineCodec.WriteStrings(
                [(_state.Watermark?.Ticks ?? 0).ToString(CultureInfo.InvariantCulture), _state.Scope ?? string.Empty]), ct);
            _metaChanged = false;
        }
    }

    // ---------- Server ----------

    public override async Task DownloadAsync(IEntityRemote remote, CancellationToken ct)
    {
        var (conditions, any) = _scope is null ? ([], false) : Filters.FromPredicate(_scope);
        var scope = Fingerprint(conditions, any);

        if (_incremental && _state.Watermark is { } since && scope == _state.Scope)
            await DownloadChangesAsync(remote, since, ct);
        else
            await DownloadAllAsync(remote, conditions, any, scope, ct);
        _metaChanged = true;
    }

    public override async Task<int?> CountServerChangesAsync(IEntityRemote remote, DateTime since, CancellationToken ct)
    {
        if (SyncMetadata.LastModified(typeof(TEntity)) is null)
            return null;

        var from = _state.Watermark ?? since;
        var changed = Read(await remote.GetChangesAsync(Name, from, ct));
        // ">=" on the server, so that nothing is lost: the rows at the watermark itself are already in the copy.
        return changed.Count(row => SyncMetadata.LastModifiedUtc(row) > from && (_inScope(row) || _state.Rows.ContainsKey(_key(row))));
    }

    /// <summary>Every row of the scope: the first time, for a type without [LastModified] and [SoftDelete], or a new scope.</summary>
    private async Task DownloadAllAsync(IEntityRemote remote, FilterCondition[] conditions, bool any, string scope, CancellationToken ct)
    {
        var rows = Read(_scope is null
            ? await remote.GetAllAsync(Name, ct)
            : await remote.SelectAsync(Name, conditions, any, ct));

        var previous = _state.Rows;
        _state = new State(rows.ToDictionary(_key), [], [], Latest(rows, null), scope);

        // Every row is new to the storage; the ones gone are removed from it.
        foreach (var key in previous.Keys)
            _changed.Add(key);
        foreach (var key in _state.Rows.Keys)
            _changed.Add(key);
        foreach (var keyText in _stored)
            _changed.Add(DbRecordKey.Parse<TKey>(keyText));
    }

    /// <summary>The rows changed on the server since the watermark, and the rows written offline, read again by key.</summary>
    private async Task DownloadChangesAsync(IEntityRemote remote, DateTime since, CancellationToken ct)
    {
        var changes = Read(await remote.GetChangesAsync(Name, SyncMetadata.Before(since, _mode.SyncOverlap), ct));

        // Read again from the server: what was written offline (sent, refused, or lost) is replaced by what it holds.
        var local = _state.Local.Where(k => !_state.Temporary.Contains(k)).ToList();
        var current = new Dictionary<TKey, TEntity?>();
        foreach (var chunk in local.Chunk(KeysPerRequest))
        {
            var keys = chunk.ToList();
            foreach (var key in keys)
                current[key] = null;
            foreach (var row in Read(await remote.SelectAsync(Name, KeyIn(keys), false, ct)))
                current[_key(row)] = row;
        }

        // Nothing failed: the copy changes now, all at once.
        foreach (var key in _state.Temporary)
        {
            _state.Rows.Remove(key);     // sent: the server gave them their own key, and they come back with it
            _changed.Add(key);
        }

        foreach (var row in changes)
            Merge(_key(row), row);
        foreach (var (key, row) in current)
            Merge(key, row);

        _state = _state with { Temporary = [], Local = [], Watermark = Latest(changes, since) };
    }

    /// <summary>The server's row in the copy, or out of it when deleted or outside the scope.</summary>
    private void Merge(TKey key, TEntity? row)
    {
        if (row is not null && !SyncMetadata.IsDeleted(row) && _inScope(row))
            _state.Rows[key] = row;
        else
            _state.Rows.Remove(key);
        _changed.Add(key);
    }

    public override byte[] PrepareForSend(byte[] payload, BatchOperationKind kind)
    {
        var entity = _mode.Serializer.Deserialize<TEntity>(payload)
            ?? throw new InvalidDataException($"Empty {typeof(TEntity).Name} in the offline queue.");

        if (kind == BatchOperationKind.Insert)
            ResetIfTemporary(entity);
        ResetChildren(entity);
        return _mode.Serializer.Serialize(entity);
    }

    public override void ResetIfTemporary(object entity)
    {
        if (entity is TEntity typed && _state.Temporary.Contains(_key(typed)))
            _setKey(typed, default!);
    }

    public override async Task<RemoteResult> SendAgainAsync(IEntityRemote remote, OfflineConflict conflict, CancellationToken ct)
    {
        var local = _mode.Serializer.Deserialize<TEntity>(conflict.Payload)
            ?? throw new InvalidDataException($"Empty {typeof(TEntity).Name} in the conflict.");
        ResetChildren(local);

        var server = (Read(await remote.SelectAsync(Name, KeyIn([_key(local)]), false, ct))).FirstOrDefault();
        if (server is null)
        {
            // Deleted on the server: an update becomes an insert, a delete has nothing left to do.
            if (conflict.Kind == BatchOperationKind.Delete)
                return RemoteResult.Success();
            _setKey(local, default!);
            return await remote.InsertAsync(Name, _mode.Serializer.Serialize(local), null, ct);
        }

        // Changed on the server: write over it, from its current version.
        if (ConcurrencyToken.Find(typeof(TEntity)) is { } version)
            ConcurrencyToken.Set(local, version, ConcurrencyToken.Get(server, version));

        var bytes = _mode.Serializer.Serialize(local);
        return conflict.Kind == BatchOperationKind.Delete
            ? await remote.DeleteAsync(Name, bytes, null, ct)
            : await remote.UpdateAsync(Name, bytes, null, ct);
    }

    // ---------- Transactions ----------

    public override object Snapshot()
        => _state with { Rows = new(_state.Rows), Temporary = [.. _state.Temporary], Local = [.. _state.Local] };

    /// <summary>Back to <paramref name="snapshot"/>: only the rows that differ are saved again.</summary>
    public override void Restore(object snapshot)
    {
        var saved = (State)snapshot;
        foreach (var (key, row) in _state.Rows)
        {
            if (!saved.Rows.TryGetValue(key, out var before) || !ReferenceEquals(before, row))
                _changed.Add(key);
        }
        foreach (var key in saved.Rows.Keys.Where(k => !_state.Rows.ContainsKey(k)))
            _changed.Add(key);

        _state = saved;
        _metaChanged = true;
    }

    // ---------- Internals ----------

    private void Written(TKey key)
    {
        _changed.Add(key);
        _state.Local.Add(key);
        _metaChanged = true;
    }

    private List<TEntity> Read(RemoteResult result)
    {
        var data = result.GetDataOrThrow();
        return data is { Length: > 0 } ? [.. _mode.Serializer.Deserialize<TEntity[]>(data) ?? []] : [];
    }

    /// <summary><c>e => keys.Contains(e.Key)</c> as conditions: the rows of these keys, in one request.</summary>
    private FilterCondition[] KeyIn(List<TKey> keys)
    {
        var parameter = _keyExpression.Parameters[0];
        var body = new Replacer(ListContains.Parameters[0], Expression.Constant(keys))
            .Visit(new Replacer(ListContains.Parameters[1], _keyExpression.Body).Visit(ListContains.Body))!;
        return Filters.FromPredicate(Expression.Lambda<Func<TEntity, bool>>(body, parameter)).Conditions;
    }

    /// <summary>The latest modification date among <paramref name="rows"/>, or <paramref name="previous"/>.</summary>
    private static DateTime? Latest(IEnumerable<TEntity> rows, DateTime? previous)
    {
        var latest = previous;
        foreach (var row in rows)
        {
            if (SyncMetadata.LastModifiedUtc(row) is { } modified && (latest is null || modified > latest))
                latest = modified;
        }
        return latest;
    }

    /// <summary>The scope with its values, as sent to the server: another user, another scope.</summary>
    private static string Fingerprint(FilterCondition[] conditions, bool any)
        => (any ? "or:" : "and:") + string.Join(";", conditions.Select(c => $"{c.Property}|{c.Operator}|{Format(c.Value)}"));

    private static string Format(object? value) => value switch
    {
        null => "null",
        string text => text,
        IEnumerable values => "[" + string.Join(",", values.Cast<object?>().Select(Format)) + "]",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static HashSet<TKey> ReadKeys(byte[]? data) => OfflineCodec.ReadStrings(data).Select(DbRecordKey.Parse<TKey>).ToHashSet();

    private static byte[] WriteKeys(IEnumerable<TKey> keys) => OfflineCodec.WriteStrings(keys.Select(k => DbRecordKey.Format(k)));

    private string IndexKey(string prefix) => $"{prefix}/{Name}/index";

    private string RowKey(string prefix, string keyText) => $"{prefix}/{Name}/row/{keyText}";

    private string MetaKey(string prefix) => $"{prefix}/{Name}/meta";

    private string TemporaryKey(string prefix) => $"{prefix}/{Name}/temporary";

    private string LocalKey(string prefix) => $"{prefix}/{Name}/local";

    private TEntity Copy(TEntity entity)
        => _mode.Serializer.Deserialize<TEntity>(_mode.Serializer.Serialize(entity))
           ?? throw new InvalidOperationException($"{typeof(TEntity).Name} could not be copied by the serializer.");

    /// <summary>Children created offline (in the collections of a graph) get their default key back.</summary>
    private void ResetChildren(TEntity entity)
    {
        foreach (var property in typeof(TEntity).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0 || property.PropertyType == typeof(string) || property.PropertyType == typeof(byte[]))
                continue;
            if (property.GetValue(entity) is not IList children)
                continue;
            foreach (var child in children)
            {
                if (child is not null)
                    _mode.TypeOf(child.GetType())?.ResetIfTemporary(child);
            }
        }
    }

    private static bool IsDefault(TKey key) => EqualityComparer<TKey>.Default.Equals(key, default!);

    private static Action<TEntity, TKey> Setter(Expression<Func<TEntity, TKey>> key)
    {
        var body = key.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert ? convert.Operand : key.Body;
        if (body is not MemberExpression { Member: PropertyInfo { CanWrite: true } property })
            throw new ArgumentException($"{key} must select a writable key property, for example e => e.Id.", nameof(key));

        var entity = Expression.Parameter(typeof(TEntity), "entity");
        var value = Expression.Parameter(typeof(TKey), "value");
        return Expression.Lambda<Action<TEntity, TKey>>(
            Expression.Assign(Expression.Property(entity, property), Expression.Convert(value, property.PropertyType)),
            entity, value).Compile();
    }

    private sealed class Replacer(Expression from, Expression to) : ExpressionVisitor
    {
        public override Expression? Visit(Expression? node) => node == from ? to : base.Visit(node);
    }
}
