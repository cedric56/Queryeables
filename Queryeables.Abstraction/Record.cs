namespace Queryeables.Abstraction;

internal class Record
{
    public required ChangeType type { get; set; }
    public required string database { get; set; }
    public required string table { get; set; }
    public required long rowid { get; set; }
}
