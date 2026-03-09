#nullable enable
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Basalt.Sdk.Analyzers;

/// <summary>
/// BST011: Warns when Dictionary&lt;,&gt; or HashSet&lt;&gt; are used inside a
/// BasaltContract. These collections have non-deterministic iteration order
/// across runtimes, which breaks consensus.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CollectionOrderingAnalyzer : DiagnosticAnalyzer
{
    private static readonly string[] BannedTypeNames =
    {
        "Dictionary", "HashSet",
    };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(DiagnosticIds.NonDeterministicCollection);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeGenericName, SyntaxKind.GenericName);
    }

    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
    {
        var creation = (ObjectCreationExpressionSyntax)context.Node;

        if (!AnalyzerHelper.IsInsideBasaltContract(creation))
            return;

        var typeName = GetUnqualifiedTypeName(creation.Type);
        if (typeName == null) return;

        for (int i = 0; i < BannedTypeNames.Length; i++)
        {
            if (typeName.StartsWith(BannedTypeNames[i]))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticIds.NonDeterministicCollection,
                    creation.GetLocation(),
                    $"new {typeName}() — hash-based collections have non-deterministic iteration order; use StorageMap or SortedDictionary instead"));
                return;
            }
        }
    }

    private static void AnalyzeGenericName(SyntaxNodeAnalysisContext context)
    {
        var genericName = (GenericNameSyntax)context.Node;

        if (!AnalyzerHelper.IsInsideBasaltContract(genericName))
            return;

        // Only flag field declarations (not local variables used transiently)
        if (genericName.Parent is not TypeSyntax typeParent)
            return;

        // Walk up to find if this is a field declaration
        var current = typeParent.Parent;
        while (current != null)
        {
            if (current is FieldDeclarationSyntax)
            {
                var typeName = genericName.Identifier.Text;
                for (int i = 0; i < BannedTypeNames.Length; i++)
                {
                    if (typeName == BannedTypeNames[i])
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            DiagnosticIds.NonDeterministicCollection,
                            genericName.GetLocation(),
                            $"{typeName}<> field — hash-based collections have non-deterministic iteration order; use StorageMap or SortedDictionary instead"));
                        return;
                    }
                }
                break;
            }
            if (current is MemberDeclarationSyntax)
                break;
            current = current.Parent;
        }
    }

    private static string? GetUnqualifiedTypeName(TypeSyntax type)
    {
        return type switch
        {
            GenericNameSyntax generic => generic.Identifier.Text,
            QualifiedNameSyntax qualified => GetUnqualifiedTypeName(qualified.Right),
            IdentifierNameSyntax id => id.Identifier.Text,
            _ => null,
        };
    }
}
