#nullable enable
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Basalt.Sdk.Analyzers;

internal static class AnalyzerHelper
{
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
                if (name == "BasaltContract" || name == "BasaltContractAttribute")
                    return true;
            }
        }

        return false;
    }
}
