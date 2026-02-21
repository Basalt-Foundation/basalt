#nullable enable
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Basalt.Sdk.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DeterminismAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(DiagnosticIds.NonDeterminism);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSyntaxNodeAction(AnalyzeMemberAccess, SyntaxKind.SimpleMemberAccessExpression);
        context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression);
        context.RegisterSyntaxNodeAction(AnalyzePredefinedType, SyntaxKind.PredefinedType);
    }

    // M-03: Non-deterministic type/member pairs to check via semantic model
    private static readonly (string Type, string[] Members)[] NonDeterministicApis =
    {
        ("System.DateTime", new[] { "Now", "UtcNow" }),
        ("System.DateTimeOffset", new[] { "Now", "UtcNow" }),
        ("System.Guid", new[] { "NewGuid" }),
        ("System.Environment", new[] { "TickCount", "TickCount64" }),
    };

    private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context)
    {
        var memberAccess = (MemberAccessExpressionSyntax)context.Node;

        if (!AnalyzerHelper.IsInsideBasaltContract(memberAccess))
            return;

        var memberName = memberAccess.Name.Identifier.Text;

        // M-03: Try semantic model first to catch fully-qualified and aliased calls
        var symbolInfo = context.SemanticModel.GetSymbolInfo(memberAccess, context.CancellationToken);
        var symbol = symbolInfo.Symbol;
        if (symbol != null)
        {
            var containingType = symbol.ContainingType?.ToDisplayString();
            if (containingType != null)
            {
                foreach (var (type, members) in NonDeterministicApis)
                {
                    if (containingType == type)
                    {
                        foreach (var m in members)
                        {
                            if (memberName == m)
                            {
                                Report(context, memberAccess, $"{type.Substring(type.LastIndexOf('.') + 1)}.{memberName}");
                                return;
                            }
                        }
                    }
                }
            }
        }
        else
        {
            // Fallback: syntactic check for simple names
            var expressionText = memberAccess.Expression.ToString();
            var fullText = $"{expressionText}.{memberName}";

            if ((expressionText == "DateTime" || expressionText == "DateTimeOffset") && (memberName == "Now" || memberName == "UtcNow"))
            {
                Report(context, memberAccess, fullText);
                return;
            }
            if (expressionText == "Guid" && memberName == "NewGuid")
            {
                Report(context, memberAccess, fullText);
                return;
            }
            if (expressionText == "Environment" && (memberName == "TickCount" || memberName == "TickCount64"))
            {
                Report(context, memberAccess, fullText);
                return;
            }
        }
    }

    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
    {
        var creation = (ObjectCreationExpressionSyntax)context.Node;

        if (!AnalyzerHelper.IsInsideBasaltContract(creation))
            return;

        var typeName = creation.Type.ToString();
        if (typeName == "Random" || typeName == "System.Random")
        {
            // Verify via semantic model
            var typeInfo = context.SemanticModel.GetTypeInfo(creation, context.CancellationToken);
            var fullName = typeInfo.Type?.ToDisplayString();
            if (fullName == "System.Random")
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticIds.NonDeterminism,
                    creation.GetLocation(),
                    "new Random() — use deterministic randomness from contract context"));
            }
        }
    }

    private static void AnalyzePredefinedType(SyntaxNodeAnalysisContext context)
    {
        var predefinedType = (PredefinedTypeSyntax)context.Node;

        if (!AnalyzerHelper.IsInsideBasaltContract(predefinedType))
            return;

        var keyword = predefinedType.Keyword.Text;

        if (keyword == "float" || keyword == "double" || keyword == "decimal")
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticIds.NonDeterminism,
                predefinedType.GetLocation(),
                $"'{keyword}' type — floating point and decimal types are non-deterministic across platforms"));
        }
    }

    private static void Report(SyntaxNodeAnalysisContext context, SyntaxNode node, string message)
    {
        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticIds.NonDeterminism,
            node.GetLocation(),
            message));
    }
}
