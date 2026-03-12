#nullable enable
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Basalt.Sdk.Analyzers;

/// <summary>
/// BST010: Warns when a storage .Set() or .Delete() call appears before an
/// EnforceTransfer() or EnforceNftTransfer() call within the same method body.
/// State should be mutated only after policy enforcement passes.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PolicyOrderingAnalyzer : DiagnosticAnalyzer
{
    private static readonly string[] StorageTypeNames =
    {
        "StorageValue", "StorageMap", "StorageList",
    };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(DiagnosticIds.StateWriteBeforePolicyCheck);

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

        SyntaxNode bodyNode = (SyntaxNode?)methodDecl.Body ?? methodDecl.ExpressionBody!;

        var storageWrites = new List<(SyntaxNode Node, int Position, string MethodName)>();
        int firstEnforcePosition = int.MaxValue;

        foreach (var node in bodyNode.DescendantNodes())
        {
            if (node is InvocationExpressionSyntax invocation &&
                invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                var methodName = memberAccess.Name.Identifier.Text;

                if (methodName == "EnforceTransfer" || methodName == "EnforceNftTransfer")
                {
                    if (invocation.SpanStart < firstEnforcePosition)
                        firstEnforcePosition = invocation.SpanStart;
                }
                else if (methodName == "Set" || methodName == "Delete")
                {
                    // Verify the receiver is a Basalt storage type via semantic model
                    var receiverType = context.SemanticModel.GetTypeInfo(
                        memberAccess.Expression, context.CancellationToken).Type;

                    if (receiverType != null && IsStorageType(receiverType))
                    {
                        storageWrites.Add((invocation, invocation.SpanStart, methodName));
                    }
                }
            }
        }

        // No policy enforcement in this method — nothing to check
        if (firstEnforcePosition == int.MaxValue)
            return;

        foreach (var write in storageWrites)
        {
            if (write.Position < firstEnforcePosition)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticIds.StateWriteBeforePolicyCheck,
                    write.Node.GetLocation(),
                    $".{write.MethodName}() called before policy enforcement — move state writes after EnforceTransfer()/EnforceNftTransfer()"));
            }
        }
    }

    private static bool IsStorageType(ITypeSymbol type)
    {
        var name = type.Name;
        for (int i = 0; i < StorageTypeNames.Length; i++)
        {
            if (name == StorageTypeNames[i])
                return true;
        }
        return false;
    }
}
