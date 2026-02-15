#nullable enable
using Microsoft.CodeAnalysis;

namespace Basalt.Sdk.Analyzers;

internal static class DiagnosticIds
{
    public static readonly DiagnosticDescriptor NoReflection = new(
        "BST001",
        "Reflection is not allowed in Basalt contracts",
        "Reflection usage: {0} — incompatible with AOT compilation",
        "Basalt.Compatibility",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoDynamic = new(
        "BST002",
        "Dynamic types are not allowed in Basalt contracts",
        "Dynamic type usage: {0} — incompatible with AOT compilation",
        "Basalt.Compatibility",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NonDeterminism = new(
        "BST003",
        "Non-deterministic API usage detected",
        "Non-deterministic: {0}",
        "Basalt.Determinism",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ReentrancyRisk = new(
        "BST004",
        "Potential reentrancy vulnerability",
        "Reentrancy risk: {0}",
        "Basalt.Safety",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor OverflowRisk = new(
        "BST005",
        "Unchecked arithmetic may overflow",
        "Unchecked arithmetic in contract: {0}",
        "Basalt.Safety",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor RawStorageAccess = new(
        "BST006",
        "Direct ContractStorage access detected",
        "Prefer StorageValue/StorageMap over raw {0}",
        "Basalt.Safety",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor GasEstimate = new(
        "BST007",
        "Estimated gas cost",
        "Estimated gas: {0}",
        "Basalt.Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor AotIncompatible = new(
        "BST008",
        "API incompatible with Basalt AOT sandbox",
        "'{0}' is not allowed in Basalt contracts",
        "Basalt.Compatibility",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
