#nullable enable
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Basalt.Sdk.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class OverflowAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(DiagnosticIds.OverflowRisk);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeUnchecked, SyntaxKind.UncheckedExpression, SyntaxKind.UncheckedStatement);
    }

    private static void AnalyzeUnchecked(SyntaxNodeAnalysisContext context)
    {
        if (!AnalyzerHelper.IsInsideBasaltContract(context.Node))
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticIds.OverflowRisk,
            context.Node.GetLocation(),
            "unchecked arithmetic may overflow"));
    }
}
