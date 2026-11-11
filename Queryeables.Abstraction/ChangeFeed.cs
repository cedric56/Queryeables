using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;

namespace Queryeables.Abstraction;

/// <summary>
/// Change feed without an entity type: this is what the notification source sees
/// (SQLite update hook, trigger...), through <see cref="ChangeFeedRegistry"/>.
/// </summary>
public interface IChangeFeed
{
    /// <summary>
    /// Reports that a row changed. The change feed ignores the tables it does not care about.
    /// </summary>
    /// <param name="entityKey">Row key, in the <see cref="EntityKey.Format"/> format.</param>
    void Publish(ChangeType type, string table, string entityKey, string? connectionId);

    /// <summary>
    /// Reports that some changes may have been missed (a change source that reconnected): subscribers synchronize. A
    /// record with an empty key means exactly that; <see cref="EntityCache{TEntity, TKey}"/> then calls its synchronization.
    /// </summary>
    void PublishResync()
    {
    }

    /// <summary>
    /// Reports that the source of the data itself changed (the offline copy instead of the server, another user): what
    /// subscribers hold may not exist there at all, so they reload everything rather than catch up. A record with an empty
    /// key and <see cref="ChangeType.None"/> means exactly that.
    /// </summary>
    void PublishReload()
    {
    }
}

/// <summary>Change feed of one entity type: its subscribers (for example <see cref="EntityCache{TEntity, TKey}"/>) react to changes.</summary>
public interface IChangeFeed<TEntity> : IChangeFeed where TEntity : class
{
    event EventHandler<EntityChange<TEntity>>? ChangeReceived;

    /// <summary>Raises <see cref="ChangeReceived"/> with a change received as is (for example from a hub).</summary>
    void Publish(EntityChange<TEntity> record);
}

/// <summary>
/// Standard implementation. Server side, the change source calls
/// <see cref="Publish(ChangeType, string, string, string?)"/>; client side, the hub receiver calls
/// <see cref="Publish(EntityChange{TEntity})"/>.
/// </summary>
public sealed class ChangeFeed<TEntity> : IChangeFeed<TEntity> where TEntity : class
{
    /// <param name="table">
    /// Name of the monitored table. Defaults to the name given by [Table] on the entity type (with its schema:
    /// <c>sales.Orders</c>), otherwise the type name.
    /// </param>
    /// <remarks>
    /// Without a name or [Table], the feed also answers to the plural Dommel gives the type by default (Person → Persons,
    /// Category → Categories): the table Dommel creates and writes. A table named otherwise (EF Core's DbSet name...)
    /// needs [Table].
    /// </remarks>
    public ChangeFeed(string? table = null)
    {
        var attribute = typeof(TEntity).GetCustomAttribute<TableAttribute>();
        if (table is not null)
        {
            Table = table;
            _names = [table];
        }
        else if (attribute is null)
        {
            Table = typeof(TEntity).Name;
            _names = [Table, DommelPlural(Table)];
        }
        else if (attribute.Schema is { Length: > 0 } schema)
        {
            // A source that knows schemas (PostgreSQL) reports "schema.table"; one that does not (SQLite), the name only.
            Table = $"{schema}.{attribute.Name}";
            _names = [Table, attribute.Name];
        }
        else
        {
            Table = attribute.Name;
            _names = [Table];
        }
    }

    private readonly string[] _names;

    /// <summary>Name of the monitored table.</summary>
    public string Table { get; }

    /// <summary>Dommel's default table name: Product → Products, Category → Categories, Address → Address.</summary>
    private static string DommelPlural(string name)
        => name.EndsWith('y') || name.EndsWith('Y') ? name[..^1] + "ies"
            : name.EndsWith('s') || name.EndsWith('S') ? name
            : name + "s";

    public event EventHandler<EntityChange<TEntity>>? ChangeReceived;

    public void Publish(EntityChange<TEntity> record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ChangeReceived?.Invoke(this, record);
    }

    public void PublishResync() => Publish(new EntityChange<TEntity>(string.Empty, ChangeType.Update, []));

    public void PublishReload() => Publish(new EntityChange<TEntity>(string.Empty, ChangeType.None, []));

    public void Publish(ChangeType type, string table, string entityKey, string? connectionId)
    {
        if (!Array.Exists(_names, name => string.Equals(table, name, StringComparison.OrdinalIgnoreCase)))
            return;

        List<string> connections = string.IsNullOrWhiteSpace(connectionId) ? [] : [connectionId];
        Publish(new EntityChange<TEntity>(entityKey, type, connections));
    }
}
