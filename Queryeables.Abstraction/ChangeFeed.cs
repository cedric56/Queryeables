using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;

namespace Queryeables.Abstraction;

/// <summary>
/// Change change feed without an entity type: this is what the notification source sees
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
    /// Name of the monitored table. Defaults to the name given by [Table] on the entity type, otherwise the type name.
    /// </param>
    public ChangeFeed(string? table = null)
        => Table = table ?? typeof(TEntity).GetCustomAttribute<TableAttribute>()?.Name ?? typeof(TEntity).Name;

    /// <summary>Name of the monitored table.</summary>
    public string Table { get; }

    public event EventHandler<EntityChange<TEntity>>? ChangeReceived;

    public void Publish(EntityChange<TEntity> record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ChangeReceived?.Invoke(this, record);
    }

    public void PublishResync() => Publish(new EntityChange<TEntity>(string.Empty, ChangeType.Update, []));

    public void Publish(ChangeType type, string table, string entityKey, string? connectionId)
    {
        if (!string.Equals(table, Table, StringComparison.OrdinalIgnoreCase))
            return;

        List<string> connections = string.IsNullOrWhiteSpace(connectionId) ? [] : [connectionId];
        Publish(new EntityChange<TEntity>(entityKey, type, connections));
    }
}
