#nullable enable
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Basalt.Sdk.Analyzers;

/// <summary>
/// BST012: Warns when a class that derives from a BST token base type
/// (BST20Token, BST721Token, BST1155Token, BST3525Token) has a method named
/// TransferInternal that does not contain a call to EnforceTransfer or
/// EnforceNftTransfer.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PolicyEnforcementAnalyzer : DiagnosticAnalyzer
{
    private static readonly string[] BstTokenBaseTypes =
    {
        "BST20Token", "BST721Token", "BST1155Token", "BST3525Token",
    };

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(DiagnosticIds.MissingPolicyEnforcement);

    /// <summary>
    /// Transfer method names that require policy enforcement, mapped to the base types
    /// on which they are defined. TransferInternal applies to BST20/BST721.
    /// SafeTransferFrom/SafeBatchTransferFrom apply to BST1155.
    /// TransferValueFrom/TransferFrom apply to BST3525.
    /// </summary>
    private static readonly (string MethodName, string[] BaseTypes)[] TransferMethods =
    {
        ("TransferInternal", new[] { "BST20Token", "BST721Token" }),
        ("SafeTransferFrom", new[] { "BST1155Token" }),
        ("SafeBatchTransferFrom", new[] { "BST1155Token" }),
        ("TransferValueFrom", new[] { "BST3525Token" }),
        ("TransferFrom", new[] { "BST3525Token" }),
    };

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

        var methodName = methodDecl.Identifier.Text;

        // Find which base types this method name applies to
        string[]? applicableBaseTypes = null;
        for (int i = 0; i < TransferMethods.Length; i++)
        {
            if (TransferMethods[i].MethodName == methodName)
            {
                applicableBaseTypes = TransferMethods[i].BaseTypes;
                break;
            }
        }
        if (applicableBaseTypes == null)
            return;

        // Check if the enclosing class derives from an applicable BST token base type
        var classDecl = methodDecl.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (classDecl?.BaseList == null)
            return;

        bool derivesBstToken = false;
        foreach (var baseType in classDecl.BaseList.Types)
        {
            var name = GetUnqualifiedName(baseType.Type);
            if (name == null) continue;
            for (int i = 0; i < applicableBaseTypes.Length; i++)
            {
                if (name == applicableBaseTypes[i])
                {
                    derivesBstToken = true;
                    break;
                }
            }
            if (derivesBstToken) break;
        }

        if (!derivesBstToken)
            return;

        // Check if the method body contains EnforceTransfer or EnforceNftTransfer
        SyntaxNode? bodyNode = (SyntaxNode?)methodDecl.Body ?? methodDecl.ExpressionBody;
        if (bodyNode == null)
            return;

        bool hasEnforcement = false;
        foreach (var node in bodyNode.DescendantNodes())
        {
            if (node is InvocationExpressionSyntax invocation &&
                invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                var name = memberAccess.Name.Identifier.Text;
                if (name == "EnforceTransfer" || name == "EnforceNftTransfer")
                {
                    hasEnforcement = true;
                    break;
                }
            }
        }

        if (!hasEnforcement)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticIds.MissingPolicyEnforcement,
                methodDecl.Identifier.GetLocation(),
                $"{methodName} in {classDecl.Identifier.Text} does not call EnforceTransfer()/EnforceNftTransfer() — policy hooks will be bypassed"));
        }
    }

    private static string? GetUnqualifiedName(TypeSyntax type)
    {
        return type switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            GenericNameSyntax generic => generic.Identifier.Text,
            QualifiedNameSyntax qualified => GetUnqualifiedName(qualified.Right),
            _ => null,
        };
    }
}
