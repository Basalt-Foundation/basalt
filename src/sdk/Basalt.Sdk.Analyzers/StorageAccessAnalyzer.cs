#nullable enable
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Basalt.Sdk.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StorageAccessAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(DiagnosticIds.RawStorageAccess);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeMemberAccess, SyntaxKind.SimpleMemberAccessExpression);
    }

    private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context)
    {
        if (!AnalyzerHelper.IsInsideBasaltContract(context.Node))
            return;

        var memberAccess = (MemberAccessExpressionSyntax)context.Node;
        var expr = memberAccess.Expression.ToString();
        var member = memberAccess.Name.Identifier.Text;

        if (expr == "ContractStorage" && (member == "Read" || member == "Write"))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticIds.RawStorageAccess,
                memberAccess.GetLocation(),
                $"ContractStorage.{member}"));
        }
    }
}
