#nullable enable
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Basalt.Sdk.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class GasEstimationAnalyzer : DiagnosticAnalyzer
{
    private const int BaseGas = 21000;
    private const int StorageOpGas = 5000;
    private const int LoopGas = 10000;
    private const int ExternalCallGas = 25000;

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(DiagnosticIds.GasEstimate);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
    }

    private static void AnalyzeMethod(SyntaxNodeAnalysisContext context)
    {
        var method = (MethodDeclarationSyntax)context.Node;
        if (!HasEntrypointAttribute(method))
            return;

        int gas = BaseGas;
        if (method.Body == null && method.ExpressionBody == null)
            return;

        var descendants = method.DescendantNodes();

        // Count loops
        int loops = descendants.Count(n => n is ForStatementSyntax || n is WhileStatementSyntax || n is ForEachStatementSyntax);
        gas += loops * LoopGas;

        // H-02: Count storage operations using member name + receiver type heuristic
        // to avoid false positives from Dictionary.Add, List.Remove, etc.
        int storageOps = descendants.Count(n =>
        {
            if (n is InvocationExpressionSyntax invocation &&
                invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                var methodName = memberAccess.Name.Identifier.Text;
                if (methodName != "Get" && methodName != "Set" && methodName != "Add" && methodName != "Remove")
                    return false;

                // Check receiver expression text for known storage types
                var receiverText = memberAccess.Expression.ToString();
                if (receiverText.Contains("Storage") || receiverText.StartsWith("_"))
                    return true;

                // Fallback: use semantic model to check the receiver type
                var typeInfo = context.SemanticModel.GetTypeInfo(memberAccess.Expression, context.CancellationToken);
                var typeName = typeInfo.Type?.ToDisplayString() ?? "";
                return typeName.Contains("StorageValue") || typeName.Contains("StorageMap") || typeName.Contains("StorageList");
            }
            return false;
        });
        gas += storageOps * StorageOpGas;

        // Count external calls
        int externalCalls = descendants.Count(n =>
        {
            if (n is InvocationExpressionSyntax invocation)
            {
                var text = invocation.Expression.ToString();
                return text.Contains("Context.CallContract") || text.EndsWith(".CallContract");
            }
            return false;
        });
        gas += externalCalls * ExternalCallGas;

        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticIds.GasEstimate,
            method.Identifier.GetLocation(),
            $"{gas} (base: {BaseGas}, loops: {loops}×{LoopGas}, storage: {storageOps}×{StorageOpGas}, calls: {externalCalls}×{ExternalCallGas})"));
    }

    private static bool HasEntrypointAttribute(MethodDeclarationSyntax method)
    {
        foreach (var attrList in method.AttributeLists)
        {
            foreach (var attr in attrList.Attributes)
            {
                var name = attr.Name.ToString();
                if (name == "BasaltEntrypoint" || name == "BasaltEntrypointAttribute" ||
                    name == "BasaltView" || name == "BasaltViewAttribute")
                    return true;
            }
        }
        return false;
    }
}
