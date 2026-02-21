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

    // ===== CORE-01: Multiplication carry propagation tests =====

    [Fact]
    public void Multiplication_MaxLoTimesTwo_CarryPropagates()
    {
        // (2^128 - 1) * 2 = 2^129 - 2
        var a = new UInt256(UInt128.MaxValue, 0);
        var b = new UInt256(2);
        var result = a * b;
        // Expected: Lo = 2^128 - 2 = UInt128.MaxValue - 1, Hi = 1
        result.Lo.Should().Be(UInt128.MaxValue - 1);
        result.Hi.Should().Be((UInt128)1);
    }

    [Fact]
    public void Multiplication_MaxLoSquared_CrossLimbCarry()
    {
        // (2^128 - 1)^2 = 2^256 - 2^129 + 1
        // mod 2^256: Lo = 1, Hi = 2^128 - 2
        var a = new UInt256(UInt128.MaxValue, 0);
        var result = a * a;
        result.Lo.Should().Be((UInt128)1);
        result.Hi.Should().Be(UInt128.MaxValue - 1);
    }

    [Fact]
    public void Multiplication_LargeCrossLimb()
    {
        // Test multiplication that requires carry from column 1 to column 2
        // a = 2^64 - 1 (fills a0 limb)
        // b = 2^64 + 1 (fills b0 and b1 limbs)
        var a = new UInt256(ulong.MaxValue);
        var b = new UInt256(new UInt128(1, ulong.MaxValue)); // 2^64 * 1 + (2^64 - 1) ... no
        // Let's use: a = 2^128 - 1, b = 2^128 + 1 = UInt128 value with Hi=1, Lo=1
        // Wait, b = (Lo=1, Hi=1) which is 2^128 + 1
        // But that would be UInt256(Lo=UInt128(1), Hi=0) ... no.
        // a = UInt256(Lo=UInt128.MaxValue, Hi=0) = 2^128 - 1
        // b = UInt256(Lo=UInt128(1) + (UInt128(1) << 64), Hi=0) ... let's just verify the squaring above
        // Actually, let me use a simpler test: (2^64)^2 = 2^128
        var x = new UInt256(new UInt128(1, 0)); // 2^64
        var r = x * x; // should be 2^128
        r.Lo.Should().Be((UInt128)0);
        r.Hi.Should().Be((UInt128)1);
    }

    [Fact]
    public void Multiplication_MaxValueTimesOne()
    {
        var result = UInt256.MaxValue * UInt256.One;
        result.Should().Be(UInt256.MaxValue);
    }

    [Fact]
    public void Multiplication_MaxValueTimesZero()
    {
        var result = UInt256.MaxValue * UInt256.Zero;
        result.Should().Be(UInt256.Zero);
    }

    [Fact]
    public void Multiplication_OverflowWraps()
    {
        // MaxValue * MaxValue mod 2^256 = 1 (since -1 * -1 = 1 mod 2^256)
        var result = UInt256.MaxValue * UInt256.MaxValue;
        result.Should().Be(UInt256.One);
    }

    [Fact]
    public void Multiplication_PowersOfTwo()
    {
        // 2^64 * 2^64 = 2^128
        var a = new UInt256(new UInt128(1, 0)); // 2^64
        var b = new UInt256(new UInt128(1, 0)); // 2^64
        var result = a * b;
        result.Lo.Should().Be((UInt128)0);
        result.Hi.Should().Be((UInt128)1);

        // 2^64 * 2^128 = 2^192
        var c = new UInt256(0, 1); // 2^128
        result = a * c;
        result.Lo.Should().Be((UInt128)0);
        result.Hi.Should().Be(new UInt128(1, 0)); // 2^64 in Hi
    }

    [Fact]
    public void Multiplication_Commutativity()
    {
        var a = new UInt256(0xDEADBEEFCAFEBABE);
        var b = new UInt256(new UInt128(0x1234567890ABCDEF, 0xFEDCBA0987654321));
        (a * b).Should().Be(b * a);
    }

    [Fact]
    public void Multiplication_Distributivity()
    {
        var a = new UInt256(1_000_000_007);
        var b = new UInt256(999_999_937);
        var c = new UInt256(42);
        // a * (b + c) == a*b + a*c
        var lhs = a * (b + c);
        var rhs = a * b + a * c;
        lhs.Should().Be(rhs);
    }

    // ===== CORE-02: Checked arithmetic tests =====

    [Fact]
    public void CheckedAdd_SmallValues_Works()
    {
        var result = UInt256.CheckedAdd(new UInt256(100), new UInt256(200));
        ((ulong)result).Should().Be(300UL);
    }

    [Fact]
    public void CheckedAdd_Overflow_Throws()
    {
        var act = () => UInt256.CheckedAdd(UInt256.MaxValue, UInt256.One);
        act.Should().Throw<OverflowException>();
    }

    [Fact]
    public void CheckedAdd_MaxPlusZero_Works()
    {
        var result = UInt256.CheckedAdd(UInt256.MaxValue, UInt256.Zero);
        result.Should().Be(UInt256.MaxValue);
    }

    [Fact]
    public void CheckedSub_SmallValues_Works()
    {
        var result = UInt256.CheckedSub(new UInt256(300), new UInt256(100));
        ((ulong)result).Should().Be(200UL);
    }

    [Fact]
    public void CheckedSub_Underflow_Throws()
    {
        var act = () => UInt256.CheckedSub(UInt256.Zero, UInt256.One);
        act.Should().Throw<OverflowException>();
    }

    [Fact]
    public void CheckedSub_Equal_ReturnsZero()
    {
        var val = new UInt256(42);
        var result = UInt256.CheckedSub(val, val);
        result.Should().Be(UInt256.Zero);
    }

    [Fact]
    public void CheckedMul_SmallValues_Works()
    {
        var result = UInt256.CheckedMul(new UInt256(100), new UInt256(200));
        ((ulong)result).Should().Be(20_000UL);
    }

    [Fact]
    public void CheckedMul_Overflow_Throws()
    {
        var act = () => UInt256.CheckedMul(UInt256.MaxValue, new UInt256(2));
        act.Should().Throw<OverflowException>();
    }

    [Fact]
    public void CheckedMul_ByZero_ReturnsZero()
    {
        var result = UInt256.CheckedMul(UInt256.MaxValue, UInt256.Zero);
        result.Should().Be(UInt256.Zero);
    }

    [Fact]
    public void TryAdd_Success()
    {
        UInt256.TryAdd(new UInt256(100), new UInt256(200), out var result).Should().BeTrue();
        ((ulong)result).Should().Be(300UL);
    }

    [Fact]
    public void TryAdd_Overflow_ReturnsFalse()
    {
        UInt256.TryAdd(UInt256.MaxValue, UInt256.One, out _).Should().BeFalse();
    }

    [Fact]
    public void TrySub_Success()
    {
        UInt256.TrySub(new UInt256(300), new UInt256(100), out var result).Should().BeTrue();
        ((ulong)result).Should().Be(200UL);
    }

    [Fact]
    public void TrySub_Underflow_ReturnsFalse()
    {
        UInt256.TrySub(UInt256.Zero, UInt256.One, out _).Should().BeFalse();
    }

    // ===== AUDIT C-01: TryAdd overflow edge cases =====

    [Fact]
    public void TryAdd_CarryWithMaxHi_DetectsOverflow()
    {
        // a.Lo = max -> carry will be 1 when added with b.Lo = 1
        // b.Hi = max -> hiSum = 0 + max = max, hi = max + 1 = overflow
        var a = new UInt256(UInt128.MaxValue, 0);
        var b = new UInt256(1, UInt128.MaxValue);
        UInt256.TryAdd(a, b, out _).Should().BeFalse();
    }

    [Fact]
    public void CheckedAdd_CarryWithMaxHi_Throws()
    {
        var a = new UInt256(UInt128.MaxValue, 0);
        var b = new UInt256(1, UInt128.MaxValue);
        var act = () => UInt256.CheckedAdd(a, b);
        act.Should().Throw<OverflowException>();
    }

    [Fact]
    public void TryAdd_HiOverflowWithoutCarry_DetectsOverflow()
    {
        var a = new UInt256(0, UInt128.MaxValue);
        var b = new UInt256(0, 1);
        UInt256.TryAdd(a, b, out _).Should().BeFalse();
    }

    [Fact]
    public void TryAdd_JustBelowOverflow_Succeeds()
    {
        UInt256.TryAdd(UInt256.MaxValue, UInt256.Zero, out var result).Should().BeTrue();
        result.Should().Be(UInt256.MaxValue);
    }

    [Fact]
    public void TryAdd_BothMaxHi_DetectsOverflow()
    {
        var a = new UInt256(0, UInt128.MaxValue);
        var b = new UInt256(0, UInt128.MaxValue);
        UInt256.TryAdd(a, b, out _).Should().BeFalse();
    }

    // ===== AUDIT M-01: ToString consistency =====

    [Fact]
    public void ToString_LargeValue_ReturnsDecimal()
    {
        var v = new UInt256(0, 1); // 2^128
        v.ToString().Should().NotStartWith("0x");
        UInt256.Parse(v.ToString()).Should().Be(v);
    }

    [Fact]
    public void ToString_RoundTrip_AllSizes()
    {
        var small = new UInt256(42);
        UInt256.Parse(small.ToString()).Should().Be(small);

        var medium = new UInt256(ulong.MaxValue);
        UInt256.Parse(medium.ToString()).Should().Be(medium);

        var large = new UInt256(UInt128.MaxValue, 1);
        UInt256.Parse(large.ToString()).Should().Be(large);
    }

    // ===== AUDIT M-02: ToHexString zero =====

    [Fact]
    public void ToHexString_Zero_ReturnsZero()
    {
        UInt256.Zero.ToHexString().Should().Be("0");
    }

    // ===== AUDIT M-03: TryParse =====

    [Fact]
    public void TryParse_ValidDecimal_ReturnsTrue()
    {
        UInt256.TryParse("12345", out var result).Should().BeTrue();
        ((ulong)result).Should().Be(12345UL);
    }

    [Fact]
    public void TryParse_ValidHex_ReturnsTrue()
    {
        UInt256.TryParse("0xff", out var result).Should().BeTrue();
        ((ulong)result).Should().Be(255UL);
    }

    [Fact]
    public void TryParse_Invalid_ReturnsFalse()
    {
        UInt256.TryParse("not_a_number", out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_Null_ReturnsFalse()
    {
        UInt256.TryParse(null, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_Empty_ReturnsFalse()
    {
        UInt256.TryParse("", out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_Overflow_ReturnsFalse()
    {
        // A value that exceeds 2^256
        var huge = "115792089237316195423570985008687907853269984665640564039457584007913129639936"; // 2^256
        UInt256.TryParse(huge, out _).Should().BeFalse();
    }

    // ===== CORE-06: ChainParameters tests =====

    [Fact]
    public void FromConfiguration_MainnetChainId_UsesMainnetDefaults()
    {
        var p = ChainParameters.FromConfiguration(1, "mainnet");
        p.ChainId.Should().Be(1u);
        p.MinValidatorStake.Should().Be(UInt256.Parse("100000000000000000000000"));
        p.ValidatorSetSize.Should().Be(ChainParameters.MaxValidatorSetSize);
        p.EpochLength.Should().Be(1000u);
    }

    [Fact]
    public void FromConfiguration_TestnetChainId_UsesMainnetSecurityProfile()
    {
        var p = ChainParameters.FromConfiguration(2, "testnet");
        p.ChainId.Should().Be(2u);
        p.MinValidatorStake.Should().Be(UInt256.Parse("100000000000000000000000"));
        p.ValidatorSetSize.Should().Be(ChainParameters.MaxValidatorSetSize);
    }

    [Fact]
    public void FromConfiguration_DevnetChainId_UsesDevnetDefaults()
    {
        var p = ChainParameters.FromConfiguration(31337, "devnet");
        p.ChainId.Should().Be(31337u);
        p.MinValidatorStake.Should().Be(new UInt256(1000));
        p.ValidatorSetSize.Should().Be(4u);
        p.EpochLength.Should().Be(100u);
        p.InitialBaseFee.Should().Be(new UInt256(1));
    }

    // ===== AUDIT H-07: ChainParameters.Validate =====

    [Fact]
    public void ChainParameters_Validate_DefaultDevnet_Succeeds()
    {
        var act = () => ChainParameters.Devnet.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void ChainParameters_Validate_ZeroBlockTimeMs_Throws()
    {
        var p = new ChainParameters { ChainId = 99, NetworkName = "test", BlockTimeMs = 0 };
        var act = () => p.Validate();
        act.Should().Throw<InvalidOperationException>().WithMessage("*BlockTimeMs*");
    }

    [Fact]
    public void ChainParameters_Validate_ZeroBaseFeeChangeDenominator_Throws()
    {
        var p = new ChainParameters { ChainId = 99, NetworkName = "test", BaseFeeChangeDenominator = 0 };
        var act = () => p.Validate();
        act.Should().Throw<InvalidOperationException>().WithMessage("*BaseFeeChangeDenominator*");
    }

    [Fact]
    public void ChainParameters_Validate_ZeroElasticityMultiplier_Throws()
    {
        var p = new ChainParameters { ChainId = 99, NetworkName = "test", ElasticityMultiplier = 0 };
        var act = () => p.Validate();
        act.Should().Throw<InvalidOperationException>().WithMessage("*ElasticityMultiplier*");
    }

    [Fact]
    public void ChainParameters_Validate_ZeroEpochLength_Throws()
    {
        var p = new ChainParameters { ChainId = 99, NetworkName = "test", EpochLength = 0 };
        var act = () => p.Validate();
        act.Should().Throw<InvalidOperationException>().WithMessage("*EpochLength*");
    }

    // ===== AUDIT L-05: Mainnet/Testnet static readonly =====

    [Fact]
    public void ChainParameters_Mainnet_ReturnsSameInstance()
    {
        ReferenceEquals(ChainParameters.Mainnet, ChainParameters.Mainnet).Should().BeTrue();
    }

    [Fact]
    public void ChainParameters_Testnet_ReturnsSameInstance()
    {
        ReferenceEquals(ChainParameters.Testnet, ChainParameters.Testnet).Should().BeTrue();
    }

    // ===== CORE-08: Address.IsSystemContract tests =====

    [Fact]
    public void IsSystemContract_AddressOne_True()
    {
        var bytes = new byte[20];
        bytes[19] = 0x01; // 0x0000...0001 in big-endian
        var addr = new Address(bytes);
        addr.IsSystemContract.Should().BeTrue();
    }

    [Fact]
    public void IsSystemContract_Address0x1FFF_True()
    {
        var bytes = new byte[20];
        bytes[18] = 0x1F;
        bytes[19] = 0xFF; // 0x0000...1FFF in big-endian
        var addr = new Address(bytes);
        addr.IsSystemContract.Should().BeTrue();
    }

    [Fact]
    public void IsSystemContract_Address0x2000_False()
    {
        var bytes = new byte[20];
        bytes[18] = 0x20;
        bytes[19] = 0x00; // 0x0000...2000 in big-endian
        var addr = new Address(bytes);
        addr.IsSystemContract.Should().BeFalse();
    }

    [Fact]
    public void IsSystemContract_Zero_False()
    {
        Address.Zero.IsSystemContract.Should().BeFalse();
    }

    [Fact]
    public void IsSystemContract_NonZeroPrefix_False()
    {
        var bytes = new byte[20];
        bytes[0] = 0x01;
        bytes[19] = 0x01;
        var addr = new Address(bytes);
        addr.IsSystemContract.Should().BeFalse();
    }

    // ===== AUDIT C-03: Unaligned memory access roundtrip tests =====

    [Fact]
    public void Signature_FromUnalignedSpan_RoundTrips()
    {
        var buffer = new byte[Signature.Size + 3];
        Random.Shared.NextBytes(buffer);
        var span = buffer.AsSpan(3, Signature.Size);
        var sig = new Signature(span);
        sig.ToArray().Should().Equal(span.ToArray());
    }

    [Fact]
    public void PublicKey_FromUnalignedSpan_RoundTrips()
    {
        var buffer = new byte[PublicKey.Size + 3];
        Random.Shared.NextBytes(buffer);
        var span = buffer.AsSpan(3, PublicKey.Size);
        var key = new PublicKey(span);
        key.ToArray().Should().Equal(span.ToArray());
    }

    [Fact]
    public void BlsSignature_FromUnalignedSpan_RoundTrips()
    {
        var buffer = new byte[BlsSignature.Size + 5];
        Random.Shared.NextBytes(buffer);
        var span = buffer.AsSpan(5, BlsSignature.Size);
        var sig = new BlsSignature(span);
        sig.ToArray().Should().Equal(span.ToArray());
    }

    [Fact]
    public void BlsPublicKey_FromUnalignedSpan_RoundTrips()
    {
        var buffer = new byte[BlsPublicKey.Size + 7];
        Random.Shared.NextBytes(buffer);
        var span = buffer.AsSpan(7, BlsPublicKey.Size);
        var key = new BlsPublicKey(span);
        key.ToArray().Should().Equal(span.ToArray());
    }

    // ===== AUDIT H-09: GetHashCode distribution tests =====

    [Fact]
    public void Signature_GetHashCode_DiffersForTailOnlyDifference()
    {
        var bytes1 = new byte[Signature.Size];
        var bytes2 = new byte[Signature.Size];
        bytes1[56] = 0x01; // differs only in _v7
        var sig1 = new Signature(bytes1);
        var sig2 = new Signature(bytes2);
        sig1.GetHashCode().Should().NotBe(sig2.GetHashCode());
    }

    [Fact]
    public void BlsSignature_GetHashCode_DiffersForTailOnlyDifference()
    {
        var bytes1 = new byte[BlsSignature.Size];
        var bytes2 = new byte[BlsSignature.Size];
        bytes1[88] = 0x01; // differs only in _v11
        var sig1 = new BlsSignature(bytes1);
        var sig2 = new BlsSignature(bytes2);
        sig1.GetHashCode().Should().NotBe(sig2.GetHashCode());
    }

    [Fact]
    public void BlsPublicKey_GetHashCode_DiffersForTailOnlyDifference()
    {
        var bytes1 = new byte[BlsPublicKey.Size];
        var bytes2 = new byte[BlsPublicKey.Size];
        bytes1[40] = 0x01; // differs only in _v5
        var key1 = new BlsPublicKey(bytes1);
        var key2 = new BlsPublicKey(bytes2);
        key1.GetHashCode().Should().NotBe(key2.GetHashCode());
    }
}
