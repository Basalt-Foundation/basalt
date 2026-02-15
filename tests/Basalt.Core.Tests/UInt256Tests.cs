using Basalt.Core;
using FluentAssertions;
using Xunit;

namespace Basalt.Core.Tests;

public class UInt256Tests
{
    [Fact]
    public void Zero_IsDefault()
    {
        UInt256.Zero.IsZero.Should().BeTrue();
        UInt256.Zero.Lo.Should().Be((UInt128)0);
        UInt256.Zero.Hi.Should().Be((UInt128)0);
    }

    [Fact]
    public void One_HasCorrectValue()
    {
        UInt256.One.Lo.Should().Be((UInt128)1);
        UInt256.One.Hi.Should().Be((UInt128)0);
    }

    [Fact]
    public void Addition_SmallValues()
    {
        var a = new UInt256(100);
        var b = new UInt256(200);
        var result = a + b;
        ((ulong)result).Should().Be(300UL);
    }

    [Fact]
    public void Addition_WithCarry()
    {
        var a = new UInt256(UInt128.MaxValue, 0);
        var b = new UInt256(1);
        var result = a + b;
        result.Lo.Should().Be((UInt128)0);
        result.Hi.Should().Be((UInt128)1);
    }

    [Fact]
    public void Subtraction_SmallValues()
    {
        var a = new UInt256(300);
        var b = new UInt256(100);
        var result = a - b;
        ((ulong)result).Should().Be(200UL);
    }

    [Fact]
    public void Multiplication_SmallValues()
    {
        var a = new UInt256(12);
        var b = new UInt256(34);
        var result = a * b;
        ((ulong)result).Should().Be(408UL);
    }

    [Fact]
    public void Division_SmallValues()
    {
        var a = new UInt256(100);
        var b = new UInt256(3);
        var result = a / b;
        ((ulong)result).Should().Be(33UL);
    }

    [Fact]
    public void Modulo_SmallValues()
    {
        var a = new UInt256(100);
        var b = new UInt256(3);
        var result = a % b;
        ((ulong)result).Should().Be(1UL);
    }

    [Fact]
    public void Division_ByZero_Throws()
    {
        var act = () => { var _ = new UInt256(1) / UInt256.Zero; };
        act.Should().Throw<DivideByZeroException>();
    }

    [Fact]
    public void Comparison_LessThan()
    {
        var a = new UInt256(5);
        var b = new UInt256(10);
        (a < b).Should().BeTrue();
        (b < a).Should().BeFalse();
        (a > b).Should().BeFalse();
        (b > a).Should().BeTrue();
    }

    [Fact]
    public void Comparison_WithHighBits()
    {
        var a = new UInt256(0, 1); // Hi=1
        var b = new UInt256(UInt128.MaxValue, 0); // Lo=max, Hi=0
        (a > b).Should().BeTrue();
    }

    [Fact]
    public void ShiftLeft_Works()
    {
        var a = new UInt256(1);
        var result = a << 128;
        result.Lo.Should().Be((UInt128)0);
        result.Hi.Should().Be((UInt128)1);
    }

    [Fact]
    public void ShiftRight_Works()
    {
        var a = new UInt256(0, 1);
        var result = a >> 128;
        result.Lo.Should().Be((UInt128)1);
        result.Hi.Should().Be((UInt128)0);
    }

    [Fact]
    public void ByteRoundtrip_LittleEndian()
    {
        var original = new UInt256(0x123456789ABCDEF0, 0);
        var bytes = original.ToArray(isBigEndian: false);
        var parsed = new UInt256(bytes, isBigEndian: false);
        parsed.Should().Be(original);
    }

    [Fact]
    public void ByteRoundtrip_BigEndian()
    {
        var original = new UInt256(0x123456789ABCDEF0, 0);
        var bytes = original.ToArray(isBigEndian: true);
        var parsed = new UInt256(bytes, isBigEndian: true);
        parsed.Should().Be(original);
    }

    [Fact]
    public void Parse_Decimal()
    {
        var value = UInt256.Parse("1000000000000000000");
        ((ulong)value).Should().Be(1_000_000_000_000_000_000UL);
    }

    [Fact]
    public void Parse_Hex()
    {
        var value = UInt256.Parse("0xff");
        ((ulong)value).Should().Be(255UL);
    }

    [Fact]
    public void Equality_Works()
    {
        var a = new UInt256(42);
        var b = new UInt256(42);
        var c = new UInt256(43);
        (a == b).Should().BeTrue();
        (a != c).Should().BeTrue();
    }

    [Fact]
    public void ImplicitConversion_FromUlong()
    {
        UInt256 value = 12345UL;
        ((ulong)value).Should().Be(12345UL);
    }

    [Fact]
    public void Multiplication_LargeValues()
    {
        var a = new UInt256(1_000_000_000);
        var b = new UInt256(1_000_000_000);
        var result = a * b;
        ((ulong)result).Should().Be(1_000_000_000_000_000_000UL);
    }
}
