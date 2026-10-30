using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Queryeables.Generated;

/// <summary>Where a diagnostic goes, kept as values so that the models stay equatable (incremental caching).</summary>
internal sealed record LocationInfo(string FilePath, TextSpan Span, LinePositionSpan LineSpan)
{
    public static LocationInfo? From(Location? location)
        => location is null || location.SourceTree is null
            ? null
            : new LocationInfo(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);

    public Location ToLocation() => Location.Create(FilePath, Span, LineSpan);
}

internal sealed record DiagnosticInfo(string Id, string Message, LocationInfo? Location);

/// <summary>A property: its C# name and its fully qualified type.</summary>
internal sealed record PropertyModel(string Name, string Type, bool IsNullableValue, string? UnderlyingType);

/// <summary>A class marked [EntityCache], or the target of a lookup navigation.</summary>
internal sealed record CacheModel(string TypeName, PropertyModel Key);

internal sealed record RelationModel(
    string Collection,
    string ChildType,
    PropertyModel ChildKey,
    PropertyModel ForeignKey,
    string? ParentNavigation,
    string? LookupNavigation,
    PropertyModel? LookupForeignKey,
    CacheModel? Lookup);

/// <summary>One [EntityGraph] collection, with the parent it belongs to.</summary>
internal sealed record RelationResult(
    string ParentTypeName,
    string ParentName,
    string? Namespace,
    PropertyModel? ParentKey,
    RelationModel? Relation,
    EquatableArray<DiagnosticInfo> Diagnostics);

internal sealed record CacheResult(CacheModel? Cache, DiagnosticInfo? Diagnostic);

/// <summary>What the registration needs to know about the compilation: Queryeables.Core present, module names.</summary>
internal sealed record AssemblyModel(bool HasCore, string Module, EquatableArray<string> ReferencedModules);
