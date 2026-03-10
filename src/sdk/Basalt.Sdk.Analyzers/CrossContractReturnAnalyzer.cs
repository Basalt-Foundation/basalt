#nullable enable
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Basalt.Sdk.Analyzers;

/// <summary>
/// BST009: Warns when Context.CallContract&lt;bool&gt; return value is discarded
/// (used as a statement expression rather than checked in a condition or Require).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CrossContractReturnAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(DiagnosticIds.UncheckedCrossContractReturn);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSyntaxNodeAction(AnalyzeExpressionStatement, SyntaxKind.ExpressionStatement);
    }

    private static void AnalyzeExpressionStatement(SyntaxNodeAnalysisContext context)
    {
        var exprStmt = (ExpressionStatementSyntax)context.Node;

        if (!AnalyzerHelper.IsInsideBasaltContract(exprStmt))
            return;

        if (exprStmt.Expression is not InvocationExpressionSyntax invocation)
            return;

        // Use semantic model to resolve the invocation target
        var symbolInfo = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken);
        if (symbolInfo.Symbol is not IMethodSymbol method)
            return;

        // Check for Context.CallContract<bool> — a generic method named CallContract
        // on a type named Context, with bool as the type argument
        if (method.Name != "CallContract" || !method.IsGenericMethod)
            return;

        if (method.ContainingType?.Name != "Context")
            return;

        if (method.TypeArguments.Length == 1 &&
            method.TypeArguments[0].SpecialType == SpecialType.System_Boolean)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticIds.UncheckedCrossContractReturn,
                invocation.GetLocation(),
                "Context.CallContract<bool>() return value discarded — check the result with Context.Require()"));
        }
    }
}
