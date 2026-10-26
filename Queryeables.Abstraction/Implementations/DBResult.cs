using MessagePack;

namespace Queryeables.Abstraction;

[MessagePackObject]
public partial class DBResult
{
    [Key(0)]
    public string? Exception { get; set; }

    [Key(1)]
    public bool Successful { get; set; }

    [Key(2)]
    public byte[]? Data { get; set; }
}
