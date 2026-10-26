using MessagePack;
using Queryeables.Abstraction;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;

namespace Queryeables.Onion;

public record DBAlias(string alias, string columnName);

public partial class MagicOnionLayer(IMagicOnionLayer layer, int? defaultCommandTimeout = null)
{
    public Dictionary<Type, string> Sessions { get; } = new();

    private async Task<T> InvokeAsync<T>(
        Func<IMagicOnionLayer, Task<DBResult>> call,
        Func<byte[], T> deserialize,
        T empty,
        CancellationToken cancellationToken = default)
    {
        var result = await call(layer.WithCancellationToken(cancellationToken))
            .ConfigureAwait(false);

        if (!result.Successful)
            throw new InvalidOperationException(result.Exception ?? "RPC failed");

        if (result.Data is null || result.Data.Length == 0 || result.Data == Array.Empty<byte>())
            return empty;

        return deserialize(result.Data);
    }

    private static IEnumerable<T> DeserializeMany<T>(byte[] data) =>
        MessagePackSerializer.Deserialize<IEnumerable<T>>(data) ?? [];

    private static T? DeserializeOne<T>(byte[] data) =>
        MessagePackSerializer.Deserialize<T>(data);

    private static int DeserializeInt(byte[] data) =>
        MessagePackSerializer.Deserialize<int>(data);

    private static IEnumerable<IDictionary<string, object>> DeserializeRows(byte[] data) =>
        MessagePackSerializer.Deserialize<IEnumerable<IDictionary<string, object>>>(data) ?? [];

    public virtual Task<DBResult> RemoveAsync<T>(T transactional, CancellationToken cancellationToken = default) where T : class
    {
        Sessions.TryGetValue(transactional.GetType(), out var connectionId);
        return InvokeRawAsync(async s => await s.Remove(
            transactional.GetType().Name,
            MessagePackSerializer.Serialize<T>(transactional)
            , connectionId ?? string.Empty, defaultCommandTimeout), cancellationToken);
    }

    public virtual Task<DBResult> SaveAsync<T>(T transactional, CancellationToken cancellationToken = default) where T : class
    {
        Sessions.TryGetValue(transactional.GetType(), out var connectionId);
        return InvokeRawAsync(async s => await s.Save(
            transactional.GetType().Name,
            MessagePackSerializer.Serialize<T>(transactional)
            , connectionId ?? string.Empty, defaultCommandTimeout), cancellationToken);
    }

    private async Task<DBResult> InvokeRawAsync(Func<IMagicOnionLayer, Task<DBResult>> call, CancellationToken cancellationToken = default)
    {
        return await call(layer.WithCancellationToken(cancellationToken)).ConfigureAwait(false);
    }

    public virtual Task<T?> GetAsync<T>(long id, CancellationToken cancellationToken = default)
        where T : class, new()
        => InvokeAsync(
            async s => await s.GetAsync(typeof(T).Name, id, defaultCommandTimeout),
            DeserializeOne<T>,
            default,
            cancellationToken);

    public virtual Task<IEnumerable<T>> GetAllAsync<T>(CancellationToken cancellationToken = default)
        where T : class, new()
        => InvokeAsync(
            async s => await s.GetAllAsync(typeof(T).Name, defaultCommandTimeout),
            DeserializeMany<T>,
            Enumerable.Empty<T>(),
            cancellationToken);

    public virtual Task<IEnumerable<T>> WhereAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        Dictionary<string, object> filters, CancellationToken cancellationToken = default)
        where T : class, new()
        => InvokeAsync(
            async s => await s.WhereAsync(typeof(T).Name, filters, defaultCommandTimeout),
            DeserializeMany<T>,
            Enumerable.Empty<T>(),
            cancellationToken);

    public virtual Task<IEnumerable<T>> FindByAnyAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(
        Dictionary<string, object> filters, CancellationToken cancellationToken = default)
        where T : class, new()
        => InvokeAsync(
            async s => await s.FindByAnyAsync(typeof(T).Name, filters, defaultCommandTimeout),
            DeserializeMany<T>,
            Enumerable.Empty<T>(),
            cancellationToken);

    public virtual Task<int> ExecuteAsync(
        string sql, object? param = null, int? commandTimeout = null, CommandType? commandType = null)
        => InvokeAsync(
            async s => await s.ExecuteAsync(sql, param, commandTimeout ?? defaultCommandTimeout, commandType ?? CommandType.Text),
            DeserializeInt,
            0);

    public virtual Task<IEnumerable<TReturn>> QueryAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TFirst, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TSecond, TReturn>(
        string sql, Func<TFirst, TSecond, TReturn> map, object? param = null, string splitOn = "Id",
        int? commandTimeout = null, CommandType? commandType = null, IEnumerable<DBAlias>? aliases = null)
        where TFirst : new() where TSecond : new()
        => QueryCoreAsync(sql, param, commandTimeout ?? defaultCommandTimeout, commandType ?? CommandType.Text,
            row => MapRow(row, map, splitOn, aliases));

    public virtual Task<IEnumerable<TReturn>> QueryAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TFirst, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TSecond, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TThird, TReturn>(
        string sql, Func<TFirst, TSecond, TThird, TReturn> map, object? param = null, string splitOn = "Id",
        int? commandTimeout = null, CommandType? commandType = null, IEnumerable<DBAlias>? aliases = null)
        where TFirst : new() where TSecond : new() where TThird : new()
        => QueryCoreAsync(sql, param, commandTimeout ?? defaultCommandTimeout, commandType ?? CommandType.Text,
            row => MapRow(row, map, splitOn, aliases));

    public virtual Task<IEnumerable<TReturn>> QueryAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TFirst, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TSecond, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TThird, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TFourth, TReturn>(string sql, Func<TFirst, TSecond, TThird, TFourth, TReturn> map, object? param = null, string splitOn = "Id", int? commandTimeout = null, CommandType? commandType = null, IEnumerable<DBAlias>? aliases = null) where TFirst : new() where TSecond : new() where TThird : new() where TFourth : new()
        => QueryCoreAsync(sql, param, commandTimeout ?? defaultCommandTimeout, commandType ?? CommandType.Text,
            row => MapRow(row, map, splitOn, aliases));

    public virtual Task<IEnumerable<TReturn>> QueryAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TFirst, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TSecond, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TThird, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TFourth, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TFifth, TReturn>(string sql, Func<TFirst, TSecond, TThird, TFourth, TFifth, TReturn> map, object? param = null, string splitOn = "Id", int? commandTimeout = null, CommandType? commandType = null, IEnumerable<DBAlias>? aliases = null) where TFirst : new() where TSecond : new() where TThird : new() where TFourth : new() where TFifth : new()
    => QueryCoreAsync(sql, param, commandTimeout ?? defaultCommandTimeout, commandType ?? CommandType.Text,
            row => MapRow(row, map, splitOn, aliases));

    public virtual Task<IEnumerable<TReturn>> QueryAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TFirst, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TSecond, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TThird, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TFourth, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TFifth, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TSixth, TReturn>(string sql, Func<TFirst, TSecond, TThird, TFourth, TFifth, TSixth, TReturn> map, object? param = null, string splitOn = "Id", int? commandTimeout = null, CommandType? commandType = null, IEnumerable<DBAlias>? aliases = null) where TFirst : new() where TSecond : new() where TThird : new() where TFourth : new() where TFifth : new() where TSixth : new()
    => QueryCoreAsync(sql, param, commandTimeout ?? defaultCommandTimeout, commandType ?? CommandType.Text,
            row => MapRow(row, map, splitOn, aliases));

    public virtual Task<IEnumerable<TReturn>> QueryAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TFirst, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TSecond, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TThird, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TFourth, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TFifth, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TSixth, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TSeventh, TReturn>(string sql, Func<TFirst, TSecond, TThird, TFourth, TFifth, TSixth, TSeventh, TReturn> map, object? param = null, string splitOn = "Id", int? commandTimeout = null, CommandType? commandType = null, IEnumerable<DBAlias>? aliases = null) where TFirst : new() where TSecond : new() where TThird : new() where TFourth : new() where TFifth : new() where TSixth : new() where TSeventh : new()
    => QueryCoreAsync(sql, param, commandTimeout ?? defaultCommandTimeout, commandType ?? CommandType.Text,
            row => MapRow(row, map, splitOn, aliases));

    private Task<IEnumerable<TReturn>> QueryCoreAsync<TReturn>(
        string sql, object? param, int? commandTimeout, CommandType? commandType,
        Func<IDictionary<string, object>, TReturn> mapRow)
        => InvokeAsync(
            async s => await s.QueryAsync(sql, param, commandTimeout ?? defaultCommandTimeout, commandType ?? CommandType.Text),
            data => DeserializeRows(data).Select(mapRow).ToList(),
            Enumerable.Empty<TReturn>());
    private static List<Dictionary<string, object>> SplitRow(IDictionary<string, object> row, string splitOn, int groupCount, IEnumerable<DBAlias>? aliases)
    {
        var splitOns = splitOn.Split(',', StringSplitOptions.TrimEntries);
        var keys = row.Keys.ToList();
        var groups = new List<Dictionary<string, object>>();
        int groupStart = 0;

        for (int g = 0; g < groupCount; g++)
        {
            int groupEnd;

            if (g == groupCount - 1)
            {
                groupEnd = keys.Count;
            }
            else
            {
                // Use the matching splitOn if one was given per-group, else reuse the single value (Dapper convention)
                string splitKey = g < splitOns.Length ? splitOns[g] : splitOns[^1];
                groupEnd = keys.Count;

                for (int i = groupStart + 1; i < keys.Count; i++)
                {
                    if (string.Equals(keys[i], splitKey, StringComparison.OrdinalIgnoreCase))
                    {
                        groupEnd = i;
                        break;
                    }
                }
            }

            var dict = new Dictionary<string, object>();
            for (int i = groupStart; i < groupEnd; i++)
                dict[GetKey(keys[i], aliases)] = row[keys[i]];

            groups.Add(dict);
            groupStart = groupEnd;
        }

        return groups;

        static string GetKey(string key, IEnumerable<DBAlias>? aliases)
        {
            if (aliases is null) return key;
            var alias = aliases.SingleOrDefault(a => a.alias == key);
            return alias?.columnName ?? key;
        }
    }

    private static T MapToObject<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] T>(IDictionary<string, object> dict) where T : new()
    {
        var obj = new T();
        var props = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite);

        foreach (var prop in props)
        {
            var match = dict.Keys.FirstOrDefault(k => string.Equals(k, prop.Name, StringComparison.OrdinalIgnoreCase));
            if (match is null) continue;

            var value = dict[match];
            if (value is null)
            {
                prop.SetValue(obj, null);
                continue;
            }

            var targetType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            var converted = targetType.IsEnum
                ? Enum.ToObject(targetType, value)
                : Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);

            prop.SetValue(obj, converted);
        }

        return obj;
    }

    private static TReturn MapRow<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TFirst, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TSecond, TReturn>(IDictionary<string, object> row, Func<TFirst, TSecond, TReturn> map, string splitOn, IEnumerable<DBAlias>? aliases) where TFirst : new() where TSecond : new()
    {
        var groups = SplitRow(row, splitOn, 2, aliases);

        var first = MapToObject<TFirst>(groups[0]);
        var second = MapToObject<TSecond>(groups[1]);

        return map(first, second);
    }

    private static TReturn MapRow<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TFirst, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TSecond, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TThird, TReturn>(IDictionary<string, object> row, Func<TFirst, TSecond, TThird, TReturn> map, string splitOn, IEnumerable<DBAlias>? aliases) where TFirst : new() where TSecond : new() where TThird : new()
    {
        var groups = SplitRow(row, splitOn, 3, aliases);

        var first = MapToObject<TFirst>(groups[0]);
        var second = MapToObject<TSecond>(groups[1]);
        var third = MapToObject<TThird>(groups[2]);

        return map(first, second, third);
    }

    private static TReturn MapRow<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TFirst, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TSecond, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TThird, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TFourth, TReturn>(IDictionary<string, object> row, Func<TFirst, TSecond, TThird, TFourth, TReturn> map, string splitOn, IEnumerable<DBAlias>? aliases) where TFirst : new() where TSecond : new() where TThird : new() where TFourth : new()
    {
        var groups = SplitRow(row, splitOn, 4, aliases);

        var first = MapToObject<TFirst>(groups[0]);
        var second = MapToObject<TSecond>(groups[1]);
        var third = MapToObject<TThird>(groups[2]);
        var fourth = MapToObject<TFourth>(groups[3]);

        return map(first, second, third, fourth);
    }

    private static TReturn MapRow<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TFirst, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TSecond, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TThird, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TFourth, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TFifth, TReturn>(IDictionary<string, object> row, Func<TFirst, TSecond, TThird, TFourth, TFifth, TReturn> map, string splitOn, IEnumerable<DBAlias>? aliases) where TFirst : new() where TSecond : new() where TThird : new() where TFourth : new() where TFifth : new()
    {
        var groups = SplitRow(row, splitOn, 5, aliases);

        var first = MapToObject<TFirst>(groups[0]);
        var second = MapToObject<TSecond>(groups[1]);
        var third = MapToObject<TThird>(groups[2]);
        var fourth = MapToObject<TFourth>(groups[3]);
        var fifth = MapToObject<TFifth>(groups[4]);

        return map(first, second, third, fourth, fifth);
    }

    private static TReturn MapRow<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TFirst, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TSecond, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TThird, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TFourth, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TFifth, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TSixth, TReturn>(IDictionary<string, object> row, Func<TFirst, TSecond, TThird, TFourth, TFifth, TSixth, TReturn> map, string splitOn, IEnumerable<DBAlias>? aliases) where TFirst : new() where TSecond : new() where TThird : new() where TFourth : new() where TFifth : new() where TSixth : new()
    {
        var groups = SplitRow(row, splitOn, 6, aliases);

        var first = MapToObject<TFirst>(groups[0]);
        var second = MapToObject<TSecond>(groups[1]);
        var third = MapToObject<TThird>(groups[2]);
        var fourth = MapToObject<TFourth>(groups[3]);
        var fifth = MapToObject<TFifth>(groups[4]);
        var sixth = MapToObject<TSixth>(groups[5]);

        return map(first, second, third, fourth, fifth, sixth);
    }

    private static TReturn MapRow<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TFirst, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TSecond, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TThird, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TFourth, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TFifth, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TSixth, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TSeventh, TReturn>(IDictionary<string, object> row, Func<TFirst, TSecond, TThird, TFourth, TFifth, TSixth, TSeventh, TReturn> map, string splitOn, IEnumerable<DBAlias>? aliases) where TFirst : new() where TSecond : new() where TThird : new() where TFourth : new() where TFifth : new() where TSixth : new() where TSeventh : new()
    {
        var groups = SplitRow(row, splitOn, 7, aliases);

        var first = MapToObject<TFirst>(groups[0]);
        var second = MapToObject<TSecond>(groups[1]);
        var third = MapToObject<TThird>(groups[2]);
        var fourth = MapToObject<TFourth>(groups[3]);
        var fifth = MapToObject<TFifth>(groups[4]);
        var sixth = MapToObject<TSixth>(groups[5]);
        var seventh = MapToObject<TSeventh>(groups[6]);

        return map(first, second, third, fourth, fifth, sixth, seventh);
    }
}