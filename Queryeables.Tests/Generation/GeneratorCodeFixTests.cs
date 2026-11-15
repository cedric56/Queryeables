using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Queryeables.Abstraction;
using Queryeables.Generated;
using Queryeables.Generated.CodeFixes;

namespace Queryeables.Tests;

/// <summary>The code fixes of the generator's diagnostics: applied, the diagnostic is gone.</summary>
public sealed class GeneratorCodeFixTests
{
    private static readonly MetadataReference[] References = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Append(typeof(EntityGraphAttribute).Assembly.Location)
        .Where(path => path.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
        .ToArray();

    private static IReadOnlyList<Diagnostic> Diagnose(Compilation compilation)
    {
        CSharpGeneratorDriver.Create(new EntityGenerator()).RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);
        return diagnostics;
    }

    /// <summary>Applies the fix of the first diagnostic <paramref name="id"/>; returns the source after it, and the diagnostics left.</summary>
    private static async Task<(string Source, IReadOnlyList<Diagnostic> Left)> FixAsync(string source, string id)
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("Sample", LanguageNames.CSharp)
            .WithMetadataReferences(References)
            .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var document = project.AddDocument("Entities.cs", source);
        var compilation = (await document.Project.GetCompilationAsync())!;
        var diagnostic = Diagnose(compilation).First(d => d.Id == id);

        var actions = new List<CodeAction>();
        var context = new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None);
        await new EntityCodeFixProvider().RegisterCodeFixesAsync(context);
        var operation = (await Assert.Single(actions).GetOperationsAsync(CancellationToken.None)).OfType<ApplyChangesOperation>().Single();

        var fixedDocument = operation.ChangedSolution.GetDocument(document.Id)!;
        var fixedCompilation = (await fixedDocument.Project.GetCompilationAsync())!;
        return ((await fixedDocument.GetTextAsync()).ToString(), Diagnose(fixedCompilation));
    }

    private const string Usings = "using System.Collections.Generic; using Queryeables.Abstraction;\n";

    [Fact]
    public async Task An_entity_without_key_gets_an_Id()
    {
        var (source, left) = await FixAsync(Usings + """
            namespace Shop;
            [EntityCache]
            public class Tag { public string Name { get; set; } = ""; }
            """, "QRY001");

        Assert.Contains("public long Id { get; set; }", source);
        Assert.Empty(left);
    }

    [Fact]
    public async Task A_child_without_foreign_key_gets_one_of_the_type_of_the_parent_key()
    {
        var (source, left) = await FixAsync(Usings + """
            namespace Shop;
            public class Order { public System.Guid Id { get; set; } [EntityGraph] public List<Line>? Lines { get; set; } }
            public class Line { public long Id { get; set; } }
            """, "QRY003");

        Assert.Contains("public Guid OrderId { get; set; }", source.Replace("System.Guid", "Guid"));
        Assert.Empty(left);
    }

    [Fact]
    public async Task A_foreign_key_of_the_wrong_type_takes_the_type_of_the_parent_key()
    {
        var (source, left) = await FixAsync(Usings + """
            namespace Shop;
            public class Order { public long Id { get; set; } [EntityGraph] public List<Line>? Lines { get; set; } }
            public class Line { public long Id { get; set; } public string OrderId { get; set; } = ""; }
            """, "QRY005");

        Assert.Contains("public long OrderId { get; set; }", source);
        Assert.Empty(left);
    }
}
