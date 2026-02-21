using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Basalt.Generators.Json;

[Generator]
public sealed class JsonGenerator : IIncrementalGenerator
{
    private const string AttributeFullName = "Basalt.Sdk.Contracts.BasaltJsonSerializableAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Collect types annotated with [BasaltJsonSerializable].
        var annotatedTypes = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AttributeFullName,
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: static (ctx, _) => GetTypeInfo(ctx))
            .Where(static t => t is not null)
            .Collect();

        // 3. When at least one annotated type exists, emit the shared BasaltJsonConverters class.
        context.RegisterSourceOutput(annotatedTypes, static (spc, types) =>
        {
            if (types.IsDefaultOrEmpty)
                return;

            spc.AddSource("BasaltJsonConverters.g.cs", BuildConvertersSource());

            foreach (var typeInfo in types)
            {
                if (typeInfo is null)
                    continue;

                var source = BuildPerTypeSource(typeInfo.Value);
                spc.AddSource($"{typeInfo.Value.FullyQualifiedMetadataName}.JsonOptions.g.cs", source);
            }
        });
    }

    // ──────────────────────────────────────────────────────────────────
    // Extract minimal type info from the semantic model
    // ──────────────────────────────────────────────────────────────────

    private readonly struct AnnotatedTypeInfo : IEquatable<AnnotatedTypeInfo>
    {
        public readonly string Name;
        public readonly string Namespace;
        public readonly string FullyQualifiedMetadataName;
        public readonly bool IsRecord;
        public readonly bool IsStruct;
        public readonly EquatableArray<string> ContainingTypes;

        public AnnotatedTypeInfo(
            string name,
            string @namespace,
            string fullyQualifiedMetadataName,
            bool isRecord,
            bool isStruct,
            EquatableArray<string> containingTypes)
        {
            Name = name;
            Namespace = @namespace;
            FullyQualifiedMetadataName = fullyQualifiedMetadataName;
            IsRecord = isRecord;
            IsStruct = isStruct;
            ContainingTypes = containingTypes;
        }

        public bool Equals(AnnotatedTypeInfo other) =>
            Name == other.Name &&
            Namespace == other.Namespace &&
            FullyQualifiedMetadataName == other.FullyQualifiedMetadataName &&
            IsRecord == other.IsRecord &&
            IsStruct == other.IsStruct &&
            ContainingTypes.Equals(other.ContainingTypes);

        public override bool Equals(object? obj) =>
            obj is AnnotatedTypeInfo other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + (Name?.GetHashCode() ?? 0);
                hash = hash * 31 + (Namespace?.GetHashCode() ?? 0);
                hash = hash * 31 + (FullyQualifiedMetadataName?.GetHashCode() ?? 0);
                hash = hash * 31 + IsRecord.GetHashCode();
                hash = hash * 31 + IsStruct.GetHashCode();
                hash = hash * 31 + ContainingTypes.GetHashCode();
                return hash;
            }
        }
    }

    private static AnnotatedTypeInfo? GetTypeInfo(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol typeSymbol)
            return null;

        var containingTypes = ImmutableArray.CreateBuilder<string>();
        var outer = typeSymbol.ContainingType;
        while (outer is not null)
        {
            containingTypes.Insert(0, $"{GetTypeKeyword(outer)} {outer.Name}");
            outer = outer.ContainingType;
        }

        return new AnnotatedTypeInfo(
            name: typeSymbol.Name,
            @namespace: typeSymbol.ContainingNamespace.IsGlobalNamespace
                ? string.Empty
                : typeSymbol.ContainingNamespace.ToDisplayString(),
            fullyQualifiedMetadataName: typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                .Replace("global::", "")
                .Replace("<", "_")
                .Replace(">", "_")
                .Replace(",", "_")
                .Replace(" ", ""),
            isRecord: typeSymbol.IsRecord,
            isStruct: typeSymbol.IsValueType,
            containingTypes: new EquatableArray<string>(containingTypes.ToImmutable()));
    }

    private static string GetTypeKeyword(INamedTypeSymbol type)
    {
        if (type.IsRecord && type.IsValueType)
            return "partial record struct";
        if (type.IsRecord)
            return "partial record";
        if (type.IsValueType)
            return "partial struct";
        return "partial class";
    }

    // ──────────────────────────────────────────────────────────────────
    // Build the shared BasaltJsonConverters source
    // ──────────────────────────────────────────────────────────────────

    private static string BuildConvertersSource()
    {
        return @"// <auto-generated />
// Basalt JSON converters for blockchain primitive types.
#nullable enable

namespace Basalt.Generators.Json
{
    /// <summary>
    /// Provides pre-built <see cref=""System.Text.Json.Serialization.JsonConverter""/> instances
    /// for all Basalt blockchain primitive types and a factory method to create configured
    /// <see cref=""System.Text.Json.JsonSerializerOptions""/>.
    /// </summary>
    public static class BasaltJsonConverters
    {
        /// <summary>
        /// Creates a new <see cref=""System.Text.Json.JsonSerializerOptions""/> instance with all
        /// Basalt primitive type converters registered.
        /// </summary>
        public static System.Text.Json.JsonSerializerOptions CreateOptions()
        {
            var options = new System.Text.Json.JsonSerializerOptions();
            AddConverters(options);
            return options;
        }

        /// <summary>
        /// Adds all Basalt primitive type converters to an existing options instance.
        /// </summary>
        public static void AddConverters(System.Text.Json.JsonSerializerOptions options)
        {
            options.Converters.Add(new Hash256Converter());
            options.Converters.Add(new AddressConverter());
            options.Converters.Add(new UInt256Converter());
            options.Converters.Add(new SignatureConverter());
            options.Converters.Add(new PublicKeyConverter());
            options.Converters.Add(new BlsSignatureConverter());
            options.Converters.Add(new BlsPublicKeyConverter());
        }

        // ────────────────────────────────────────────
        // Hash256  (32 bytes → ""0x"" + 64 hex chars)
        // ────────────────────────────────────────────

        public sealed class Hash256Converter : System.Text.Json.Serialization.JsonConverter<global::Basalt.Core.Hash256>
        {
            public override global::Basalt.Core.Hash256 Read(
                ref System.Text.Json.Utf8JsonReader reader,
                System.Type typeToConvert,
                System.Text.Json.JsonSerializerOptions options)
            {
                var hex = reader.GetString();
                if (hex is null)
                    throw new System.Text.Json.JsonException(""Expected a non-null string for Hash256."");

                return global::Basalt.Core.Hash256.FromHexString(hex);
            }

            public override void Write(
                System.Text.Json.Utf8JsonWriter writer,
                global::Basalt.Core.Hash256 value,
                System.Text.Json.JsonSerializerOptions options)
            {
                writer.WriteStringValue(value.ToHexString());
            }
        }

        // ────────────────────────────────────────────
        // Address  (20 bytes → ""0x"" + 40 hex chars)
        // ────────────────────────────────────────────

        public sealed class AddressConverter : System.Text.Json.Serialization.JsonConverter<global::Basalt.Core.Address>
        {
            public override global::Basalt.Core.Address Read(
                ref System.Text.Json.Utf8JsonReader reader,
                System.Type typeToConvert,
                System.Text.Json.JsonSerializerOptions options)
            {
                var hex = reader.GetString();
                if (hex is null)
                    throw new System.Text.Json.JsonException(""Expected a non-null string for Address."");

                return global::Basalt.Core.Address.FromHexString(hex);
            }

            public override void Write(
                System.Text.Json.Utf8JsonWriter writer,
                global::Basalt.Core.Address value,
                System.Text.Json.JsonSerializerOptions options)
            {
                writer.WriteStringValue(value.ToHexString());
            }
        }

        // ────────────────────────────────────────────
        // UInt256  (decimal string)
        // ────────────────────────────────────────────

        public sealed class UInt256Converter : System.Text.Json.Serialization.JsonConverter<global::Basalt.Core.UInt256>
        {
            public override global::Basalt.Core.UInt256 Read(
                ref System.Text.Json.Utf8JsonReader reader,
                System.Type typeToConvert,
                System.Text.Json.JsonSerializerOptions options)
            {
                var s = reader.GetString();
                if (s is null)
                    throw new System.Text.Json.JsonException(""Expected a non-null string for UInt256."");

                return global::Basalt.Core.UInt256.Parse(s);
            }

            public override void Write(
                System.Text.Json.Utf8JsonWriter writer,
                global::Basalt.Core.UInt256 value,
                System.Text.Json.JsonSerializerOptions options)
            {
                writer.WriteStringValue(value.ToString());
            }
        }

        // ────────────────────────────────────────────
        // Signature  (64 bytes → ""0x"" + 128 hex chars)
        // ────────────────────────────────────────────

        public sealed class SignatureConverter : System.Text.Json.Serialization.JsonConverter<global::Basalt.Core.Signature>
        {
            public override global::Basalt.Core.Signature Read(
                ref System.Text.Json.Utf8JsonReader reader,
                System.Type typeToConvert,
                System.Text.Json.JsonSerializerOptions options)
            {
                var hex = reader.GetString();
                if (hex is null)
                    throw new System.Text.Json.JsonException(""Expected a non-null string for Signature."");

                var span = hex.AsSpan();
                if (span.StartsWith(""0x"".AsSpan(), System.StringComparison.OrdinalIgnoreCase))
                    span = span.Slice(2);

                if (span.Length != global::Basalt.Core.Signature.Size * 2)
                    throw new System.Text.Json.JsonException(
                        $""Invalid hex length for Signature. Expected {global::Basalt.Core.Signature.Size * 2} hex characters."");

                var bytes = System.Convert.FromHexString(span.ToString());
                return new global::Basalt.Core.Signature(bytes);
            }

            public override void Write(
                System.Text.Json.Utf8JsonWriter writer,
                global::Basalt.Core.Signature value,
                System.Text.Json.JsonSerializerOptions options)
            {
                writer.WriteStringValue(""0x"" + System.Convert.ToHexString(value.ToArray()).ToLowerInvariant());
            }
        }

        // ────────────────────────────────────────────
        // PublicKey  (32 bytes → ""0x"" + 64 hex chars)
        // ────────────────────────────────────────────

        public sealed class PublicKeyConverter : System.Text.Json.Serialization.JsonConverter<global::Basalt.Core.PublicKey>
        {
            public override global::Basalt.Core.PublicKey Read(
                ref System.Text.Json.Utf8JsonReader reader,
                System.Type typeToConvert,
                System.Text.Json.JsonSerializerOptions options)
            {
                var hex = reader.GetString();
                if (hex is null)
                    throw new System.Text.Json.JsonException(""Expected a non-null string for PublicKey."");

                var span = hex.AsSpan();
                if (span.StartsWith(""0x"".AsSpan(), System.StringComparison.OrdinalIgnoreCase))
                    span = span.Slice(2);

                if (span.Length != global::Basalt.Core.PublicKey.Size * 2)
                    throw new System.Text.Json.JsonException(
                        $""Invalid hex length for PublicKey. Expected {global::Basalt.Core.PublicKey.Size * 2} hex characters."");

                var bytes = System.Convert.FromHexString(span.ToString());
                return new global::Basalt.Core.PublicKey(bytes);
            }

            public override void Write(
                System.Text.Json.Utf8JsonWriter writer,
                global::Basalt.Core.PublicKey value,
                System.Text.Json.JsonSerializerOptions options)
            {
                writer.WriteStringValue(""0x"" + System.Convert.ToHexString(value.ToArray()).ToLowerInvariant());
            }
        }

        // ────────────────────────────────────────────
        // BlsSignature  (96 bytes → ""0x"" + 192 hex chars)
        // ────────────────────────────────────────────

        public sealed class BlsSignatureConverter : System.Text.Json.Serialization.JsonConverter<global::Basalt.Core.BlsSignature>
        {
            public override global::Basalt.Core.BlsSignature Read(
                ref System.Text.Json.Utf8JsonReader reader,
                System.Type typeToConvert,
                System.Text.Json.JsonSerializerOptions options)
            {
                var hex = reader.GetString();
                if (hex is null)
                    throw new System.Text.Json.JsonException(""Expected a non-null string for BlsSignature."");

                var span = hex.AsSpan();
                if (span.StartsWith(""0x"".AsSpan(), System.StringComparison.OrdinalIgnoreCase))
                    span = span.Slice(2);

                if (span.Length != global::Basalt.Core.BlsSignature.Size * 2)
                    throw new System.Text.Json.JsonException(
                        $""Invalid hex length for BlsSignature. Expected {global::Basalt.Core.BlsSignature.Size * 2} hex characters."");

                var bytes = System.Convert.FromHexString(span.ToString());
                return new global::Basalt.Core.BlsSignature(bytes);
            }

            public override void Write(
                System.Text.Json.Utf8JsonWriter writer,
                global::Basalt.Core.BlsSignature value,
                System.Text.Json.JsonSerializerOptions options)
            {
                writer.WriteStringValue(""0x"" + System.Convert.ToHexString(value.ToArray()).ToLowerInvariant());
            }
        }

        // ────────────────────────────────────────────
        // BlsPublicKey  (48 bytes → ""0x"" + 96 hex chars)
        // ────────────────────────────────────────────

        public sealed class BlsPublicKeyConverter : System.Text.Json.Serialization.JsonConverter<global::Basalt.Core.BlsPublicKey>
        {
            public override global::Basalt.Core.BlsPublicKey Read(
                ref System.Text.Json.Utf8JsonReader reader,
                System.Type typeToConvert,
                System.Text.Json.JsonSerializerOptions options)
            {
                var hex = reader.GetString();
                if (hex is null)
                    throw new System.Text.Json.JsonException(""Expected a non-null string for BlsPublicKey."");

                var span = hex.AsSpan();
                if (span.StartsWith(""0x"".AsSpan(), System.StringComparison.OrdinalIgnoreCase))
                    span = span.Slice(2);

                if (span.Length != global::Basalt.Core.BlsPublicKey.Size * 2)
                    throw new System.Text.Json.JsonException(
                        $""Invalid hex length for BlsPublicKey. Expected {global::Basalt.Core.BlsPublicKey.Size * 2} hex characters."");

                var bytes = System.Convert.FromHexString(span.ToString());
                return new global::Basalt.Core.BlsPublicKey(bytes);
            }

            public override void Write(
                System.Text.Json.Utf8JsonWriter writer,
                global::Basalt.Core.BlsPublicKey value,
                System.Text.Json.JsonSerializerOptions options)
            {
                writer.WriteStringValue(""0x"" + System.Convert.ToHexString(value.ToArray()).ToLowerInvariant());
            }
        }
    }
}
";
    }

    // ──────────────────────────────────────────────────────────────────
    // Build per-type partial class with JsonSerializerOptions property
    // ──────────────────────────────────────────────────────────────────

    private static string BuildPerTypeSource(AnnotatedTypeInfo info)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("// Basalt JSON serialization support for " + info.Name);
        sb.AppendLine("#nullable enable");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(info.Namespace))
        {
            sb.AppendLine($"namespace {info.Namespace}");
            sb.AppendLine("{");
        }

        var indent = string.IsNullOrEmpty(info.Namespace) ? "" : "    ";

        // Open containing types
        foreach (var container in info.ContainingTypes.AsImmutableArray())
        {
            sb.AppendLine($"{indent}{container}");
            sb.AppendLine($"{indent}{{");
            indent += "    ";
        }

        var typeKeyword = info.IsRecord && info.IsStruct
            ? "partial record struct"
            : info.IsRecord
                ? "partial record"
                : info.IsStruct
                    ? "partial struct"
                    : "partial class";

        sb.AppendLine($"{indent}{typeKeyword} {info.Name}");
        sb.AppendLine($"{indent}{{");

        var bodyIndent = indent + "    ";

        // M-06: Thread-safe lazy initialization using Lazy<T>
        sb.AppendLine($"{bodyIndent}private static readonly System.Lazy<System.Text.Json.JsonSerializerOptions> _basaltJsonOptionsLazy =");
        sb.AppendLine($"{bodyIndent}    new System.Lazy<System.Text.Json.JsonSerializerOptions>(() => global::Basalt.Generators.Json.BasaltJsonConverters.CreateOptions());");
        sb.AppendLine();
        sb.AppendLine($"{bodyIndent}/// <summary>");
        sb.AppendLine($"{bodyIndent}/// Gets a cached <see cref=\"System.Text.Json.JsonSerializerOptions\"/> instance");
        sb.AppendLine($"{bodyIndent}/// pre-configured with converters for all Basalt blockchain primitive types.");
        sb.AppendLine($"{bodyIndent}/// </summary>");
        sb.AppendLine($"{bodyIndent}public static System.Text.Json.JsonSerializerOptions BasaltJsonOptions => _basaltJsonOptionsLazy.Value;");

        sb.AppendLine($"{indent}}}");

        // Close containing types
        foreach (var _ in info.ContainingTypes.AsImmutableArray())
        {
            indent = indent.Substring(4);
            sb.AppendLine($"{indent}}}");
        }

        if (!string.IsNullOrEmpty(info.Namespace))
        {
            sb.AppendLine("}");
        }

        return sb.ToString();
    }

    // ──────────────────────────────────────────────────────────────────
    // Equatable wrapper for ImmutableArray<T> (required for caching)
    // ──────────────────────────────────────────────────────────────────

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
            unchecked
            {
                int hash = 17;
                foreach (var item in arr)
                    hash = hash * 31 + item.GetHashCode();
                return hash;
            }
        }
    }
}
