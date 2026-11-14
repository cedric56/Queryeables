using Queryeables.Abstraction;

namespace Queryeables.Remote.Offline;

/// <summary>A write made offline, waiting for the server.</summary>
/// <param name="Sent">
/// Sent at least once, without knowing whether the server applied it (answer lost, crash): it must reach the server as it
/// was first sent, under its id, so that the server recognizes it. Later writes on the row are queued after it, never
/// merged into it.
/// </param>
internal sealed record OutboxEntry(Guid Id, string Type, BatchOperationKind Kind, string Key, byte[] Payload, DateTime At, bool Sent = false);

/// <summary>
/// The queue of writes made offline, in order. Several writes on the same row are merged, so that the server receives the
/// final state: an update after an insert stays an insert, a delete after an insert cancels both. A write is merged into
/// the previous one on its row only when no write on another row came in between (the order of the writes is kept).
/// Stored entry by entry: a write saves its own entry, and the list of ids only when an entry comes or goes.
/// </summary>
internal sealed class Outbox
{
    private readonly List<OutboxEntry> _entries = [];
    private readonly HashSet<Guid> _changed = [];
    private HashSet<Guid> _stored = [];
    private bool _indexChanged;
    private bool _stagedLeft;

    // Recovered from the staged list at load (not in the index yet): staged again with the next new ones, until an index
    // names them. Otherwise a second stop, after the staged list is replaced and before the index, would lose them.
    private HashSet<Guid> _unindexed = [];

    public IReadOnlyList<OutboxEntry> Entries => _entries;

    public int Count => _entries.Count;

    public void Add(BatchOperationKind kind, string type, string key, byte[] payload)
    {
        var index = _entries.FindLastIndex(e => e.Type == type && e.Key == key);
        if (index < 0)
        {
            Append(kind, type, key, payload);
            return;
        }

        var pending = _entries[index];
        if (pending.Sent)
        {
            // Maybe applied already: merging would change a write the server may have recorded under this id, and the
            // change would be lost (the server answers a known id with its first result).
            Append(kind, type, key, payload);
            return;
        }

        if (index != _entries.Count - 1)
        {
            // Writes on other rows came in between, and may depend on the state this row had then (a row it now refers
            // to, a unique value it gave up, a child inserted under it): merging would send the final state before them, or
            // drop a row they need. Kept in order instead.
            Append(kind, type, key, payload);
            return;
        }

        switch (pending.Kind, kind)
        {
            case (BatchOperationKind.Insert, BatchOperationKind.Update):
                Replace(index, pending with { Payload = payload });          // still an insert, with the final content
                break;
            case (BatchOperationKind.Insert, BatchOperationKind.Delete):
                RemoveAt(index);                                              // never reached the server: nothing to send
                break;
            case (BatchOperationKind.Update, BatchOperationKind.Update):
                Replace(index, pending with { Payload = payload });
                break;
            case (BatchOperationKind.Update, BatchOperationKind.Delete):
                RemoveAt(index);
                Append(kind, type, key, payload);
                break;
            default:
                Append(kind, type, key, payload);
                break;
        }
    }

    /// <summary>About to be sent: from now on, it is never merged with a later write (see <see cref="OutboxEntry.Sent"/>).</summary>
    /// <returns>true if it was not marked yet (and must be saved before sending).</returns>
    public bool MarkSent(Guid id)
    {
        var index = _entries.FindIndex(e => e.Id == id);
        if (index < 0 || _entries[index].Sent)
            return false;
        Replace(index, _entries[index] with { Sent = true });
        return true;
    }

    public OutboxEntry? Find(Guid id) => _entries.Find(e => e.Id == id);

    /// <summary>
    /// The row inserted under the temporary <paramref name="temporaryKey"/> got <paramref name="serverKey"/>: the writes
    /// queued after it on that row now name the server's row (<paramref name="rebase"/> rewrites their content).
    /// </summary>
    public void Rekey(string type, string temporaryKey, string serverKey, Func<byte[], byte[]> rebase)
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            if (entry.Type == type && entry.Key == temporaryKey)
                Replace(i, entry with { Key = serverKey, Payload = rebase(entry.Payload) });
        }
    }

    /// <summary>Rewrites the content of the entries <paramref name="rewrite"/> gives a new content for (null: unchanged).</summary>
    public void Rewrite(Func<OutboxEntry, byte[]?> rewrite)
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            if (rewrite(_entries[i]) is { } payload)
                Replace(i, _entries[i] with { Payload = payload });
        }
    }

    public void Remove(Guid id)
    {
        var index = _entries.FindIndex(e => e.Id == id);
        if (index >= 0)
            RemoveAt(index);
    }

    public List<OutboxEntry> Snapshot() => [.. _entries];

    /// <summary>Back to <paramref name="entries"/> (a rolled-back transaction): only the entries that differ are saved again.</summary>
    public void Restore(IEnumerable<OutboxEntry> entries)
    {
        var before = _entries.ToDictionary(e => e.Id);
        _entries.Clear();
        _entries.AddRange(entries);
        foreach (var entry in _entries)
        {
            if (!before.Remove(entry.Id, out var current) || !ReferenceEquals(current, entry))
                _changed.Add(entry.Id);
        }
        _indexChanged = true;
    }

    public async Task LoadAsync(IOfflineStorage storage, string prefix, CancellationToken ct)
    {
        _entries.Clear();
        _changed.Clear();
        _indexChanged = false;
        var ids = OfflineCodec.ReadStrings(await storage.ReadAsync($"{prefix}/outbox/index", ct)).ToList();
        var indexed = ids.ToHashSet();
        // Entries written before a stop that came before the index: still in the queue, after the others, in their order.
        foreach (var staged in OfflineCodec.ReadStrings(await storage.ReadAsync(StagedKey(prefix), ct)))
        {
            if (!ids.Contains(staged))
                ids.Add(staged);
        }
        foreach (var id in ids)
        {
            if (OfflineCodec.ReadEntries(await storage.ReadAsync(EntryKey(prefix, id), ct)) is [var entry])
                _entries.Add(entry);
        }
        _stored = _entries.Select(e => e.Id).ToHashSet();
        _unindexed = _entries.Where(e => !indexed.Contains(e.Id.ToString("N"))).Select(e => e.Id).ToHashSet();
        // What was recovered from the staged list belongs to the index from now on: the next save writes it, then clears it.
        _stagedLeft = await storage.ReadAsync(StagedKey(prefix), ct) is not null;
        _indexChanged = _stagedLeft;
    }

    /// <summary>
    /// Writes what changed, so that a stop between any two writes loses nothing: the ids of the new entries first (the
    /// staged list), then the entries, then the index, then the removals, and the staged list last. A load reads the index
    /// and the staged list: an entry written but not yet indexed is still found.
    /// </summary>
    public async Task SaveAsync(IOfflineStorage storage, string prefix, CancellationToken ct)
    {
        var current = _entries.ToDictionary(e => e.Id);
        var added = _entries.Where(e => !_stored.Contains(e.Id) || _unindexed.Contains(e.Id)).Select(e => e.Id.ToString("N")).ToList();
        if (added.Count > 0)
            await storage.WriteAsync(StagedKey(prefix), OfflineCodec.WriteStrings(added), ct);
        foreach (var id in _changed)
        {
            if (current.TryGetValue(id, out var entry))
                await storage.WriteAsync(EntryKey(prefix, id.ToString("N")), OfflineCodec.Write([entry]), ct);
        }
        if (_indexChanged)
            await storage.WriteAsync($"{prefix}/outbox/index", OfflineCodec.WriteStrings(_entries.Select(e => e.Id.ToString("N"))), ct);
        // Removed after the index no longer names them: an index never names a missing entry.
        foreach (var id in _stored.Where(id => !current.ContainsKey(id)).ToList())
            await storage.DeleteAsync(EntryKey(prefix, id.ToString("N")), ct);
        if (added.Count > 0 || _stagedLeft)
            await storage.DeleteAsync(StagedKey(prefix), ct);
        _stagedLeft = false;

        _stored = [.. current.Keys];
        if (_indexChanged)
            _unindexed.Clear();
        _changed.Clear();
        _indexChanged = false;
    }

    private static string EntryKey(string prefix, string id) => $"{prefix}/outbox/{id}";

    private static string StagedKey(string prefix) => $"{prefix}/outbox/staged";

    private void Append(BatchOperationKind kind, string type, string key, byte[] payload)
    {
        var entry = new OutboxEntry(Guid.NewGuid(), type, kind, key, payload, DateTime.UtcNow);
        _entries.Add(entry);
        _changed.Add(entry.Id);
        _indexChanged = true;
    }

    private void Replace(int index, OutboxEntry entry)
    {
        _entries[index] = entry;
        _changed.Add(entry.Id);
    }

    private void RemoveAt(int index)
    {
        _changed.Remove(_entries[index].Id);
        _entries.RemoveAt(index);
        _indexChanged = true;
    }
}

/// <summary>Binary form of the queue, of the conflicts and of the state: no serializer contract to register.</summary>
internal static class OfflineCodec
{
    private const byte Version = 1;

    // The queue's own format: 2 adds OutboxEntry.Sent. A queue written by version 1 is still read (nothing was sent).
    private const byte OutboxVersion = 2;

    public static byte[] Write(IReadOnlyList<OutboxEntry> entries)
        => Write(writer =>
        {
            writer.Write(entries.Count);
            foreach (var entry in entries)
            {
                WriteEntry(writer, entry.Id, entry.Type, entry.Kind, entry.Key, entry.Payload, entry.At);
                writer.Write(entry.Sent);
            }
        }, OutboxVersion);

    public static List<OutboxEntry> ReadEntries(byte[]? data)
        => Read(data, (reader, version) =>
        {
            var list = new List<OutboxEntry>();
            for (var count = reader.ReadInt32(); count > 0; count--)
            {
                var (id, type, kind, key, payload, at) = ReadEntry(reader);
                var sent = version >= 2 && reader.ReadBoolean();
                list.Add(new OutboxEntry(id, type, kind, key, payload, at, sent));
            }
            return list;
        }, Version, OutboxVersion) ?? [];

    public static byte[] Write(IReadOnlyList<OfflineConflict> conflicts)
        => Write(writer =>
        {
            writer.Write(conflicts.Count);
            foreach (var conflict in conflicts)
            {
                WriteEntry(writer, conflict.Id, conflict.Type, conflict.Kind, conflict.Key, conflict.Payload, conflict.At);
                writer.Write((int)conflict.ErrorCode);
                writer.Write(conflict.Message);
            }
        });

    public static List<OfflineConflict> ReadConflicts(byte[]? data)
        => Read(data, reader =>
        {
            var list = new List<OfflineConflict>();
            for (var count = reader.ReadInt32(); count > 0; count--)
            {
                var (id, type, kind, key, payload, at) = ReadEntry(reader);
                list.Add(new OfflineConflict(id, type, kind, key, payload, (EntityErrorCode)reader.ReadInt32(), reader.ReadString(), at));
            }
            return list;
        }) ?? [];

    public static byte[] Write(OfflineState state)
        => Write(writer =>
        {
            writer.Write((byte)state.Mode);
            writer.Write(state.SnapshotTakenAt?.UtcTicks ?? 0L);
            writer.Write(state.NextTemporaryKey);
        });

    public static OfflineState ReadState(byte[]? data)
        => Read(data, reader =>
        {
            var mode = (ConnectionMode)reader.ReadByte();
            var ticks = reader.ReadInt64();
            return new OfflineState(mode, ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero), reader.ReadInt64());
        }) ?? new OfflineState(ConnectionMode.Online, null, -1);

    public static byte[] WriteStrings(IEnumerable<string> values)
        => Write(writer =>
        {
            var list = values.ToList();
            writer.Write(list.Count);
            foreach (var value in list)
                writer.Write(value);
        });

    public static List<string> ReadStrings(byte[]? data)
        => Read(data, reader =>
        {
            var list = new List<string>();
            for (var count = reader.ReadInt32(); count > 0; count--)
                list.Add(reader.ReadString());
            return list;
        }) ?? [];

    private static void WriteEntry(BinaryWriter writer, Guid id, string type, BatchOperationKind kind, string key, byte[] payload, DateTime at)
    {
        writer.Write(id.ToByteArray());
        writer.Write(type);
        writer.Write((byte)kind);
        writer.Write(key);
        writer.Write(payload.Length);
        writer.Write(payload);
        writer.Write(at.Ticks);
    }

    private static (Guid, string, BatchOperationKind, string, byte[], DateTime) ReadEntry(BinaryReader reader)
    {
        var id = new Guid(reader.ReadBytes(16));
        var type = reader.ReadString();
        var kind = (BatchOperationKind)reader.ReadByte();
        var key = reader.ReadString();
        var payload = reader.ReadBytes(reader.ReadInt32());
        var at = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
        return (id, type, kind, key, payload, at);
    }

    private static byte[] Write(Action<BinaryWriter> write, byte version = Version)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(version);
            write(writer);
        }
        return stream.ToArray();
    }

    private static T? Read<T>(byte[]? data, Func<BinaryReader, T> read) where T : class
        => Read(data, (reader, _) => read(reader), Version, Version);

    /// <param name="read">Reads the content, knowing the version it was written with (between the two bounds).</param>
    private static T? Read<T>(byte[]? data, Func<BinaryReader, byte, T> read, byte oldest, byte newest) where T : class
    {
        if (data is not { Length: > 0 })
            return null;

        using var reader = new BinaryReader(new MemoryStream(data));
        var version = reader.ReadByte();
        return version >= oldest && version <= newest
            ? read(reader, version)
            : throw new InvalidDataException("The offline data was written by another version of Queryeables.");
    }
}

/// <summary>What survives a restart besides the data: the mode, the age of the copy, the next temporary key.</summary>
internal sealed record OfflineState(ConnectionMode Mode, DateTimeOffset? SnapshotTakenAt, long NextTemporaryKey);
