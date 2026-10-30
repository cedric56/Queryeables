using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore.Infrastructure;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace Queryeables.EntityFramework;

/// <summary>
/// EF Core CRUD directly on IDbConnection, with the same shape as Dommel.
/// Each call creates a lightweight DbContext on the given connection (and transaction);
/// the EF model of each entity type is built once and then cached.
///
/// At application startup, one provider per connection type:
///     EfConnectionExtensions.UseProvider&lt;SqliteConnection&gt;((b, c) => b.UseSqlite(c));
///     EfConnectionExtensions.UseProvider&lt;NpgsqlConnection&gt;((b, c) => b.UseNpgsql(c));
/// </summary>
[RequiresUnreferencedCode(EfAot.Trimming)]
[RequiresDynamicCode(EfAot.DynamicCode)]
public static class EfConnectionExtensions
{
    private static readonly ConcurrentDictionary<Type, Action<DbContextOptionsBuilder, DbConnection>> _providers = new();
    private static Action<DbContextOptionsBuilder, DbConnection>? _defaultProvider;

    /// <summary>
    /// EF provider for connections of type <typeparamref name="TConnection"/>. Several providers
    /// can coexist in the same application: each one is picked from the connection type.
    /// </summary>
    public static void UseProvider<TConnection>(Action<DbContextOptionsBuilder, TConnection> configure)
        where TConnection : DbConnection
    {
        ArgumentNullException.ThrowIfNull(configure);
        _providers[typeof(TConnection)] = (builder, connection) => configure(builder, (TConnection)connection);
    }

    /// <summary>
    /// EF provider used when no provider is registered for the connection type.
    /// Prefer <see cref="UseProvider{TConnection}"/> when the application uses several databases.
    /// </summary>
    public static void UseProvider(Action<DbContextOptionsBuilder, DbConnection> configure)
        => _defaultProvider = configure ?? throw new ArgumentNullException(nameof(configure));

    private static Action<DbContextOptionsBuilder, DbConnection> ProviderFor(DbConnection connection)
    {
        for (var type = connection.GetType(); type is not null && type != typeof(DbConnection); type = type.BaseType)
        {
            if (_providers.TryGetValue(type, out var configure))
                return configure;
        }

        return _defaultProvider ?? throw new InvalidOperationException(
            $"No EF provider for {connection.GetType().Name}: " +
            $"call EfConnectionExtensions.UseProvider<{connection.GetType().Name}>(...) at startup.");
    }

    // ---------- Read ----------

    public static async Task<IEnumerable<TEntity>> SelectAsync<TEntity>(this IDbConnection connection,
        Expression<Func<TEntity, bool>> predicate, IDbTransaction? transaction = null,
        CancellationToken cancellationToken = default) where TEntity : class
    {
        await using var ctx = await CreateContextAsync<TEntity>(connection, transaction, cancellationToken);
        // Materialized before the context is closed.
        return await ctx.Set<TEntity>().Where(predicate).ToListAsync(cancellationToken);
    }

    public static async Task<TEntity?> FirstOrDefaultAsync<TEntity>(this IDbConnection connection,
        Expression<Func<TEntity, bool>> predicate, IDbTransaction? transaction = null,
        CancellationToken cancellationToken = default) where TEntity : class
    {
        await using var ctx = await CreateContextAsync<TEntity>(connection, transaction, cancellationToken);
        return await ctx.Set<TEntity>().FirstOrDefaultAsync(predicate, cancellationToken);
    }

    /// <param name="pageNumber">Starts at 1, like Dommel.</param>
    public static async Task<IEnumerable<TEntity>> SelectPagedAsync<TEntity>(this IDbConnection connection,
        Expression<Func<TEntity, bool>> predicate, int pageNumber, int pageSize,
        IDbTransaction? transaction = null, CancellationToken cancellationToken = default) where TEntity : class
    {
        if (pageNumber < 1) throw new ArgumentOutOfRangeException(nameof(pageNumber), "The first page is 1.");
        if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));

        await using var ctx = await CreateContextAsync<TEntity>(connection, transaction, cancellationToken);
        return await OrderByKey(ctx, ctx.Set<TEntity>().Where(predicate))
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
    }

    // ---------- Write ----------

    /// <summary>Inserts the entity and returns the id (also written back into the entity by EF).</summary>
    public static async Task<object?> InsertAsync<TEntity>(this IDbConnection connection, TEntity entity,
        IDbTransaction? transaction = null, CancellationToken cancellationToken = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        await using var ctx = await CreateContextAsync<TEntity>(connection, transaction, cancellationToken);

        var entry = ctx.Add(entity);
        await ctx.SaveChangesAsync(cancellationToken);

        var key = entry.Metadata.FindPrimaryKey();
        return key is { Properties.Count: 1 }
            ? entry.Property(key.Properties[0].Name).CurrentValue
            : null; // composite key: the values are in the entity
    }

    public static async Task InsertAllAsync<TEntity>(this IDbConnection connection, IEnumerable<TEntity> entities,
        IDbTransaction? transaction = null, CancellationToken cancellationToken = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entities);
        await using var ctx = await CreateContextAsync<TEntity>(connection, transaction, cancellationToken);
        ctx.AddRange(entities);
        await ctx.SaveChangesAsync(cancellationToken);
    }

    public static async Task<bool> UpdateAsync<TEntity>(this IDbConnection connection, TEntity entity,
        IDbTransaction? transaction = null, CancellationToken cancellationToken = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        await using var ctx = await CreateContextAsync<TEntity>(connection, transaction, cancellationToken);
        ctx.Update(entity);
        return await SaveAffectsRowsAsync(ctx, cancellationToken);
    }

    public static async Task<bool> DeleteAsync<TEntity>(this IDbConnection connection, TEntity entity,
        IDbTransaction? transaction = null, CancellationToken cancellationToken = default) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        await using var ctx = await CreateContextAsync<TEntity>(connection, transaction, cancellationToken);
        ctx.Remove(entity);
        return await SaveAffectsRowsAsync(ctx, cancellationToken);
    }

    // ---------- Internals ----------

    private static async Task<EntityContext> CreateContextAsync<TEntity>(IDbConnection connection,
        IDbTransaction? transaction, CancellationToken ct) where TEntity : class
    {
        var dbConnection = connection as DbConnection
            ?? throw new ArgumentException("EF Core requires a DbConnection.", nameof(connection));
        var configure = ProviderFor(dbConnection);

        var builder = new DbContextOptionsBuilder();
        configure(builder, dbConnection); // EF does not dispose a connection lent to it
        builder.ReplaceService<IModelCacheKeyFactory, EntityModelCacheKeyFactory>();
        builder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);

        var ctx = new EntityContext(builder.Options, typeof(TEntity));

        if (transaction != null)
        {
            var dbTransaction = transaction as DbTransaction
                ?? throw new ArgumentException("EF Core requires a DbTransaction.", nameof(transaction));
            await ctx.Database.UseTransactionAsync(dbTransaction, ct);
        }

        return ctx;
    }

    private static async Task<bool> SaveAffectsRowsAsync(DbContext ctx, CancellationToken ct)
    {
        try
        {
            return await ctx.SaveChangesAsync(ct) > 0;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false; // row not found: same result as Dommel
        }
    }

    /// <summary>Skip/Take requires a stable order: sort by primary key.</summary>
    private static IQueryable<TEntity> OrderByKey<TEntity>(DbContext ctx, IQueryable<TEntity> query)
        where TEntity : class
    {
        var key = ctx.Model.FindEntityType(typeof(TEntity))?.FindPrimaryKey()
            ?? throw new InvalidOperationException($"{typeof(TEntity).Name} has no primary key.");

        IOrderedQueryable<TEntity>? ordered = null;
        foreach (var property in key.Properties)
        {
            var name = property.Name;
            ordered = ordered is null
                ? query.OrderBy(e => EF.Property<object>(e, name))
                : ordered.ThenBy(e => EF.Property<object>(e, name));
        }
        return ordered!;
    }

    /// <summary>DbContext whose model only contains the requested type (+ what EF discovers through its navigations).</summary>
    private sealed class EntityContext : DbContext
    {
        public Type EntityType { get; }

        public EntityContext(DbContextOptions options, Type entityType) : base(options)
            => EntityType = entityType;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity(EntityType); // [Table], [Key], [Column]... are honored, as with Dommel
    }

    /// <summary>One model cached per entity type, instead of one per DbContext type.</summary>
    private sealed class EntityModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime)
            => context is EntityContext c
                ? (typeof(EntityContext), c.EntityType, designTime)
                : (context.GetType(), designTime);
    }
}
