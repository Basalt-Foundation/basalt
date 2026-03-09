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

        // Check if the expression is an invocation
        if (exprStmt.Expression is not InvocationExpressionSyntax invocation)
            return;

        var exprText = invocation.Expression.ToString();

        // Match Context.CallContract<bool>(...) used as a statement (return value discarded)
        if (exprText.Contains("CallContract<bool>") ||
            exprText.Contains("CallContract<Bool>"))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticIds.UncheckedCrossContractReturn,
                invocation.GetLocation(),
                "Context.CallContract<bool>() return value discarded — check the result with Context.Require()"));
        }
    }
}
