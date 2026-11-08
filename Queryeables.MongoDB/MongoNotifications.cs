namespace Queryeables.MongoDB;

/// <summary>The collections whose changes a <see cref="MongoChangeListener"/> reports to the change feeds.</summary>
public sealed class MongoNotifications
{
    private readonly List<string> _collections = [];

    /// <summary>The watched collections, named as the change feeds name their tables.</summary>
    public IReadOnlyList<string> Collections => _collections;

    /// <summary>The first wait before opening the change stream again after a failure; it doubles up to 30 seconds.</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Reports the changes of the collection of <typeparamref name="TEntity"/> ([Table] name, otherwise the type name).</summary>
    public MongoNotifications Watch<TEntity>() where TEntity : class => Watch(MongoCollections.NameOf(typeof(TEntity)));

    public MongoNotifications Watch(string collection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        if (!_collections.Contains(collection, StringComparer.Ordinal))
            _collections.Add(collection);
        return this;
    }
}
