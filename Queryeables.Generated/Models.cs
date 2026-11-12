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

/// <summary>
/// One relation of a parent. <c>Kind</c>: <c>Many</c> (a collection of children), <c>One</c> (one child), <c>Through</c>
/// (entities linked through <c>LinkType</c>: <c>ChildType</c> is the linked type, <c>ForeignKey</c> and <c>LinkTarget</c> are
/// properties of the link), <c>Reference</c> (a navigation of the parent: <c>ForeignKey</c> is a property of the parent).
/// </summary>
internal sealed record RelationModel(
    string Kind,
    string Collection,
    string ChildType,
    PropertyModel ChildKey,
    string ChildKeyExpression,
    PropertyModel ForeignKey,
    string? ParentNavigation,
    string? LookupNavigation,
    PropertyModel? LookupForeignKey,
    CacheModel? Lookup,
    bool NullDeletes = false,
    string? LinkType = null,
    PropertyModel? LinkTarget = null);

/// <summary>One [EntityGraph] or [EntityReference] relation, with the parent it belongs to.</summary>
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
