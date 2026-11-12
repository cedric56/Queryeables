using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Queryeables.Abstraction;
using Queryeables.Generated;

namespace Queryeables.Tests;

/// <summary>The generator on small sources: mistakes become diagnostics, and the generated code compiles.</summary>
public sealed class GeneratorDiagnosticsTests
{
    private static (ImmutableArrayDiagnostics Generator, ImmutableArrayDiagnostics Compilation, string[] Sources) Run(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Append(typeof(EntityGraphAttribute).Assembly.Location)
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();

        var compilation = CSharpCompilation.Create("Sample",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var driver = CSharpGeneratorDriver.Create(new EntityGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);

        var sources = driver.GetRunResult().GeneratedTrees.Select(t => t.ToString()).ToArray();
        // Errors anywhere, and warnings in the generated code: an application may treat warnings as errors.
        var problems = output.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error
                        || d.Severity == DiagnosticSeverity.Warning && d.Location.SourceTree?.FilePath.EndsWith(".g.cs", StringComparison.Ordinal) == true)
            .ToArray();
        return (new(generatorDiagnostics), new(problems), sources);
    }

    public readonly record struct ImmutableArrayDiagnostics(IReadOnlyList<Diagnostic> Items)
    {
        public IEnumerable<string> Ids => Items.Select(d => d.Id);
    }

    private const string Usings = "using System.Collections.Generic; using Queryeables.Abstraction;\n";

    [Fact]
    public void A_valid_graph_compiles_without_diagnostics()
    {
        var (generator, compilation, sources) = Run(Usings + """
            namespace Shop;
            public class Order { public long Id { get; set; } [EntityGraph(LookupNavigation = nameof(Line.Product))] public List<Line> Lines { get; set; } = new(); }
            public class Line { public long Id { get; set; } public long OrderId { get; set; } public long ProductId { get; set; } public Product? Product { get; set; } }
            public class Product { public long Id { get; set; } }
            """);

        Assert.Empty(generator.Ids);
        Assert.Empty(compilation.Ids);
        var graph = Assert.Single(sources, s => s.Contains("class OrderGraph"));
        Assert.Contains("IEntityCache<global::Shop.Product, long> productCache", graph);   // the cache of the looked-up type
    }

    [Fact]
    public void A_graph_without_lookup_has_a_parameterless_graph()
    {
        var (generator, compilation, sources) = Run(Usings + """
            public class Folder { public int Id { get; set; } [EntityGraph] public List<Note> Notes { get; set; } = new(); }
            public class Note { public int Id { get; set; } public int? FolderId { get; set; } }
            """);

        Assert.Empty(generator.Ids);
        Assert.Empty(compilation.Ids);
        Assert.Contains(sources, s => s.Contains("public FolderGraph()"));
    }

    [Fact]
    public void A_missing_key_is_reported()
        => Assert.Contains("QRY001", Run(Usings + """
            public class Folder { public string Name { get; set; } = ""; [EntityGraph] public List<Note> Notes { get; set; } = new(); }
            public class Note { public int Id { get; set; } public int FolderId { get; set; } }
            """).Generator.Ids);

    [Fact]
    public void A_property_that_is_not_an_entity_is_reported()
        => Assert.Contains("QRY002", Run(Usings + """
            public class Folder { public int Id { get; set; } [EntityGraph] public string Note { get; set; } = ""; }
            """).Generator.Ids);

    [Fact]
    public void One_child_a_link_table_and_a_reference_are_described()
    {
        var run = Run(Usings + """
            public class Folder
            {
                public int Id { get; set; }
                public int? OwnerId { get; set; }
                [EntityGraph] public Note? Note { get; set; }
                [EntityGraph(Through = typeof(FolderTag))] public List<Tag>? Tags { get; set; }
                [EntityReference] public Owner? Owner { get; set; }
            }
            public class Note { public int Id { get; set; } public int FolderId { get; set; } }
            public class Tag { public int Id { get; set; } }
            public class FolderTag { [System.ComponentModel.DataAnnotations.Key] public int FolderId { get; set; } [System.ComponentModel.DataAnnotations.Key] public int TagId { get; set; } }
            public class Owner { public int Id { get; set; } }
            """);
        Assert.Empty(run.Generator.Ids);
        Assert.Empty(run.Compilation.Ids);
        var graph = Assert.Single(run.Sources, s => s.Contains("class FolderGraph"));
        Assert.Contains("created.HasOne<", graph);
        Assert.Contains("created.HasManyThrough<global::FolderTag, global::Tag, int>", graph);
        Assert.Contains("created.References<global::Owner, int>(p => p.Owner, \"OwnerId\"", graph);
    }

    [Fact]
    public void A_tree_and_a_second_level_are_described()
    {
        var run = Run(Usings + """
            public class Order { public int Id { get; set; } [EntityGraph] public List<Line>? Lines { get; set; } }
            public class Line { public int Id { get; set; } public int OrderId { get; set; } [EntityGraph] public List<Allocation>? Allocations { get; set; } }
            public class Allocation { public int Id { get; set; } public int LineId { get; set; } }
            public class Task { public int Id { get; set; } public int? TaskId { get; set; } [EntityGraph(ForeignKey = nameof(TaskId))] public List<Task>? SubTasks { get; set; } }
            """);
        Assert.Empty(run.Generator.Ids);
        Assert.Empty(run.Compilation.Ids);
        Assert.Contains(run.Sources, s => s.Contains("class OrderGraph") && s.Contains("global::LineGraph.Create(null, services)"));
        Assert.Contains(run.Sources, s => s.Contains("class TaskGraph") && s.Contains("created);"));
    }

    [Fact]
    public void A_link_without_the_key_of_the_parent_is_reported()
        => Assert.Contains("QRY003", Run(Usings + """
            public class Folder { public int Id { get; set; } [EntityGraph(Through = typeof(FolderTag))] public List<Tag>? Tags { get; set; } }
            public class Tag { public int Id { get; set; } }
            public class FolderTag { public int Id { get; set; } public int TagId { get; set; } }
            """).Generator.Ids);

    [Fact]
    public void A_missing_foreign_key_is_reported()
        => Assert.Contains("QRY003", Run(Usings + """
            public class Folder { public int Id { get; set; } [EntityGraph] public List<Note> Notes { get; set; } = new(); }
            public class Note { public int Id { get; set; } public int Parent { get; set; } }
            """).Generator.Ids);

    [Fact]
    public void A_misnamed_property_is_reported()
        => Assert.Contains("QRY004", Run(Usings + """
            public class Folder { public int Id { get; set; } [EntityGraph(ForeignKey = "Nope")] public List<Note> Notes { get; set; } = new(); }
            public class Note { public int Id { get; set; } public int FolderId { get; set; } }
            """).Generator.Ids);

    [Fact]
    public void A_foreign_key_of_the_wrong_type_is_reported()
        => Assert.Contains("QRY005", Run(Usings + """
            public class Folder { public int Id { get; set; } [EntityGraph] public List<Note> Notes { get; set; } = new(); }
            public class Note { public int Id { get; set; } public string FolderId { get; set; } = ""; }
            """).Generator.Ids);

    [Fact]
    public void A_missing_lookup_key_is_reported()
        => Assert.Contains("QRY006", Run(Usings + """
            public class Folder { public int Id { get; set; } [EntityGraph(LookupNavigation = nameof(Note.Tag))] public List<Note> Notes { get; set; } = new(); }
            public class Note { public int Id { get; set; } public int FolderId { get; set; } public Tag? Tag { get; set; } }
            public class Tag { public int Id { get; set; } }
            """).Generator.Ids);

    [Fact]
    public void A_self_referencing_tree_with_a_Children_collection_compiles()
    {
        var (generator, compilation, _) = Run(Usings + """
            public abstract class ModelBase { public long Id { get; set; } }
            [EntityCache]
            public class Node : ModelBase
            {
                public long? ParentId { get; set; }
                public Node? Parent { get; set; }
                [EntityGraph(ParentNavigation = nameof(Parent), ForeignKey = nameof(ParentId))]
                public ICollection<Node> Children { get; set; } = new List<Node>();
            }
            """);

        Assert.Empty(generator.Ids);
        Assert.Empty(compilation.Ids);
    }

    [Fact]
    public void A_parent_navigation_to_another_type_is_reported()
        => Assert.Contains("QRY008", Run(Usings + """
            public class Person { public long Id { get; set; } [EntityGraph(ParentNavigation = nameof(Line.Product))] public List<Line> Lines { get; set; } = new(); }
            public class Line { public long Id { get; set; } public long PersonId { get; set; } public Product? Product { get; set; } }
            public class Product { public long Id { get; set; } }
            """).Generator.Ids);

    [Fact]
    public void A_lookup_key_that_is_the_key_of_the_child_is_reported()
        => Assert.Contains("QRY009", Run(Usings + """
            public class Person { public long Id { get; set; } [EntityGraph(LookupNavigation = nameof(Line.Product), LookupForeignKey = nameof(Product.Id))] public List<Line> Lines { get; set; } = new(); }
            public class Line { public long Id { get; set; } public long PersonId { get; set; } public long IdProduct { get; set; } public Product? Product { get; set; } }
            public class Product { public long Id { get; set; } }
            """).Generator.Ids);

    [Fact]
    public void Two_parents_with_the_same_name_do_not_collide()
    {
        var (generator, compilation, _) = Run(Usings + """
            namespace A { public class Folder { public int Id { get; set; } [EntityGraph] public List<Note> Notes { get; set; } = new(); } public class Note { public int Id { get; set; } public int FolderId { get; set; } } }
            namespace B { public class Folder { public int Id { get; set; } [EntityGraph] public List<Note> Notes { get; set; } = new(); } public class Note { public int Id { get; set; } public int FolderId { get; set; } } }
            """);

        Assert.Empty(generator.Ids);
        Assert.Empty(compilation.Ids);
    }
}
