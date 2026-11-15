using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Queryeables.Remote;

/// <summary>
/// Entities as JSON (System.Text.Json): no attribute on the classes, a navigation back to the parent is written once
/// (a cycle is cut), <c>[JsonIgnore]</c> leaves a property out. The default serializer of the transports. Client and server
/// must use the same.
/// </summary>
/// <remarks>
/// <see cref="Default"/> reads the types by reflection. For Native AOT or trimming, give the options of a
/// <see cref="JsonSerializerContext"/> that lists the entity types and the protocol's values
/// (<c>new JsonEntitySerializer(AppJsonContext.Default.Options)</c>).
/// </remarks>
public sealed class JsonEntitySerializer : IEntitySerializer
{
    private readonly JsonSerializerOptions _options;

    /// <param name="options">The options, with a <see cref="JsonSerializerOptions.TypeInfoResolver"/> (a source-generated context for AOT).</param>
    public JsonEntitySerializer(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.TypeInfoResolver is null)
            throw new ArgumentException("The options need a TypeInfoResolver (a JsonSerializerContext, or DefaultJsonTypeInfoResolver).", nameof(options));
        _options = options;
    }

    /// <summary>By reflection, cycles cut. Not for Native AOT: give a <see cref="JsonSerializerContext"/> there.</summary>
    [RequiresUnreferencedCode("Reads the entity types by reflection: give the options of a JsonSerializerContext for trimming.")]
    [RequiresDynamicCode("Reads the entity types by reflection: give the options of a JsonSerializerContext for Native AOT.")]
    public JsonEntitySerializer()
        : this(new JsonSerializerOptions
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            ReferenceHandler = ReferenceHandler.IgnoreCycles,
        })
    {
    }

    private static JsonEntitySerializer? _default;

    /// <inheritdoc cref="JsonEntitySerializer()"/>
    public static JsonEntitySerializer Default
    {
        [RequiresUnreferencedCode("Reads the entity types by reflection: give the options of a JsonSerializerContext for trimming.")]
        [RequiresDynamicCode("Reads the entity types by reflection: give the options of a JsonSerializerContext for Native AOT.")]
        get => _default ??= new JsonEntitySerializer();
    }

    public byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, (JsonTypeInfo<T>)_options.GetTypeInfo(typeof(T)));

    public T? Deserialize<T>(byte[] data) => JsonSerializer.Deserialize(data, (JsonTypeInfo<T>)_options.GetTypeInfo(typeof(T)));

    /// <summary>System.Text.Json leaves out <c>[JsonIgnore]</c> (always): an update keeps the stored value of such a property.</summary>
    public bool Omits(PropertyInfo property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return property.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always };
    }
}
