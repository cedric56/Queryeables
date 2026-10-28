using Queryeables.Abstraction;

namespace Queryeables.Tests.Unit;

public class RecordKeyTests
{
    public enum Kind { A, B }

    private static void AssertRoundTrip<TKey>(TKey key) where TKey : notnull
        => Assert.Equal(key, DbRecordKey.Parse<TKey>(DbRecordKey.Format(key)));

    [Fact] public void Long() => AssertRoundTrip(42L);
    [Fact] public void Negative_int() => AssertRoundTrip(-7);
    [Fact] public void Guid_key() => AssertRoundTrip(Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"));
    [Fact] public void String_key() => AssertRoundTrip("CLI-001");
    [Fact] public void Enum_key() => AssertRoundTrip(Kind.B);
    [Fact] public void Decimal_key() => AssertRoundTrip(12.5m);
    [Fact] public void Date_keeps_milliseconds_and_kind() => AssertRoundTrip(new DateTime(2026, 10, 26, 9, 55, 1, 123, DateTimeKind.Utc));
    [Fact] public void Composite_key() => AssertRoundTrip((7, 2));
    [Fact] public void Composite_key_with_string_and_guid() => AssertRoundTrip(("FR", Guid.Empty, 3L));

    [Fact]
    public void Long_is_formatted_without_culture()
        => Assert.Equal("1234567", DbRecordKey.Format(1234567L));

    [Fact]
    public void Wrong_number_of_parts_is_rejected()
        => Assert.Throws<FormatException>(() => DbRecordKey.Parse<(int, int)>("1"));

    [Fact]
    public void Record_from_rowid_uses_the_same_format()
    {
        var record = new DbRecord<TestClient>(42L, ChangeType.Update);
        Assert.Equal(42L, DbRecordKey.Parse<long>(record.EntityKey));
    }
}
