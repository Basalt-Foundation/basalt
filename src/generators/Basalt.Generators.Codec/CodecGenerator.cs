using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Basalt.Generators.Codec;

/// <summary>
/// Incremental source generator that produces <c>WriteTo</c>, <c>ReadFrom</c>, and
/// <c>GetSerializedSize</c> methods for types annotated with
/// <c>[BasaltSerializable]</c>.
/// </summary>
[Generator]
public sealed class CodecGenerator : IIncrementalGenerator
{
    private const string AttributeFullName = "Basalt.Sdk.Contracts.BasaltSerializableAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var types = context.SyntaxProvider.ForAttributeWithMetadataName(
            AttributeFullName,
            predicate: static (node, _) => node is TypeDeclarationSyntax,
            transform: static (ctx, _) => GetTypeInfo(ctx))
            .Where(static t => t is not null);

        context.RegisterSourceOutput(types, static (spc, typeInfo) => Generate(spc, typeInfo!));
    }

    // ──────────────────────────────────────────────
    //  Model
    // ──────────────────────────────────────────────

    private sealed class MemberInfo : IEquatable<MemberInfo>
    {
        public string Name { get; }
        public string TypeFullName { get; }

        public MemberInfo(string name, string typeFullName)
        {
            Name = name;
            TypeFullName = typeFullName;
        }

        public bool Equals(MemberInfo? other) =>
            other is not null && Name == other.Name && TypeFullName == other.TypeFullName;

        public override bool Equals(object? obj) => obj is MemberInfo other && Equals(other);
        public override int GetHashCode() => Name.GetHashCode() ^ TypeFullName.GetHashCode();
    }

    private sealed class TypeToGenerate : IEquatable<TypeToGenerate>
    {
        public string Name { get; }
        public string Namespace { get; }
        public bool IsStruct { get; }
        public EquatableArray<MemberInfo> Members { get; }

        public TypeToGenerate(string name, string ns, bool isStruct, EquatableArray<MemberInfo> members)
        {
            Name = name;
            Namespace = ns;
            IsStruct = isStruct;
            Members = members;
        }

        public bool Equals(TypeToGenerate? other) =>
            other is not null && Name == other.Name && Namespace == other.Namespace &&
            IsStruct == other.IsStruct && Members.Equals(other.Members);

        public override bool Equals(object? obj) => obj is TypeToGenerate other && Equals(other);
        public override int GetHashCode() => Name.GetHashCode() ^ Namespace.GetHashCode();
    }

    // ──────────────────────────────────────────────
    //  Extraction
    // ──────────────────────────────────────────────

    private static TypeToGenerate? GetTypeInfo(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol typeSymbol)
            return null;

        var isStruct = typeSymbol.TypeKind == TypeKind.Struct;
        var isClass = typeSymbol.TypeKind == TypeKind.Class;
        if (!isStruct && !isClass)
            return null;

        var ns = typeSymbol.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : typeSymbol.ContainingNamespace.ToDisplayString();

        var members = ImmutableArray.CreateBuilder<MemberInfo>();

        // Collect public instance properties with both getter and setter, in declaration order.
        foreach (var member in typeSymbol.GetMembers())
        {
            if (member is IPropertySymbol prop
                && prop.DeclaredAccessibility == Accessibility.Public
                && !prop.IsStatic
                && !prop.IsIndexer
                && prop.GetMethod is not null
                && prop.SetMethod is not null)
            {
                var fullTypeName = prop.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                members.Add(new MemberInfo(prop.Name, fullTypeName));
            }
            else if (member is IFieldSymbol field
                && field.DeclaredAccessibility == Accessibility.Public
                && !field.IsStatic
                && !field.IsConst
                && !field.IsImplicitlyDeclared)
            {
                var fullTypeName = field.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                members.Add(new MemberInfo(field.Name, fullTypeName));
            }
        }

        return new TypeToGenerate(
            typeSymbol.Name,
            ns,
            isStruct,
            new EquatableArray<MemberInfo>(members.ToImmutable()));
    }

    // ──────────────────────────────────────────────
    //  Code generation
    // ──────────────────────────────────────────────

    private static void Generate(SourceProductionContext spc, TypeToGenerate type)
    {
        var sb = new StringBuilder(2048);

        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using Basalt.Codec;");
        sb.AppendLine("using Basalt.Core;");
        sb.AppendLine();

        var hasNamespace = !string.IsNullOrEmpty(type.Namespace);
        if (hasNamespace)
        {
            sb.Append("namespace ").Append(type.Namespace).AppendLine(";");
            sb.AppendLine();
        }

        var keyword = type.IsStruct ? "struct" : "class";
        sb.Append("partial ").Append(keyword).Append(' ').AppendLine(type.Name);
        sb.AppendLine("{");

        EmitWriteTo(sb, type);
        sb.AppendLine();
        EmitReadFrom(sb, type);
        sb.AppendLine();
        EmitGetSerializedSize(sb, type);

        sb.AppendLine("}");

        spc.AddSource($"{type.Name}.BasaltCodec.g.cs", sb.ToString());
    }

    // ── WriteTo ──────────────────────────────────

    private static void EmitWriteTo(StringBuilder sb, TypeToGenerate type)
    {
        sb.AppendLine("    public void WriteTo(ref BasaltWriter writer)");
        sb.AppendLine("    {");

        foreach (var m in type.Members.AsImmutableArray())
        {
            var method = GetWriteMethod(m.TypeFullName);
            if (method is null)
            {
                sb.Append("        // Skipped unsupported type: ").Append(m.TypeFullName).Append(" ").AppendLine(m.Name);
                continue;
            }

            sb.Append("        writer.").Append(method).Append('(').Append(m.Name).AppendLine(");");
        }

        sb.AppendLine("    }");
    }

    // ── ReadFrom ─────────────────────────────────

    private static void EmitReadFrom(StringBuilder sb, TypeToGenerate type)
    {
        sb.Append("    public static ").Append(type.Name).AppendLine(" ReadFrom(ref BasaltReader reader)");
        sb.AppendLine("    {");

        if (type.IsStruct)
        {
            sb.Append("        var result = default(").Append(type.Name).AppendLine(");");
        }
        else
        {
            sb.Append("        var result = new ").Append(type.Name).AppendLine("();");
        }

        foreach (var m in type.Members.AsImmutableArray())
        {
            var method = GetReadMethod(m.TypeFullName);
            if (method is null)
            {
                sb.Append("        // Skipped unsupported type: ").Append(m.TypeFullName).Append(" ").AppendLine(m.Name);
                continue;
            }

            sb.Append("        result.").Append(m.Name).Append(" = reader.").Append(method).AppendLine("();");
        }

        sb.AppendLine("        return result;");
        sb.AppendLine("    }");
    }

    // ── GetSerializedSize ────────────────────────

    private static void EmitGetSerializedSize(StringBuilder sb, TypeToGenerate type)
    {
        sb.AppendLine("    public int GetSerializedSize()");
        sb.AppendLine("    {");

        // Accumulate fixed-size total at compile time, emit variable parts separately.
        int fixedTotal = 0;
        var variableParts = new List<string>();

        foreach (var m in type.Members.AsImmutableArray())
        {
            var size = GetFixedSize(m.TypeFullName);
            if (size is null)
            {
                // Unsupported type -- skip.
                continue;
            }

            if (size.Value >= 0)
            {
                fixedTotal += size.Value;
            }
            else
            {
                // Variable-size: use 64 as estimate.
                variableParts.Add(m.Name);
            }
        }

        if (variableParts.Count == 0)
        {
            sb.Append("        return ").Append(fixedTotal).AppendLine(";");
        }
        else
        {
            sb.Append("        int size = ").Append(fixedTotal).AppendLine(";");
            foreach (var name in variableParts)
            {
                sb.Append("        size += 64; // estimate for ").AppendLine(name);
            }
            sb.AppendLine("        return size;");
        }

        sb.AppendLine("    }");
    }

    // ──────────────────────────────────────────────
    //  Type mapping helpers
    // ──────────────────────────────────────────────

    /// <summary>
    /// Returns the BasaltWriter method name for the given fully-qualified type, or null if unsupported.
    /// </summary>
    private static string? GetWriteMethod(string typeFullName) => NormalizeType(typeFullName) switch
    {
        "byte" => "WriteByte",
        "ushort" => "WriteUInt16",
        "uint" => "WriteUInt32",
        "int" => "WriteInt32",
        "ulong" => "WriteUInt64",
        "long" => "WriteInt64",
        "bool" => "WriteBool",
        "string" => "WriteString",
        "byte[]" => "WriteBytes",
        "Basalt.Core.Hash256" => "WriteHash256",
        "Basalt.Core.Address" => "WriteAddress",
        "Basalt.Core.UInt256" => "WriteUInt256",
        "Basalt.Core.Signature" => "WriteSignature",
        "Basalt.Core.PublicKey" => "WritePublicKey",
        "Basalt.Core.BlsSignature" => "WriteBlsSignature",
        "Basalt.Core.BlsPublicKey" => "WriteBlsPublicKey",
        _ => null,
    };

    /// <summary>
    /// Returns the BasaltReader method name for the given fully-qualified type, or null if unsupported.
    /// For byte[] the caller will need to append <c>.ToArray()</c>.
    /// </summary>
    private static string? GetReadMethod(string typeFullName) => NormalizeType(typeFullName) switch
    {
        "byte" => "ReadByte",
        "ushort" => "ReadUInt16",
        "uint" => "ReadUInt32",
        "int" => "ReadInt32",
        "ulong" => "ReadUInt64",
        "long" => "ReadInt64",
        "bool" => "ReadBool",
        "string" => "ReadString",
        "byte[]" => "ReadBytes().ToArray",
        "Basalt.Core.Hash256" => "ReadHash256",
        "Basalt.Core.Address" => "ReadAddress",
        "Basalt.Core.UInt256" => "ReadUInt256",
        "Basalt.Core.Signature" => "ReadSignature",
        "Basalt.Core.PublicKey" => "ReadPublicKey",
        "Basalt.Core.BlsSignature" => "ReadBlsSignature",
        "Basalt.Core.BlsPublicKey" => "ReadBlsPublicKey",
        _ => null,
    };

    /// <summary>
    /// Returns the fixed byte size for the type, -1 for variable-size types (string, byte[]),
    /// or null if the type is unsupported.
    /// </summary>
    private static int? GetFixedSize(string typeFullName) => NormalizeType(typeFullName) switch
    {
        "byte" => 1,
        "ushort" => 2,
        "uint" => 4,
        "int" => 4,
        "ulong" => 8,
        "long" => 8,
        "bool" => 1,
        "string" => -1,
        "byte[]" => -1,
        "Basalt.Core.Hash256" => 32,
        "Basalt.Core.Address" => 20,
        "Basalt.Core.UInt256" => 32,
        "Basalt.Core.Signature" => 64,
        "Basalt.Core.PublicKey" => 32,
        "Basalt.Core.BlsSignature" => 96,
        "Basalt.Core.BlsPublicKey" => 48,
        _ => null,
    };

    /// <summary>
    /// Normalize fully-qualified names produced by Roslyn (e.g. <c>global::System.Byte</c>)
    /// to the short forms used in our mapping tables.
    /// </summary>
    private static string NormalizeType(string fqn)
    {
        // Strip "global::" prefix used by FullyQualifiedFormat.
        var name = fqn.StartsWith("global::") ? fqn.Substring("global::".Length) : fqn;

        // Map CLR names to C# keyword forms for primitives.
        return name switch
        {
            "System.Byte" => "byte",
            "System.UInt16" => "ushort",
            "System.UInt32" => "uint",
            "System.Int32" => "int",
            "System.UInt64" => "ulong",
            "System.Int64" => "long",
            "System.Boolean" => "bool",
            "System.String" => "string",
            "System.Byte[]" => "byte[]",
            _ => name,
        };
    }

    // ──────────────────────────────────────────────
    //  EquatableArray - needed for incremental caching
    // ──────────────────────────────────────────────

    /// <summary>
    /// A wrapper around <see cref="ImmutableArray{T}"/> that implements value equality,
    /// which is required for correct caching in incremental generators.
    /// </summary>
    private readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>
        where T : IEquatable<T>
    {
        private readonly ImmutableArray<T> _array;

        public EquatableArray(ImmutableArray<T> array) => _array = array;

        public ImmutableArray<T> AsImmutableArray() =>
            _array.IsDefault ? ImmutableArray<T>.Empty : _array;

        public bool Equals(EquatableArray<T> other)
        {
            var a = AsImmutableArray();
            var b = other.AsImmutableArray();

            if (a.Length != b.Length)
                return false;

            for (int i = 0; i < a.Length; i++)
            {
                if (!a[i].Equals(b[i]))
                    return false;
            }

            return true;
        }

        public override bool Equals(object? obj) =>
            obj is EquatableArray<T> other && Equals(other);

        public override int GetHashCode()
        {
            var arr = AsImmutableArray();
            int hash = 17;
            foreach (var item in arr)
                hash = hash * 31 + item.GetHashCode();
            return hash;
        }
    }
}
