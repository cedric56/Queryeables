using MongoDB.Bson;
using MongoDB.Driver;
using Queryeables.Abstraction;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace Queryeables.MongoDB;

/// <summary>
/// <see cref="IEntityStore"/> on MongoDB: one collection per entity type (its [Table] name, otherwise the type name), the
/// key stored as <c>_id</c>. It behaves as the Dommel and EF Core stores do, and passes the same contract tests:
/// <list type="bullet">
/// <item>Keys: a numeric key left at 0 is generated from a counter (collection <c>queryeables_counters</c>), a Guid key
/// left empty gets a new Guid, a string key left empty a new ObjectId.</item>
/// <item>[ConcurrencyCheck]: a stale version is a conflict (<see cref="EntityErrorCode.Conflict"/>); an update increments it.</item>
/// <item>Transactions: a MongoDB session (a replica set is needed, as for any MongoDB transaction).</item>
/// <item>Filters are translated by the driver: comparisons, <c>&amp;&amp;</c>, <c>||</c>, <c>!</c>, null,
/// <c>StartsWith</c>/<c>EndsWith</c>/<c>Contains</c>, <c>list.Contains(e.Prop)</c>.</item>
/// </list>
/// A store is cheap: the client and its connection pool are shared; create one per unit of work.
/// </summary>
[RequiresUnreferencedCode(MongoAot.Trimming)]
[RequiresDynamicCode(MongoAot.DynamicCode)]
public sealed class MongoEntityStore : IEntityStore
{
    /// <summary>Sorted pages are a sort, skip and limit of the server.</summary>
    public bool SortsInStore => true;

    /// <summary>The collection of the counters that generate numeric keys.</summary>
    public const string CountersCollection = "queryeables_counters";

    private readonly IMongoDatabase _database;
    private IClientSessionHandle? _session;

    public MongoEntityStore(IMongoDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    /// <summary>A factory of stores on <paramref name="database"/>.</summary>
    public static IEntityStoreFactory Factory(IMongoDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return new EntityStoreFactory(() => new MongoEntityStore(database));
    }

    /// <inheritdoc cref="MongoCollections.NameOf"/>
    public static string CollectionName<TEntity>() => MongoCollections.NameOf(typeof(TEntity));

    // ---------- Read ----------

    public async Task<IReadOnlyList<TEntity>> GetAllAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(CancellationToken ct = default)
        where TEntity : class
        => await Find(Builders<TEntity>.Filter.Empty).ToListAsync(ct);

    public async Task<IReadOnlyList<TEntity>> SelectAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return await Find<TEntity>(predicate).ToListAsync(ct);
    }

    public async Task<TEntity?> FirstOrDefaultAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return await Find<TEntity>(predicate).Limit(1).FirstOrDefaultAsync(ct);
    }

    /// <summary>Sorted by key: pages are stable.</summary>
    public async Task<IReadOnlyList<TEntity>> SelectPagedAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, int pageNumber, int pageSize, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(predicate);
        Paging.Validate(pageNumber, pageSize);
        return await Find<TEntity>(predicate)
            .Sort(Builders<TEntity>.Sort.Ascending("_id"))
            .Skip((pageNumber - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);
    }

    /// <summary>Sorted by <paramref name="sort"/>, then by key: rows with equal values keep one order across pages.</summary>
    public async Task<IReadOnlyList<TEntity>> SelectPagedAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, int pageNumber, int pageSize, Sort<TEntity> sort, CancellationToken ct = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(sort);
        Paging.Validate(pageNumber, pageSize);
        return await Find<TEntity>(predicate)
            .Sort(SortOf(sort))
            .Skip((pageNumber - 1) * pageSize)
            .Limit(pageSize)
            .ToListAsync(ct);
    }

    /// <summary>Projected by MongoDB: only the fields the selector reads come back.</summary>
    public async Task<IReadOnlyList<TResult>> SelectAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity, TResult>(
        Expression<Func<TEntity, bool>> predicate, Expression<Func<TEntity, TResult>> selector, CancellationToken ct = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(selector);
        return await Find<TEntity>(predicate).Project(selector).ToListAsync(ct);
    }

    public Task<long> CountAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(
        Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(predicate);
        FilterDefinition<TEntity> filter = predicate;
        return _session is null
            ? Collection<TEntity>().CountDocumentsAsync(filter, cancellationToken: ct)
            : Collection<TEntity>().CountDocumentsAsync(_session, filter, cancellationToken: ct);
    }

    // ---------- Write ----------

    public async Task InsertAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        await AssignKeyAsync(entity, ct);
        RoundLastModified(entity);
        try
        {
            if (_session is null)
                await Collection<TEntity>().InsertOneAsync(entity, cancellationToken: ct);
            else
                await Collection<TEntity>().InsertOneAsync(_session, entity, cancellationToken: ct);
        }
        catch (MongoWriteException ex) when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new EntityStoreException(EntityErrorCode.Conflict, $"{typeof(TEntity).Name} {KeyOf(entity)} already exists.", innerException: ex);
        }
    }

    /// <exception cref="EntityStoreException">Conflict when the entity has a stale version ([ConcurrencyCheck]).</exception>
    public async Task<bool> UpdateAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        RoundLastModified(entity);

        if (ConcurrencyToken.Find(typeof(TEntity)) is not { } version)
            return await ReplaceAsync(SameKey(entity), entity, ct);

        // Matched only at the version it was read with; written with the next one.
        var read = ConcurrencyToken.Get(entity, version);
        ConcurrencyToken.Set(entity, version, read + 1);
        try
        {
            if (await ReplaceAsync(Builders<TEntity>.Filter.And(SameKey(entity), Builders<TEntity>.Filter.Eq(FieldOf<TEntity>(version.Name), BoxedVersion(version, read))), entity, ct))
                return true;
        }
        catch
        {
            ConcurrencyToken.Set(entity, version, read);
            throw;
        }

        ConcurrencyToken.Set(entity, version, read);
        return await ExistsAsync(entity, ct) ? throw ConcurrencyToken.ConflictFor(typeof(TEntity)) : false;
    }

    /// <exception cref="EntityStoreException">Conflict when the entity has a stale version ([ConcurrencyCheck]).</exception>
    public async Task<bool> DeleteAsync<[DynamicallyAccessedMembers(EntityMembers.Required)] TEntity>(TEntity entity, CancellationToken ct = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        var filter = SameKey(entity);
        if (ConcurrencyToken.Find(typeof(TEntity)) is { } version)
            filter = Builders<TEntity>.Filter.And(filter, Builders<TEntity>.Filter.Eq(FieldOf<TEntity>(version.Name), BoxedVersion(version, ConcurrencyToken.Get(entity, version))));

        var result = _session is null
            ? await Collection<TEntity>().DeleteOneAsync(filter, ct)
            : await Collection<TEntity>().DeleteOneAsync(_session, filter, cancellationToken: ct);
        if (result.DeletedCount > 0)
            return true;
        return ConcurrencyToken.Find(typeof(TEntity)) is not null && await ExistsAsync(entity, ct)
            ? throw ConcurrencyToken.ConflictFor(typeof(TEntity))
            : false;
    }

    // ---------- Transactions ----------

    public async Task<IEntityTransaction> BeginTransactionAsync(CancellationToken ct = default)
    {
        if (_session is not null)
            throw new InvalidOperationException("A transaction is already in progress on this store.");

        var session = await _database.Client.StartSessionAsync(cancellationToken: ct);
        try
        {
            session.StartTransaction();
        }
        catch
        {
            session.Dispose();
            throw;
        }
        _session = session;
        return new Transaction(this, session);
    }

    public void Dispose()
    {
        _session?.Dispose();        // a session disposed in a transaction aborts it
        _session = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is { } session)
        {
            _session = null;
            if (session.IsInTransaction)
                await session.AbortTransactionAsync();
            session.Dispose();
        }
    }

    private sealed class Transaction(MongoEntityStore store, IClientSessionHandle session) : IEntityTransaction
    {
        public Task<EntityResult> CommitAsync(CancellationToken ct = default) => EntityResult.RunAsync(async () =>
        {
            await session.CommitTransactionAsync(ct);
            End();
        });

        public Task<EntityResult> RollbackAsync(CancellationToken ct = default) => EntityResult.RunAsync(async () =>
        {
            if (session.IsInTransaction)
                await session.AbortTransactionAsync(ct);
            End();
        });

        public async ValueTask DisposeAsync()
        {
            if (!ReferenceEquals(store._session, session))
                return;
            if (session.IsInTransaction)
                await session.AbortTransactionAsync();
            End();
        }

        private void End()
        {
            if (ReferenceEquals(store._session, session))
                store._session = null;
            session.Dispose();
        }
    }

    // ---------- Internals ----------

    private IMongoCollection<TEntity> Collection<TEntity>() => _database.GetCollection<TEntity>(CollectionName<TEntity>());

    private IFindFluent<TEntity, TEntity> Find<TEntity>(FilterDefinition<TEntity> filter)
        => _session is null ? Collection<TEntity>().Find(filter) : Collection<TEntity>().Find(_session, filter);

    private async Task<bool> ReplaceAsync<TEntity>(FilterDefinition<TEntity> filter, TEntity entity, CancellationToken ct)
    {
        var result = _session is null
            ? await Collection<TEntity>().ReplaceOneAsync(filter, entity, new ReplaceOptions<TEntity>(), ct)
            : await Collection<TEntity>().ReplaceOneAsync(_session, filter, entity, new ReplaceOptions<TEntity>(), ct);
        return result.MatchedCount > 0;
    }

    private async Task<bool> ExistsAsync<TEntity>(TEntity entity, CancellationToken ct)
    {
        var filter = SameKey(entity);
        var count = _session is null
            ? await Collection<TEntity>().CountDocumentsAsync(filter, new CountOptions { Limit = 1 }, ct)
            : await Collection<TEntity>().CountDocumentsAsync(_session, filter, new CountOptions { Limit = 1 }, ct);
        return count > 0;
    }

    private static PropertyInfo Key<TEntity>()
        => SyncMetadata.Key(typeof(TEntity))
           ?? throw new NotSupportedException($"{typeof(TEntity).Name} has no key: add an Id property, or mark one with [Key].");

    private static object? KeyOf<TEntity>(TEntity entity) => Key<TEntity>().GetValue(entity);

    /// <summary><c>_id == entity.Key</c>, through the class map: the key property is stored as <c>_id</c>.</summary>
    private static FilterDefinition<TEntity> SameKey<TEntity>(TEntity entity)
    {
        var key = Key<TEntity>();
        var parameter = Expression.Parameter(typeof(TEntity), "e");
        var body = Expression.Equal(Expression.Property(parameter, key), Expression.Constant(key.GetValue(entity), key.PropertyType));
        return Expression.Lambda<Func<TEntity, bool>>(body, parameter);
    }

    /// <summary>The field a property is stored in: <c>_id</c> for the key, its name otherwise.</summary>
    private static string FieldOf<TEntity>(string property)
        => string.Equals(Key<TEntity>().Name, property, StringComparison.Ordinal) ? "_id" : property;

    private static SortDefinition<TEntity> SortOf<TEntity>(Sort<TEntity> sort) where TEntity : class
    {
        var builder = Builders<TEntity>.Sort;
        var keys = sort.Keys
            .Select(k => k.Descending ? builder.Descending(FieldOf<TEntity>(k.Property)) : builder.Ascending(FieldOf<TEntity>(k.Property)))
            .ToList();
        if (!sort.Keys.Any(k => FieldOf<TEntity>(k.Property) == "_id"))
            keys.Add(builder.Ascending("_id"));
        return builder.Combine(keys);
    }

    /// <summary>The version as stored: same numeric type as the property, so that the comparison matches.</summary>
    private static object BoxedVersion(PropertyInfo version, long value)
        => Convert.ChangeType(value, Nullable.GetUnderlyingType(version.PropertyType) ?? version.PropertyType, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A key left empty gets one: from a counter for numbers, a new Guid, or a new ObjectId for strings.</summary>
    private async Task AssignKeyAsync<TEntity>(TEntity entity, CancellationToken ct)
    {
        var key = Key<TEntity>();
        var value = key.GetValue(entity);
        var type = Nullable.GetUnderlyingType(key.PropertyType) ?? key.PropertyType;

        if (type == typeof(Guid))
        {
            if (value is not Guid guid || guid == Guid.Empty)
                key.SetValue(entity, Guid.NewGuid());
            return;
        }
        if (type == typeof(string))
        {
            if (string.IsNullOrEmpty(value as string))
                key.SetValue(entity, ObjectId.GenerateNewId().ToString());
            return;
        }
        if (type == typeof(ObjectId))
        {
            if (value is not ObjectId id || id == ObjectId.Empty)
                key.SetValue(entity, ObjectId.GenerateNewId());
            return;
        }
        if (Type.GetTypeCode(type) is not (TypeCode.Int64 or TypeCode.Int32 or TypeCode.Int16))
            return;
        if (value is not null && Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) != 0)
            return;

        var next = await NextValueAsync(CollectionName<TEntity>(), ct);
        key.SetValue(entity, Convert.ChangeType(next, type, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>The next number of <paramref name="collection"/>: an atomic increment, one document per collection.</summary>
    private async Task<long> NextValueAsync(string collection, CancellationToken ct)
    {
        var counters = _database.GetCollection<BsonDocument>(CountersCollection);
        var filter = Builders<BsonDocument>.Filter.Eq("_id", collection);
        var update = Builders<BsonDocument>.Update.Inc("value", 1L);
        var options = new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After };
        var counter = _session is null
            ? await counters.FindOneAndUpdateAsync(filter, update, options, ct)
            : await counters.FindOneAndUpdateAsync(_session, filter, update, options, ct);
        return counter["value"].ToInt64();
    }

    /// <summary>
    /// MongoDB keeps dates to the millisecond: a [LastModified] date is rounded before it is written, so that the entity
    /// holds exactly what is stored and "changes since" this date finds the row.
    /// </summary>
    private static void RoundLastModified<TEntity>(TEntity entity)
    {
        if (SyncMetadata.LastModified(typeof(TEntity)) is not { } property)
            return;
        switch (property.GetValue(entity))
        {
            case DateTime date:
                property.SetValue(entity, new DateTime(date.Ticks - date.Ticks % TimeSpan.TicksPerMillisecond, date.Kind));
                break;
            case DateTimeOffset date:
                property.SetValue(entity, new DateTimeOffset(date.Ticks - date.Ticks % TimeSpan.TicksPerMillisecond, date.Offset));
                break;
        }
    }
}

public static class MongoCollections
{
    /// <summary>The collection of an entity type: its [Table] name, otherwise its type name, as the change feeds name their tables.</summary>
    public static string NameOf(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.GetCustomAttribute<TableAttribute>()?.Name ?? type.Name;
    }
}
