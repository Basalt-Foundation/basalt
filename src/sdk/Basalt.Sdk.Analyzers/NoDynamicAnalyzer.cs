#nullable enable
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Basalt.Sdk.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NoDynamicAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(DiagnosticIds.NoDynamic);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSyntaxNodeAction(AnalyzeIdentifierName, SyntaxKind.IdentifierName);
        context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression);
    }

    private static void AnalyzeIdentifierName(SyntaxNodeAnalysisContext context)
    {
        var identifierName = (IdentifierNameSyntax)context.Node;

        if (!AnalyzerHelper.IsInsideBasaltContract(identifierName))
            return;

        var identifierText = identifierName.Identifier.Text;

        // Check if identifier refers to the 'dynamic' type
        if (identifierText == "dynamic")
        {
            var typeInfo = context.SemanticModel.GetTypeInfo(identifierName, context.CancellationToken);
            if (typeInfo.Type != null && typeInfo.Type.TypeKind == TypeKind.Dynamic)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticIds.NoDynamic,
                    identifierName.GetLocation(),
                    "dynamic type"));
                return;
            }

            var symbolInfo = context.SemanticModel.GetSymbolInfo(identifierName, context.CancellationToken);
            if (symbolInfo.Symbol is ITypeSymbol typeSymbol && typeSymbol.TypeKind == TypeKind.Dynamic)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticIds.NoDynamic,
                    identifierName.GetLocation(),
                    "dynamic type"));
                return;
            }
        }

        // Check for ExpandoObject and DynamicObject usage as type references
        if (identifierText == "ExpandoObject" || identifierText == "DynamicObject")
        {
            var symbolInfo = context.SemanticModel.GetSymbolInfo(identifierName, context.CancellationToken);
            var symbol = symbolInfo.Symbol;
            if (symbol == null && symbolInfo.CandidateSymbols.Length > 0)
            {
                symbol = symbolInfo.CandidateSymbols[0];
            }

            if (symbol is ITypeSymbol ts)
            {
                var fullName = ts.ToDisplayString();
                if (fullName == "System.Dynamic.ExpandoObject" ||
                    fullName == "System.Dynamic.DynamicObject")
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticIds.NoDynamic,
                        identifierName.GetLocation(),
                        identifierText));
                }
            }
            else if (symbol is INamedTypeSymbol nts)
            {
                var fullName = nts.ToDisplayString();
                if (fullName == "System.Dynamic.ExpandoObject" ||
                    fullName == "System.Dynamic.DynamicObject")
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticIds.NoDynamic,
                        identifierName.GetLocation(),
                        identifierText));
                }
            }
        }
    }

    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
    {
        var creation = (ObjectCreationExpressionSyntax)context.Node;

        if (!AnalyzerHelper.IsInsideBasaltContract(creation))
            return;

        var typeName = creation.Type.ToString();
        if (typeName == "ExpandoObject" || typeName == "DynamicObject")
        {
            var typeInfo = context.SemanticModel.GetTypeInfo(creation, context.CancellationToken);
            var fullName = typeInfo.Type?.ToDisplayString();
            if (fullName == "System.Dynamic.ExpandoObject" ||
                fullName == "System.Dynamic.DynamicObject")
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticIds.NoDynamic,
                    creation.GetLocation(),
                    $"new {typeName}()"));
            }
        }
    }
}
