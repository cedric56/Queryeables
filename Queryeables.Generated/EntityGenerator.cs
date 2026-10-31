using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Immutable;
using System.Text;

namespace Queryeables.Generated;

/// <summary>
/// Writes the code behind <c>[EntityCache]</c> and <c>[EntityGraph]</c>:
/// <list type="bullet">
/// <item>for each parent of an <c>[EntityGraph]</c> collection: <c>I{Parent}Graph</c> / <c>{Parent}Graph</c> (navigation
/// properties set after loading, with the caches of the lookup navigations), and <c>I{Parent}Repository</c> /
/// <c>{Parent}Repository</c> (save, remove and load the parent with its children);</item>
/// <item><c>AddGeneratedEntities()</c> on <c>QueryeablesBuilder</c>, which registers the caches, graphs and services of this
/// project <b>and of the referenced projects</b>: each project with entities also gets a public module class
/// (<c>Queryeables.Generated.Modules.*</c>) that the projects referencing it call. Entities kept in a shared library are
/// thus registered by the client and by the server with the same single call.</item>
/// </list>
/// Problems are reported as diagnostics (QRY001-QRY009); the generator never throws.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class EntityGenerator : IIncrementalGenerator
{
    private const string CacheAttribute = "Queryeables.Abstraction.EntityCacheAttribute";
    private const string GraphAttribute = "Queryeables.Abstraction.EntityGraphAttribute";
    private const string Abstraction = "global::Queryeables.Abstraction";
    private const string Category = "Queryeables";

    // One field per rule, with literal arguments: the release tracking analyzer (AnalyzerReleases.*.md) reads them.
    private static readonly DiagnosticDescriptor EntityWithoutKey = new(
        "QRY001", "Entity without key", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor NotACollection = new(
        "QRY002", "[EntityGraph] on a property that is not a writable collection of entities", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ForeignKeyNotFound = new(
        "QRY003", "Foreign key not found", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor PropertyNotFound = new(
        "QRY004", "Property named by [EntityGraph] not found", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ForeignKeyType = new(
        "QRY005", "Foreign key of the wrong type", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor LookupKeyNotFound = new(
        "QRY006", "Lookup key not found", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor LookupKeyType = new(
        "QRY007", "Lookup key of the wrong type", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ParentNavigationType = new(
        "QRY008", "ParentNavigation of the wrong type", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor LookupKeyIsChildKey = new(
        "QRY009", "LookupForeignKey is the key of the child", "{0}", Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    private static DiagnosticDescriptor DescriptorOf(string id) => id switch
    {
        "QRY001" => EntityWithoutKey,
        "QRY002" => NotACollection,
        "QRY003" => ForeignKeyNotFound,
        "QRY004" => PropertyNotFound,
        "QRY005" => ForeignKeyType,
        "QRY006" => LookupKeyNotFound,
        "QRY007" => LookupKeyType,
        "QRY008" => ParentNavigationType,
        _ => LookupKeyIsChildKey,
    };

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var caches = context.SyntaxProvider.ForAttributeWithMetadataName(
                CacheAttribute,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => ReadCache(ctx))
            .Collect();

        var relations = context.SyntaxProvider.ForAttributeWithMetadataName(
                GraphAttribute,
                static (node, _) => node is PropertyDeclarationSyntax,
                static (ctx, _) => ReadRelation(ctx))
            .Collect();

        // The registration needs Queryeables.Core (the "Queryeables" package); without it, only the classes are written.
        // Also: this assembly's module name, and the modules of the referenced assemblies.
        var assembly = context.CompilationProvider.Select(static (compilation, _) => new AssemblyModel(
            compilation.GetTypeByMetadataName("Queryeables.QueryeablesBuilder") is not null,
            ModuleName(compilation.AssemblyName ?? "Assembly"),
            new EquatableArray<string>(compilation.SourceModule.ReferencedAssemblySymbols
                .Select(reference => (Reference: reference, Module: ModuleName(reference.Name)))
                .Where(r => r.Reference.GetTypeByMetadataName(ModuleNamespace + "." + r.Module) is { DeclaredAccessibility: Accessibility.Public })
                .Select(r => r.Module)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(module => module, StringComparer.Ordinal)
                .ToArray())));

        context.RegisterSourceOutput(caches.Combine(relations).Combine(assembly), static (spc, input) =>
        {
            var ((cacheResults, relationResults), target) = input;
            var core = target.HasCore;

            foreach (var result in cacheResults)
            {
                if (result.Diagnostic is { } diagnostic)
                    Report(spc, diagnostic);
            }

            foreach (var result in relationResults)
            {
                foreach (var diagnostic in result.Diagnostics)
                    Report(spc, diagnostic);
            }

            var graphs = relationResults
                .Where(r => r.Relation is not null && r.ParentKey is not null)
                .GroupBy(r => r.ParentTypeName, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new GraphModel(g.First(), g.Select(r => r.Relation!).ToArray()))
                .ToArray();

            foreach (var graph in graphs)
                spc.AddSource(HintName("Graph", graph.ParentTypeName), WriteGraph(graph));

            if (!core)
                return;

            // Caches: the [EntityCache] types, and the targets of the lookup navigations (the graphs need them).
            var cacheModels = cacheResults.Where(r => r.Cache is not null).Select(r => r.Cache!)
                .Concat(graphs.SelectMany(g => g.Relations).Where(r => r.Lookup is not null).Select(r => r.Lookup!))
                .GroupBy(c => c.TypeName, StringComparer.Ordinal)
                .Select(g => g.First())
                .OrderBy(c => c.TypeName, StringComparer.Ordinal)
                .ToArray();

            var ownModule = cacheModels.Length > 0 || graphs.Length > 0 ? target.Module : null;
            spc.AddSource("Queryeables.GeneratedEntities.g.cs", WriteRegistration(cacheModels, graphs, ownModule, target.ReferencedModules));
        });
    }

    // ---------------------------------------------------------------------
    // Reading the symbols (only values leave this part)
    // ---------------------------------------------------------------------

    private static CacheResult ReadCache(GeneratorAttributeSyntaxContext context)
    {
        var type = (INamedTypeSymbol)context.TargetSymbol;
        var key = FindKey(type);
        return key is null
            ? new CacheResult(null, Diagnostic("QRY001",
                $"{type.Name} has no key: add an Id property or mark the key with [Key].", context.TargetNode.GetLocation()))
            : new CacheResult(new CacheModel(TypeName(type), key), null);
    }

    private static RelationResult ReadRelation(GeneratorAttributeSyntaxContext context)
    {
        var property = (IPropertySymbol)context.TargetSymbol;
        var parent = property.ContainingType;
        var location = context.Attributes.FirstOrDefault()?.ApplicationSyntaxReference?.GetSyntax().GetLocation()
                       ?? context.TargetNode.GetLocation();
        var diagnostics = new List<DiagnosticInfo>();

        RelationResult Result(PropertyModel? parentKey, RelationModel? relation) => new(
            TypeName(parent),
            parent.Name,
            parent.ContainingNamespace.IsGlobalNamespace ? null : parent.ContainingNamespace.ToDisplayString(),
            parentKey,
            relation,
            new EquatableArray<DiagnosticInfo>(diagnostics.ToArray()));

        RelationResult Fail(string id, string message)
        {
            diagnostics.Add(Diagnostic(id, message, location));
            return Result(null, null);
        }

        var parentKey = FindKey(parent);
        if (parentKey is null)
            return Fail("QRY001", $"{parent.Name} has no key: add an Id property or mark the key with [Key].");

        var compilation = context.SemanticModel.Compilation;
        if (ElementType(property.Type) is not INamedTypeSymbol { TypeKind: TypeKind.Class } child
            || property.SetMethod is null
            || !AcceptsList(compilation, property.Type, child))
        {
            return Fail("QRY002",
                $"[EntityGraph] on {parent.Name}.{property.Name}: the property must be a collection of entities that accepts a " +
                "List<T> (List<T>, IList<T>, ICollection<T>, IEnumerable<T>, IReadOnlyList<T>...) and has a setter. " +
                "A reference to the parent takes no attribute: name it in ParentNavigation of the collection.");
        }

        var childKey = FindKey(child);
        if (childKey is null)
            return Fail("QRY001", $"{child.Name} has no key: add an Id property or mark the key with [Key].");

        var attribute = context.Attributes.First();
        var foreignKeyName = Named(attribute, "ForeignKey");
        var parentNavigation = Named(attribute, "ParentNavigation");
        var lookupNavigation = Named(attribute, "LookupNavigation");
        var lookupForeignKeyName = Named(attribute, "LookupForeignKey");

        // The foreign key toward the parent: named, or by convention Id{Parent} / {Parent}Id.
        var foreignKey = foreignKeyName is not null
            ? FindProperty(child, foreignKeyName)
            : FindProperty(child, "Id" + parent.Name) ?? FindProperty(child, parent.Name + "Id");
        if (foreignKey is null)
        {
            return Fail(foreignKeyName is null ? "QRY003" : "QRY004", foreignKeyName is null
                ? $"The foreign key of {child.Name} toward {parent.Name} was not found (looked for Id{parent.Name} and " +
                  $"{parent.Name}Id): set ForeignKey on [EntityGraph]."
                : $"ForeignKey = \"{foreignKeyName}\": {child.Name} has no writable property {foreignKeyName}.");
        }

        if (!SameKeyType(foreignKey.Type, parentKey.Type, parentKey.UnderlyingType))
        {
            return Fail("QRY005",
                $"{child.Name}.{foreignKey.Name} is {Display(foreignKey.Type)}, but the key of {parent.Name} is {parentKey.Type}.");
        }

        if (parentNavigation is not null)
        {
            var navigation = FindProperty(child, parentNavigation);
            if (navigation is null)
                return Fail("QRY004", $"ParentNavigation = \"{parentNavigation}\": {child.Name} has no writable property {parentNavigation}.");

            // The parent is assigned to it: it must accept the parent (its type, a base type or an interface of it).
            if (!compilation.ClassifyConversion(parent, navigation.Type).IsImplicit)
            {
                return Fail("QRY008",
                    $"ParentNavigation = \"{parentNavigation}\": {child.Name}.{parentNavigation} is {Display(navigation.Type)}, it cannot hold the " +
                    $"parent {parent.Name}. For a navigation to another entity found in its cache, use LookupNavigation = \"{parentNavigation}\" " +
                    "with LookupForeignKey (its key), and ForeignKey for the key of the parent.");
            }
        }

        PropertyModel? lookupForeignKey = null;
        CacheModel? lookup = null;
        if (lookupNavigation is not null)
        {
            var navigation = FindProperty(child, lookupNavigation);
            if (navigation?.Type is not INamedTypeSymbol { TypeKind: TypeKind.Class } lookupType)
                return Fail("QRY004", $"LookupNavigation = \"{lookupNavigation}\": {child.Name} has no writable property {lookupNavigation} of an entity type.");

            var lookupKey = FindKey(lookupType);
            if (lookupKey is null)
                return Fail("QRY001", $"{lookupType.Name} has no key: add an Id property or mark the key with [Key].");

            var lookupForeignKeySymbol = lookupForeignKeyName is not null
                ? FindProperty(child, lookupForeignKeyName, writable: false)
                : FindProperty(child, "Id" + lookupNavigation, writable: false) ?? FindProperty(child, lookupNavigation + "Id", writable: false);
            if (lookupForeignKeySymbol is null)
            {
                return Fail(lookupForeignKeyName is null ? "QRY006" : "QRY004", lookupForeignKeyName is null
                    ? $"The key of the navigation {child.Name}.{lookupNavigation} was not found (looked for Id{lookupNavigation} and " +
                      $"{lookupNavigation}Id): set LookupForeignKey on [EntityGraph]."
                    : $"LookupForeignKey = \"{lookupForeignKeyName}\": {child.Name} has no property {lookupForeignKeyName}.");
            }

            // nameof(Product.Id) gives "Id": the key of the child itself, not the key of the product it points to.
            if (lookupForeignKeySymbol.Name == childKey.Name)
            {
                return Fail("QRY009",
                    $"LookupForeignKey = \"{lookupForeignKeySymbol.Name}\" is the key of {child.Name} itself. Name the property of " +
                    $"{child.Name} that holds the key of {lookupType.Name}, for example nameof({child.Name}.Id{lookupNavigation}).");
            }

            if (!SameKeyType(lookupForeignKeySymbol.Type, lookupKey.Type, lookupKey.UnderlyingType))
            {
                return Fail("QRY007",
                    $"{child.Name}.{lookupForeignKeySymbol.Name} is {Display(lookupForeignKeySymbol.Type)}, but the key of {lookupType.Name} is {lookupKey.Type}.");
            }

            lookupForeignKey = Property(lookupForeignKeySymbol);
            lookup = new CacheModel(TypeName(lookupType), lookupKey);
        }

        var relation = new RelationModel(
            property.Name,
            TypeName(child),
            childKey,
            Property(foreignKey),
            parentNavigation,
            lookupNavigation,
            lookupForeignKey,
            lookup);
        return Result(parentKey, relation);
    }

    private static PropertyModel? FindKey(INamedTypeSymbol type)
    {
        IPropertySymbol? byName = null;
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.IsStatic || property.GetMethod is null || property.DeclaredAccessibility != Accessibility.Public)
                    continue;

                if (property.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "System.ComponentModel.DataAnnotations.KeyAttribute"))
                    return Property(property);

                if (byName is null && string.Equals(property.Name, "Id", StringComparison.OrdinalIgnoreCase))
                    byName = property;
            }
        }
        return byName is null ? null : Property(byName);
    }

    private static IPropertySymbol? FindProperty(INamedTypeSymbol type, string name, bool writable = true)
    {
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            var property = current.GetMembers(name).OfType<IPropertySymbol>()
                .FirstOrDefault(p => !p.IsStatic && p.DeclaredAccessibility == Accessibility.Public && p.GetMethod is not null);
            if (property is not null)
                return !writable || property.SetMethod is not null ? property : null;
        }
        return null;
    }

    private static ITypeSymbol? ElementType(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol)
            return null;   // an array does not accept a List<T>

        return type is INamedTypeSymbol named
            ? named.AllInterfaces.Prepend(named)
                .FirstOrDefault(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
                ?.TypeArguments[0]
            : null;
    }

    private static bool AcceptsList(Compilation compilation, ITypeSymbol collection, ITypeSymbol element)
    {
        var list = compilation.GetTypeByMetadataName("System.Collections.Generic.List`1");
        if (list is null)
            return false;
        var conversion = compilation.ClassifyConversion(list.Construct(element), collection);
        return conversion.IsIdentity || conversion.IsImplicit && conversion.IsReference;
    }

    /// <summary>The type of a key, or the nullable form of it (<c>long?</c> for a <c>long</c> key).</summary>
    private static bool SameKeyType(ITypeSymbol candidate, string keyType, string? keyUnderlyingType)
    {
        var name = TypeName(candidate);
        if (name == keyType)
            return true;
        return candidate is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
               && TypeName(nullable.TypeArguments[0]) == (keyUnderlyingType ?? keyType);
    }

    private static PropertyModel Property(IPropertySymbol property)
    {
        var nullableValue = property.Type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            ? nullable.TypeArguments[0]
            : null;
        return new PropertyModel(property.Name, TypeName(property.Type), nullableValue is not null,
            nullableValue is null ? null : TypeName(nullableValue));
    }

    private static string? Named(AttributeData attribute, string name)
    {
        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.Key == name && argument.Value.Value is string { Length: > 0 } value)
                return value;
        }
        return null;
    }

    // ---------------------------------------------------------------------
    // Writing the graph of a parent
    // ---------------------------------------------------------------------

    private sealed class GraphModel(RelationResult first, RelationModel[] relations)
    {
        public string ParentTypeName { get; } = first.ParentTypeName;
        public string ParentName { get; } = first.ParentName;
        public string? Namespace { get; } = first.Namespace;
        public PropertyModel Key { get; } = first.ParentKey!;
        public RelationModel[] Relations { get; } = relations;

        /// <summary>The cache of each lookup type, one constructor parameter each.</summary>
        public CacheModel[] Lookups { get; } = relations.Where(r => r.Lookup is not null).Select(r => r.Lookup!)
            .GroupBy(l => l.TypeName, StringComparer.Ordinal).Select(g => g.First()).ToArray();

        public string Graph => ParentName + "Graph";
        public string Repository => ParentName + "Repository";
        public string Qualified(string name) => Namespace is null ? "global::" + name : "global::" + Namespace + "." + name;
    }

    private static string WriteGraph(GraphModel graph)
    {
        var parent = graph.ParentTypeName;
        var key = graph.Key.Type;
        var entityGraph = $"{Abstraction}.EntityGraph<{parent}, {key}>";
        var sb = new StringBuilder();

        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#nullable enable");
        if (graph.Namespace is not null)
        {
            sb.Append("namespace ").Append(graph.Namespace).AppendLine(";");
        }
        sb.AppendLine();

        // ---- I{Parent}Graph
        sb.AppendLine("/// <summary>Sets the navigation properties of the children of a <see cref=\"" + Cref(parent) + "\"/> after loading.</summary>");
        sb.Append("public partial interface I").Append(graph.Graph).AppendLine();
        sb.AppendLine("{");
        foreach (var relation in graph.Relations)
        {
            sb.Append("    void Attach").Append(relation.Collection).Append('(').Append(parent)
              .Append(" parent, global::System.Collections.Generic.IEnumerable<").Append(relation.ChildType).AppendLine("> children);");
        }
        sb.AppendLine("}");
        sb.AppendLine();

        // ---- {Parent}Graph
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// The graph of <see cref=\"" + Cref(parent) + "\"/>: <see cref=\"Create\"/> describes it for");
        sb.AppendLine("/// <see cref=\"" + Abstraction.Substring(8) + ".EntityGraph{TParent, TKey}\"/>, the Attach methods set the navigation properties.");
        sb.AppendLine("/// </summary>");
        sb.Append("public sealed partial class ").Append(graph.Graph).Append(" : I").Append(graph.Graph).AppendLine();
        sb.AppendLine("{");
        foreach (var lookup in graph.Lookups)
        {
            sb.Append("    private readonly ").Append(CacheType(lookup)).Append(' ').Append(Field(lookup)).AppendLine(";");
        }
        if (graph.Lookups.Length > 0)
            sb.AppendLine();

        sb.Append("    public ").Append(graph.Graph).Append('(');
        sb.Append(string.Join(", ", graph.Lookups.Select(l => CacheType(l) + " " + Parameter(l))));
        sb.AppendLine(")");
        sb.AppendLine("    {");
        foreach (var lookup in graph.Lookups)
        {
            sb.Append("        ").Append(Field(lookup)).Append(" = ").Append(Parameter(lookup))
              .Append(" ?? throw new global::System.ArgumentNullException(nameof(").Append(Parameter(lookup)).AppendLine("));");
        }
        sb.AppendLine("    }");

        foreach (var relation in graph.Relations)
        {
            sb.AppendLine();
            sb.Append("    public void Attach").Append(relation.Collection).Append('(').Append(parent)
              .Append(" parent, global::System.Collections.Generic.IEnumerable<").Append(relation.ChildType).AppendLine("> children)");
            sb.AppendLine("    {");
            sb.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(parent);");
            sb.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(children);");
            sb.AppendLine("        foreach (var child in children)");
            sb.AppendLine("        {");
            if (relation.ParentNavigation is not null)
                sb.Append("            child.").Append(Escape(relation.ParentNavigation)).AppendLine(" = parent;");
            if (relation is { LookupNavigation: not null, LookupForeignKey: not null, Lookup: not null })
            {
                var cache = Field(relation.Lookup);
                var foreignKey = "child." + Escape(relation.LookupForeignKey.Name);
                var navigation = "child." + Escape(relation.LookupNavigation);
                if (relation.LookupForeignKey.IsNullableValue)
                {
                    sb.Append("            if (").Append(foreignKey).Append(" is { } ").Append(LookupVariable(relation)).AppendLine(")");
                    sb.Append("                ").Append(navigation).Append(" = ").Append(cache).Append(".Find(")
                      .Append(LookupVariable(relation)).Append(") ?? ").Append(navigation).AppendLine(";");
                }
                else
                {
                    sb.Append("            ").Append(navigation).Append(" = ").Append(cache).Append(".Find(")
                      .Append(foreignKey).Append(") ?? ").Append(navigation).AppendLine(";");
                }
            }
            sb.AppendLine("        }");
            sb.AppendLine("    }");
        }

        sb.AppendLine();
        sb.AppendLine("    /// <summary>Describes the graph; <paramref name=\"graph\"/> sets the navigation properties after loading.</summary>");
        sb.Append("    public static ").Append(entityGraph).Append(" Create(I").Append(graph.Graph).AppendLine("? graph = null)");
        sb.AppendLine("    {");
        sb.Append("        return ").Append(Abstraction).Append(".EntityGraph.For<").Append(parent).Append(", ").Append(key)
          .Append(">(p => p.").Append(Escape(graph.Key.Name)).AppendLine(")");
        foreach (var relation in graph.Relations)
        {
            var foreignKey = "c." + Escape(relation.ForeignKey.Name);
            if (relation.ForeignKey.Type != key)
                foreignKey = "(" + key + ")" + foreignKey + "!";   // a nullable foreign key toward a non-nullable key

            sb.Append("            .HasMany<").Append(relation.ChildType).Append(", ").Append(relation.ChildKey.Type).AppendLine(">(");
            sb.Append("                p => p.").Append(Escape(relation.Collection)).AppendLine(",");
            sb.Append("                c => ").Append(foreignKey).AppendLine(",");
            sb.Append("                c => c.").Append(Escape(relation.ChildKey.Name)).AppendLine(",");
            sb.Append("                graph is null ? null : new global::System.Action<").Append(parent)
              .Append(", global::System.Collections.Generic.IReadOnlyList<").Append(relation.ChildType)
              .Append(">>(graph.Attach").Append(relation.Collection).AppendLine("))");
        }
        // The chain ends with the last HasMany: put the semicolon at the end of its line.
        sb.Length -= sb.Length > 1 && sb[sb.Length - 2] == '\r' ? 2 : 1;
        sb.AppendLine(";");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();

        // ---- I{Parent}Repository / {Parent}Repository
        sb.AppendLine("/// <summary>Saves, removes and loads a <see cref=\"" + Cref(parent) + "\"/> with its children.</summary>");
        sb.Append("public partial interface I").Append(graph.Repository).Append(" : ").Append(Abstraction)
          .Append(".IAggregateRepository<").Append(parent).AppendLine(">");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>Reads the children of the entity into its collections.</summary>");
        sb.Append("    global::System.Threading.Tasks.Task LoadChildrenAsync(").Append(parent)
          .AppendLine(" entity, global::System.Threading.CancellationToken ct = default);");
        foreach (var relation in graph.Relations.Where(HasOwnLoadMethod))
        {
            sb.AppendLine();
            sb.Append("    /// <summary>Reads the ").Append(relation.Collection).AppendLine(" of the entity, and only them.</summary>");
            sb.Append("    global::System.Threading.Tasks.Task Load").Append(relation.Collection).Append("Async(").Append(parent)
              .AppendLine(" entity, global::System.Threading.CancellationToken ct = default);");
        }
        sb.AppendLine("}");
        sb.AppendLine();
        sb.Append("public sealed partial class ").Append(graph.Repository).Append(" : ").Append(Abstraction)
          .Append(".GraphRepository<").Append(parent).Append(", ").Append(key).Append(">, I").Append(graph.Repository).AppendLine();
        sb.AppendLine("{");
        sb.Append("    public ").Append(graph.Repository).Append('(').Append(Abstraction).Append(".IEntityStoreFactory stores, ")
          .Append(entityGraph).AppendLine(" graph)");
        sb.AppendLine("        : base(stores, graph)");
        sb.AppendLine("    {");
        sb.AppendLine("    }");
        foreach (var relation in graph.Relations.Where(HasOwnLoadMethod))
        {
            sb.AppendLine();
            sb.Append("    public global::System.Threading.Tasks.Task Load").Append(relation.Collection).Append("Async(").Append(parent)
              .AppendLine(" entity, global::System.Threading.CancellationToken ct = default)");
            sb.Append("        => LoadChildrenAsync(entity, \"").Append(relation.Collection).AppendLine("\", ct);");
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    // ---------------------------------------------------------------------
    // Writing the registration
    // ---------------------------------------------------------------------

    private const string ModuleNamespace = "Queryeables.Generated.Modules";

    /// <summary>Name of the module class of an assembly: <c>Entities_</c> and the assembly name as an identifier.</summary>
    private static string ModuleName(string assemblyName)
    {
        var name = new StringBuilder("Entities_");
        foreach (var c in assemblyName)
            name.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        return name.ToString();
    }

    private static string WriteRegistration(CacheModel[] caches, GraphModel[] graphs, string? ownModule,
        EquatableArray<string> referencedModules)
    {
        const string Di = "global::Microsoft.Extensions.DependencyInjection";
        const string Builder = "global::Queryeables.QueryeablesBuilder";
        var sb = new StringBuilder();

        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("namespace Queryeables.Generated");
        sb.AppendLine("{");
        sb.AppendLine("    internal static class GeneratedEntities");
        sb.AppendLine("    {");
        sb.AppendLine("        /// <summary>");
        sb.AppendLine("        /// Registers what [EntityCache] and [EntityGraph] describe, in this project and in the referenced projects:");
        sb.AppendLine("        /// the caches (and their change feeds), the graphs and their repositories (<c>I{Parent}Repository</c>, also registered as");
        sb.AppendLine("        /// <c>IAggregateRepository&lt;Parent&gt;</c>).");
        sb.AppendLine("        /// </summary>");
        sb.Append("        public static ").Append(Builder).Append(" AddGeneratedEntities(this ").Append(Builder).AppendLine(" builder)");
        sb.AppendLine("        {");
        sb.AppendLine("            global::System.ArgumentNullException.ThrowIfNull(builder);");
        foreach (var module in referencedModules)
            sb.Append("            global::").Append(ModuleNamespace).Append('.').Append(module).AppendLine(".Register(builder);");
        if (ownModule is not null)
            sb.Append("            global::").Append(ModuleNamespace).Append('.').Append(ownModule).AppendLine(".Register(builder);");
        sb.AppendLine("            return builder;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        if (ownModule is null)
            return sb.ToString();

        sb.AppendLine();
        sb.Append("namespace ").AppendLine(ModuleNamespace);
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>The entities of this project, registered by <c>AddGeneratedEntities()</c> here and in the projects that reference it.</summary>");
        sb.AppendLine("    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
        sb.Append("    public static class ").AppendLine(ownModule);
        sb.AppendLine("    {");
        sb.Append("        public static void Register(").Append(Builder).AppendLine(" builder)");
        sb.AppendLine("        {");
        sb.AppendLine("            global::System.ArgumentNullException.ThrowIfNull(builder);");
        sb.AppendLine("            var services = builder.Services;");
        var body = new StringBuilder();
        var outer = sb;
        sb = body;

        foreach (var cache in caches)
        {
            sb.Append("        builder.AddCache<").Append(cache.TypeName).Append(", ").Append(cache.Key.Type)
              .Append(">(e => e.").Append(Escape(cache.Key.Name)).AppendLine(");");
        }

        foreach (var graph in graphs)
        {
            var parent = graph.ParentTypeName;
            var key = graph.Key.Type;
            var graphInterface = graph.Qualified("I" + graph.Graph);
            var serviceInterface = graph.Qualified("I" + graph.Repository);

            sb.AppendLine();
            sb.Append("        // ").AppendLine(graph.ParentName);
            sb.Append("        ").Append(Di).Append(".Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<")
              .Append(graphInterface).Append(", ").Append(graph.Qualified(graph.Graph)).AppendLine(">(services);");
            sb.Append("        builder.AddGraph<").Append(parent).Append(", ").Append(key).Append(">(sp => ")
              .Append(graph.Qualified(graph.Graph)).Append(".Create(").Append(Di)
              .Append(".ServiceProviderServiceExtensions.GetRequiredService<").Append(graphInterface).AppendLine(">(sp)));");
            sb.Append("        ").Append(Di).Append(".Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<")
              .Append(serviceInterface).Append(">(services, sp => new ").Append(graph.Qualified(graph.Repository)).AppendLine("(");
            sb.Append("            ").Append(Di).Append(".ServiceProviderServiceExtensions.GetRequiredService<")
              .Append(Abstraction).AppendLine(".IEntityStoreFactory>(sp),");
            sb.Append("            ").Append(Di).Append(".ServiceProviderServiceExtensions.GetRequiredService<")
              .Append(Abstraction).Append(".EntityGraph<").Append(parent).Append(", ").Append(key).AppendLine(">>(sp)));");
            sb.Append("        ").Append(Di).Append(".Extensions.ServiceCollectionDescriptorExtensions.Replace(services, ")
              .Append(Di).Append(".ServiceDescriptor.Singleton<").Append(Abstraction).Append(".IAggregateRepository<").Append(parent)
              .Append(">>(sp => ").Append(Di).Append(".ServiceProviderServiceExtensions.GetRequiredService<")
              .Append(serviceInterface).AppendLine(">(sp)));");
        }

        // The registrations, indented one more level inside the module.
        foreach (var line in body.ToString().Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length > 0)
                outer.Append("    ").AppendLine(trimmed);
            else
                outer.AppendLine();
        }
        outer.AppendLine("        }");
        outer.AppendLine("    }");
        outer.AppendLine("}");
        return outer.ToString();
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static string TypeName(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static string Display(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

    private static string Cref(string typeName) => typeName.StartsWith("global::", StringComparison.Ordinal) ? typeName.Substring(8) : typeName;

    private static string CacheType(CacheModel cache) => $"{Abstraction}.IEntityCache<{cache.TypeName}, {cache.Key.Type}>";

    private static string ShortName(CacheModel cache)
    {
        var name = cache.TypeName;
        var dot = name.LastIndexOf('.');
        name = dot >= 0 ? name.Substring(dot + 1) : name.Replace("global::", string.Empty);
        return char.ToLowerInvariant(name[0]) + name.Substring(1);
    }

    private static string Parameter(CacheModel cache) => Escape(ShortName(cache) + "Cache");

    private static string Field(CacheModel cache) => "_" + ShortName(cache) + "Cache";

    /// <summary>A collection named Children already has LoadChildrenAsync, from GraphRepository.</summary>
    private static bool HasOwnLoadMethod(RelationModel relation) => relation.Collection != "Children";

    private static string LookupVariable(RelationModel relation) => "__" + relation.LookupForeignKey!.Name;

    private static string Escape(string identifier)
        => SyntaxFacts.GetKeywordKind(identifier) != SyntaxKind.None ? "@" + identifier : identifier;

    private static string HintName(string kind, string typeName)
    {
        var name = new StringBuilder("Queryeables.").Append(kind).Append('.');
        foreach (var c in typeName.Replace("global::", string.Empty))
            name.Append(char.IsLetterOrDigit(c) || c == '.' ? c : '_');
        return name.Append(".g.cs").ToString();
    }

    private static DiagnosticInfo Diagnostic(string id, string message, Location? location)
        => new(id, message, LocationInfo.From(location));

    private static void Report(SourceProductionContext context, DiagnosticInfo diagnostic)
        => context.ReportDiagnostic(Microsoft.CodeAnalysis.Diagnostic.Create(
            DescriptorOf(diagnostic.Id), diagnostic.Location?.ToLocation(), diagnostic.Message));
}
