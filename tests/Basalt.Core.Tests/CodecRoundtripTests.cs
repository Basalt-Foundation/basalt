using Basalt.Codec;
using Basalt.Core;
using FluentAssertions;
using Xunit;

namespace Basalt.Core.Tests;

public class CodecRoundtripTests
{
    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(127UL)]
    [InlineData(128UL)]
    [InlineData(300UL)]
    [InlineData(16384UL)]
    [InlineData(ulong.MaxValue)]
    public void VarInt_Roundtrip(ulong value)
    {
        Span<byte> buffer = stackalloc byte[16];
        var writer = new BasaltWriter(buffer);
        writer.WriteVarInt(value);

        var reader = new BasaltReader(buffer[..writer.Position]);
        var result = reader.ReadVarInt();
        result.Should().Be(value, $"VarInt roundtrip failed for {value}");
    }

    [Fact]
    public void UInt64_Roundtrip()
    {
        Span<byte> buffer = stackalloc byte[8];
        var writer = new BasaltWriter(buffer);
        writer.WriteUInt64(0xDEADBEEFCAFEBABE);

        var reader = new BasaltReader(buffer);
        reader.ReadUInt64().Should().Be(0xDEADBEEFCAFEBABE);
    }

    [Fact]
    public void Hash256_Roundtrip()
    {
        var original = new byte[32];
        Random.Shared.NextBytes(original);
        var hash = new Hash256(original);

        Span<byte> buffer = stackalloc byte[32];
        var writer = new BasaltWriter(buffer);
        writer.WriteHash256(hash);

        var reader = new BasaltReader(buffer);
        var result = reader.ReadHash256();
        result.Should().Be(hash);
    }

    [Fact]
    public void Address_Roundtrip()
    {
        var original = new byte[20];
        Random.Shared.NextBytes(original);
        var addr = new Address(original);

        Span<byte> buffer = stackalloc byte[20];
        var writer = new BasaltWriter(buffer);
        writer.WriteAddress(addr);

        var reader = new BasaltReader(buffer);
        var result = reader.ReadAddress();
        result.Should().Be(addr);
    }

    [Fact]
    public void String_Roundtrip()
    {
        var value = "Hello, Basalt!";
        Span<byte> buffer = stackalloc byte[256];
        var writer = new BasaltWriter(buffer);
        writer.WriteString(value);

        var reader = new BasaltReader(buffer[..writer.Position]);
        reader.ReadString().Should().Be(value);
    }

    [Fact]
    public void Bytes_Roundtrip()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        Span<byte> buffer = stackalloc byte[64];
        var writer = new BasaltWriter(buffer);
        writer.WriteBytes(data);

        var reader = new BasaltReader(buffer[..writer.Position]);
        reader.ReadBytes().ToArray().Should().Equal(data);
    }

    [Fact]
    public void UInt256_Roundtrip()
    {
        var value = new UInt256(0xDEADBEEF, 0x12345678);

        Span<byte> buffer = stackalloc byte[32];
        var writer = new BasaltWriter(buffer);
        writer.WriteUInt256(value);

        var reader = new BasaltReader(buffer);
        var result = reader.ReadUInt256();
        result.Should().Be(value);
    }

    [Fact]
    public void ComplexMessage_Roundtrip()
    {
        Span<byte> buffer = stackalloc byte[512];
        var writer = new BasaltWriter(buffer);

        writer.WriteByte(42);
        writer.WriteUInt32(1000);
        writer.WriteUInt64(123456789);
        writer.WriteBool(true);
        writer.WriteString("test");

        var reader = new BasaltReader(buffer[..writer.Position]);
        reader.ReadByte().Should().Be(42);
        reader.ReadUInt32().Should().Be(1000u);
        reader.ReadUInt64().Should().Be(123456789ul);
        reader.ReadBool().Should().BeTrue();
        reader.ReadString().Should().Be("test");
    }

    [Fact]
    public void BufferOverflow_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
        {
            Span<byte> buffer = stackalloc byte[2];
            var writer = new BasaltWriter(buffer);
            writer.WriteUInt64(0);
        });
    }

    [Fact]
    public void ReadPastEnd_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
        {
            var reader = new BasaltReader(ReadOnlySpan<byte>.Empty);
            reader.ReadByte();
        });
    }
}
