using Xunit;
using FluentAssertions;
using Basalt.Core;
using Basalt.Sdk.Wallet.Contracts;

namespace Basalt.Sdk.Wallet.Tests;

public sealed class AbiEncoderTests
{
    [Fact]
    public void ComputeSelector_Returns4Bytes()
    {
        byte[] selector = AbiEncoder.ComputeSelector("transfer");

        selector.Should().HaveCount(4);
    }

    [Fact]
    public void ComputeSelector_Deterministic()
    {
        byte[] first = AbiEncoder.ComputeSelector("transfer");
        byte[] second = AbiEncoder.ComputeSelector("transfer");

        first.Should().BeEquivalentTo(second);
    }

    [Fact]
    public void ComputeSelector_DifferentMethods_DifferentSelectors()
    {
        byte[] transferSelector = AbiEncoder.ComputeSelector("transfer");
        byte[] approveSelector = AbiEncoder.ComputeSelector("approve");

        transferSelector.Should().NotBeEquivalentTo(approveSelector);
    }

    [Fact]
    public void EncodeCall_CombinesSelectorAndArgs()
    {
        byte[] arg1 = [0x01, 0x02];
        byte[] arg2 = [0x03, 0x04, 0x05];

        byte[] callData = AbiEncoder.EncodeCall("foo", arg1, arg2);

        byte[] expectedSelector = AbiEncoder.ComputeSelector("foo");
        callData[..4].Should().BeEquivalentTo(expectedSelector);
        callData[4..6].Should().BeEquivalentTo(arg1);
        callData[6..9].Should().BeEquivalentTo(arg2);
        callData.Should().HaveCount(4 + arg1.Length + arg2.Length);
    }

    [Fact]
    public void EncodeUInt256_RoundTrip()
    {
        var original = new UInt256(123456789);

        byte[] encoded = AbiEncoder.EncodeUInt256(original);
        int offset = 0;
        UInt256 decoded = AbiEncoder.DecodeUInt256(encoded, ref offset);

        decoded.Should().Be(original);
        offset.Should().Be(32);
    }

    [Fact]
    public void EncodeUInt64_RoundTrip()
    {
        ulong original = 9876543210UL;

        byte[] encoded = AbiEncoder.EncodeUInt64(original);
        int offset = 0;
        ulong decoded = AbiEncoder.DecodeUInt64(encoded, ref offset);

        decoded.Should().Be(original);
        offset.Should().Be(8);
    }

    [Fact]
    public void EncodeAddress_RoundTrip()
    {
        var original = new Address(new byte[20] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 });

        byte[] encoded = AbiEncoder.EncodeAddress(original);
        int offset = 0;
        Address decoded = AbiEncoder.DecodeAddress(encoded, ref offset);

        decoded.Should().Be(original);
        offset.Should().Be(20);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EncodeBool_RoundTrip(bool original)
    {
        byte[] encoded = AbiEncoder.EncodeBool(original);
        int offset = 0;
        bool decoded = AbiEncoder.DecodeBool(encoded, ref offset);

        decoded.Should().Be(original);
        offset.Should().Be(1);
    }

    [Fact]
    public void EncodeBytes_RoundTrip()
    {
        byte[] original = "hello"u8.ToArray();

        byte[] encoded = AbiEncoder.EncodeBytes(original);
        int offset = 0;
        byte[] decoded = AbiEncoder.DecodeBytes(encoded, ref offset);

        decoded.Should().BeEquivalentTo(original);
        offset.Should().Be(4 + original.Length);
    }

    [Fact]
    public void EncodeString_RoundTrip()
    {
        string original = "hello";

        byte[] encoded = AbiEncoder.EncodeString(original);
        int offset = 0;
        string decoded = AbiEncoder.DecodeString(encoded, ref offset);

        decoded.Should().Be(original);
    }

    [Fact]
    public void DecodeMultipleValues()
    {
        var value = new UInt256(42);
        var addr = new Address(new byte[20] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130, 140, 150, 160, 170, 180, 190, 200 });
        bool flag = true;

        byte[] encodedValue = AbiEncoder.EncodeUInt256(value);
        byte[] encodedAddr = AbiEncoder.EncodeAddress(addr);
        byte[] encodedBool = AbiEncoder.EncodeBool(flag);

        byte[] combined = new byte[encodedValue.Length + encodedAddr.Length + encodedBool.Length];
        encodedValue.CopyTo(combined, 0);
        encodedAddr.CopyTo(combined, encodedValue.Length);
        encodedBool.CopyTo(combined, encodedValue.Length + encodedAddr.Length);

        int offset = 0;
        UInt256 decodedValue = AbiEncoder.DecodeUInt256(combined, ref offset);
        Address decodedAddr = AbiEncoder.DecodeAddress(combined, ref offset);
        bool decodedBool = AbiEncoder.DecodeBool(combined, ref offset);

        decodedValue.Should().Be(value);
        decodedAddr.Should().Be(addr);
        decodedBool.Should().Be(flag);
        offset.Should().Be(32 + 20 + 1);
    }
}
