using Basalt.Codec;
using Basalt.Execution.VM;
using Basalt.Sdk.Contracts;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests;

public class ContractRegistryTests
{
    // ---- IsSdkContract ----

    [Fact]
    public void IsSdkContract_ReturnsTrue_ForMagicBytes()
    {
        var code = new byte[] { 0xBA, 0x5A, 0x00, 0x01 };
        ContractRegistry.IsSdkContract(code).Should().BeTrue();
    }

    [Fact]
    public void IsSdkContract_ReturnsTrue_ForLongerCode()
    {
        var code = new byte[] { 0xBA, 0x5A, 0x00, 0x01, 0xFF, 0xEE, 0xDD };
        ContractRegistry.IsSdkContract(code).Should().BeTrue();
    }

    [Fact]
    public void IsSdkContract_ReturnsFalse_ForWrongMagic()
    {
        var code = new byte[] { 0x00, 0x00, 0x00, 0x01 };
        ContractRegistry.IsSdkContract(code).Should().BeFalse();
    }

    [Fact]
    public void IsSdkContract_ReturnsFalse_ForPartialMagic()
    {
        var code = new byte[] { 0xBA, 0x00, 0x00, 0x01 };
        ContractRegistry.IsSdkContract(code).Should().BeFalse();
    }

    [Fact]
    public void IsSdkContract_ReturnsFalse_ForTooShortCode()
    {
        var code = new byte[] { 0xBA, 0x5A };
        ContractRegistry.IsSdkContract(code).Should().BeFalse();
    }

    [Fact]
    public void IsSdkContract_ReturnsFalse_ForEmptyCode()
    {
        ContractRegistry.IsSdkContract([]).Should().BeFalse();
    }

    // ---- BuildManifest + ParseManifest roundtrip ----

    [Fact]
    public void BuildManifest_ParseManifest_Roundtrip_NoArgs()
    {
        var manifest = ContractRegistry.BuildManifest(0x0001, []);
        var (typeId, ctorArgs) = ContractRegistry.ParseManifest(manifest);

        typeId.Should().Be(0x0001);
        ctorArgs.Should().BeEmpty();
    }

    [Fact]
    public void BuildManifest_ParseManifest_Roundtrip_WithArgs()
    {
        var args = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
        var manifest = ContractRegistry.BuildManifest(0x0100, args);
        var (typeId, ctorArgs) = ContractRegistry.ParseManifest(manifest);

        typeId.Should().Be(0x0100);
        ctorArgs.Should().Equal(args);
    }

    [Fact]
    public void BuildManifest_StartsWithMagicBytes()
    {
        var manifest = ContractRegistry.BuildManifest(0x0001, []);

        manifest[0].Should().Be(0xBA);
        manifest[1].Should().Be(0x5A);
    }

    [Fact]
    public void BuildManifest_TypeId_BigEndian()
    {
        // Type 0x0102 should be stored as [0x01, 0x02]
        var manifest = ContractRegistry.BuildManifest(0x0102, []);

        manifest[2].Should().Be(0x01);
        manifest[3].Should().Be(0x02);
    }

    [Fact]
    public void BuildManifest_IsRecognizedAsSdkContract()
    {
        var manifest = ContractRegistry.BuildManifest(0x0001, []);
        ContractRegistry.IsSdkContract(manifest).Should().BeTrue();
    }

    [Fact]
    public void ParseManifest_Throws_ForTooShortCode()
    {
        var act = () => ContractRegistry.ParseManifest(new byte[] { 0xBA, 0x5A });
        act.Should().Throw<ArgumentException>();
    }

    // ---- BuildManifest roundtrip with BasaltWriter-encoded args ----

    [Fact]
    public void BuildManifest_ParseManifest_Roundtrip_WithWriterArgs()
    {
        // Simulate BST-20 constructor args: name, symbol, decimals
        var argsBuf = new byte[256];
        var writer = new BasaltWriter(argsBuf);
        writer.WriteString("TestToken");
        writer.WriteString("TST");
        writer.WriteByte(18);
        var ctorArgs = argsBuf[..writer.Position];

        var manifest = ContractRegistry.BuildManifest(0x0001, ctorArgs);
        var (typeId, parsedArgs) = ContractRegistry.ParseManifest(manifest);

        typeId.Should().Be(0x0001);
        parsedArgs.Should().Equal(ctorArgs);

        // Verify the args can be read back
        var reader = new BasaltReader(parsedArgs);
        reader.ReadString().Should().Be("TestToken");
        reader.ReadString().Should().Be("TST");
        reader.ReadByte().Should().Be(18);
    }

    // ---- CreateDefault ----

    [Fact]
    public void CreateDefault_RegistersAllUserDeployableTypes()
    {
        var registry = ContractRegistry.CreateDefault();

        registry.IsRegistered(0x0001).Should().BeTrue();  // BST20Token
        registry.IsRegistered(0x0002).Should().BeTrue();  // BST721Token
        registry.IsRegistered(0x0003).Should().BeTrue();  // BST1155Token
        registry.IsRegistered(0x0004).Should().BeTrue();  // BSTDIDRegistry
    }

    [Fact]
    public void CreateDefault_RegistersAllSystemContracts()
    {
        var registry = ContractRegistry.CreateDefault();

        registry.IsRegistered(0x0100).Should().BeTrue();  // WBSLT
        registry.IsRegistered(0x0101).Should().BeTrue();  // BasaltNameService
        registry.IsRegistered(0x0102).Should().BeTrue();  // SimpleGovernance
        registry.IsRegistered(0x0103).Should().BeTrue();  // Escrow
        registry.IsRegistered(0x0104).Should().BeTrue();  // StakingPool
    }

    // ---- GetName ----

    [Fact]
    public void GetName_ReturnsCorrectNames()
    {
        var registry = ContractRegistry.CreateDefault();

        registry.GetName(0x0001).Should().Be("BST20Token");
        registry.GetName(0x0002).Should().Be("BST721Token");
        registry.GetName(0x0003).Should().Be("BST1155Token");
        registry.GetName(0x0004).Should().Be("BSTDIDRegistry");
        registry.GetName(0x0100).Should().Be("WBSLT");
        registry.GetName(0x0101).Should().Be("BasaltNameService");
        registry.GetName(0x0102).Should().Be("SimpleGovernance");
        registry.GetName(0x0103).Should().Be("Escrow");
        registry.GetName(0x0104).Should().Be("StakingPool");
    }

    [Fact]
    public void GetName_ReturnsNull_ForUnregistered()
    {
        var registry = ContractRegistry.CreateDefault();
        registry.GetName(0xFFFF).Should().BeNull();
    }

    // ---- IsRegistered ----

    [Fact]
    public void IsRegistered_ReturnsFalse_ForUnregisteredId()
    {
        var registry = ContractRegistry.CreateDefault();
        registry.IsRegistered(0x9999).Should().BeFalse();
    }

    [Fact]
    public void IsRegistered_ReturnsTrue_AfterManualRegister()
    {
        var registry = new ContractRegistry();
        registry.IsRegistered(0xAAAA).Should().BeFalse();

        registry.Register(0xAAAA, "CustomContract", _ =>
            new Basalt.Sdk.Contracts.Standards.Escrow());

        registry.IsRegistered(0xAAAA).Should().BeTrue();
        registry.GetName(0xAAAA).Should().Be("CustomContract");
    }

    // ---- CreateInstance ----

    [Fact]
    public void CreateInstance_BST20_ReturnsDispatchable()
    {
        var registry = ContractRegistry.CreateDefault();

        // Build BST-20 constructor args
        var argsBuf = new byte[256];
        var writer = new BasaltWriter(argsBuf);
        writer.WriteString("MyToken");
        writer.WriteString("MTK");
        writer.WriteByte(8);
        var ctorArgs = argsBuf[..writer.Position];

        var instance = registry.CreateInstance(0x0001, ctorArgs);
        instance.Should().NotBeNull();
        instance.Should().BeAssignableTo<IDispatchable>();
    }

    [Fact]
    public void CreateInstance_WBSLT_ReturnsDispatchable()
    {
        var registry = ContractRegistry.CreateDefault();
        var instance = registry.CreateInstance(0x0100, []);
        instance.Should().NotBeNull();
        instance.Should().BeAssignableTo<IDispatchable>();
    }

    [Fact]
    public void CreateInstance_UnknownTypeId_Throws()
    {
        var registry = ContractRegistry.CreateDefault();

        var act = () => registry.CreateInstance(0xFFFF, []);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Unknown contract type ID*");
    }

    [Fact]
    public void CreateInstance_Escrow_ReturnsDispatchable()
    {
        var registry = ContractRegistry.CreateDefault();
        var instance = registry.CreateInstance(0x0103, []);
        instance.Should().NotBeNull();
        instance.Should().BeAssignableTo<IDispatchable>();
    }

    [Fact]
    public void CreateInstance_StakingPool_ReturnsDispatchable()
    {
        var registry = ContractRegistry.CreateDefault();
        var instance = registry.CreateInstance(0x0104, []);
        instance.Should().NotBeNull();
        instance.Should().BeAssignableTo<IDispatchable>();
    }

    // ---- Custom registry ----

    [Fact]
    public void EmptyRegistry_HasNoRegistrations()
    {
        var registry = new ContractRegistry();
        registry.IsRegistered(0x0001).Should().BeFalse();
        registry.IsRegistered(0x0100).Should().BeFalse();
    }

    [Fact]
    public void Register_OverwritesExisting()
    {
        var registry = new ContractRegistry();
        registry.Register(0x0001, "First", _ => new Basalt.Sdk.Contracts.Standards.Escrow());
        registry.GetName(0x0001).Should().Be("First");

        registry.Register(0x0001, "Second", _ => new Basalt.Sdk.Contracts.Standards.Escrow());
        registry.GetName(0x0001).Should().Be("Second");
    }
}
