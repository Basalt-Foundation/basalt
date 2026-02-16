using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Basalt.Generators.Contracts;

[Generator]
public sealed class ContractGenerator : IIncrementalGenerator
{
    private const string BasaltContractAttribute = "Basalt.Sdk.Contracts.BasaltContractAttribute";
    private const string BasaltEntrypointAttribute = "Basalt.Sdk.Contracts.BasaltEntrypointAttribute";
    private const string BasaltViewAttribute = "Basalt.Sdk.Contracts.BasaltViewAttribute";
    private const string BasaltConstructorAttribute = "Basalt.Sdk.Contracts.BasaltConstructorAttribute";
    private const string BasaltEventAttribute = "Basalt.Sdk.Contracts.BasaltEventAttribute";
    private const string IndexedAttribute = "Basalt.Sdk.Contracts.IndexedAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var contracts = context.SyntaxProvider.ForAttributeWithMetadataName(
            BasaltContractAttribute,
            predicate: static (node, _) => node is ClassDeclarationSyntax,
            transform: static (ctx, _) => GetContractInfo(ctx))
            .Where(static t => t is not null);

        context.RegisterSourceOutput(contracts, static (spc, info) => Generate(spc, info!));
    }

    private static ContractInfo? GetContractInfo(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol typeSymbol)
            return null;

        var classDecl = (ClassDeclarationSyntax)ctx.TargetNode;

        var namespaceName = typeSymbol.ContainingNamespace.IsGlobalNamespace
            ? null
            : typeSymbol.ContainingNamespace.ToDisplayString();

        var typeName = typeSymbol.Name;

        var methods = new List<MethodInfo>();

        foreach (var member in typeSymbol.GetMembers())
        {
            if (member is not IMethodSymbol method)
                continue;

            var mutability = GetMutability(method);
            if (mutability is null)
                continue;

            var parameters = new List<ParameterInfo>();
            foreach (var param in method.Parameters)
            {
                parameters.Add(new ParameterInfo(
                    param.Name,
                    GetTypeName(param.Type),
                    GetFullTypeName(param.Type)));
            }

            var returnTypeName = method.ReturnsVoid ? "void" : GetTypeName(method.ReturnType);
            var returnTypeFullName = method.ReturnsVoid ? "void" : GetFullTypeName(method.ReturnType);

            methods.Add(new MethodInfo(
                method.Name,
                mutability,
                parameters,
                returnTypeName,
                returnTypeFullName,
                method.ReturnsVoid));
        }

        var events = new List<EventInfo>();
        CollectEvents(typeSymbol, events);

        // Check if base class also has [BasaltContract] — need `new` modifier on generated members
        var hasContractBase = false;
        var baseType = typeSymbol.BaseType;
        while (baseType is not null && baseType.SpecialType != SpecialType.System_Object)
        {
            if (HasAttribute(baseType, BasaltContractAttribute))
            {
                hasContractBase = true;
                break;
            }
            baseType = baseType.BaseType;
        }

        return new ContractInfo(namespaceName, typeName, methods, events, hasContractBase);
    }

    private static void CollectEvents(INamedTypeSymbol contractType, List<EventInfo> events)
    {
        // Collect nested types with [BasaltEvent]
        foreach (var nested in contractType.GetTypeMembers())
        {
            if (HasAttribute(nested, BasaltEventAttribute))
            {
                events.Add(BuildEventInfo(nested));
            }
        }

        // Also scan for event types referenced in the contract's methods and base types.
        // Walk the base type chain to find event types used in the contract hierarchy.
        var visited = new HashSet<string>();
        CollectReferencedEvents(contractType, events, visited);
    }

    private static void CollectReferencedEvents(INamedTypeSymbol type, List<EventInfo> events, HashSet<string> visited)
    {
        if (type is null)
            return;

        // Scan methods for Context.Emit<TEvent> calls by looking at method bodies is not
        // feasible in a semantic model pass. Instead, scan the containing namespace and
        // base type interfaces for types marked [BasaltEvent].
        // Scan base types
        var baseType = type.BaseType;
        while (baseType is not null && baseType.SpecialType != SpecialType.System_Object)
        {
            foreach (var nested in baseType.GetTypeMembers())
            {
                if (HasAttribute(nested, BasaltEventAttribute))
                {
                    var key = nested.ToDisplayString();
                    if (visited.Add(key))
                        events.Add(BuildEventInfo(nested));
                }
            }
            baseType = baseType.BaseType;
        }

        // Scan interfaces
        foreach (var iface in type.AllInterfaces)
        {
            // Check the containing type/namespace of the interface for sibling event types.
            // Events are often declared alongside the interface (e.g., IBST20 + TransferEvent).
            var containingType = iface.ContainingType;
            if (containingType is not null)
            {
                foreach (var sibling in containingType.GetTypeMembers())
                {
                    if (HasAttribute(sibling, BasaltEventAttribute))
                    {
                        var key = sibling.ToDisplayString();
                        if (visited.Add(key))
                            events.Add(BuildEventInfo(sibling));
                    }
                }
            }

            // Check the containing namespace for event types declared alongside the interface
            var containingNamespace = iface.ContainingNamespace;
            if (containingNamespace is not null)
            {
                foreach (var member in containingNamespace.GetTypeMembers())
                {
                    if (HasAttribute(member, BasaltEventAttribute))
                    {
                        var key = member.ToDisplayString();
                        if (visited.Add(key))
                            events.Add(BuildEventInfo(member));
                    }
                }
            }
        }

        // Scan the contract's own containing namespace for event types
        var contractNamespace = type.ContainingNamespace;
        if (contractNamespace is not null)
        {
            foreach (var member in contractNamespace.GetTypeMembers())
            {
                if (HasAttribute(member, BasaltEventAttribute))
                {
                    var key = member.ToDisplayString();
                    if (visited.Add(key))
                        events.Add(BuildEventInfo(member));
                }
            }
        }
    }

    private static EventInfo BuildEventInfo(INamedTypeSymbol eventType)
    {
        var properties = new List<EventPropertyInfo>();

        foreach (var member in eventType.GetMembers())
        {
            if (member is IPropertySymbol prop && prop.DeclaredAccessibility == Accessibility.Public)
            {
                var indexed = HasAttribute(prop, IndexedAttribute);
                properties.Add(new EventPropertyInfo(
                    prop.Name,
                    GetTypeName(prop.Type),
                    indexed));
            }
        }

        return new EventInfo(eventType.Name, properties);
    }

    private static bool HasAttribute(ISymbol symbol, string attributeFullName)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() == attributeFullName)
                return true;
        }
        return false;
    }

    private static string? GetMutability(IMethodSymbol method)
    {
        foreach (var attr in method.GetAttributes())
        {
            var attrName = attr.AttributeClass?.ToDisplayString();
            if (attrName == BasaltEntrypointAttribute)
                return "mutable";
            if (attrName == BasaltViewAttribute)
                return "view";
        }
        return null;
    }

    private static string GetTypeName(ITypeSymbol type)
    {
        return type.SpecialType switch
        {
            SpecialType.System_Byte => "byte",
            SpecialType.System_UInt16 => "ushort",
            SpecialType.System_UInt32 => "uint",
            SpecialType.System_Int32 => "int",
            SpecialType.System_UInt64 => "ulong",
            SpecialType.System_Int64 => "long",
            SpecialType.System_Boolean => "bool",
            SpecialType.System_String => "string",
            _ => type switch
            {
                IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte } => "byte[]",
                _ => type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
            }
        };
    }

    private static string GetFullTypeName(ITypeSymbol type)
    {
        return type.SpecialType switch
        {
            SpecialType.System_Byte => "byte",
            SpecialType.System_UInt16 => "ushort",
            SpecialType.System_UInt32 => "uint",
            SpecialType.System_Int32 => "int",
            SpecialType.System_UInt64 => "ulong",
            SpecialType.System_Int64 => "long",
            SpecialType.System_Boolean => "bool",
            SpecialType.System_String => "string",
            _ => type switch
            {
                IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte } => "byte[]",
                _ => type.ToDisplayString()
            }
        };
    }

    private static void Generate(SourceProductionContext spc, ContractInfo info)
    {
        var sb = new StringBuilder();

        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        if (info.Namespace is not null)
        {
            sb.AppendLine($"namespace {info.Namespace};");
            sb.AppendLine();
        }

        sb.AppendLine($"partial class {info.TypeName} : Basalt.Sdk.Contracts.IDispatchable");
        sb.AppendLine("{");

        // Generate ABI metadata
        GenerateAbiMetadata(sb, info);
        sb.AppendLine();

        // Generate Dispatch method
        GenerateDispatchMethod(sb, info);

        sb.AppendLine("}");

        var hintName = info.Namespace is not null
            ? $"{info.Namespace}.{info.TypeName}.g.cs"
            : $"{info.TypeName}.g.cs";

        spc.AddSource(hintName, sb.ToString());
    }

    private static void GenerateAbiMetadata(StringBuilder sb, ContractInfo info)
    {
        var modifier = info.HasContractBase ? "new " : "";
        sb.Append($"    public {modifier}const string ContractAbi = @\"{{\"\"methods\"\":[");

        for (int i = 0; i < info.Methods.Count; i++)
        {
            var method = info.Methods[i];
            if (i > 0)
                sb.Append(',');

            var selector = ComputeSelector(method.Name);
            var selectorHex = $"0x{selector:X8}";

            sb.Append("{\"\"name\"\":\"\"");
            sb.Append(method.Name);
            sb.Append("\"\",\"\"selector\"\":\"\"");
            sb.Append(selectorHex);
            sb.Append("\"\",\"\"mutability\"\":\"\"");
            sb.Append(method.Mutability);
            sb.Append("\"\",\"\"params\"\":[");

            for (int j = 0; j < method.Parameters.Count; j++)
            {
                var param = method.Parameters[j];
                if (j > 0)
                    sb.Append(',');
                sb.Append("{\"\"name\"\":\"\"");
                sb.Append(param.Name);
                sb.Append("\"\",\"\"type\"\":\"\"");
                sb.Append(param.TypeName);
                sb.Append("\"\"}");
            }

            sb.Append("],\"\"returns\"\":\"\"");
            sb.Append(method.ReturnTypeName);
            sb.Append("\"\"}");
        }

        sb.Append("],\"\"events\"\":[");

        for (int i = 0; i < info.Events.Count; i++)
        {
            var evt = info.Events[i];
            if (i > 0)
                sb.Append(',');

            sb.Append("{\"\"name\"\":\"\"");
            sb.Append(evt.Name);
            sb.Append("\"\",\"\"properties\"\":[");

            for (int j = 0; j < evt.Properties.Count; j++)
            {
                var prop = evt.Properties[j];
                if (j > 0)
                    sb.Append(',');
                sb.Append("{\"\"name\"\":\"\"");
                sb.Append(prop.Name);
                sb.Append("\"\",\"\"type\"\":\"\"");
                sb.Append(prop.TypeName);
                sb.Append("\"\",\"\"indexed\"\":");
                sb.Append(prop.Indexed ? "true" : "false");
                sb.Append('}');
            }

            sb.Append("]}");
        }

        sb.AppendLine("]}\";");
    }

    private static void GenerateDispatchMethod(StringBuilder sb, ContractInfo info)
    {
        var modifier = info.HasContractBase ? "new " : "";
        sb.AppendLine($"    public {modifier}byte[] Dispatch(byte[] selector, byte[] calldata)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (selector is null || selector.Length < 4)");
        sb.AppendLine("            throw new System.InvalidOperationException(\"Invalid selector: must be at least 4 bytes.\");");
        sb.AppendLine();
        sb.AppendLine("        uint sel = (uint)(selector[0] | (selector[1] << 8) | (selector[2] << 16) | (selector[3] << 24));");
        sb.AppendLine();

        var dispatchable = info.Methods.Where(IsMethodDispatchable).ToList();

        if (dispatchable.Count == 0)
        {
            sb.AppendLine("        throw new System.InvalidOperationException($\"Unknown selector: 0x{sel:X8}\");");
        }
        else
        {
            sb.AppendLine("        switch (sel)");
            sb.AppendLine("        {");

            foreach (var method in dispatchable)
            {
                var selector = ComputeSelector(method.Name);
                sb.AppendLine($"            case 0x{selector:X8}u: // {method.Name}");
                sb.AppendLine("            {");

                if (method.Parameters.Count > 0)
                {
                    sb.AppendLine("                var reader = new Basalt.Codec.BasaltReader(calldata);");

                    foreach (var param in method.Parameters)
                    {
                        var readExpr = GetReadExpression(param.FullTypeName);
                        sb.AppendLine($"                var _p_{param.Name} = {readExpr};");
                    }
                }

                // Build method call
                var args = string.Join(", ", method.Parameters.Select(p => $"_p_{p.Name}"));
                var call = $"this.{method.Name}({args})";

                if (method.ReturnsVoid)
                {
                    sb.AppendLine($"                {call};");
                    sb.AppendLine("                return System.Array.Empty<byte>();");
                }
                else
                {
                    sb.AppendLine($"                var _result = {call};");
                    var writeInfo = GetWriteInfo(method.ReturnTypeFullName)!.Value;
                    if (writeInfo.BufferSize is not null)
                    {
                        sb.AppendLine($"                var _buf = new byte[{writeInfo.BufferSize}];");
                    }
                    else
                    {
                        // Variable-length return: estimate a buffer size
                        sb.AppendLine("                var _buf = new byte[4096];");
                    }
                    sb.AppendLine("                var writer = new Basalt.Codec.BasaltWriter(_buf);");
                    sb.AppendLine($"                {writeInfo.WriteCall};");
                    sb.AppendLine("                return _buf[..writer.Position];");
                }

                sb.AppendLine("            }");
            }

            sb.AppendLine("            default:");
            sb.AppendLine("                throw new System.InvalidOperationException($\"Unknown selector: 0x{sel:X8}\");");
            sb.AppendLine("        }");
        }

        sb.AppendLine("    }");
    }

    private static string? GetReadExpression(string fullTypeName)
    {
        return fullTypeName switch
        {
            "byte" => "reader.ReadByte()",
            "ushort" => "reader.ReadUInt16()",
            "uint" => "reader.ReadUInt32()",
            "int" => "reader.ReadInt32()",
            "ulong" => "reader.ReadUInt64()",
            "long" => "reader.ReadInt64()",
            "bool" => "reader.ReadBool()",
            "string" => "reader.ReadString()",
            "byte[]" => "reader.ReadBytes().ToArray()",
            "Basalt.Core.Hash256" => "reader.ReadHash256()",
            "Basalt.Core.Address" => "reader.ReadAddress()",
            "Basalt.Core.UInt256" => "reader.ReadUInt256()",
            _ => null // Unsupported type — method will be skipped in dispatch
        };
    }

    /// <summary>
    /// Returns (bufferSize, writeStatement) for the given return type.
    /// bufferSize is null for variable-length types. Returns null if unsupported.
    /// </summary>
    private static (string? BufferSize, string WriteCall)? GetWriteInfo(string fullTypeName)
    {
        return fullTypeName switch
        {
            "byte" => ("1", "writer.WriteByte(_result)"),
            "ushort" => ("2", "writer.WriteUInt16(_result)"),
            "uint" => ("4", "writer.WriteUInt32(_result)"),
            "int" => ("4", "writer.WriteInt32(_result)"),
            "ulong" => ("8", "writer.WriteUInt64(_result)"),
            "long" => ("8", "writer.WriteInt64(_result)"),
            "bool" => ("1", "writer.WriteBool(_result)"),
            "string" => (null, "writer.WriteString(_result)"),
            "byte[]" => (null, "writer.WriteBytes(_result)"),
            "Basalt.Core.Hash256" => ("32", "writer.WriteHash256(_result)"),
            "Basalt.Core.Address" => ("20", "writer.WriteAddress(_result)"),
            "Basalt.Core.UInt256" => ("32", "writer.WriteUInt256(_result)"),
            _ => null // Unsupported return type — method will be skipped
        };
    }

    /// <summary>
    /// Check if a method can be dispatched (all parameter types and return type supported).
    /// </summary>
    private static bool IsMethodDispatchable(MethodInfo method)
    {
        foreach (var param in method.Parameters)
        {
            if (GetReadExpression(param.FullTypeName) is null)
                return false;
        }
        if (!method.ReturnsVoid && GetWriteInfo(method.ReturnTypeFullName) is null)
            return false;
        return true;
    }

    /// <summary>
    /// Compute a 4-byte selector using FNV-1a hash of the method name.
    /// </summary>
    private static uint ComputeSelector(string methodName)
    {
        uint hash = 2166136261;
        foreach (char c in methodName)
        {
            hash ^= (byte)c;
            hash *= 16777619;
        }
        return hash;
    }

    // ---- Data models ----

    private sealed class ContractInfo
    {
        public string? Namespace { get; }
        public string TypeName { get; }
        public List<MethodInfo> Methods { get; }
        public List<EventInfo> Events { get; }
        public bool HasContractBase { get; }

        public ContractInfo(string? ns, string typeName, List<MethodInfo> methods, List<EventInfo> events, bool hasContractBase = false)
        {
            Namespace = ns;
            TypeName = typeName;
            Methods = methods;
            Events = events;
            HasContractBase = hasContractBase;
        }
    }

    private sealed class MethodInfo
    {
        public string Name { get; }
        public string? Mutability { get; }
        public List<ParameterInfo> Parameters { get; }
        public string ReturnTypeName { get; }
        public string ReturnTypeFullName { get; }
        public bool ReturnsVoid { get; }

        public MethodInfo(string name, string? mutability, List<ParameterInfo> parameters,
            string returnTypeName, string returnTypeFullName, bool returnsVoid)
        {
            Name = name;
            Mutability = mutability;
            Parameters = parameters;
            ReturnTypeName = returnTypeName;
            ReturnTypeFullName = returnTypeFullName;
            ReturnsVoid = returnsVoid;
        }
    }

    private sealed class ParameterInfo
    {
        public string Name { get; }
        public string TypeName { get; }
        public string FullTypeName { get; }

        public ParameterInfo(string name, string typeName, string fullTypeName)
        {
            Name = name;
            TypeName = typeName;
            FullTypeName = fullTypeName;
        }
    }

    private sealed class EventInfo
    {
        public string Name { get; }
        public List<EventPropertyInfo> Properties { get; }

        public EventInfo(string name, List<EventPropertyInfo> properties)
        {
            Name = name;
            Properties = properties;
        }
    }

    private sealed class EventPropertyInfo
    {
        public string Name { get; }
        public string TypeName { get; }
        public bool Indexed { get; }

        public EventPropertyInfo(string name, string typeName, bool indexed)
        {
            Name = name;
            TypeName = typeName;
            Indexed = indexed;
        }
    }
}
