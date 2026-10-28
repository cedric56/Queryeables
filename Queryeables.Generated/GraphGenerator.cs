using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Queryeables.Generators;

[Generator]
public sealed class EntityGraphGenerator : IIncrementalGenerator
{
    private const string AttributeMetadataName =
        "Queryeables.Abstraction.EntityGraphAttribute";

    private const string Abstraction =
        "global::Queryeables.Abstraction";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var graphs = context.SyntaxProvider.ForAttributeWithMetadataName(
                AttributeMetadataName,
                static (node, _) =>
                    node is Microsoft.CodeAnalysis.CSharp.Syntax.PropertyDeclarationSyntax,
                static (ctx, _) => GetGraphInfo(ctx))
            .Where(static info => info is not null)
            .Select(static (info, _) => info!);

        var input = context.CompilationProvider.Combine(graphs.Collect());

        context.RegisterSourceOutput(input, static (spc, source) =>
        {
            foreach (var group in source.Right.GroupBy(
                         x => x.ParentType,
                         SymbolEqualityComparer.Default))
            {
                if (group.Key is not INamedTypeSymbol parentType)
                    continue;

                var info = BuildEntityInfo(
                    parentType,
                    group.ToImmutableArray());

                spc.AddSource(
                    //Sanitize(parentType) + ".EntityGraph.g.cs",
                    parentType.Name + ".Contracts.g.cs",
                    SourceText.From(
                        GenerateFile(info, source.Left),
                        Encoding.UTF8));
            }
        });
    }

    // ---------------------------------------------------------------------
    // Découverte des relations
    // ---------------------------------------------------------------------

    private static GraphInfo? GetGraphInfo(
        GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not IPropertySymbol property)
            return null;

        if (property.ContainingType is not INamedTypeSymbol parentType)
            return null;

        var childType = GetCollectionElementType(property.Type);

        if (childType is not INamedTypeSymbol child)
            return null;

        var attribute = context.Attributes.FirstOrDefault();

        if (attribute is null)
            return null;

        return new GraphInfo(
            parentType,
            property,
            child,
            GetNamedString(attribute, "ParentNavigation"),
            GetNamedString(attribute, "LookupNavigation"),
            GetNamedString(attribute, "ForeignKey"),
            GetNamedString(attribute, "LookupForeignKey"));
    }

    private static ITypeSymbol? GetCollectionElementType(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol namedType)
            return null;

        foreach (var candidate in namedType.AllInterfaces.Prepend(namedType))
        {
            if (candidate is not INamedTypeSymbol named ||
                named.TypeArguments.Length != 1)
                continue;

            if (named.OriginalDefinition.SpecialType ==
                SpecialType.System_Collections_Generic_IEnumerable_T)
            {
                return named.TypeArguments[0];
            }

            if (named.OriginalDefinition.ToDisplayString() is
                "System.Collections.Generic.ICollection<T>" or
                "System.Collections.Generic.IList<T>" or
                "System.Collections.Generic.List<T>" or
                "System.Collections.Generic.IReadOnlyCollection<T>" or
                "System.Collections.Generic.IReadOnlyList<T>")
            {
                return named.TypeArguments[0];
            }
        }

        return null;
    }

    private static string? GetNamedString(
        AttributeData attribute,
        string name)
    {
        foreach (var argument in attribute.NamedArguments)
        {
            if (argument.Key == name &&
                argument.Value.Value is string value)
            {
                return value;
            }
        }

        return null;
    }

    private static EntityInfo BuildEntityInfo(
        INamedTypeSymbol parentType,
        ImmutableArray<GraphInfo> graphs)
    {
        var parentKey = FindKeyProperty(parentType)
            ?? throw new InvalidOperationException(
                $"L'entité '{parentType.ToDisplayString()}' doit posséder une propriété Id ou ID.");

        var relations = graphs.Select(graph =>
        {
            var childKey = FindKeyProperty(graph.ChildType);

            // ForeignKey est réservé à EntityGraph.HasMany.
            var foreignKey =
                FindProperty(graph.ChildType, graph.ForeignKey);

            // LookupForeignKey est utilisé uniquement pour cache.Find.
            // Sans configuration explicite, on tente de déduire la FK.
            var lookupForeignKey =
                FindProperty(graph.ChildType, graph.LookupForeignKey)
                ?? (graph.LookupForeignKey is null
                    ? FindForeignKey(
                        graph.ChildType,
                        parentType,
                        parentKey.Type)
                    : null);

            var parentNavigation =
                FindProperty(graph.ChildType, graph.ParentNavigation);

            var lookupNavigation =
                FindProperty(graph.ChildType, graph.LookupNavigation);

            return new RelationInfo(
                graph.Property,
                graph.ChildType,
                childKey,
                foreignKey,
                lookupForeignKey,
                parentNavigation,
                lookupNavigation);
        }).ToImmutableArray();

        return new EntityInfo(
            parentType,
            parentKey,
            relations);
    }

    private static IPropertySymbol? FindProperty(
        INamedTypeSymbol type,
        string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return type.GetMembers()
            .OfType<IPropertySymbol>()
            .FirstOrDefault(p => p.Name == name);
    }

    private static IPropertySymbol? FindKeyProperty(ITypeSymbol type)
    {
        return type.GetMembers()
            .OfType<IPropertySymbol>()
            .FirstOrDefault(p =>
                p.Name.Equals("Id", StringComparison.OrdinalIgnoreCase) &&
                !p.IsStatic &&
                p.GetMethod is not null);
    }

    private static IPropertySymbol? FindForeignKey(
        INamedTypeSymbol childType,
        INamedTypeSymbol parentType,
        ITypeSymbol parentKeyType)
    {
        var expectedName = "Id" + parentType.Name;

        var properties = childType.GetMembers()
            .OfType<IPropertySymbol>()
            .ToArray();

        return properties.FirstOrDefault(p =>
                   p.Name.Equals(
                       expectedName,
                       StringComparison.OrdinalIgnoreCase) &&
                   SymbolEqualityComparer.Default.Equals(
                       p.Type,
                       parentKeyType))
               ?? properties.FirstOrDefault(p =>
                   p.Name.StartsWith(
                       "Id",
                       StringComparison.OrdinalIgnoreCase) &&
                   SymbolEqualityComparer.Default.Equals(
                       p.Type,
                       parentKeyType));
    }

    // ---------------------------------------------------------------------
    // Génération du fichier
    // ---------------------------------------------------------------------

    private static string GenerateFile(
        EntityInfo info,
        Compilation compilation)
    {
        var parent = TypeName(info.ParentType);
        var key = TypeName(info.ParentKey.Type);

        var parentName = info.ParentType.Name;
        var graphName = parentName + "Graph";
        var graphInterface = "I" + graphName;
        var serviceName = parentName + "Service";
        var serviceInterface = "I" + serviceName;

        var graphType =
            $"{Abstraction}.EntityGraph<{parent}, {key}>";

        var ns = info.ParentType.ContainingNamespace.IsGlobalNamespace
            ? null
            : info.ParentType.ContainingNamespace.ToDisplayString();

        var cacheType = FindType(compilation, "IEntityCache", 2);

        if (cacheType is null)
        {
            throw new InvalidOperationException(
                "Impossible de trouver IEntityCache<T, TKey>. " +
                "Vérifie son namespace et les références du projet.");
        }

        var cacheTypeName = TypeNameWithoutTypeArguments(cacheType);

        var sb = new StringBuilder();

        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine("using System.Threading;");
        sb.AppendLine("using System.Threading.Tasks;");

        if (ns is not null)
        {
            sb.AppendLine();
            sb.Append("namespace ").Append(ns).AppendLine(";");
        }

        AppendGraphInterface(
            sb,
            info,
            parent,
            graphInterface);

        AppendGraphClass(
            sb,
            info,
            parent,
            key,
            graphName,
            graphInterface,
            graphType,
            cacheTypeName);

        AppendService(
            sb,
            info,
            parent,
            key,
            serviceName,
            serviceInterface,
            compilation,
            graphType);

        AppendDiExtensions(
            sb,
            info,
            compilation,
            graphType,
            graphName,
            graphInterface,
            serviceName,
            serviceInterface);

        return sb.ToString();
    }

    // ---------------------------------------------------------------------
    // Interface du graphe
    // ---------------------------------------------------------------------

    private static void AppendGraphInterface(
        StringBuilder sb,
        EntityInfo info,
        string parent,
        string graphInterface)
    {
        sb.AppendLine();
        sb.Append("public interface ")
            .Append(graphInterface)
            .AppendLine();
        sb.AppendLine("{");

        foreach (var relation in info.Relations)
        {
            sb.Append("    void Attach")
                .Append(relation.Collection.Name)
                .Append('(')
                .Append(parent)
                .Append(" person, global::System.Collections.Generic.IEnumerable<")
                .Append(TypeName(relation.ChildType))
                .AppendLine("> children);");
        }

        sb.AppendLine("}");
    }

    // ---------------------------------------------------------------------
    // Classe du graphe
    // ---------------------------------------------------------------------

    private static void AppendGraphClass(
        StringBuilder sb,
        EntityInfo info,
        string parent,
        string key,
        string graphName,
        string graphInterface,
        string graphType,
        string cacheTypeName)
    {
        sb.AppendLine();
        sb.Append("public sealed class ")
            .Append(graphName)
            .Append('(')
            .Append(cacheTypeName)
            .Append('<')
            .Append(parent)
            .Append(", ")
            .Append(key)
            .Append("> cache) : ")
            .Append(graphInterface)
            .AppendLine();

        sb.AppendLine("{");

        foreach (var relation in info.Relations)
        {
            AppendAttachMethod(sb, parent, relation);
            sb.AppendLine();
        }

        sb.Append("    public static ")
            .Append(graphType)
            .Append(" Create(")
            .Append(graphInterface)
            .AppendLine("? graph = null)");
        sb.AppendLine("    {");

        sb.Append("        var entityGraph = ")
            .Append(Abstraction)
            .Append(".EntityGraph.For<")
            .Append(parent)
            .Append(", ")
            .Append(key)
            .Append(">(p => p.")
            .Append(EscapeIdentifier(info.ParentKey.Name))
            .AppendLine(");");

        foreach (var relation in info.Relations)
            AppendHasMany(sb, relation);

        sb.AppendLine();
        sb.AppendLine("        return entityGraph;");
        sb.AppendLine("    }");
        sb.AppendLine("}");

    }

    // ---------------------------------------------------------------------
    // HasMany : utilise ForeignKey, par exemple IdContact
    // ---------------------------------------------------------------------

    private static void AppendHasMany(
        StringBuilder sb,
        RelationInfo relation)
    {
        if (relation.ChildKey is null ||
            relation.ForeignKey is null)
        {
            return;
        }

        var child = TypeName(relation.ChildType);
        var childKeyType = TypeName(relation.ChildKey.Type);

        sb.AppendLine();
        sb.Append("        entityGraph.HasMany<")
            .Append(child)
            .Append(", ")
            .Append(childKeyType)
            .AppendLine(">(");

        sb.Append("            p => p.")
            .Append(EscapeIdentifier(relation.Collection.Name))
            .AppendLine(",");

        sb.Append("            c => c.")
            .Append(EscapeIdentifier(relation.ForeignKey.Name))
            .AppendLine(",");

        sb.Append("            c => c.")
            .Append(EscapeIdentifier(relation.ChildKey.Name))
            .AppendLine(",");

        sb.Append("            onLoaded: graph is null ? null : ")
            .Append("(person, children) => graph.Attach")
            .Append(relation.Collection.Name)
            .AppendLine("(person, children));");
    }

    // ---------------------------------------------------------------------
    // Attach : LookupForeignKey est utilisé pour cache.Find
    // ---------------------------------------------------------------------

    private static void AppendAttachMethod(
        StringBuilder sb,
        string parent,
        RelationInfo relation)
    {
        var child = TypeName(relation.ChildType);

        sb.Append("    public void Attach")
            .Append(relation.Collection.Name)
            .Append('(')
            .Append(parent)
            .Append(" person, global::System.Collections.Generic.IEnumerable<")
            .Append(child)
            .AppendLine("> children)");

        sb.AppendLine("    {");
        sb.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(person);");
        sb.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(children);");
        sb.AppendLine();
        sb.AppendLine("        foreach (var child in children)");
        sb.AppendLine("        {");

        if (relation.LookupForeignKey is not null &&
            relation.LookupNavigation is not null &&
            relation.LookupNavigation.SetMethod is not null)
        {
            sb.Append("            var other = cache.Find(child.")
                .Append(EscapeIdentifier(relation.LookupForeignKey.Name))
                .AppendLine(");");

            sb.AppendLine("            if (other is not null)");
            sb.Append("                child.")
                .Append(EscapeIdentifier(relation.LookupNavigation.Name))
                .AppendLine(" = other;");
        }

        if (relation.ParentNavigation is not null &&
            relation.ParentNavigation.SetMethod is not null)
        {
            sb.Append("            child.")
                .Append(EscapeIdentifier(relation.ParentNavigation.Name))
                .AppendLine(" = person;");
        }

        sb.AppendLine("        }");
        sb.AppendLine("    }");
    }

    // ---------------------------------------------------------------------
    // Service
    // ---------------------------------------------------------------------

    private static void AppendService(
        StringBuilder sb,
        EntityInfo info,
        string parent,
        string key,
        string serviceName,
        string serviceInterface,
        Compilation compilation,
        string graphType)
    {
        var serviceBase = FindType(compilation, "EntityGraphService", 2);
        var entityStore = FindType(compilation, "IEntityStore");

        if (serviceBase is null || entityStore is null)
            return;

        var serviceBaseName = TypeNameWithoutTypeArguments(serviceBase);
        var entityStoreName = TypeName(entityStore);

        sb.AppendLine();
        sb.Append("public interface ")
            .Append(serviceInterface)
            .Append(" : ")
            .Append(Abstraction)
            .Append(".IDbTransaction<")
            .Append(parent)
            .AppendLine(">");
        sb.AppendLine("{");

        foreach (var relation in info.Relations)
        {
            sb.Append("    global::System.Threading.Tasks.Task Load")
                .Append(relation.Collection.Name)
                .Append("Async(")
                .Append(parent)
                .AppendLine(" entity, global::System.Threading.CancellationToken ct = default);");
        }

        sb.AppendLine("}");

        sb.AppendLine();
        sb.Append("public sealed class ")
            .Append(serviceName)
            .Append(" : ")
            .Append(serviceBaseName)
            .Append('<')
            .Append(parent)
            .Append(", ")
            .Append(key)
            .Append(">, ")
            .Append(serviceInterface)
            .AppendLine();
        sb.AppendLine("{");

        sb.Append("    public ")
            .Append(serviceName)
            .Append("(global::System.Func<")
            .Append(entityStoreName)
            .Append("> stores, ")
            .Append(graphType)
            .AppendLine(" graph, bool childrenSavedByServer)");
        sb.AppendLine("        : base(stores, graph, childrenSavedByServer)");
        sb.AppendLine("    {");
        sb.AppendLine("    }");

        foreach (var relation in info.Relations)
        {
            sb.AppendLine();
            sb.Append("    public global::System.Threading.Tasks.Task Load")
                .Append(relation.Collection.Name)
                .Append("Async(")
                .Append(parent)
                .AppendLine(" entity, global::System.Threading.CancellationToken ct = default)");
            sb.AppendLine("        => LoadChildrenAsync(entity, ct);");
        }

        sb.AppendLine("}");
    }

    // ---------------------------------------------------------------------
    // Extensions DI : une seule méthode Add{Entity}Services
    // ---------------------------------------------------------------------

    private static void AppendDiExtensions(
        StringBuilder sb,
        EntityInfo info,
        Compilation compilation,
        string graphType,
        string graphName,
        string graphInterface,
        string serviceName,
        string serviceInterface)
    {
        var serviceCollection = FindType(compilation, "IServiceCollection");
        var entityStore = FindType(compilation, "IEntityStore");

        if (serviceCollection is null ||
            entityStore is null)
        {
            return;
        }

        var collectionType = TypeName(serviceCollection);
        var entityStoreType = TypeName(entityStore);
        var parentName = info.ParentType.Name;

        sb.AppendLine();
        sb.Append("public static class ")
            .Append(parentName)
            .AppendLine("ServiceCollectionExtensions");
        sb.AppendLine("{");

        sb.Append("    public static ")
            .Append(collectionType)
            .Append(" Add")
            .Append(parentName)
            .Append("Contracts(this ")
            .Append(collectionType)
            .AppendLine(" services, bool childrenSavedByServer = false)");
        sb.AppendLine("    {");

        sb.AppendLine(
            "        global::System.ArgumentNullException.ThrowIfNull(services);");
        sb.AppendLine();

        // Enregistrement de l'interface et de l'implémentation du graphe.
        sb.Append("        global::Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<")
            .Append(graphInterface)
            .Append(", ")
            .Append(graphName)
            .AppendLine(">(services);");

        // Enregistrement du graphe EntityGraph.
        sb.Append("        global::Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<")
            .Append(graphType)
            .Append(">(services, sp => ")
            .Append(graphName)
            .Append(".Create(global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<")
            .Append(graphInterface)
            .AppendLine(">(sp)));");

        // Enregistrement du service : la résolution du store passe
        // exclusivement par IEntityStore.
        sb.Append("        global::Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<")
            .Append(serviceInterface)
            .Append(">(services, sp => new ")
            .Append(serviceName)
            .AppendLine("(");

        sb.Append("            () => global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<")
            .Append(entityStoreType)
            .AppendLine(">(sp),");

        sb.Append("            global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<")
            .Append(graphType)
            .AppendLine(">(sp),");

        sb.AppendLine("            childrenSavedByServer));");

        sb.AppendLine();
        sb.AppendLine("        return services;");
        sb.AppendLine("    }");
        sb.AppendLine("}");

    }

    // ---------------------------------------------------------------------
    // Recherche des symboles
    // ---------------------------------------------------------------------

    private static INamedTypeSymbol? FindType(
        Compilation compilation,
        string name,
        int? arity = null)
    {
        var assemblies = new List<IAssemblySymbol>
        {
            compilation.Assembly
        };

        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference)
                is IAssemblySymbol assembly)
            {
                assemblies.Add(assembly);
            }
        }

        foreach (var assembly in assemblies)
        {
            var found = FindTypeInNamespace(
                assembly.GlobalNamespace,
                name,
                arity);

            if (found is not null)
                return found;
        }

        return null;
    }

    private static INamedTypeSymbol? FindTypeInNamespace(
        INamespaceSymbol ns,
        string name,
        int? arity)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamedTypeSymbol type)
            {
                if (type.Name == name &&
                    (!arity.HasValue || type.Arity == arity.Value))
                {
                    return type;
                }

                var nested = FindNestedType(type, name, arity);

                if (nested is not null)
                    return nested;
            }
            else if (member is INamespaceSymbol childNamespace)
            {
                var found = FindTypeInNamespace(
                    childNamespace,
                    name,
                    arity);

                if (found is not null)
                    return found;
            }
        }

        return null;
    }

    private static INamedTypeSymbol? FindNestedType(
        INamedTypeSymbol type,
        string name,
        int? arity)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            if (nested.Name == name &&
                (!arity.HasValue || nested.Arity == arity.Value))
            {
                return nested;
            }

            var found = FindNestedType(nested, name, arity);

            if (found is not null)
                return found;
        }

        return null;
    }

    // ---------------------------------------------------------------------
    // Utilitaires
    // ---------------------------------------------------------------------

    private static string TypeName(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static string TypeNameWithoutTypeArguments(
        INamedTypeSymbol type)
    {
        var prefix = type.ContainingNamespace.IsGlobalNamespace
            ? "global::"
            : "global::" + type.ContainingNamespace.ToDisplayString() + ".";

        return prefix + type.Name;
    }

    private static string Sanitize(INamedTypeSymbol type) =>
        string.Join(
            "_",
            type.ToDisplayString()
                .Select(c => char.IsLetterOrDigit(c) ? c : '_'));

    private static string EscapeIdentifier(string identifier) =>
        SyntaxFacts.GetKeywordKind(identifier) != SyntaxKind.None
            ? "@" + identifier
            : identifier;

    // ---------------------------------------------------------------------
    // Modèles internes
    // ---------------------------------------------------------------------

    private sealed record GraphInfo(
        INamedTypeSymbol ParentType,
        IPropertySymbol Property,
        INamedTypeSymbol ChildType,
        string? ParentNavigation,
        string? LookupNavigation,
        string? ForeignKey,
        string? LookupForeignKey);

    private sealed record RelationInfo(
        IPropertySymbol Collection,
        INamedTypeSymbol ChildType,
        IPropertySymbol? ChildKey,
        IPropertySymbol? ForeignKey,
        IPropertySymbol? LookupForeignKey,
        IPropertySymbol? ParentNavigation,
        IPropertySymbol? LookupNavigation);

    private sealed record EntityInfo(
        INamedTypeSymbol ParentType,
        IPropertySymbol ParentKey,
        ImmutableArray<RelationInfo> Relations);
}