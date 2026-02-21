using Basalt.Core;
using FluentAssertions;
using Xunit;

namespace Basalt.Core.Tests;

public class Hash256Tests
{
    [Fact]
    public void Zero_IsAllZeros()
    {
        Hash256.Zero.IsZero.Should().BeTrue();
    }

    [Fact]
    public void Constructor_WithValidBytes_Succeeds()
    {
        var bytes = new byte[32];
        bytes[0] = 0x42;
        bytes[31] = 0xFF;

        var hash = new Hash256(bytes);
        hash.IsZero.Should().BeFalse();

        var output = hash.ToArray();
        output[0].Should().Be(0x42);
        output[31].Should().Be(0xFF);
    }

    [Fact]
    public void Constructor_WithInvalidLength_Throws()
    {
        var act = () => new Hash256(new byte[16]);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Roundtrip_BytesToHashToBytes()
    {
        var original = new byte[32];
        Random.Shared.NextBytes(original);
        var hash = new Hash256(original);
        var result = hash.ToArray();
        result.Should().Equal(original);
    }

    [Fact]
    public void Equality_SameHash_AreEqual()
    {
        var bytes = new byte[32];
        bytes[15] = 0xAB;
        var a = new Hash256(bytes);
        var b = new Hash256(bytes);
        a.Should().Be(b);
        (a == b).Should().BeTrue();
        (a != b).Should().BeFalse();
    }

    [Fact]
    public void Equality_DifferentHash_AreNotEqual()
    {
        var a = new Hash256(new byte[32]);
        var bytesB = new byte[32];
        bytesB[0] = 1;
        var b = new Hash256(bytesB);
        a.Should().NotBe(b);
    }

    [Fact]
    public void ToHexString_Roundtrip()
    {
        var bytes = new byte[32];
        bytes[0] = 0xDE;
        bytes[1] = 0xAD;
        bytes[30] = 0xBE;
        bytes[31] = 0xEF;
        var hash = new Hash256(bytes);
        var hex = hash.ToHexString();
        hex.Should().StartWith("0x");

        var parsed = Hash256.FromHexString(hex);
        parsed.Should().Be(hash);
    }

    [Fact]
    public void GetHashCode_SameValues_SameHashCode()
    {
        var bytes = new byte[32];
        bytes[7] = 42;
        var a = new Hash256(bytes);
        var b = new Hash256(bytes);
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void CompareTo_OrdersCorrectly()
    {
        var lower = new Hash256(new byte[32]);
        var higherBytes = new byte[32];
        higherBytes[0] = 1;
        var higher = new Hash256(higherBytes);

        lower.CompareTo(higher).Should().BeLessThan(0);
        higher.CompareTo(lower).Should().BeGreaterThan(0);
        lower.CompareTo(lower).Should().Be(0);
    }

    // ===== AUDIT L-01: TryFromHexString without exceptions =====

    [Fact]
    public void TryFromHexString_ValidHex_ReturnsTrue()
    {
        var bytes = new byte[32];
        bytes[0] = 0xAB;
        bytes[31] = 0xCD;
        var expected = new Hash256(bytes);
        var hex = expected.ToHexString();

        Hash256.TryFromHexString(hex, out var result).Should().BeTrue();
        result.Should().Be(expected);
    }

    [Fact]
    public void TryFromHexString_Null_ReturnsFalse()
    {
        Hash256.TryFromHexString(null, out var result).Should().BeFalse();
        result.Should().Be(Hash256.Zero);
    }

    [Fact]
    public void TryFromHexString_WrongLength_ReturnsFalse()
    {
        Hash256.TryFromHexString("0xabcd", out var result).Should().BeFalse();
        result.Should().Be(Hash256.Zero);
    }

    [Fact]
    public void TryFromHexString_InvalidChars_ReturnsFalse()
    {
        // 64 hex chars but with invalid 'Z' characters
        Hash256.TryFromHexString("0x" + "ZZ" + new string('0', 62), out var result).Should().BeFalse();
        result.Should().Be(Hash256.Zero);
    }
}
