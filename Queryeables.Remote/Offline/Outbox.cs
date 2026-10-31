using Queryeables.Abstraction;

namespace Queryeables.Remote.Offline;

/// <summary>A write made offline, waiting for the server.</summary>
internal sealed record OutboxEntry(Guid Id, string Type, BatchOperationKind Kind, string Key, byte[] Payload, DateTime At);

/// <summary>
/// The queue of writes made offline, in order. Several writes on the same row are merged, so that the server receives the
/// final state: an update after an insert stays an insert, a delete after an insert cancels both.
/// Stored entry by entry: a write saves its own entry, and the list of ids only when an entry comes or goes.
/// </summary>
internal sealed class Outbox
{
    private readonly List<OutboxEntry> _entries = [];
    private readonly HashSet<Guid> _changed = [];
    private HashSet<Guid> _stored = [];
    private bool _indexChanged;

    public IReadOnlyList<OutboxEntry> Entries => _entries;

    public int Count => _entries.Count;

    public void Add(BatchOperationKind kind, string type, string key, byte[] payload)
    {
        var index = _entries.FindIndex(e => e.Type == type && e.Key == key);
        if (index < 0)
        {
            Append(kind, type, key, payload);
            return;
        }

        var pending = _entries[index];
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
        var ids = OfflineCodec.ReadStrings(await storage.ReadAsync($"{prefix}/outbox/index", ct));
        foreach (var id in ids)
        {
            if (OfflineCodec.ReadEntries(await storage.ReadAsync(EntryKey(prefix, id), ct)) is [var entry])
                _entries.Add(entry);
        }
        _stored = _entries.Select(e => e.Id).ToHashSet();
    }

    public async Task SaveAsync(IOfflineStorage storage, string prefix, CancellationToken ct)
    {
        var current = _entries.ToDictionary(e => e.Id);
        foreach (var id in _changed)
        {
            if (current.TryGetValue(id, out var entry))
                await storage.WriteAsync(EntryKey(prefix, id.ToString("N")), OfflineCodec.Write([entry]), ct);
        }
        foreach (var id in _stored.Where(id => !current.ContainsKey(id)).ToList())
            await storage.DeleteAsync(EntryKey(prefix, id.ToString("N")), ct);
        if (_indexChanged)
            await storage.WriteAsync($"{prefix}/outbox/index", OfflineCodec.WriteStrings(_entries.Select(e => e.Id.ToString("N"))), ct);

        _stored = [.. current.Keys];
        _changed.Clear();
        _indexChanged = false;
    }

    private static string EntryKey(string prefix, string id) => $"{prefix}/outbox/{id}";

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

    public static byte[] Write(IReadOnlyList<OutboxEntry> entries)
        => Write(writer =>
        {
            writer.Write(entries.Count);
            foreach (var entry in entries)
                WriteEntry(writer, entry.Id, entry.Type, entry.Kind, entry.Key, entry.Payload, entry.At);
        });

    public static List<OutboxEntry> ReadEntries(byte[]? data)
        => Read(data, reader =>
        {
            var list = new List<OutboxEntry>();
            for (var count = reader.ReadInt32(); count > 0; count--)
            {
                var (id, type, kind, key, payload, at) = ReadEntry(reader);
                list.Add(new OutboxEntry(id, type, kind, key, payload, at));
            }
            return list;
        }) ?? [];

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

    private static byte[] Write(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Version);
            write(writer);
        }
        return stream.ToArray();
    }

    private static T? Read<T>(byte[]? data, Func<BinaryReader, T> read) where T : class
    {
        if (data is not { Length: > 0 })
            return null;

        using var reader = new BinaryReader(new MemoryStream(data));
        return reader.ReadByte() == Version
            ? read(reader)
            : throw new InvalidDataException("The offline data was written by another version of Queryeables.");
    }
}

/// <summary>What survives a restart besides the data: the mode, the age of the copy, the next temporary key.</summary>
internal sealed record OfflineState(ConnectionMode Mode, DateTimeOffset? SnapshotTakenAt, long NextTemporaryKey);
