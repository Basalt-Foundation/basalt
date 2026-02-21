using Basalt.Core;
using FluentAssertions;
using Xunit;

namespace Basalt.Core.Tests;

public class AddressTests
{
    [Fact]
    public void Zero_IsAllZeros()
    {
        Address.Zero.IsZero.Should().BeTrue();
    }

    [Fact]
    public void Constructor_WithValidBytes()
    {
        var bytes = new byte[20];
        bytes[0] = 0xAB;
        bytes[19] = 0xCD;
        var addr = new Address(bytes);
        addr.IsZero.Should().BeFalse();

        var output = addr.ToArray();
        output[0].Should().Be(0xAB);
        output[19].Should().Be(0xCD);
    }

    [Fact]
    public void Constructor_InvalidLength_Throws()
    {
        var act = () => new Address(new byte[16]);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void HexRoundtrip()
    {
        var hex = "0xabcdef0123456789abcdef0123456789abcdef01";
        var addr = Address.FromHexString(hex);
        addr.ToHexString().Should().Be(hex);
    }

    [Fact]
    public void Equality()
    {
        var bytes = new byte[20];
        bytes[10] = 42;
        var a = new Address(bytes);
        var b = new Address(bytes);
        (a == b).Should().BeTrue();
    }

    // ===== AUDIT L-01: TryFromHexString without exceptions =====

    [Fact]
    public void TryFromHexString_ValidHex_ReturnsTrue()
    {
        var hex = "0xabcdef0123456789abcdef0123456789abcdef01";
        Address.TryFromHexString(hex, out var result).Should().BeTrue();
        result.ToHexString().Should().Be(hex);
    }

    [Fact]
    public void TryFromHexString_Null_ReturnsFalse()
    {
        Address.TryFromHexString(null, out var result).Should().BeFalse();
        result.Should().Be(Address.Zero);
    }

    [Fact]
    public void TryFromHexString_Empty_ReturnsFalse()
    {
        Address.TryFromHexString("", out var result).Should().BeFalse();
        result.Should().Be(Address.Zero);
    }

    [Fact]
    public void TryFromHexString_WrongLength_ReturnsFalse()
    {
        Address.TryFromHexString("0xabcd", out var result).Should().BeFalse();
        result.Should().Be(Address.Zero);
    }

    [Fact]
    public void TryFromHexString_InvalidChars_ReturnsFalse()
    {
        Address.TryFromHexString("0xGGGGGG0123456789abcdef0123456789abcdef01", out var result).Should().BeFalse();
        result.Should().Be(Address.Zero);
    }
}
