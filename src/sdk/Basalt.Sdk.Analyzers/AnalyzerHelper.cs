#nullable enable
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Basalt.Sdk.Analyzers;

internal static class AnalyzerHelper
{
    private const string BasaltContractFullName = "Basalt.Sdk.Contracts.BasaltContractAttribute";

    /// <summary>
    /// Syntactic-only check (kept for backward compatibility with analyzers that
    /// don't have a <see cref="SemanticModel"/>).
    /// </summary>
    public static bool IsInsideBasaltContract(SyntaxNode node)
    {
        var classDecl = node.FirstAncestorOrSelf<ClassDeclarationSyntax>();
        if (classDecl == null)
            return false;

        foreach (var attrList in classDecl.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var name = attr.Name.ToString();
                if (name == "BasaltContract" || name == "BasaltContractAttribute"
                    || name == "Basalt.Sdk.Contracts.BasaltContract"
                    || name == "Basalt.Sdk.Contracts.BasaltContractAttribute")
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// H-01: Semantic model check — resolves the attribute symbol and compares
    /// against the fully-qualified name. Handles aliases, fully-qualified usage,
    /// and using aliases correctly.
    /// </summary>
    public static bool IsInsideBasaltContract(SyntaxNode node, SemanticModel semanticModel, CancellationToken ct = default)
    {
        var classDecl = node.FirstAncestorOrSelf<ClassDeclarationSyntax>();
        if (classDecl == null)
            return false;

        var symbol = semanticModel.GetDeclaredSymbol(classDecl, ct) as INamedTypeSymbol;
        if (symbol == null)
            return false;

        // Walk the type hierarchy to check for the attribute
        var current = symbol;
        while (current != null)
        {
            foreach (var attr in current.GetAttributes())
            {
                if (attr.AttributeClass?.ToDisplayString() == BasaltContractFullName)
                    return true;
            }
            current = current.BaseType;
        }

        return false;
    }
}
