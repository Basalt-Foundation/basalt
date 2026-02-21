#nullable enable
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Basalt.Sdk.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AotCompatibilityAnalyzer : DiagnosticAnalyzer
{
    private static readonly string[] BannedMemberAccesses = new[]
    {
        "Type.MakeGenericType",
        "Activator.CreateInstance",
        "Assembly.LoadFrom",
        "Assembly.Load",
        "Task.Run",
        "Parallel.For",
        "Parallel.ForEach",
        "Parallel.Invoke",
        "File.ReadAllText",
        "File.WriteAllText",
        "File.ReadAllBytes",
        "File.WriteAllBytes",
        "File.Exists",
        "File.Delete",
        "File.Open",
        "Directory.Exists",
        "Directory.CreateDirectory",
    };

    private static readonly string[] BannedConstructorTypes = new[]
    {
        "Thread",
        "HttpClient",
        "TcpClient",
        "Socket",
        "WebClient",
        "FileStream",
        "StreamReader",
        "StreamWriter",
    };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(DiagnosticIds.AotIncompatible);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeMemberAccess, SyntaxKind.SimpleMemberAccessExpression);
        context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression);
    }

    // H-03: Use semantic model to resolve the actual containing type and member name,
    // instead of string.Contains() which causes false positives on user types.
    private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context)
    {
        if (!AnalyzerHelper.IsInsideBasaltContract(context.Node))
            return;

        var memberAccess = (MemberAccessExpressionSyntax)context.Node;

        // First try semantic resolution
        var symbolInfo = context.SemanticModel.GetSymbolInfo(memberAccess, context.CancellationToken);
        var symbol = symbolInfo.Symbol;

        if (symbol != null)
        {
            var containingType = symbol.ContainingType?.ToDisplayString();
            var memberName = symbol.Name;

            if (containingType != null)
            {
                var fullQualified = containingType + "." + memberName;
                foreach (var banned in BannedMemberAccesses)
                {
                    // Match "Type.Member" against "Namespace.Type.Member"
                    if (fullQualified.EndsWith(banned))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            DiagnosticIds.AotIncompatible,
                            memberAccess.GetLocation(),
                            banned));
                        return;
                    }
                }
            }
        }
        else
        {
            // Fallback to syntactic matching if semantic model can't resolve
            var expressionText = memberAccess.Expression.ToString();
            var memberName = memberAccess.Name.Identifier.Text;
            var syntacticFull = expressionText + "." + memberName;

            foreach (var banned in BannedMemberAccesses)
            {
                if (syntacticFull == banned)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticIds.AotIncompatible,
                        memberAccess.GetLocation(),
                        banned));
                    return;
                }
            }
        }
    }

    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
    {
        if (!AnalyzerHelper.IsInsideBasaltContract(context.Node))
            return;

        var creation = (ObjectCreationExpressionSyntax)context.Node;
        var typeName = creation.Type.ToString();

        foreach (var banned in BannedConstructorTypes)
        {
            if (typeName == banned || typeName.EndsWith("." + banned))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticIds.AotIncompatible,
                    creation.GetLocation(),
                    $"new {banned}()"));
                return;
            }
        }
    }
}
