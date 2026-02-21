using System.Reflection;
using Basalt.Generators.Codec;
using Basalt.Generators.Contracts;
using Basalt.Generators.Json;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Basalt.Sdk.Analyzers.Tests;

public class GeneratorTests
{
    private static readonly MetadataReference[] s_references = GetReferences();

#pragma warning disable IL3000 // Assembly.Location returns empty string in single-file apps
    private static MetadataReference[] GetReferences()
    {
        var refs = new List<MetadataReference>();
        // Add basic runtime references
        var trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var path in trustedAssemblies)
        {
            try { refs.Add(MetadataReference.CreateFromFile(path)); }
            catch { /* skip */ }
        }

        // Add Basalt.Core and Basalt.Codec
        var coreAssembly = typeof(Basalt.Core.Hash256).Assembly.Location;
        if (!string.IsNullOrEmpty(coreAssembly))
            refs.Add(MetadataReference.CreateFromFile(coreAssembly));

        var codecAssembly = typeof(Basalt.Codec.BasaltWriter).Assembly.Location;
        if (!string.IsNullOrEmpty(codecAssembly))
            refs.Add(MetadataReference.CreateFromFile(codecAssembly));

        var contractsAssembly = typeof(Basalt.Sdk.Contracts.BasaltContractAttribute).Assembly.Location;
        if (!string.IsNullOrEmpty(contractsAssembly))
            refs.Add(MetadataReference.CreateFromFile(contractsAssembly));

        return refs.ToArray();
    }
#pragma warning restore IL3000

    private static GeneratorDriverRunResult RunGenerator(IIncrementalGenerator generator, string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create("TestAssembly",
            [syntaxTree],
            s_references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver.Create(generator);
        driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation(
            compilation, out var outputCompilation, out var diagnostics);

        return driver.GetRunResult();
    }

    // ── Codec Generator Tests ───────────────────────────

    [Fact]
    public void CodecGenerator_GeneratesWriteToReadFromForSerializableStruct()
    {
        var source = @"
using Basalt.Core;
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltSerializable]
public partial struct MyData
{
    public uint Id { get; set; }
    public ulong Value { get; set; }
    public Hash256 Hash { get; set; }
}
";
        var result = RunGenerator(new CodecGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        generatedSource.Should().Contain("WriteTo");
        generatedSource.Should().Contain("ReadFrom");
        generatedSource.Should().Contain("GetSerializedSize");
        generatedSource.Should().Contain("writer.WriteUInt32(Id)");
        generatedSource.Should().Contain("writer.WriteUInt64(Value)");
        generatedSource.Should().Contain("writer.WriteHash256(Hash)");
        generatedSource.Should().Contain("reader.ReadUInt32()");
        generatedSource.Should().Contain("reader.ReadUInt64()");
        generatedSource.Should().Contain("reader.ReadHash256()");
    }

    [Fact]
    public void CodecGenerator_HandlesClassWithStringAndByteArray()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltSerializable]
public partial class Message
{
    public string Name { get; set; }
    public byte[] Data { get; set; }
    public bool Active { get; set; }
}
";
        var result = RunGenerator(new CodecGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        generatedSource.Should().Contain("writer.WriteString(Name)");
        generatedSource.Should().Contain("writer.WriteBytes(Data)");
        generatedSource.Should().Contain("writer.WriteBool(Active)");
        generatedSource.Should().Contain("reader.ReadString()");
        generatedSource.Should().Contain("reader.ReadBytes().ToArray()");
        generatedSource.Should().Contain("reader.ReadBool()");
        generatedSource.Should().Contain("new Message()"); // class uses constructor
    }

    [Fact]
    public void CodecGenerator_StructUsesDefault()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltSerializable]
public partial struct Pair
{
    public int A { get; set; }
    public int B { get; set; }
}
";
        var result = RunGenerator(new CodecGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        generatedSource.Should().Contain("default(Pair)"); // struct uses default
    }

    [Fact]
    public void CodecGenerator_CorrectSerializedSize()
    {
        var source = @"
using Basalt.Core;
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltSerializable]
public partial struct Fixed
{
    public byte A { get; set; }
    public uint B { get; set; }
    public Hash256 C { get; set; }
}
";
        var result = RunGenerator(new CodecGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        // byte=1 + uint=4 + Hash256=32 = 37
        generatedSource.Should().Contain("return 37;");
    }

    [Fact]
    public void CodecGenerator_BlsTypes()
    {
        var source = @"
using Basalt.Core;
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltSerializable]
public partial struct ConsensusData
{
    public BlsSignature Sig { get; set; }
    public BlsPublicKey Key { get; set; }
}
";
        var result = RunGenerator(new CodecGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        generatedSource.Should().Contain("WriteBlsSignature");
        generatedSource.Should().Contain("WriteBlsPublicKey");
        generatedSource.Should().Contain("ReadBlsSignature");
        generatedSource.Should().Contain("ReadBlsPublicKey");
        // BlsSignature=96 + BlsPublicKey=48 = 144
        generatedSource.Should().Contain("return 144;");
    }

    [Fact]
    public void CodecGenerator_NoOutput_ForUnannotatedType()
    {
        var source = @"
namespace TestNs;

public partial struct Plain
{
    public int X { get; set; }
}
";
        var result = RunGenerator(new CodecGenerator(), source);
        result.GeneratedTrees.Should().BeEmpty();
    }

    // ── JSON Generator Tests ────────────────────────────

    [Fact]
    public void JsonGenerator_GeneratesConvertersWhenAnnotatedTypeExists()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltJsonSerializable]
public partial class MyDto
{
    public string Name { get; set; }
}
";
        var result = RunGenerator(new JsonGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        // Should have the shared converters file
        var allSource = string.Join("\n", result.GeneratedTrees.Select(t => t.GetText().ToString()));
        allSource.Should().Contain("BasaltJsonConverters");
        allSource.Should().Contain("Hash256Converter");
        allSource.Should().Contain("AddressConverter");
        allSource.Should().Contain("BlsSignatureConverter");
        allSource.Should().Contain("BlsPublicKeyConverter");
    }

    [Fact]
    public void JsonGenerator_GeneratesPerTypeOptions()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltJsonSerializable]
public partial class MyDto
{
    public string Name { get; set; }
}
";
        var result = RunGenerator(new JsonGenerator(), source);

        var allSource = string.Join("\n", result.GeneratedTrees.Select(t => t.GetText().ToString()));
        allSource.Should().Contain("BasaltJsonOptions");
        allSource.Should().Contain("CreateOptions()");
    }

    [Fact]
    public void JsonGenerator_NoOutput_ForUnannotatedType()
    {
        var source = @"
namespace TestNs;

public partial class Plain
{
    public string Name { get; set; }
}
";
        var result = RunGenerator(new JsonGenerator(), source);
        result.GeneratedTrees.Should().BeEmpty();
    }

    // ── Contract Generator Tests ────────────────────────

    [Fact]
    public void ContractGenerator_GeneratesDispatchAndAbi()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltContract]
public partial class MyToken
{
    [BasaltView]
    public ulong BalanceOf(byte[] account)
    {
        return 0;
    }

    [BasaltEntrypoint]
    public bool Transfer(byte[] to, ulong amount)
    {
        return true;
    }
}
";
        var result = RunGenerator(new ContractGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        generatedSource.Should().Contain("Dispatch(byte[] selector, byte[] calldata)");
        generatedSource.Should().Contain("ContractAbi");
        generatedSource.Should().Contain("BalanceOf");
        generatedSource.Should().Contain("Transfer");
        generatedSource.Should().Contain("\"view\"");
        generatedSource.Should().Contain("\"mutable\"");
    }

    [Fact]
    public void ContractGenerator_DispatchDeserializesParameters()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltContract]
public partial class Calculator
{
    [BasaltEntrypoint]
    public int Add(int a, int b)
    {
        return a + b;
    }
}
";
        var result = RunGenerator(new ContractGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        generatedSource.Should().Contain("reader.ReadInt32()");
        generatedSource.Should().Contain("writer.WriteInt32(_result)");
    }

    [Fact]
    public void ContractGenerator_VoidMethodReturnsEmptyArray()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltContract]
public partial class Store
{
    [BasaltEntrypoint]
    public void SetValue(uint key)
    {
    }
}
";
        var result = RunGenerator(new ContractGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        generatedSource.Should().Contain("System.Array.Empty<byte>()");
    }

    [Fact]
    public void ContractGenerator_NoOutput_ForUnannotatedClass()
    {
        var source = @"
namespace TestNs;

public partial class NotAContract
{
    public int Value { get; set; }
}
";
        var result = RunGenerator(new ContractGenerator(), source);
        result.GeneratedTrees.Should().BeEmpty();
    }

    [Fact]
    public void ContractGenerator_SelectorIsDeterministic()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltContract]
public partial class Token
{
    [BasaltView]
    public ulong TotalSupply()
    {
        return 0;
    }
}
";
        var result1 = RunGenerator(new ContractGenerator(), source);
        var result2 = RunGenerator(new ContractGenerator(), source);

        var source1 = result1.GeneratedTrees[0].GetText().ToString();
        var source2 = result2.GeneratedTrees[0].GetText().ToString();

        source1.Should().Be(source2);
    }

    // ── Additional Codec Generator Tests ────────────────────

    [Fact]
    public void CodecGenerator_AllPrimitiveTypes()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltSerializable]
public partial struct AllPrimitives
{
    public byte A { get; set; }
    public ushort B { get; set; }
    public uint C { get; set; }
    public int D { get; set; }
    public ulong E { get; set; }
    public long F { get; set; }
    public bool G { get; set; }
}
";
        var result = RunGenerator(new CodecGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        generatedSource.Should().Contain("writer.WriteByte(A)");
        generatedSource.Should().Contain("writer.WriteUInt16(B)");
        generatedSource.Should().Contain("writer.WriteUInt32(C)");
        generatedSource.Should().Contain("writer.WriteInt32(D)");
        generatedSource.Should().Contain("writer.WriteUInt64(E)");
        generatedSource.Should().Contain("writer.WriteInt64(F)");
        generatedSource.Should().Contain("writer.WriteBool(G)");

        generatedSource.Should().Contain("reader.ReadByte()");
        generatedSource.Should().Contain("reader.ReadUInt16()");
        generatedSource.Should().Contain("reader.ReadUInt32()");
        generatedSource.Should().Contain("reader.ReadInt32()");
        generatedSource.Should().Contain("reader.ReadUInt64()");
        generatedSource.Should().Contain("reader.ReadInt64()");
        generatedSource.Should().Contain("reader.ReadBool()");

        // byte=1 + ushort=2 + uint=4 + int=4 + ulong=8 + long=8 + bool=1 = 28
        generatedSource.Should().Contain("return 28;");
    }

    [Fact]
    public void CodecGenerator_AddressType()
    {
        var source = @"
using Basalt.Core;
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltSerializable]
public partial struct AddrHolder
{
    public Address Addr { get; set; }
}
";
        var result = RunGenerator(new CodecGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        generatedSource.Should().Contain("writer.WriteAddress(Addr)");
        generatedSource.Should().Contain("reader.ReadAddress()");
        // Address = 20 bytes
        generatedSource.Should().Contain("return 20;");
    }

    [Fact]
    public void CodecGenerator_UInt256Type()
    {
        var source = @"
using Basalt.Core;
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltSerializable]
public partial struct BigNumHolder
{
    public UInt256 Value { get; set; }
}
";
        var result = RunGenerator(new CodecGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        generatedSource.Should().Contain("writer.WriteUInt256(Value)");
        generatedSource.Should().Contain("reader.ReadUInt256()");
        // UInt256 = 32 bytes
        generatedSource.Should().Contain("return 32;");
    }

    [Fact]
    public void CodecGenerator_SignatureType()
    {
        var source = @"
using Basalt.Core;
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltSerializable]
public partial struct SigHolder
{
    public Signature Sig { get; set; }
}
";
        var result = RunGenerator(new CodecGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        generatedSource.Should().Contain("writer.WriteSignature(Sig)");
        generatedSource.Should().Contain("reader.ReadSignature()");
        // Signature = 64 bytes
        generatedSource.Should().Contain("return 64;");
    }

    [Fact]
    public void CodecGenerator_PublicKeyType()
    {
        var source = @"
using Basalt.Core;
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltSerializable]
public partial struct PubKeyHolder
{
    public PublicKey Key { get; set; }
}
";
        var result = RunGenerator(new CodecGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        generatedSource.Should().Contain("writer.WritePublicKey(Key)");
        generatedSource.Should().Contain("reader.ReadPublicKey()");
        // PublicKey = 32 bytes
        generatedSource.Should().Contain("return 32;");
    }

    [Fact]
    public void CodecGenerator_MixedFixedAndVariableSize()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltSerializable]
public partial struct Mixed
{
    public uint Id { get; set; }
    public string Name { get; set; }
}
";
        var result = RunGenerator(new CodecGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        // Variable-size means GetSerializedSize computes actual runtime length
        generatedSource.Should().Contain("int size = 4;");
        generatedSource.Should().Contain("size += 4 + System.Text.Encoding.UTF8.GetByteCount(Name");
        generatedSource.Should().Contain("return size;");
    }

    [Fact]
    public void CodecGenerator_EmptyStruct()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltSerializable]
public partial struct Empty
{
}
";
        var result = RunGenerator(new CodecGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        generatedSource.Should().Contain("WriteTo");
        generatedSource.Should().Contain("ReadFrom");
        generatedSource.Should().Contain("GetSerializedSize");
        // No members → size = 0
        generatedSource.Should().Contain("return 0;");
    }

    [Fact]
    public void CodecGenerator_ClassWithMultipleStringFields()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltSerializable]
public partial class MultiString
{
    public string First { get; set; }
    public string Second { get; set; }
    public string Third { get; set; }
}
";
        var result = RunGenerator(new CodecGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        generatedSource.Should().Contain("writer.WriteString(First)");
        generatedSource.Should().Contain("writer.WriteString(Second)");
        generatedSource.Should().Contain("writer.WriteString(Third)");

        // Three variable-length fields → 3 runtime-computed lines
        generatedSource.Should().Contain("size += 4 + System.Text.Encoding.UTF8.GetByteCount(First");
        generatedSource.Should().Contain("size += 4 + System.Text.Encoding.UTF8.GetByteCount(Second");
        generatedSource.Should().Contain("size += 4 + System.Text.Encoding.UTF8.GetByteCount(Third");
    }

    [Fact]
    public void CodecGenerator_PublicFieldsAreIncluded()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltSerializable]
public partial struct WithFields
{
    public int X;
    public int Y;
}
";
        var result = RunGenerator(new CodecGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        generatedSource.Should().Contain("writer.WriteInt32(X)");
        generatedSource.Should().Contain("writer.WriteInt32(Y)");
        generatedSource.Should().Contain("reader.ReadInt32()");
    }

    [Fact]
    public void CodecGenerator_PrivatePropertiesAreExcluded()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltSerializable]
public partial class WithPrivate
{
    public uint Visible { get; set; }
    private uint Hidden { get; set; }
}
";
        var result = RunGenerator(new CodecGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        generatedSource.Should().Contain("writer.WriteUInt32(Visible)");
        generatedSource.Should().NotContain("Hidden");
    }

    [Fact]
    public void CodecGenerator_StaticPropertiesAreExcluded()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltSerializable]
public partial class WithStatic
{
    public uint Instance { get; set; }
    public static uint StaticProp { get; set; }
}
";
        var result = RunGenerator(new CodecGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        generatedSource.Should().Contain("writer.WriteUInt32(Instance)");
        generatedSource.Should().NotContain("StaticProp");
    }

    [Fact]
    public void CodecGenerator_NamespaceIsIncluded()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace My.Deep.Namespace;

[BasaltSerializable]
public partial struct Namespaced
{
    public int Val { get; set; }
}
";
        var result = RunGenerator(new CodecGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        generatedSource.Should().Contain("namespace My.Deep.Namespace;");
    }

    [Fact]
    public void CodecGenerator_AutoGeneratedHeader()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltSerializable]
public partial struct HeaderCheck
{
    public int A { get; set; }
}
";
        var result = RunGenerator(new CodecGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        generatedSource.Should().StartWith("// <auto-generated />");
    }

    [Fact]
    public void CodecGenerator_HintNamePattern()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltSerializable]
public partial struct MyType
{
    public int X { get; set; }
}
";
        var result = RunGenerator(new CodecGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var hintName = result.GeneratedTrees[0].FilePath;
        hintName.Should().EndWith("MyType.BasaltCodec.g.cs");
    }

    [Fact]
    public void CodecGenerator_MultipleTypes_GeneratesSeparateOutputs()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltSerializable]
public partial struct TypeA
{
    public int X { get; set; }
}

[BasaltSerializable]
public partial struct TypeB
{
    public uint Y { get; set; }
}
";
        var result = RunGenerator(new CodecGenerator(), source);

        result.GeneratedTrees.Should().HaveCount(2);

        var allSource = string.Join("\n", result.GeneratedTrees.Select(t => t.GetText().ToString()));
        allSource.Should().Contain("partial struct TypeA");
        allSource.Should().Contain("partial struct TypeB");
    }

    // ── Additional JSON Generator Tests ─────────────────────

    [Fact]
    public void JsonGenerator_GeneratesUInt256Converter()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltJsonSerializable]
public partial class Dto
{
    public string Name { get; set; }
}
";
        var result = RunGenerator(new JsonGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var allSource = string.Join("\n", result.GeneratedTrees.Select(t => t.GetText().ToString()));
        allSource.Should().Contain("UInt256Converter");
    }

    [Fact]
    public void JsonGenerator_GeneratesSignatureConverter()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltJsonSerializable]
public partial class Dto
{
    public string Name { get; set; }
}
";
        var result = RunGenerator(new JsonGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var allSource = string.Join("\n", result.GeneratedTrees.Select(t => t.GetText().ToString()));
        allSource.Should().Contain("SignatureConverter");
    }

    [Fact]
    public void JsonGenerator_RecordType()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltJsonSerializable]
public partial record MyRecord
{
    public string Name { get; set; }
}
";
        var result = RunGenerator(new JsonGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var allSource = string.Join("\n", result.GeneratedTrees.Select(t => t.GetText().ToString()));
        allSource.Should().Contain("partial record MyRecord");
    }

    [Fact]
    public void JsonGenerator_StructType()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltJsonSerializable]
public partial struct MyStruct
{
    public string Name { get; set; }
}
";
        var result = RunGenerator(new JsonGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var allSource = string.Join("\n", result.GeneratedTrees.Select(t => t.GetText().ToString()));
        allSource.Should().Contain("partial struct MyStruct");
    }

    [Fact]
    public void JsonGenerator_RecordStruct()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltJsonSerializable]
public partial record struct MyRecordStruct
{
    public string Name { get; set; }
}
";
        var result = RunGenerator(new JsonGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var allSource = string.Join("\n", result.GeneratedTrees.Select(t => t.GetText().ToString()));
        allSource.Should().Contain("partial record struct MyRecordStruct");
    }

    [Fact]
    public void JsonGenerator_MultipleAnnotatedTypes_GeneratesMultipleOutputs()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltJsonSerializable]
public partial class DtoA
{
    public string A { get; set; }
}

[BasaltJsonSerializable]
public partial class DtoB
{
    public string B { get; set; }
}
";
        var result = RunGenerator(new JsonGenerator(), source);

        // 1 shared converters file + 2 per-type files = 3
        result.GeneratedTrees.Should().HaveCount(3);

        var allSource = string.Join("\n", result.GeneratedTrees.Select(t => t.GetText().ToString()));
        allSource.Should().Contain("partial class DtoA");
        allSource.Should().Contain("partial class DtoB");
        allSource.Should().Contain("BasaltJsonConverters");
    }

    [Fact]
    public void JsonGenerator_PerTypeContainsBasaltJsonOptions()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltJsonSerializable]
public partial class MyDto
{
    public string Name { get; set; }
}
";
        var result = RunGenerator(new JsonGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        // Find the per-type source (not the shared converters file)
        var perTypeSource = result.GeneratedTrees
            .Select(t => t.GetText().ToString())
            .FirstOrDefault(s => s.Contains("partial class MyDto"));

        perTypeSource.Should().NotBeNull();
        perTypeSource.Should().Contain("BasaltJsonOptions");
        perTypeSource.Should().Contain("static System.Text.Json.JsonSerializerOptions");
    }

    [Fact]
    public void JsonGenerator_ConvertersContainsCreateOptions()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltJsonSerializable]
public partial class MyDto
{
    public string Name { get; set; }
}
";
        var result = RunGenerator(new JsonGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var convertersSource = result.GeneratedTrees
            .Select(t => t.GetText().ToString())
            .FirstOrDefault(s => s.Contains("BasaltJsonConverters"));

        convertersSource.Should().NotBeNull();
        convertersSource.Should().Contain("public static System.Text.Json.JsonSerializerOptions CreateOptions()");
    }

    [Fact]
    public void JsonGenerator_ConvertersContainsAddConverters()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltJsonSerializable]
public partial class MyDto
{
    public string Name { get; set; }
}
";
        var result = RunGenerator(new JsonGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var convertersSource = result.GeneratedTrees
            .Select(t => t.GetText().ToString())
            .FirstOrDefault(s => s.Contains("BasaltJsonConverters"));

        convertersSource.Should().NotBeNull();
        convertersSource.Should().Contain("public static void AddConverters(System.Text.Json.JsonSerializerOptions options)");
    }

    [Fact]
    public void JsonGenerator_AutoGeneratedHeader()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltJsonSerializable]
public partial class MyDto
{
    public string Name { get; set; }
}
";
        var result = RunGenerator(new JsonGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        foreach (var tree in result.GeneratedTrees)
        {
            var text = tree.GetText().ToString();
            text.Should().StartWith("// <auto-generated");
        }
    }

    // ── Additional Contract Generator Tests ─────────────────

    [Fact]
    public void ContractGenerator_EventInAbi()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltContract]
public partial class TokenContract
{
    [BasaltEvent]
    public class TransferEvent
    {
        public string From { get; set; }
        public string To { get; set; }
        public ulong Amount { get; set; }
    }

    [BasaltEntrypoint]
    public bool Transfer(byte[] to, ulong amount)
    {
        return true;
    }
}
";
        var result = RunGenerator(new ContractGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        generatedSource.Should().Contain("\"\"events\"\"");
        generatedSource.Should().Contain("TransferEvent");
        generatedSource.Should().Contain("\"\"name\"\":\"\"From\"\"");
        generatedSource.Should().Contain("\"\"name\"\":\"\"To\"\"");
        generatedSource.Should().Contain("\"\"name\"\":\"\"Amount\"\"");
    }

    [Fact]
    public void ContractGenerator_EventWithIndexedProperty()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltContract]
public partial class TokenContract
{
    [BasaltEvent]
    public class TransferEvent
    {
        [Indexed]
        public string From { get; set; }
        [Indexed]
        public string To { get; set; }
        public ulong Amount { get; set; }
    }

    [BasaltEntrypoint]
    public void DoTransfer()
    {
    }
}
";
        var result = RunGenerator(new ContractGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        // Indexed properties should have indexed:true
        generatedSource.Should().Contain("\"\"indexed\"\":true");
        // Non-indexed should have indexed:false
        generatedSource.Should().Contain("\"\"indexed\"\":false");
    }

    [Fact]
    public void ContractGenerator_MultipleMethodsInDispatch()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltContract]
public partial class MultiMethod
{
    [BasaltView]
    public uint GetA()
    {
        return 0;
    }

    [BasaltView]
    public uint GetB()
    {
        return 0;
    }

    [BasaltEntrypoint]
    public void SetC(uint val)
    {
    }
}
";
        var result = RunGenerator(new ContractGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        generatedSource.Should().Contain("switch (sel)");
        generatedSource.Should().Contain("GetA");
        generatedSource.Should().Contain("GetB");
        generatedSource.Should().Contain("SetC");
        // 3 case labels
        generatedSource.Should().Contain("case 0x");
    }

    [Fact]
    public void ContractGenerator_ParameterTypes_String()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltContract]
public partial class StringContract
{
    [BasaltEntrypoint]
    public void SetName(string name)
    {
    }
}
";
        var result = RunGenerator(new ContractGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        generatedSource.Should().Contain("reader.ReadString()");
    }

    [Fact]
    public void ContractGenerator_ParameterTypes_ByteArray()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltContract]
public partial class BytesContract
{
    [BasaltEntrypoint]
    public void SetData(byte[] data)
    {
    }
}
";
        var result = RunGenerator(new ContractGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        generatedSource.Should().Contain("reader.ReadBytes().ToArray()");
    }

    [Fact]
    public void ContractGenerator_ParameterTypes_Hash256()
    {
        var source = @"
using Basalt.Core;
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltContract]
public partial class HashContract
{
    [BasaltEntrypoint]
    public void SetHash(Hash256 hash)
    {
    }
}
";
        var result = RunGenerator(new ContractGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        generatedSource.Should().Contain("reader.ReadHash256()");
    }

    [Fact]
    public void ContractGenerator_ParameterTypes_Address()
    {
        var source = @"
using Basalt.Core;
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltContract]
public partial class AddrContract
{
    [BasaltEntrypoint]
    public void SetAddr(Address addr)
    {
    }
}
";
        var result = RunGenerator(new ContractGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        generatedSource.Should().Contain("reader.ReadAddress()");
    }

    [Fact]
    public void ContractGenerator_ParameterTypes_UInt256()
    {
        var source = @"
using Basalt.Core;
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltContract]
public partial class BigNumContract
{
    [BasaltEntrypoint]
    public void SetAmount(UInt256 amount)
    {
    }
}
";
        var result = RunGenerator(new ContractGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        generatedSource.Should().Contain("reader.ReadUInt256()");
    }

    [Fact]
    public void ContractGenerator_ReturnType_String()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltContract]
public partial class StringReturnContract
{
    [BasaltView]
    public string GetName()
    {
        return """";
    }
}
";
        var result = RunGenerator(new ContractGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        generatedSource.Should().Contain("writer.WriteString(_result)");
        // Variable-length return uses 4096 estimate buffer
        generatedSource.Should().Contain("new byte[4096]");
    }

    [Fact]
    public void ContractGenerator_ReturnType_Bool()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltContract]
public partial class BoolReturnContract
{
    [BasaltView]
    public bool IsActive()
    {
        return true;
    }
}
";
        var result = RunGenerator(new ContractGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        generatedSource.Should().Contain("writer.WriteBool(_result)");
        generatedSource.Should().Contain("new byte[1]");
    }

    [Fact]
    public void ContractGenerator_ReturnType_Hash256()
    {
        var source = @"
using Basalt.Core;
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltContract]
public partial class HashReturnContract
{
    [BasaltView]
    public Hash256 GetHash()
    {
        return default;
    }
}
";
        var result = RunGenerator(new ContractGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        generatedSource.Should().Contain("writer.WriteHash256(_result)");
        generatedSource.Should().Contain("new byte[32]");
    }

    [Fact]
    public void ContractGenerator_MultipleParameters()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltContract]
public partial class MultiParamContract
{
    [BasaltEntrypoint]
    public bool DoSomething(uint id, string name, bool flag)
    {
        return true;
    }
}
";
        var result = RunGenerator(new ContractGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        generatedSource.Should().Contain("reader.ReadUInt32()");
        generatedSource.Should().Contain("reader.ReadString()");
        generatedSource.Should().Contain("reader.ReadBool()");
        generatedSource.Should().Contain("_p_id");
        generatedSource.Should().Contain("_p_name");
        generatedSource.Should().Contain("_p_flag");
    }

    [Fact]
    public void ContractGenerator_DispatchInvalidSelector_ThrowsException()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltContract]
public partial class SimpleContract
{
    [BasaltView]
    public uint GetVal()
    {
        return 0;
    }
}
";
        var result = RunGenerator(new ContractGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        generatedSource.Should().Contain("default:");
        generatedSource.Should().Contain("throw new System.InvalidOperationException");
        generatedSource.Should().Contain("Unknown selector");
    }

    [Fact]
    public void ContractGenerator_AbiContainsMethodSelector()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace TestNs;

[BasaltContract]
public partial class SelectorContract
{
    [BasaltView]
    public uint GetBalance()
    {
        return 0;
    }
}
";
        var result = RunGenerator(new ContractGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        // ABI should contain a hex selector string like "0xABCDEF01"
        generatedSource.Should().Contain("\"\"selector\"\":\"\"0x");
    }

    [Fact]
    public void ContractGenerator_NamespaceInOutput()
    {
        var source = @"
using Basalt.Sdk.Contracts;

namespace My.Custom.Namespace;

[BasaltContract]
public partial class NamespacedContract
{
    [BasaltView]
    public uint GetVal()
    {
        return 0;
    }
}
";
        var result = RunGenerator(new ContractGenerator(), source);

        result.GeneratedTrees.Should().NotBeEmpty();

        var generatedSource = result.GeneratedTrees[0].GetText().ToString();

        generatedSource.Should().Contain("namespace My.Custom.Namespace;");
    }
}
