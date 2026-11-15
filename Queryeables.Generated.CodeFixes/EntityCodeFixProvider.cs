using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;

namespace Queryeables.Generated.CodeFixes;

/// <summary>
/// Fixes the generator's diagnostics in one click, from what each tells in its properties (key <c>QueryeablesFix</c>):
/// adds the missing key (QRY001), adds the missing foreign key (QRY003, QRY006), gives a foreign key or a navigation the
/// type it needs (QRY005, QRY007, QRY008). The class changed may be in another file than the diagnostic.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(EntityCodeFixProvider)), Shared]
public sealed class EntityCodeFixProvider : CodeFixProvider
{
    private const string FixKey = "QueryeablesFix";

    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create("QRY001", "QRY003", "QRY004", "QRY005", "QRY006", "QRY007", "QRY008");

    public override FixAllProvider? GetFixAllProvider() => null;      // each fix reads its own compilation

    public override Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        foreach (var diagnostic in context.Diagnostics)
        {
            if (!diagnostic.Properties.TryGetValue(FixKey, out var fix) || fix is null)
                continue;
            var parts = fix.Split('|');
            var title = parts[0] switch
            {
                "AddKey" when parts.Length == 2 => $"Add the key Id to {Short(parts[1])}",
                "AddProperty" when parts.Length == 4 => $"Add {parts[2]} to {Short(parts[1])}",
                "ChangeType" when parts.Length == 4 => $"Make {Short(parts[1])}.{parts[2]} a {Short(parts[3])}",
                _ => null,
            };
            if (title is null)
                continue;
            context.RegisterCodeFix(
                CodeAction.Create(title, ct => ApplyAsync(context.Document.Project.Solution, parts, ct), equivalenceKey: fix),
                diagnostic);
        }
        return Task.CompletedTask;
    }

    private static string Short(string name)
    {
        var bare = name.Replace("global::", string.Empty).TrimEnd('?');
        var dot = bare.LastIndexOf('.');
        return dot >= 0 ? bare.Substring(dot + 1) : bare;
    }

    private static async Task<Solution> ApplyAsync(Solution solution, string[] fix, CancellationToken ct)
    {
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
            if (compilation?.GetTypeByMetadataName(fix[1]) is not { } type
                || type.DeclaringSyntaxReferences.FirstOrDefault() is not { } reference
                || solution.GetDocument(reference.SyntaxTree) is not { } document)
                continue;

            var root = await reference.SyntaxTree.GetRootAsync(ct).ConfigureAwait(false);
            if (await reference.GetSyntaxAsync(ct).ConfigureAwait(false) is not TypeDeclarationSyntax declaration)
                return solution;

            TypeDeclarationSyntax changed;
            switch (fix[0])
            {
                case "AddKey":
                    changed = declaration.WithMembers(declaration.Members.Insert(0, Property("long", "Id")));
                    break;
                case "AddProperty":
                    changed = declaration.AddMembers(Property(fix[3], fix[2]));
                    break;
                default:
                    var property = declaration.Members.OfType<PropertyDeclarationSyntax>().FirstOrDefault(p => p.Identifier.ValueText == fix[2]);
                    if (property is null)
                        return solution;
                    changed = declaration.ReplaceNode(property, property.WithType(TypeOf(fix[3]).WithTriviaFrom(property.Type)));
                    break;
            }
            return solution.WithDocumentSyntaxRoot(document.Id, root.ReplaceNode(declaration, changed));
        }
        return solution;
    }

    private static TypeSyntax TypeOf(string name)
        => SyntaxFactory.ParseTypeName(name).WithAdditionalAnnotations(Simplifier.Annotation);

    /// <summary><c>public {type} {name} { get; set; }</c>, formatted and simplified (global:: removed).</summary>
    private static PropertyDeclarationSyntax Property(string type, string name)
        => SyntaxFactory.PropertyDeclaration(TypeOf(type), name)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .AddAccessorListAccessors(
                SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)),
                SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)))
            .WithAdditionalAnnotations(Formatter.Annotation, Simplifier.Annotation);
}
