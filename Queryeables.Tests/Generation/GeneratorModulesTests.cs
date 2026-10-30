using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Queryeables.Abstraction;
using Queryeables.Generated;

namespace Queryeables.Tests;

/// <summary>
/// Entities declared in a shared library: <c>AddGeneratedEntities()</c> in the client or the server must register them too.
/// </summary>
public sealed class GeneratorModulesTests
{
    private static readonly MetadataReference[] References = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Append(typeof(EntityGraphAttribute).Assembly.Location)
        .Append(typeof(QueryeablesBuilder).Assembly.Location)
        .Append(typeof(IServiceCollection).Assembly.Location)
        .Where(path => path.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
        .ToArray();

    private static (Compilation Output, string[] Sources) Generate(string assemblyName, string source, params MetadataReference[] extra)
    {
        var compilation = CSharpCompilation.Create(assemblyName,
            [CSharpSyntaxTree.ParseText(source)],
            References.Concat(extra),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var driver = CSharpGeneratorDriver.Create(new EntityGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        return (output, driver.GetRunResult().GeneratedTrees.Select(t => t.ToString()).ToArray());
    }

    [Fact]
    public void AddGeneratedEntities_registers_the_entities_of_a_referenced_library()
    {
        var (models, modelSources) = Generate("Shop.Models", """
            using System.Collections.Generic; using Queryeables.Abstraction;
            namespace Shop;
            [EntityCache] public class Order { public long Id { get; set; } [EntityGraph] public List<Line>? Lines { get; set; } }
            public class Line { public long Id { get; set; } public long OrderId { get; set; } }
            """);
        Assert.Contains(modelSources, s => s.Contains("public static class Entities_Shop_Models"));

        using var image = new MemoryStream();
        Assert.True(models.Emit(image).Success);
        var modelsReference = MetadataReference.CreateFromImage(image.ToArray());

        // The server has no entity of its own, and calls its own AddGeneratedEntities.
        var (_, serverSources) = Generate("Shop.Server", """
            public static class Startup
            {
                public static void Configure(Queryeables.QueryeablesBuilder q) => Queryeables.Generated.GeneratedEntities.AddGeneratedEntities(q);
            }
            """, modelsReference);

        var registration = Assert.Single(serverSources, s => s.Contains("AddGeneratedEntities"));
        Assert.Contains("global::Queryeables.Generated.Modules.Entities_Shop_Models.Register(builder);", registration);
        Assert.False(serverSources.Any(s => s.Contains("public static class Entities_")));
    }
}
