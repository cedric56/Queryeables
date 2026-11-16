using BenchmarkDotNet.Attributes;
using Queryeables.Remote;

namespace Queryeables.Benchmarks;

/// <summary>
/// The serializers alone, on what a replica receives: 1000 items (a load) or one (a row read again on a
/// notification). The size of each message is printed at setup: what the network carries.
/// </summary>
[MemoryDiagnoser]
public class SerializerBenchmarks
{
    private IEntitySerializer _serializer = null!;
    private Item[] _items = [];
    private byte[] _itemsBytes = [];

    [Params(1, 1000)]
    public int Rows { get; set; }

    [Params(WireFormat.Json, WireFormat.MessagePack)]
    public WireFormat Format { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _serializer = Format.Serializer();
        _items = Enumerable.Range(1, Rows).Select(i => new Item { Id = i, Name = $"Item {i}", Quantity = i % 100 }).ToArray();
        _itemsBytes = _serializer.Serialize(_items);
        Console.WriteLine($"// {Format}, {Rows} row(s): {_itemsBytes.Length:N0} bytes");
    }

    [Benchmark]
    public byte[] Serialize() => _serializer.Serialize(_items);

    [Benchmark]
    public Item[]? Deserialize() => _serializer.Deserialize<Item[]>(_itemsBytes);
}
