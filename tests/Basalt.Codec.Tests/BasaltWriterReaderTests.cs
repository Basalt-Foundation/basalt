using Basalt.Codec;
using Basalt.Core;
using FluentAssertions;
using Xunit;

namespace Basalt.Codec.Tests;

public class BasaltWriterReaderTests
{
    // -------------------------------------------------------
    // Primitive round-trips
    // -------------------------------------------------------

    [Fact]
    public void WriteReadByte_RoundTrip()
    {
        var buffer = new byte[1];
        var writer = new BasaltWriter(buffer);
        writer.WriteByte(0xAB);

        var reader = new BasaltReader(buffer);
        reader.ReadByte().Should().Be(0xAB);
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)127)]
    [InlineData((byte)255)]
    public void WriteReadByte_BoundaryValues(byte value)
    {
        var buffer = new byte[1];
        var writer = new BasaltWriter(buffer);
        writer.WriteByte(value);

        var reader = new BasaltReader(buffer);
        reader.ReadByte().Should().Be(value);
    }

    [Fact]
    public void WriteReadUInt16_RoundTrip()
    {
        var buffer = new byte[2];
        var writer = new BasaltWriter(buffer);
        writer.WriteUInt16(0xCAFE);

        var reader = new BasaltReader(buffer);
        reader.ReadUInt16().Should().Be(0xCAFE);
    }

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)1)]
    [InlineData(ushort.MaxValue)]
    [InlineData((ushort)0x1234)]
    public void WriteReadUInt16_BoundaryValues(ushort value)
    {
        var buffer = new byte[2];
        var writer = new BasaltWriter(buffer);
        writer.WriteUInt16(value);

        var reader = new BasaltReader(buffer);
        reader.ReadUInt16().Should().Be(value);
    }

    [Fact]
    public void WriteReadUInt32_RoundTrip()
    {
        var buffer = new byte[4];
        var writer = new BasaltWriter(buffer);
        writer.WriteUInt32(0xDEADBEEF);

        var reader = new BasaltReader(buffer);
        reader.ReadUInt32().Should().Be(0xDEADBEEF);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(uint.MaxValue)]
    [InlineData(0x12345678u)]
    public void WriteReadUInt32_BoundaryValues(uint value)
    {
        var buffer = new byte[4];
        var writer = new BasaltWriter(buffer);
        writer.WriteUInt32(value);

        var reader = new BasaltReader(buffer);
        reader.ReadUInt32().Should().Be(value);
    }

    [Fact]
    public void WriteReadInt32_RoundTrip()
    {
        var buffer = new byte[4];
        var writer = new BasaltWriter(buffer);
        writer.WriteInt32(-42);

        var reader = new BasaltReader(buffer);
        reader.ReadInt32().Should().Be(-42);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void WriteReadInt32_BoundaryValues(int value)
    {
        var buffer = new byte[4];
        var writer = new BasaltWriter(buffer);
        writer.WriteInt32(value);

        var reader = new BasaltReader(buffer);
        reader.ReadInt32().Should().Be(value);
    }

    [Fact]
    public void WriteReadUInt64_RoundTrip()
    {
        var buffer = new byte[8];
        var writer = new BasaltWriter(buffer);
        writer.WriteUInt64(0xDEADBEEFCAFEBABE);

        var reader = new BasaltReader(buffer);
        reader.ReadUInt64().Should().Be(0xDEADBEEFCAFEBABE);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(ulong.MaxValue)]
    public void WriteReadUInt64_BoundaryValues(ulong value)
    {
        var buffer = new byte[8];
        var writer = new BasaltWriter(buffer);
        writer.WriteUInt64(value);

        var reader = new BasaltReader(buffer);
        reader.ReadUInt64().Should().Be(value);
    }

    [Fact]
    public void WriteReadInt64_RoundTrip()
    {
        var buffer = new byte[8];
        var writer = new BasaltWriter(buffer);
        writer.WriteInt64(-123456789012345L);

        var reader = new BasaltReader(buffer);
        reader.ReadInt64().Should().Be(-123456789012345L);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void WriteReadInt64_BoundaryValues(long value)
    {
        var buffer = new byte[8];
        var writer = new BasaltWriter(buffer);
        writer.WriteInt64(value);

        var reader = new BasaltReader(buffer);
        reader.ReadInt64().Should().Be(value);
    }

    // -------------------------------------------------------
    // VarInt round-trips
    // -------------------------------------------------------

    [Fact]
    public void WriteReadVarInt_SmallValue()
    {
        var buffer = new byte[10];
        var writer = new BasaltWriter(buffer);
        writer.WriteVarInt(42);

        var reader = new BasaltReader(buffer.AsSpan(0, writer.Position).ToArray());
        reader.ReadVarInt().Should().Be(42UL);
    }

    [Fact]
    public void WriteReadVarInt_MediumValue()
    {
        var buffer = new byte[10];
        var writer = new BasaltWriter(buffer);
        writer.WriteVarInt(300);

        var reader = new BasaltReader(buffer.AsSpan(0, writer.Position).ToArray());
        reader.ReadVarInt().Should().Be(300UL);
    }

    [Fact]
    public void WriteReadVarInt_LargeValue()
    {
        ulong largeValue = ulong.MaxValue >> 1;
        var buffer = new byte[10];
        var writer = new BasaltWriter(buffer);
        writer.WriteVarInt(largeValue);

        var reader = new BasaltReader(buffer.AsSpan(0, writer.Position).ToArray());
        reader.ReadVarInt().Should().Be(largeValue);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(127UL)]
    [InlineData(128UL)]
    [InlineData(16383UL)]
    [InlineData(16384UL)]
    [InlineData(ulong.MaxValue)]
    public void WriteReadVarInt_BoundaryValues(ulong value)
    {
        var buffer = new byte[10];
        var writer = new BasaltWriter(buffer);
        writer.WriteVarInt(value);

        var reader = new BasaltReader(buffer.AsSpan(0, writer.Position).ToArray());
        reader.ReadVarInt().Should().Be(value);
    }

    [Fact]
    public void WriteVarInt_SmallValue_Uses1Byte()
    {
        var buffer = new byte[10];
        var writer = new BasaltWriter(buffer);
        writer.WriteVarInt(42);
        writer.Position.Should().Be(1);
    }

    [Fact]
    public void WriteVarInt_MediumValue_Uses2Bytes()
    {
        var buffer = new byte[10];
        var writer = new BasaltWriter(buffer);
        writer.WriteVarInt(300);
        writer.Position.Should().Be(2);
    }

    [Fact]
    public void WriteVarInt_MaxValue_Uses10Bytes()
    {
        var buffer = new byte[10];
        var writer = new BasaltWriter(buffer);
        writer.WriteVarInt(ulong.MaxValue);
        writer.Position.Should().Be(10);
    }

    // -------------------------------------------------------
    // String round-trips
    // -------------------------------------------------------

    [Fact]
    public void WriteReadString_Empty()
    {
        var buffer = new byte[16];
        var writer = new BasaltWriter(buffer);
        writer.WriteString("");

        var reader = new BasaltReader(buffer.AsSpan(0, writer.Position).ToArray());
        reader.ReadString().Should().Be("");
    }

    [Fact]
    public void WriteReadString_Ascii()
    {
        var buffer = new byte[128];
        var writer = new BasaltWriter(buffer);
        writer.WriteString("Hello, Basalt!");

        var reader = new BasaltReader(buffer.AsSpan(0, writer.Position).ToArray());
        reader.ReadString().Should().Be("Hello, Basalt!");
    }

    [Fact]
    public void WriteReadString_Unicode()
    {
        var buffer = new byte[256];
        var writer = new BasaltWriter(buffer);
        var unicodeString = "Unicode: \u00e9\u00e8\u00ea \u4e16\u754c \ud83d\ude80";
        writer.WriteString(unicodeString);

        var reader = new BasaltReader(buffer.AsSpan(0, writer.Position).ToArray());
        reader.ReadString().Should().Be(unicodeString);
    }

    [Fact]
    public void WriteReadString_LongString()
    {
        var longString = new string('A', 1000);
        var buffer = new byte[1100];
        var writer = new BasaltWriter(buffer);
        writer.WriteString(longString);

        var reader = new BasaltReader(buffer.AsSpan(0, writer.Position).ToArray());
        reader.ReadString().Should().Be(longString);
    }

    // -------------------------------------------------------
    // Bytes round-trips
    // -------------------------------------------------------

    [Fact]
    public void WriteReadBytes_EmptyArray()
    {
        var buffer = new byte[16];
        var writer = new BasaltWriter(buffer);
        writer.WriteBytes(ReadOnlySpan<byte>.Empty);

        var reader = new BasaltReader(buffer.AsSpan(0, writer.Position).ToArray());
        reader.ReadBytes().ToArray().Should().BeEmpty();
    }

    [Fact]
    public void WriteReadBytes_SmallArray()
    {
        var data = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
        var buffer = new byte[32];
        var writer = new BasaltWriter(buffer);
        writer.WriteBytes(data);

        var reader = new BasaltReader(buffer.AsSpan(0, writer.Position).ToArray());
        reader.ReadBytes().ToArray().Should().Equal(data);
    }

    [Fact]
    public void WriteReadBytes_LargeArray()
    {
        var data = new byte[500];
        Random.Shared.NextBytes(data);
        var buffer = new byte[600];
        var writer = new BasaltWriter(buffer);
        writer.WriteBytes(data);

        var reader = new BasaltReader(buffer.AsSpan(0, writer.Position).ToArray());
        reader.ReadBytes().ToArray().Should().Equal(data);
    }

    [Fact]
    public void WriteReadRawBytes_RoundTrip()
    {
        var data = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
        var buffer = new byte[4];
        var writer = new BasaltWriter(buffer);
        writer.WriteRawBytes(data);

        var reader = new BasaltReader(buffer);
        reader.ReadRawBytes(4).ToArray().Should().Equal(data);
    }

    // -------------------------------------------------------
    // Bool round-trips
    // -------------------------------------------------------

    [Fact]
    public void WriteReadBool_True()
    {
        var buffer = new byte[1];
        var writer = new BasaltWriter(buffer);
        writer.WriteBool(true);

        var reader = new BasaltReader(buffer);
        reader.ReadBool().Should().BeTrue();
    }

    [Fact]
    public void WriteReadBool_False()
    {
        var buffer = new byte[1];
        var writer = new BasaltWriter(buffer);
        writer.WriteBool(false);

        var reader = new BasaltReader(buffer);
        reader.ReadBool().Should().BeFalse();
    }

    // -------------------------------------------------------
    // Basalt Core types round-trips
    // -------------------------------------------------------

    [Fact]
    public void WriteReadHash256_RoundTrip()
    {
        var hashBytes = new byte[Hash256.Size];
        Random.Shared.NextBytes(hashBytes);
        var hash = new Hash256(hashBytes);

        var buffer = new byte[Hash256.Size];
        var writer = new BasaltWriter(buffer);
        writer.WriteHash256(hash);

        var reader = new BasaltReader(buffer);
        var result = reader.ReadHash256();
        result.Should().Be(hash);
    }

    [Fact]
    public void WriteReadHash256_Zero()
    {
        var buffer = new byte[Hash256.Size];
        var writer = new BasaltWriter(buffer);
        writer.WriteHash256(Hash256.Zero);

        var reader = new BasaltReader(buffer);
        var result = reader.ReadHash256();
        result.Should().Be(Hash256.Zero);
        result.IsZero.Should().BeTrue();
    }

    [Fact]
    public void WriteReadAddress_RoundTrip()
    {
        var addrBytes = new byte[Address.Size];
        Random.Shared.NextBytes(addrBytes);
        var address = new Address(addrBytes);

        var buffer = new byte[Address.Size];
        var writer = new BasaltWriter(buffer);
        writer.WriteAddress(address);

        var reader = new BasaltReader(buffer);
        var result = reader.ReadAddress();
        result.Should().Be(address);
    }

    [Fact]
    public void WriteReadAddress_Zero()
    {
        var buffer = new byte[Address.Size];
        var writer = new BasaltWriter(buffer);
        writer.WriteAddress(Address.Zero);

        var reader = new BasaltReader(buffer);
        var result = reader.ReadAddress();
        result.Should().Be(Address.Zero);
        result.IsZero.Should().BeTrue();
    }

    [Fact]
    public void WriteReadUInt256_RoundTrip()
    {
        var valueBytes = new byte[32];
        Random.Shared.NextBytes(valueBytes);
        var value = new UInt256(valueBytes);

        var buffer = new byte[32];
        var writer = new BasaltWriter(buffer);
        writer.WriteUInt256(value);

        var reader = new BasaltReader(buffer);
        var result = reader.ReadUInt256();
        result.Should().Be(value);
    }

    [Fact]
    public void WriteReadUInt256_Zero()
    {
        var buffer = new byte[32];
        var writer = new BasaltWriter(buffer);
        writer.WriteUInt256(UInt256.Zero);

        var reader = new BasaltReader(buffer);
        var result = reader.ReadUInt256();
        result.Should().Be(UInt256.Zero);
        result.IsZero.Should().BeTrue();
    }

    [Fact]
    public void WriteReadUInt256_One()
    {
        var buffer = new byte[32];
        var writer = new BasaltWriter(buffer);
        writer.WriteUInt256(UInt256.One);

        var reader = new BasaltReader(buffer);
        var result = reader.ReadUInt256();
        result.Should().Be(UInt256.One);
    }

    [Fact]
    public void WriteReadSignature_RoundTrip()
    {
        var sigBytes = new byte[Signature.Size];
        Random.Shared.NextBytes(sigBytes);
        var sig = new Signature(sigBytes);

        var buffer = new byte[Signature.Size];
        var writer = new BasaltWriter(buffer);
        writer.WriteSignature(sig);

        var reader = new BasaltReader(buffer);
        var result = reader.ReadSignature();
        result.Should().Be(sig);
    }

    [Fact]
    public void WriteReadSignature_Empty()
    {
        var buffer = new byte[Signature.Size];
        var writer = new BasaltWriter(buffer);
        writer.WriteSignature(Signature.Empty);

        var reader = new BasaltReader(buffer);
        var result = reader.ReadSignature();
        result.Should().Be(Signature.Empty);
        result.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void WriteReadPublicKey_RoundTrip()
    {
        var keyBytes = new byte[PublicKey.Size];
        Random.Shared.NextBytes(keyBytes);
        var key = new PublicKey(keyBytes);

        var buffer = new byte[PublicKey.Size];
        var writer = new BasaltWriter(buffer);
        writer.WritePublicKey(key);

        var reader = new BasaltReader(buffer);
        var result = reader.ReadPublicKey();
        result.Should().Be(key);
    }

    [Fact]
    public void WriteReadPublicKey_Empty()
    {
        var buffer = new byte[PublicKey.Size];
        var writer = new BasaltWriter(buffer);
        writer.WritePublicKey(PublicKey.Empty);

        var reader = new BasaltReader(buffer);
        var result = reader.ReadPublicKey();
        result.Should().Be(PublicKey.Empty);
        result.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void WriteReadBlsSignature_RoundTrip()
    {
        var sigBytes = new byte[BlsSignature.Size];
        Random.Shared.NextBytes(sigBytes);
        var sig = new BlsSignature(sigBytes);

        var buffer = new byte[BlsSignature.Size];
        var writer = new BasaltWriter(buffer);
        writer.WriteBlsSignature(sig);

        var reader = new BasaltReader(buffer);
        var result = reader.ReadBlsSignature();
        result.Should().Be(sig);
    }

    [Fact]
    public void WriteReadBlsSignature_Empty()
    {
        var buffer = new byte[BlsSignature.Size];
        var writer = new BasaltWriter(buffer);
        writer.WriteBlsSignature(BlsSignature.Empty);

        var reader = new BasaltReader(buffer);
        var result = reader.ReadBlsSignature();
        result.Should().Be(BlsSignature.Empty);
        result.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void WriteReadBlsPublicKey_RoundTrip()
    {
        var keyBytes = new byte[BlsPublicKey.Size];
        Random.Shared.NextBytes(keyBytes);
        var key = new BlsPublicKey(keyBytes);

        var buffer = new byte[BlsPublicKey.Size];
        var writer = new BasaltWriter(buffer);
        writer.WriteBlsPublicKey(key);

        var reader = new BasaltReader(buffer);
        var result = reader.ReadBlsPublicKey();
        result.Should().Be(key);
    }

    [Fact]
    public void WriteReadBlsPublicKey_Empty()
    {
        var buffer = new byte[BlsPublicKey.Size];
        var writer = new BasaltWriter(buffer);
        writer.WriteBlsPublicKey(BlsPublicKey.Empty);

        var reader = new BasaltReader(buffer);
        var result = reader.ReadBlsPublicKey();
        result.Should().Be(BlsPublicKey.Empty);
        result.IsEmpty.Should().BeTrue();
    }

    // -------------------------------------------------------
    // Position tracking
    // -------------------------------------------------------

    [Fact]
    public void Writer_Position_AdvancesCorrectly()
    {
        var buffer = new byte[256];
        var writer = new BasaltWriter(buffer);

        writer.Position.Should().Be(0);

        writer.WriteByte(0x01);
        writer.Position.Should().Be(1);

        writer.WriteUInt16(0x0203);
        writer.Position.Should().Be(3);

        writer.WriteUInt32(0x04050607);
        writer.Position.Should().Be(7);

        writer.WriteInt32(-1);
        writer.Position.Should().Be(11);

        writer.WriteUInt64(0x08090A0B0C0D0E0F);
        writer.Position.Should().Be(19);

        writer.WriteInt64(-100);
        writer.Position.Should().Be(27);

        writer.WriteBool(true);
        writer.Position.Should().Be(28);
    }

    [Fact]
    public void Writer_Remaining_DecreasesCorrectly()
    {
        var buffer = new byte[16];
        var writer = new BasaltWriter(buffer);

        writer.Remaining.Should().Be(16);

        writer.WriteByte(0x01);
        writer.Remaining.Should().Be(15);

        writer.WriteUInt32(0xDEADBEEF);
        writer.Remaining.Should().Be(11);

        writer.WriteUInt64(0xCAFEBABE);
        writer.Remaining.Should().Be(3);
    }

    [Fact]
    public void Writer_WrittenSpan_MatchesWrittenBytes()
    {
        var buffer = new byte[16];
        var writer = new BasaltWriter(buffer);

        writer.WriteByte(0xAA);
        writer.WriteByte(0xBB);
        writer.WriteByte(0xCC);

        writer.WrittenSpan.Length.Should().Be(3);
        writer.WrittenSpan.ToArray().Should().Equal(0xAA, 0xBB, 0xCC);
    }

    [Fact]
    public void Reader_Position_AdvancesCorrectly()
    {
        var buffer = new byte[256];
        var writer = new BasaltWriter(buffer);
        writer.WriteByte(0x01);
        writer.WriteUInt16(0x0203);
        writer.WriteUInt32(0x04050607);
        writer.WriteInt32(-1);
        writer.WriteUInt64(0x08090A0B0C0D0E0F);
        writer.WriteInt64(-100);
        writer.WriteBool(true);

        var reader = new BasaltReader(buffer.AsSpan(0, writer.Position).ToArray());

        reader.Position.Should().Be(0);

        reader.ReadByte();
        reader.Position.Should().Be(1);

        reader.ReadUInt16();
        reader.Position.Should().Be(3);

        reader.ReadUInt32();
        reader.Position.Should().Be(7);

        reader.ReadInt32();
        reader.Position.Should().Be(11);

        reader.ReadUInt64();
        reader.Position.Should().Be(19);

        reader.ReadInt64();
        reader.Position.Should().Be(27);

        reader.ReadBool();
        reader.Position.Should().Be(28);
    }

    [Fact]
    public void Reader_Remaining_DecreasesCorrectly()
    {
        var buffer = new byte[8];
        var writer = new BasaltWriter(buffer);
        writer.WriteUInt64(0xDEADBEEFCAFEBABE);

        var reader = new BasaltReader(buffer);
        reader.Remaining.Should().Be(8);

        reader.ReadUInt32();
        reader.Remaining.Should().Be(4);

        reader.ReadUInt32();
        reader.Remaining.Should().Be(0);
    }

    [Fact]
    public void Reader_IsAtEnd_TrueWhenFullyConsumed()
    {
        var buffer = new byte[4];
        var writer = new BasaltWriter(buffer);
        writer.WriteUInt32(123);

        var reader = new BasaltReader(buffer);
        reader.IsAtEnd.Should().BeFalse();

        reader.ReadUInt32();
        reader.IsAtEnd.Should().BeTrue();
    }

    // -------------------------------------------------------
    // Buffer overflow / underflow
    // -------------------------------------------------------

    [Fact]
    public void Writer_EnsureCapacity_ThrowsOnTooSmallBuffer_Byte()
    {
        var buffer = Array.Empty<byte>();
        var writer = new BasaltWriter(buffer);
        InvalidOperationException? caught = null;
        try { writer.WriteByte(0x01); }
        catch (InvalidOperationException ex) { caught = ex; }
        caught.Should().NotBeNull();
        caught!.Message.Should().Contain("too small");
    }

    [Fact]
    public void Writer_EnsureCapacity_ThrowsOnTooSmallBuffer_UInt16()
    {
        var buffer = new byte[1];
        var writer = new BasaltWriter(buffer);
        InvalidOperationException? caught = null;
        try { writer.WriteUInt16(0x0102); }
        catch (InvalidOperationException ex) { caught = ex; }
        caught.Should().NotBeNull();
        caught!.Message.Should().Contain("too small");
    }

    [Fact]
    public void Writer_EnsureCapacity_ThrowsOnTooSmallBuffer_UInt32()
    {
        var buffer = new byte[3];
        var writer = new BasaltWriter(buffer);
        InvalidOperationException? caught = null;
        try { writer.WriteUInt32(0x01020304); }
        catch (InvalidOperationException ex) { caught = ex; }
        caught.Should().NotBeNull();
        caught!.Message.Should().Contain("too small");
    }

    [Fact]
    public void Writer_EnsureCapacity_ThrowsOnTooSmallBuffer_UInt64()
    {
        var buffer = new byte[7];
        var writer = new BasaltWriter(buffer);
        InvalidOperationException? caught = null;
        try { writer.WriteUInt64(0x0102030405060708); }
        catch (InvalidOperationException ex) { caught = ex; }
        caught.Should().NotBeNull();
        caught!.Message.Should().Contain("too small");
    }

    [Fact]
    public void Writer_EnsureCapacity_ThrowsWhenBufferExhausted()
    {
        var buffer = new byte[4];
        var writer = new BasaltWriter(buffer);
        writer.WriteUInt32(0xDEADBEEF); // fills the buffer
        InvalidOperationException? caught = null;
        try { writer.WriteByte(0x01); }
        catch (InvalidOperationException ex) { caught = ex; }
        caught.Should().NotBeNull();
        caught!.Message.Should().Contain("too small");
    }

    [Fact]
    public void Writer_EnsureCapacity_ThrowsOnTooSmallBuffer_Hash256()
    {
        var buffer = new byte[Hash256.Size - 1];
        var writer = new BasaltWriter(buffer);
        InvalidOperationException? caught = null;
        try { writer.WriteHash256(Hash256.Zero); }
        catch (InvalidOperationException ex) { caught = ex; }
        caught.Should().NotBeNull();
        caught!.Message.Should().Contain("too small");
    }

    [Fact]
    public void Writer_EnsureCapacity_ThrowsOnTooSmallBuffer_Address()
    {
        var buffer = new byte[Address.Size - 1];
        var writer = new BasaltWriter(buffer);
        InvalidOperationException? caught = null;
        try { writer.WriteAddress(Address.Zero); }
        catch (InvalidOperationException ex) { caught = ex; }
        caught.Should().NotBeNull();
        caught!.Message.Should().Contain("too small");
    }

    [Fact]
    public void Reader_ThrowsOnUnexpectedEndOfBuffer_Byte()
    {
        var buffer = Array.Empty<byte>();
        var reader = new BasaltReader(buffer);
        InvalidOperationException? caught = null;
        try { reader.ReadByte(); }
        catch (InvalidOperationException ex) { caught = ex; }
        caught.Should().NotBeNull();
        caught!.Message.Should().Contain("Unexpected end");
    }

    [Fact]
    public void Reader_ThrowsOnUnexpectedEndOfBuffer_UInt32()
    {
        var buffer = new byte[3];
        var reader = new BasaltReader(buffer);
        InvalidOperationException? caught = null;
        try { reader.ReadUInt32(); }
        catch (InvalidOperationException ex) { caught = ex; }
        caught.Should().NotBeNull();
        caught!.Message.Should().Contain("Unexpected end");
    }

    [Fact]
    public void Reader_ThrowsOnUnexpectedEndOfBuffer_UInt64()
    {
        var buffer = new byte[7];
        var reader = new BasaltReader(buffer);
        InvalidOperationException? caught = null;
        try { reader.ReadUInt64(); }
        catch (InvalidOperationException ex) { caught = ex; }
        caught.Should().NotBeNull();
        caught!.Message.Should().Contain("Unexpected end");
    }

    [Fact]
    public void Reader_ThrowsOnUnexpectedEndOfBuffer_Hash256()
    {
        var buffer = new byte[Hash256.Size - 1];
        var reader = new BasaltReader(buffer);
        InvalidOperationException? caught = null;
        try { reader.ReadHash256(); }
        catch (InvalidOperationException ex) { caught = ex; }
        caught.Should().NotBeNull();
        caught!.Message.Should().Contain("Unexpected end");
    }

    // -------------------------------------------------------
    // Multiple writes then reads
    // -------------------------------------------------------

    [Fact]
    public void MultipleWritesThenReads_AllTypesSequentially()
    {
        var hashBytes = new byte[Hash256.Size];
        for (int i = 0; i < Hash256.Size; i++) hashBytes[i] = (byte)(i + 1);
        var hash = new Hash256(hashBytes);

        var addrBytes = new byte[Address.Size];
        for (int i = 0; i < Address.Size; i++) addrBytes[i] = (byte)(i + 0x10);
        var address = new Address(addrBytes);

        var buffer = new byte[1024];
        var writer = new BasaltWriter(buffer);

        writer.WriteByte(0xFF);
        writer.WriteUInt16(12345);
        writer.WriteUInt32(0xABCD1234);
        writer.WriteInt32(-99999);
        writer.WriteUInt64(0x1122334455667788);
        writer.WriteInt64(long.MinValue);
        writer.WriteVarInt(42);
        writer.WriteVarInt(300);
        writer.WriteVarInt(ulong.MaxValue >> 1);
        writer.WriteString("Hello, Basalt!");
        writer.WriteString("");
        writer.WriteBytes(new byte[] { 0x01, 0x02, 0x03 });
        writer.WriteBool(true);
        writer.WriteBool(false);
        writer.WriteHash256(hash);
        writer.WriteAddress(address);

        var writtenData = buffer.AsSpan(0, writer.Position).ToArray();
        var reader = new BasaltReader(writtenData);

        reader.ReadByte().Should().Be(0xFF);
        reader.ReadUInt16().Should().Be(12345);
        reader.ReadUInt32().Should().Be(0xABCD1234);
        reader.ReadInt32().Should().Be(-99999);
        reader.ReadUInt64().Should().Be(0x1122334455667788);
        reader.ReadInt64().Should().Be(long.MinValue);
        reader.ReadVarInt().Should().Be(42UL);
        reader.ReadVarInt().Should().Be(300UL);
        reader.ReadVarInt().Should().Be(ulong.MaxValue >> 1);
        reader.ReadString().Should().Be("Hello, Basalt!");
        reader.ReadString().Should().Be("");
        reader.ReadBytes().ToArray().Should().Equal(0x01, 0x02, 0x03);
        reader.ReadBool().Should().BeTrue();
        reader.ReadBool().Should().BeFalse();
        reader.ReadHash256().Should().Be(hash);
        reader.ReadAddress().Should().Be(address);
        reader.IsAtEnd.Should().BeTrue();
    }

    [Fact]
    public void MultipleWritesThenReads_MixedPrimitivesAndStrings()
    {
        var buffer = new byte[512];
        var writer = new BasaltWriter(buffer);

        writer.WriteString("first");
        writer.WriteUInt32(100);
        writer.WriteString("second");
        writer.WriteInt64(-200);
        writer.WriteString("third");
        writer.WriteBool(true);

        var writtenData = buffer.AsSpan(0, writer.Position).ToArray();
        var reader = new BasaltReader(writtenData);

        reader.ReadString().Should().Be("first");
        reader.ReadUInt32().Should().Be(100u);
        reader.ReadString().Should().Be("second");
        reader.ReadInt64().Should().Be(-200L);
        reader.ReadString().Should().Be("third");
        reader.ReadBool().Should().BeTrue();
        reader.IsAtEnd.Should().BeTrue();
    }

    [Fact]
    public void MultipleWritesThenReads_AllCryptoTypes()
    {
        var sigBytes = new byte[Signature.Size];
        Random.Shared.NextBytes(sigBytes);
        var sig = new Signature(sigBytes);

        var pubKeyBytes = new byte[PublicKey.Size];
        Random.Shared.NextBytes(pubKeyBytes);
        var pubKey = new PublicKey(pubKeyBytes);

        var blsSigBytes = new byte[BlsSignature.Size];
        Random.Shared.NextBytes(blsSigBytes);
        var blsSig = new BlsSignature(blsSigBytes);

        var blsPubKeyBytes = new byte[BlsPublicKey.Size];
        Random.Shared.NextBytes(blsPubKeyBytes);
        var blsPubKey = new BlsPublicKey(blsPubKeyBytes);

        var totalSize = Signature.Size + PublicKey.Size + BlsSignature.Size + BlsPublicKey.Size;
        var buffer = new byte[totalSize];
        var writer = new BasaltWriter(buffer);

        writer.WriteSignature(sig);
        writer.WritePublicKey(pubKey);
        writer.WriteBlsSignature(blsSig);
        writer.WriteBlsPublicKey(blsPubKey);

        writer.Position.Should().Be(totalSize);

        var reader = new BasaltReader(buffer);
        reader.ReadSignature().Should().Be(sig);
        reader.ReadPublicKey().Should().Be(pubKey);
        reader.ReadBlsSignature().Should().Be(blsSig);
        reader.ReadBlsPublicKey().Should().Be(blsPubKey);
        reader.IsAtEnd.Should().BeTrue();
    }

    // -------------------------------------------------------
    // VarInt encoding specifics
    // -------------------------------------------------------

    [Fact]
    public void VarInt_Zero_EncodesAsSingleByte()
    {
        var buffer = new byte[1];
        var writer = new BasaltWriter(buffer);
        writer.WriteVarInt(0);

        writer.Position.Should().Be(1);
        buffer[0].Should().Be(0x00);
    }

    [Fact]
    public void VarInt_127_EncodesAsSingleByte()
    {
        var buffer = new byte[1];
        var writer = new BasaltWriter(buffer);
        writer.WriteVarInt(127);

        writer.Position.Should().Be(1);
        buffer[0].Should().Be(0x7F);
    }

    [Fact]
    public void VarInt_128_EncodesAsTwoBytes()
    {
        var buffer = new byte[2];
        var writer = new BasaltWriter(buffer);
        writer.WriteVarInt(128);

        writer.Position.Should().Be(2);
        buffer[0].Should().Be(0x80); // low 7 bits = 0, continuation bit set
        buffer[1].Should().Be(0x01); // high bits = 1
    }

    // -------------------------------------------------------
    // Edge cases
    // -------------------------------------------------------

    [Fact]
    public void WriteReadRawBytes_Empty()
    {
        var buffer = new byte[0];
        var writer = new BasaltWriter(buffer);
        writer.WriteRawBytes(ReadOnlySpan<byte>.Empty);
        writer.Position.Should().Be(0);

        var reader = new BasaltReader(buffer);
        reader.ReadRawBytes(0).ToArray().Should().BeEmpty();
    }

    [Fact]
    public void Writer_ExactFit_DoesNotThrow()
    {
        var buffer = new byte[4];
        var writer = new BasaltWriter(buffer);
        // Should not throw when buffer is exactly the right size
        writer.WriteUInt32(0x12345678);
        writer.Position.Should().Be(4);
        writer.Remaining.Should().Be(0);
    }

    [Fact]
    public void Reader_ReadBytesWithVarIntLength_CorrectlyAdvancesPosition()
    {
        var data = new byte[] { 0xAA, 0xBB, 0xCC };
        var buffer = new byte[32];
        var writer = new BasaltWriter(buffer);
        writer.WriteBytes(data);
        var varIntSize = writer.Position - data.Length; // 1 byte for length 3

        var reader = new BasaltReader(buffer.AsSpan(0, writer.Position).ToArray());
        var readData = reader.ReadBytes();
        readData.ToArray().Should().Equal(data);
        reader.Position.Should().Be(varIntSize + data.Length);
    }

    [Fact]
    public void UInt256_WriteThenRead_PreservesLargeValue()
    {
        var value = new UInt256(UInt128.MaxValue, 42);

        var buffer = new byte[32];
        var writer = new BasaltWriter(buffer);
        writer.WriteUInt256(value);

        var reader = new BasaltReader(buffer);
        var result = reader.ReadUInt256();
        result.Lo.Should().Be(UInt128.MaxValue);
        result.Hi.Should().Be((UInt128)42);
    }

    [Fact]
    public void WriteString_PositionIncludesLengthPrefix()
    {
        var buffer = new byte[64];
        var writer = new BasaltWriter(buffer);
        writer.WriteString("ABC"); // 3 ASCII bytes, VarInt length = 1 byte

        writer.Position.Should().Be(4); // 1 (varint) + 3 (UTF-8 bytes)
    }

    [Fact]
    public void WriteBytes_PositionIncludesLengthPrefix()
    {
        var buffer = new byte[64];
        var writer = new BasaltWriter(buffer);
        writer.WriteBytes(new byte[] { 0x01, 0x02 }); // 2 bytes, VarInt length = 1 byte

        writer.Position.Should().Be(3); // 1 (varint) + 2 (data)
    }

    // -------------------------------------------------------
    // BasaltSerializer round-trip via IBasaltSerializable
    // -------------------------------------------------------

    [Fact]
    public void BasaltSerializer_SerializeDeserialize_WithTestType()
    {
        // Test that the BasaltSerializer utility methods work correctly
        // by manually implementing a simple serializable type
        var value = new TestSerializable(42, "hello");
        var serialized = BasaltSerializer.Serialize(value);
        var deserialized = BasaltSerializer.Deserialize<TestSerializable>(serialized);

        deserialized.Value.Should().Be(42);
        deserialized.Name.Should().Be("hello");
    }

    /// <summary>
    /// A simple test type implementing IBasaltSerializable for testing BasaltSerializer.
    /// </summary>
    private readonly struct TestSerializable : IBasaltSerializable<TestSerializable>
    {
        public readonly int Value;
        public readonly string Name;

        public TestSerializable(int value, string name)
        {
            Value = value;
            Name = name;
        }

        public void WriteTo(ref BasaltWriter writer)
        {
            writer.WriteInt32(Value);
            writer.WriteString(Name);
        }

        public static TestSerializable ReadFrom(ref BasaltReader reader)
        {
            var value = reader.ReadInt32();
            var name = reader.ReadString();
            return new TestSerializable(value, name);
        }

        public int GetSerializedSize()
        {
            var nameByteCount = System.Text.Encoding.UTF8.GetByteCount(Name);
            // 4 bytes for Int32 + VarInt length of name + name bytes
            return 4 + VarIntSize((ulong)nameByteCount) + nameByteCount;
        }

        private static int VarIntSize(ulong value)
        {
            int size = 1;
            while (value >= 0x80)
            {
                size++;
                value >>= 7;
            }
            return size;
        }
    }
}
