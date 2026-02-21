#nullable enable
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Basalt.Sdk.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ReentrancyAnalyzer : DiagnosticAnalyzer
{
    private static readonly string[] StorageWriteMethods = { "Set", "Add", "Remove" };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(DiagnosticIds.ReentrancyRisk);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
    }

    private static void AnalyzeMethod(SyntaxNodeAnalysisContext context)
    {
        var methodDecl = (MethodDeclarationSyntax)context.Node;

        if (!AnalyzerHelper.IsInsideBasaltContract(methodDecl))
            return;

        if (methodDecl.Body == null && methodDecl.ExpressionBody == null)
            return;

        // Collect all statements/expressions in method body in order
        SyntaxNode bodyNode = (SyntaxNode?)methodDecl.Body ?? methodDecl.ExpressionBody!;

        // Find all invocations in the method body
        var allNodes = new List<SyntaxNode>();
        CollectDescendantNodes(bodyNode, allNodes);

        // Find positions of external calls (Context.CallContract)
        var externalCallSpans = new List<int>();
        var storageWriteNodes = new List<(SyntaxNode Node, int Position, string MethodName)>();

        for (int i = 0; i < allNodes.Count; i++)
        {
            var node = allNodes[i];

            if (node is InvocationExpressionSyntax invocation)
            {
                var exprText = invocation.Expression.ToString();

                // Check for external call pattern: Context.CallContract or similar
                if (exprText.EndsWith(".CallContract") ||
                    exprText == "Context.CallContract" ||
                    exprText.EndsWith(".Call"))
                {
                    externalCallSpans.Add(invocation.SpanStart);
                }

                // Check for storage write patterns: .Set(, .Add(, .Remove(
                if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                {
                    var methodName = memberAccess.Name.Identifier.Text;
                    for (int j = 0; j < StorageWriteMethods.Length; j++)
                    {
                        if (methodName == StorageWriteMethods[j])
                        {
                            storageWriteNodes.Add((invocation, invocation.SpanStart, methodName));
                            break;
                        }
                    }
                }
            }
        }

        // M-04: Report storage writes that occur after any external call.
        // NOTE: This uses positional heuristic (SpanStart comparison) — it does not
        // perform control-flow analysis, so mutually exclusive branches (if/else) may
        // produce false positives, and storage writes in helper methods are not detected.
        for (int i = 0; i < storageWriteNodes.Count; i++)
        {
            var storageWrite = storageWriteNodes[i];
            for (int j = 0; j < externalCallSpans.Count; j++)
            {
                if (storageWrite.Position > externalCallSpans[j])
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticIds.ReentrancyRisk,
                        storageWrite.Node.GetLocation(),
                        $".{storageWrite.MethodName}() called after external call — consider checks-effects-interactions pattern"));
                    break; // Only report once per storage write
                }
            }
        }
    }

    private static void CollectDescendantNodes(SyntaxNode node, List<SyntaxNode> result)
    {
        foreach (var child in node.ChildNodes())
        {
            result.Add(child);
            CollectDescendantNodes(child, result);
        }
    }
}
