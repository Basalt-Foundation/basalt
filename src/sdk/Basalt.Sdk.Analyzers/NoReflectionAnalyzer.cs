#nullable enable
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Basalt.Sdk.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NoReflectionAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(DiagnosticIds.NoReflection);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSyntaxNodeAction(AnalyzeUsingDirective, SyntaxKind.UsingDirective);
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeUsingDirective(SyntaxNodeAnalysisContext context)
    {
        var usingDirective = (UsingDirectiveSyntax)context.Node;

        if (!AnalyzerHelper.IsInsideBasaltContract(usingDirective))
        {
            // Using directives at file level: check if any containing class is a contract.
            // For file-scoped usings we still flag them if they import System.Reflection.
            // However, we only flag usings that are inside a contract namespace or at top level
            // when the compilation unit contains a contract class. For simplicity, flag always
            // if the using is for System.Reflection and is inside a contract class.
            // Top-level usings are not inside a class, so we need a different approach:
            // We still flag them since they enable reflection in the entire file.
        }

        var nameText = usingDirective.Name?.ToString();
        if (nameText == null)
            return;

        if (nameText == "System.Reflection" || nameText.StartsWith("System.Reflection."))
        {
            // For top-level usings, check if any class in the file is a BasaltContract
            if (!AnalyzerHelper.IsInsideBasaltContract(usingDirective))
            {
                var root = usingDirective.SyntaxTree.GetRoot(context.CancellationToken);
                if (!ContainsBasaltContract(root))
                    return;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticIds.NoReflection,
                usingDirective.GetLocation(),
                $"using {nameText}"));
        }
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        if (!AnalyzerHelper.IsInsideBasaltContract(invocation))
            return;

        var expressionText = invocation.Expression.ToString();

        // Check for specific reflection method calls
        if (expressionText.EndsWith(".GetType") && !IsObjectGetType(expressionText))
        {
            // Type.GetType(...) — static call
            if (expressionText == "Type.GetType")
            {
                Report(context, invocation, "Type.GetType()");
                return;
            }
        }

        if (expressionText.EndsWith(".GetTypes"))
        {
            Report(context, invocation, "Assembly.GetTypes()");
            return;
        }

        if (expressionText.EndsWith(".Invoke"))
        {
            // Try to determine if this is MethodInfo.Invoke via semantic model
            var symbolInfo = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken);
            var symbol = symbolInfo.Symbol;
            if (symbol != null)
            {
                var containingType = symbol.ContainingType?.ToString();
                if (containingType != null &&
                    (containingType == "System.Reflection.MethodInfo" ||
                     containingType == "System.Reflection.MethodBase"))
                {
                    Report(context, invocation, "MethodInfo.Invoke()");
                    return;
                }
            }
        }

        // Check for GetMethod, GetProperty, GetField on Type
        if (expressionText.EndsWith(".GetMethod") ||
            expressionText.EndsWith(".GetProperty") ||
            expressionText.EndsWith(".GetField"))
        {
            var symbolInfo = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken);
            var symbol = symbolInfo.Symbol;
            if (symbol != null)
            {
                var containingType = symbol.ContainingType?.ToString();
                if (containingType == "System.Type")
                {
                    var methodName = symbol.Name;
                    Report(context, invocation, $"Type.{methodName}()");
                    return;
                }
            }
        }
    }

    private static bool IsObjectGetType(string expressionText)
    {
        // object.GetType() is fine — it's not reflection per se.
        // We only flag Type.GetType(string).
        return !expressionText.Contains("Type.GetType");
    }

    private static bool ContainsBasaltContract(SyntaxNode root)
    {
        foreach (var node in root.DescendantNodes())
        {
            if (node is ClassDeclarationSyntax classDecl)
            {
                foreach (var attrList in classDecl.AttributeLists)
                {
                    foreach (var attr in attrList.Attributes)
                    {
                        var name = attr.Name.ToString();
                        if (name == "BasaltContract" || name == "BasaltContractAttribute")
                            return true;
                    }
                }
            }
        }
        return false;
    }

    private static void Report(SyntaxNodeAnalysisContext context, SyntaxNode node, string message)
    {
        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticIds.NoReflection,
            node.GetLocation(),
            message));
    }
}
