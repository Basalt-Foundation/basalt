using System.Text;
using Basalt.Crypto;
using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests;

public class SchemaRegistryTests : IDisposable
{
    private readonly BasaltTestHost _host = new();
    private readonly SchemaRegistry _registry;
    private readonly byte[] _alice;
    private readonly byte[] _bob;

    public SchemaRegistryTests()
    {
        _registry = new SchemaRegistry();
        _alice = BasaltTestHost.CreateAddress(1);
        _bob = BasaltTestHost.CreateAddress(2);
    }

    /// <summary>
    /// M-10: Schema ID now includes creator address to prevent front-running.
    /// </summary>
    private static byte[] ComputeSchemaId(string name, byte[] creator)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[nameBytes.Length + creator.Length];
        nameBytes.CopyTo(input, 0);
        creator.CopyTo(input, nameBytes.Length);
        return Blake3Hasher.Hash(input).ToArray();
    }

    [Fact]
    public void RegisterSchema_StoresNameAndFields()
    {
        _host.SetCaller(_alice);
        var vk = new byte[] { 0xAA, 0xBB, 0xCC };
        var fields = "{\"age\":\"uint8\",\"country\":\"string\"}";

        var schemaIdHex = _host.Call(() => _registry.RegisterSchema("KYCBasic", fields, vk));

        schemaIdHex.Should().NotBeNullOrEmpty();

        var schemaId = ComputeSchemaId("KYCBasic", _alice);
        _host.Call(() => _registry.GetSchema(schemaId)).Should().Be("KYCBasic");
        _host.Call(() => _registry.GetFieldDefinitions(schemaId)).Should().Be(fields);
    }

    [Fact]
    public void RegisterSchema_DuplicateName_SameCaller_Fails()
    {
        _host.SetCaller(_alice);
        var vk = new byte[] { 0x01 };
        _host.Call(() => _registry.RegisterSchema("UniqueSchema", "{}", vk));

        // M-10: Schema ID includes caller, so same name + same caller = duplicate
        var msg = _host.ExpectRevert(() => _registry.RegisterSchema("UniqueSchema", "{}", vk));
        msg.Should().Contain("already exists");
    }

    [Fact]
    public void RegisterSchema_SameName_DifferentCaller_Succeeds()
    {
        _host.SetCaller(_alice);
        var vk = new byte[] { 0x01 };
        _host.Call(() => _registry.RegisterSchema("SharedName", "{}", vk));

        // M-10: Different caller produces different schema ID, so no collision
        _host.SetCaller(_bob);
        var id = _host.Call(() => _registry.RegisterSchema("SharedName", "{}", vk));
        id.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GetSchema_NonExistent_ReturnsEmpty()
    {
        var nonExistentId = new byte[32];
        nonExistentId[0] = 0xFF;

        _host.Call(() => _registry.GetSchema(nonExistentId)).Should().Be("");
    }

    [Fact]
    public void SchemaExists_ReturnsTrueForRegistered()
    {
        _host.SetCaller(_alice);
        var vk = new byte[] { 0x01 };
        _host.Call(() => _registry.RegisterSchema("ExistingSchema", "{}", vk));

        var schemaId = ComputeSchemaId("ExistingSchema", _alice);
        _host.Call(() => _registry.SchemaExists(schemaId)).Should().BeTrue();
    }

    [Fact]
    public void SchemaExists_ReturnsFalseForNonRegistered()
    {
        var nonExistentId = new byte[32];
        nonExistentId[0] = 0xDE;

        _host.Call(() => _registry.SchemaExists(nonExistentId)).Should().BeFalse();
    }

    [Fact]
    public void UpdateVerificationKey_ByCreator_Succeeds()
    {
        _host.SetCaller(_alice);
        var originalVk = new byte[] { 0x01, 0x02 };
        _host.Call(() => _registry.RegisterSchema("UpdatableSchema", "{}", originalVk));

        var schemaId = ComputeSchemaId("UpdatableSchema", _alice);
        var newVk = new byte[] { 0xDD, 0xEE, 0xFF };

        _host.Call(() => _registry.UpdateVerificationKey(schemaId, newVk));

        _host.Call(() => _registry.GetVerificationKey(schemaId))
            .Should().Be(Convert.ToHexString(newVk));
    }

    [Fact]
    public void UpdateVerificationKey_ByNonCreator_Fails()
    {
        _host.SetCaller(_alice);
        var vk = new byte[] { 0x01 };
        _host.Call(() => _registry.RegisterSchema("ProtectedSchema", "{}", vk));

        var schemaId = ComputeSchemaId("ProtectedSchema", _alice);

        _host.SetCaller(_bob);
        var msg = _host.ExpectRevert(() => _registry.UpdateVerificationKey(schemaId, new byte[] { 0xFF }));
        msg.Should().Contain("not creator");
    }

    [Fact]
    public void GetVerificationKey_ReturnsStoredKey()
    {
        _host.SetCaller(_alice);
        var vk = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE };
        _host.Call(() => _registry.RegisterSchema("VKSchema", "{}", vk));

        var schemaId = ComputeSchemaId("VKSchema", _alice);
        _host.Call(() => _registry.GetVerificationKey(schemaId))
            .Should().Be(Convert.ToHexString(vk));
    }

    [Fact]
    public void GetFieldDefinitions_ReturnsStoredFields()
    {
        _host.SetCaller(_alice);
        var fields = "{\"name\":\"string\",\"score\":\"uint32\"}";
        var vk = new byte[] { 0x01 };
        _host.Call(() => _registry.RegisterSchema("FieldsSchema", fields, vk));

        var schemaId = ComputeSchemaId("FieldsSchema", _alice);
        _host.Call(() => _registry.GetFieldDefinitions(schemaId)).Should().Be(fields);
    }

    [Fact]
    public void RegisterSchema_EmitsSchemaRegisteredEvent()
    {
        _host.SetCaller(_alice);
        _host.ClearEvents();
        var vk = new byte[] { 0x01 };
        _host.Call(() => _registry.RegisterSchema("EventSchema", "{}", vk));

        var events = _host.GetEvents<SchemaRegisteredEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].Name.Should().Be("EventSchema");
        events[0].Creator.Should().BeEquivalentTo(_alice);
        events[0].SchemaId.Should().BeEquivalentTo(ComputeSchemaId("EventSchema", _alice));
    }

    [Fact]
    public void UpdateVerificationKey_EmitsVerificationKeyUpdatedEvent()
    {
        _host.SetCaller(_alice);
        var vk = new byte[] { 0x01 };
        _host.Call(() => _registry.RegisterSchema("VKEventSchema", "{}", vk));

        var schemaId = ComputeSchemaId("VKEventSchema", _alice);
        _host.ClearEvents();

        var newVk = new byte[] { 0xAA, 0xBB };
        _host.Call(() => _registry.UpdateVerificationKey(schemaId, newVk));

        var events = _host.GetEvents<VerificationKeyUpdatedEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].SchemaId.Should().BeEquivalentTo(schemaId);
        events[0].UpdatedBy.Should().BeEquivalentTo(_alice);
    }

    [Fact]
    public void RegisterSchema_EmptyName_Fails()
    {
        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _registry.RegisterSchema("", "{}", new byte[] { 0x01 }));
        msg.Should().Contain("name required");
    }

    [Fact]
    public void GetVerificationKey_NonExistent_ReturnsEmpty()
    {
        var nonExistentId = new byte[32];
        nonExistentId[0] = 0xAB;

        _host.Call(() => _registry.GetVerificationKey(nonExistentId)).Should().Be("");
    }

    [Fact]
    public void GetFieldDefinitions_NonExistent_ReturnsEmpty()
    {
        var nonExistentId = new byte[32];
        nonExistentId[0] = 0xCD;

        _host.Call(() => _registry.GetFieldDefinitions(nonExistentId)).Should().Be("");
    }

    [Fact]
    public void UpdateVerificationKey_NonExistentSchema_Fails()
    {
        _host.SetCaller(_alice);
        var nonExistentId = new byte[32];
        nonExistentId[0] = 0xEF;

        var msg = _host.ExpectRevert(() => _registry.UpdateVerificationKey(nonExistentId, new byte[] { 0x01 }));
        msg.Should().Contain("not found");
    }

    [Fact]
    public void RegisterSchema_ReturnsCorrectSchemaIdHex()
    {
        _host.SetCaller(_alice);
        var vk = new byte[] { 0x01 };
        var schemaIdHex = _host.Call(() => _registry.RegisterSchema("IdCheckSchema", "{}", vk));

        var expectedId = ComputeSchemaId("IdCheckSchema", _alice);
        schemaIdHex.Should().Be(Convert.ToHexString(expectedId));
    }

    public void Dispose() => _host.Dispose();
}
