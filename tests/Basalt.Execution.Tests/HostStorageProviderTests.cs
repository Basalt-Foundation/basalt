using Basalt.Codec;
using Basalt.Core;
using Basalt.Execution.VM;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests;

public class HostStorageProviderTests
{
    private readonly InMemoryStateDb _stateDb = new();

    private static readonly Address ContractAddr = new(new byte[]
    {
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10,
        11, 12, 13, 14, 15, 16, 17, 18, 19, 20
    });

    private (HostStorageProvider Provider, VmExecutionContext Context) CreateProvider(ulong gasLimit = 10_000_000)
    {
        var gasMeter = new GasMeter(gasLimit);
        var ctx = new VmExecutionContext
        {
            Caller = new Address(new byte[20]),
            ContractAddress = ContractAddr,
            Value = UInt256.Zero,
            BlockTimestamp = 100,
            BlockNumber = 1,
            BlockProposer = new Address(new byte[20]),
            ChainId = 4242,
            GasMeter = gasMeter,
            StateDb = _stateDb,
            CallDepth = 0,
        };
        var host = new HostInterface(ctx);
        var provider = new HostStorageProvider(host);
        return (provider, ctx);
    }

    // ---- ULong roundtrip ----

    [Fact]
    public void Set_Get_ULong_RoundTrips()
    {
        var (provider, _) = CreateProvider();
        provider.Set("counter", (ulong)42);
        provider.Get<ulong>("counter").Should().Be(42);
    }

    [Fact]
    public void Set_Get_ULong_MaxValue_RoundTrips()
    {
        var (provider, _) = CreateProvider();
        provider.Set("max_ulong", ulong.MaxValue);
        provider.Get<ulong>("max_ulong").Should().Be(ulong.MaxValue);
    }

    // ---- Long roundtrip ----

    [Fact]
    public void Set_Get_Long_Positive_RoundTrips()
    {
        var (provider, _) = CreateProvider();
        provider.Set("pos_long", (long)999_999);
        provider.Get<long>("pos_long").Should().Be(999_999);
    }

    [Fact]
    public void Set_Get_Long_Negative_RoundTrips()
    {
        var (provider, _) = CreateProvider();
        provider.Set("neg_long", (long)-123_456);
        provider.Get<long>("neg_long").Should().Be(-123_456);
    }

    [Fact]
    public void Set_Get_Long_MinValue_RoundTrips()
    {
        var (provider, _) = CreateProvider();
        provider.Set("min_long", long.MinValue);
        provider.Get<long>("min_long").Should().Be(long.MinValue);
    }

    // ---- Int roundtrip ----

    [Fact]
    public void Set_Get_Int_Positive_RoundTrips()
    {
        var (provider, _) = CreateProvider();
        provider.Set("pos_int", (int)12345);
        provider.Get<int>("pos_int").Should().Be(12345);
    }

    [Fact]
    public void Set_Get_Int_Negative_RoundTrips()
    {
        var (provider, _) = CreateProvider();
        provider.Set("neg_int", (int)-9999);
        provider.Get<int>("neg_int").Should().Be(-9999);
    }

    // ---- UInt roundtrip ----

    [Fact]
    public void Set_Get_UInt_RoundTrips()
    {
        var (provider, _) = CreateProvider();
        provider.Set("my_uint", (uint)3_000_000);
        provider.Get<uint>("my_uint").Should().Be(3_000_000);
    }

    // ---- UShort roundtrip ----

    [Fact]
    public void Set_Get_UShort_RoundTrips()
    {
        var (provider, _) = CreateProvider();
        provider.Set("my_ushort", (ushort)65535);
        provider.Get<ushort>("my_ushort").Should().Be(65535);
    }

    [Fact]
    public void Set_Get_UShort_Zero_RoundTrips()
    {
        var (provider, _) = CreateProvider();
        provider.Set("zero_ushort", (ushort)0);
        provider.Get<ushort>("zero_ushort").Should().Be(0);
    }

    // ---- Bool roundtrip ----

    [Fact]
    public void Set_Get_Bool_True_RoundTrips()
    {
        var (provider, _) = CreateProvider();
        provider.Set("flag", true);
        provider.Get<bool>("flag").Should().BeTrue();
    }

    [Fact]
    public void Set_Get_Bool_False_RoundTrips()
    {
        var (provider, _) = CreateProvider();
        provider.Set("flag", false);
        provider.Get<bool>("flag").Should().BeFalse();
    }

    // ---- Byte roundtrip ----

    [Fact]
    public void Set_Get_Byte_RoundTrips()
    {
        var (provider, _) = CreateProvider();
        provider.Set("my_byte", (byte)0xAB);
        provider.Get<byte>("my_byte").Should().Be(0xAB);
    }

    [Fact]
    public void Set_Get_Byte_Zero_RoundTrips()
    {
        var (provider, _) = CreateProvider();
        provider.Set("zero_byte", (byte)0);
        provider.Get<byte>("zero_byte").Should().Be(0);
    }

    // ---- String roundtrip ----

    [Fact]
    public void Set_Get_String_RoundTrips()
    {
        var (provider, _) = CreateProvider();
        provider.Set("name", "hello world");
        provider.Get<string>("name").Should().Be("hello world");
    }

    [Fact]
    public void Set_Get_String_Empty_RoundTrips()
    {
        var (provider, _) = CreateProvider();
        provider.Set("empty_str", "");
        provider.Get<string>("empty_str").Should().Be("");
    }

    [Fact]
    public void Set_Get_String_Unicode_RoundTrips()
    {
        var (provider, _) = CreateProvider();
        provider.Set("unicode", "Basalt blockchain");
        provider.Get<string>("unicode").Should().Be("Basalt blockchain");
    }

    // ---- byte[] roundtrip ----

    [Fact]
    public void Set_Get_ByteArray_RoundTrips()
    {
        var (provider, _) = CreateProvider();
        var data = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        provider.Set("data", data);
        provider.Get<byte[]>("data").Should().Equal(data);
    }

    [Fact]
    public void Set_Get_ByteArray_Empty_RoundTrips()
    {
        var (provider, _) = CreateProvider();
        provider.Set("empty_data", Array.Empty<byte>());
        provider.Get<byte[]>("empty_data").Should().BeEmpty();
    }

    [Fact]
    public void Set_Get_ByteArray_Large_RoundTrips()
    {
        var (provider, _) = CreateProvider();
        var data = new byte[256];
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)(i & 0xFF);
        provider.Set("large_data", data);
        provider.Get<byte[]>("large_data").Should().Equal(data);
    }

    // ---- ContainsKey ----

    [Fact]
    public void ContainsKey_ReturnsFalse_ForMissingKey()
    {
        var (provider, _) = CreateProvider();
        provider.ContainsKey("nonexistent").Should().BeFalse();
    }

    [Fact]
    public void ContainsKey_ReturnsTrue_AfterSet()
    {
        var (provider, _) = CreateProvider();
        provider.Set("exists", (ulong)1);
        provider.ContainsKey("exists").Should().BeTrue();
    }

    [Fact]
    public void ContainsKey_ReturnsFalse_AfterDelete()
    {
        var (provider, _) = CreateProvider();
        provider.Set("temp", (int)42);
        provider.ContainsKey("temp").Should().BeTrue();
        provider.Delete("temp");
        provider.ContainsKey("temp").Should().BeFalse();
    }

    // ---- Delete ----

    [Fact]
    public void Delete_RemovesKey()
    {
        var (provider, _) = CreateProvider();
        provider.Set("to_delete", "value");
        provider.Get<string>("to_delete").Should().Be("value");
        provider.Delete("to_delete");
        provider.ContainsKey("to_delete").Should().BeFalse();
    }

    [Fact]
    public void Delete_NonExistentKey_DoesNotThrow()
    {
        var (provider, _) = CreateProvider();
        var act = () => provider.Delete("never_existed");
        act.Should().NotThrow();
    }

    // ---- Get missing key returns default ----

    [Fact]
    public void Get_MissingKey_ReturnsDefault_ULong()
    {
        var (provider, _) = CreateProvider();
        provider.Get<ulong>("missing").Should().Be(0);
    }

    [Fact]
    public void Get_MissingKey_ReturnsDefault_String()
    {
        var (provider, _) = CreateProvider();
        provider.Get<string>("missing").Should().BeNull();
    }

    // ---- Overwrite existing key ----

    [Fact]
    public void Set_Overwrite_ReturnsNewValue()
    {
        var (provider, _) = CreateProvider();
        provider.Set("counter", (ulong)1);
        provider.Get<ulong>("counter").Should().Be(1);
        provider.Set("counter", (ulong)99);
        provider.Get<ulong>("counter").Should().Be(99);
    }

    // ---- Gas consumption ----

    [Fact]
    public void Operations_ConsumeGas()
    {
        var (provider, ctx) = CreateProvider();
        var gasBefore = ctx.GasMeter.GasUsed;
        provider.Set("key", (ulong)1);
        var gasAfterWrite = ctx.GasMeter.GasUsed;
        gasAfterWrite.Should().BeGreaterThan(gasBefore);

        provider.Get<ulong>("key");
        var gasAfterRead = ctx.GasMeter.GasUsed;
        gasAfterRead.Should().BeGreaterThan(gasAfterWrite);
    }

    // ---- Different keys are independent ----

    [Fact]
    public void DifferentKeys_AreIndependent()
    {
        var (provider, _) = CreateProvider();
        provider.Set("key_a", (int)100);
        provider.Set("key_b", (int)200);
        provider.Get<int>("key_a").Should().Be(100);
        provider.Get<int>("key_b").Should().Be(200);

        provider.Delete("key_a");
        provider.ContainsKey("key_a").Should().BeFalse();
        provider.Get<int>("key_b").Should().Be(200);
    }
}
