namespace Queryeables.Remote.Offline;

/// <summary>
/// A deterministic logical timestamp. ReplicaId and OperationId break ties without relying on wall-clock time.
/// </summary>
public readonly record struct CrdtStamp(long Counter, string ReplicaId, Guid OperationId) : IComparable<CrdtStamp>
{
    public int CompareTo(CrdtStamp other)
    {
        var order = Counter.CompareTo(other.Counter);
        if (order != 0) return order;
        order = StringComparer.Ordinal.Compare(ReplicaId, other.ReplicaId);
        return order != 0 ? order : OperationId.CompareTo(other.OperationId);
    }
}

/// <summary>
/// A Lamport clock scoped to one replica. Observe remote stamps before creating subsequent local operations.
/// Persist both ReplicaId and Counter with the replica's offline metadata.
/// </summary>
public sealed class CrdtClock
{
    private long _counter;

    public CrdtClock(string replicaId, long counter = 0)
    {
        if (string.IsNullOrWhiteSpace(replicaId))
            throw new ArgumentException("A stable replica id is required.", nameof(replicaId));
        if (counter < 0)
            throw new ArgumentOutOfRangeException(nameof(counter));
        ReplicaId = replicaId;
        _counter = counter;
    }

    public string ReplicaId { get; }
    public long Counter => Interlocked.Read(ref _counter);

    public CrdtStamp Tick()
        => new(Interlocked.Increment(ref _counter), ReplicaId, Guid.NewGuid());

    public void Observe(CrdtStamp remote)
    {
        var observed = Interlocked.Read(ref _counter);
        while (observed < remote.Counter)
        {
            var prior = Interlocked.CompareExchange(ref _counter, remote.Counter, observed);
            if (prior == observed) break;
            observed = prior;
        }
    }
}

/// <summary>
/// Last-writer-wins register. Merge is associative, commutative and idempotent.
/// Use a stable replica id and operation id; never use DateTime as the ordering key.
/// </summary>
public sealed record CrdtRegister<T>(T? Value, CrdtStamp Stamp, bool IsDeleted = false)
{
    public static CrdtRegister<T> Create(T? value, CrdtStamp stamp)
        => new(value, stamp);

    public static CrdtRegister<T> Deleted(CrdtStamp stamp)
        => new(default, stamp, true);

    public CrdtRegister<T> Merge(CrdtRegister<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Stamp.CompareTo(other.Stamp) >= 0 ? this : other;
    }
}

/// <summary>
/// A state-based positive/negative counter (PN-counter). Merge takes the per-replica maximum,
/// so duplicate and out-of-order state delivery cannot double-count increments.
/// </summary>
public sealed class CrdtCounter
{
    private readonly Dictionary<string, long> _positive;
    private readonly Dictionary<string, long> _negative;

    public CrdtCounter()
        : this(new Dictionary<string, long>(StringComparer.Ordinal), new Dictionary<string, long>(StringComparer.Ordinal))
    {
    }

    private CrdtCounter(Dictionary<string, long> positive, Dictionary<string, long> negative)
    {
        _positive = positive;
        _negative = negative;
    }

    public long Value => checked(_positive.Values.Sum() - _negative.Values.Sum());

    public void Increment(string replicaId, long amount = 1) => Add(_positive, replicaId, amount);
    public void Decrement(string replicaId, long amount = 1) => Add(_negative, replicaId, amount);

    public CrdtCounter Merge(CrdtCounter other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new CrdtCounter(MergeComponents(_positive, other._positive), MergeComponents(_negative, other._negative));
    }

    public IReadOnlyDictionary<string, long> PositiveComponents => new Dictionary<string, long>(_positive);
    public IReadOnlyDictionary<string, long> NegativeComponents => new Dictionary<string, long>(_negative);

    private static void Add(Dictionary<string, long> component, string replicaId, long amount)
    {
        if (string.IsNullOrWhiteSpace(replicaId)) throw new ArgumentException("A stable replica id is required.", nameof(replicaId));
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        component[replicaId] = checked(component.GetValueOrDefault(replicaId) + amount);
    }

    private static Dictionary<string, long> MergeComponents(Dictionary<string, long> left, Dictionary<string, long> right)
    {
        var result = new Dictionary<string, long>(left, StringComparer.Ordinal);
        foreach (var (replica, value) in right)
            result[replica] = Math.Max(result.GetValueOrDefault(replica), value);
        return result;
    }
}

/// <summary>
/// An observed-remove set (OR-Set). Remove only cancels add-tags observed by this replica;
/// a concurrent add therefore survives. Keep tombstones until every replica has acknowledged them.
/// </summary>
public sealed class CrdtSet<T> where T : notnull
{
    private readonly Dictionary<T, HashSet<Guid>> _adds;
    private readonly HashSet<Guid> _removed;

    public CrdtSet(IEqualityComparer<T>? comparer = null)
    {
        _adds = new Dictionary<T, HashSet<Guid>>(comparer);
        _removed = [];
    }

    private CrdtSet(Dictionary<T, HashSet<Guid>> adds, HashSet<Guid> removed)
    {
        _adds = adds;
        _removed = removed;
    }

    public IReadOnlyCollection<T> Values
        => _adds.Where(pair => pair.Value.Any(tag => !_removed.Contains(tag))).Select(pair => pair.Key).ToArray();

    public void Add(T value)
    {
        if (!_adds.TryGetValue(value, out var tags))
            _adds[value] = tags = [];
        tags.Add(Guid.NewGuid());
    }

    public void Remove(T value)
    {
        if (_adds.TryGetValue(value, out var tags))
            _removed.UnionWith(tags);
    }

    public CrdtSet<T> Merge(CrdtSet<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var adds = new Dictionary<T, HashSet<Guid>>(_adds.Comparer);
        foreach (var (value, tags) in _adds)
            adds[value] = [.. tags];
        foreach (var (value, tags) in other._adds)
        {
            if (!adds.TryGetValue(value, out var merged))
                adds[value] = merged = [];
            merged.UnionWith(tags);
        }
        var removed = new HashSet<Guid>(_removed);
        removed.UnionWith(other._removed);
        return new CrdtSet<T>(adds, removed);
    }
}
