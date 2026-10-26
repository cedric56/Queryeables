using Microsoft.EntityFrameworkCore;
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
/// CRUD EF Core directement sur IDbConnection, avec la même forme que Dommel.
/// Chaque appel crée un DbContext léger sur la connexion (et la transaction) fournie ;
/// le modèle EF de chaque type d'entité est construit une seule fois puis mis en cache.
///
/// Au démarrage de l'application :
///     EfConnectionExtensions.UseProvider((b, c) => b.UseSqlite(c));
///     // ou b.UseSqlServer(c), b.UseNpgsql(c), ...
/// </summary>
public static class EfConnectionExtensions
{
    private static Action<DbContextOptionsBuilder, DbConnection>? _configureProvider;

    /// <summary>Indique quel fournisseur EF utiliser pour une connexion donnée.</summary>
    public static void UseProvider(Action<DbContextOptionsBuilder, DbConnection> configure)
        => _configureProvider = configure ?? throw new ArgumentNullException(nameof(configure));

    // ---------- Lecture ----------

    public static async Task<IEnumerable<TEntity>> SelectAsync<TEntity>(this IDbConnection connection,
        Expression<Func<TEntity, bool>> predicate, IDbTransaction? transaction = null,
        CancellationToken cancellationToken = default) where TEntity : class
    {
        await using var ctx = await CreateContextAsync<TEntity>(connection, transaction, cancellationToken);
        // Matérialisé avant la fermeture du contexte.
        return await ctx.Set<TEntity>().Where(predicate).ToListAsync(cancellationToken);
    }

    public static async Task<TEntity?> FirstOrDefaultAsync<TEntity>(this IDbConnection connection,
        Expression<Func<TEntity, bool>> predicate, IDbTransaction? transaction = null,
        CancellationToken cancellationToken = default) where TEntity : class
    {
        await using var ctx = await CreateContextAsync<TEntity>(connection, transaction, cancellationToken);
        return await ctx.Set<TEntity>().FirstOrDefaultAsync(predicate, cancellationToken);
    }

    /// <param name="pageNumber">Commence à 1, comme Dommel.</param>
    public static async Task<IEnumerable<TEntity>> SelectPagedAsync<TEntity>(this IDbConnection connection,
        Expression<Func<TEntity, bool>> predicate, int pageNumber, int pageSize,
        IDbTransaction? transaction = null, CancellationToken cancellationToken = default) where TEntity : class
    {
        if (pageNumber < 1) throw new ArgumentOutOfRangeException(nameof(pageNumber), "La première page est 1.");
        if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));

        await using var ctx = await CreateContextAsync<TEntity>(connection, transaction, cancellationToken);
        return await OrderByKey(ctx, ctx.Set<TEntity>().Where(predicate))
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
    }

    // ---------- Écriture ----------

    /// <summary>Insère l'entité et renvoie l'ID (également réinjecté dans l'entité par EF).</summary>
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
            : null; // clé composite : les valeurs sont dans l'entité
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

    // ---------- Interne ----------

    private static async Task<EntityContext> CreateContextAsync<TEntity>(IDbConnection connection,
        IDbTransaction? transaction, CancellationToken ct) where TEntity : class
    {
        var dbConnection = connection as DbConnection
            ?? throw new ArgumentException("EF Core nécessite un DbConnection.", nameof(connection));
        var configure = _configureProvider
            ?? throw new InvalidOperationException(
                "Fournisseur EF non configuré : appelez EfConnectionExtensions.UseProvider(...) au démarrage.");

        var builder = new DbContextOptionsBuilder();
        configure(builder, dbConnection); // EF ne dispose pas une connexion qu'on lui prête
        builder.ReplaceService<IModelCacheKeyFactory, EntityModelCacheKeyFactory>();
        builder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);

        var ctx = new EntityContext(builder.Options, typeof(TEntity));

        if (transaction != null)
        {
            var dbTransaction = transaction as DbTransaction
                ?? throw new ArgumentException("EF Core nécessite un DbTransaction.", nameof(transaction));
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
            return false; // ligne introuvable : même résultat que Dommel
        }
    }

    /// <summary>Skip/Take exige un ordre stable : tri par clé primaire.</summary>
    private static IQueryable<TEntity> OrderByKey<TEntity>(DbContext ctx, IQueryable<TEntity> query)
        where TEntity : class
    {
        var key = ctx.Model.FindEntityType(typeof(TEntity))?.FindPrimaryKey()
            ?? throw new InvalidOperationException($"{typeof(TEntity).Name} n'a pas de clé primaire.");

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

    /// <summary>DbContext dont le modèle ne contient que le type demandé (+ ce qu'EF découvre via ses navigations).</summary>
    private sealed class EntityContext : DbContext
    {
        public Type EntityType { get; }

        public EntityContext(DbContextOptions options, Type entityType) : base(options)
            => EntityType = entityType;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity(EntityType); // [Table], [Key], [Column]... sont respectés comme avec Dommel
    }

    /// <summary>Un modèle mis en cache par type d'entité, au lieu d'un seul par type de DbContext.</summary>
    private sealed class EntityModelCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime)
            => context is EntityContext c
                ? (typeof(EntityContext), c.EntityType, designTime)
                : (context.GetType(), designTime);
    }
}
