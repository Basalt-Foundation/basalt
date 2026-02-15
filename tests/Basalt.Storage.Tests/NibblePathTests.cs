using Basalt.Storage.Trie;
using FluentAssertions;
using Xunit;

namespace Basalt.Storage.Tests;

public class NibblePathTests
{
    [Fact]
    public void FromKey_ProducesCorrectNibbles()
    {
        var path = NibblePath.FromKey([0xAB, 0xCD]);

        path.Length.Should().Be(4);
        path[0].Should().Be(0x0A);
        path[1].Should().Be(0x0B);
        path[2].Should().Be(0x0C);
        path[3].Should().Be(0x0D);
    }

    [Fact]
    public void Slice_ProducesSubPath()
    {
        var path = NibblePath.FromKey([0xAB, 0xCD]);
        var sliced = path.Slice(1);

        sliced.Length.Should().Be(3);
        sliced[0].Should().Be(0x0B);
        sliced[1].Should().Be(0x0C);
        sliced[2].Should().Be(0x0D);
    }

    [Fact]
    public void CommonPrefixLength_FullMatch()
    {
        var a = NibblePath.FromKey([0xAB, 0xCD]);
        var b = NibblePath.FromKey([0xAB, 0xCD]);

        a.CommonPrefixLength(b).Should().Be(4);
    }

    [Fact]
    public void CommonPrefixLength_PartialMatch()
    {
        var a = NibblePath.FromKey([0xAB, 0xCD]);
        var b = NibblePath.FromKey([0xAB, 0xCE]);

        a.CommonPrefixLength(b).Should().Be(3); // A, B, C match; D != E
    }

    [Fact]
    public void CommonPrefixLength_NoMatch()
    {
        var a = NibblePath.FromKey([0x12]);
        var b = NibblePath.FromKey([0x34]);

        a.CommonPrefixLength(b).Should().Be(0);
    }

    [Fact]
    public void CompactEncoding_LeafEvenPath_Roundtrips()
    {
        var original = NibblePath.FromKey([0xAB, 0xCD]);
        var encoded = original.ToCompactEncoding(isLeaf: true);
        var (decoded, isLeaf) = NibblePath.FromCompactEncoding(encoded);

        isLeaf.Should().BeTrue();
        decoded.Length.Should().Be(original.Length);
        for (int i = 0; i < original.Length; i++)
            decoded[i].Should().Be(original[i]);
    }

    [Fact]
    public void CompactEncoding_ExtensionOddPath_Roundtrips()
    {
        // Odd-length path (3 nibbles): use a 2-byte key and slice to 3 nibbles
        var original = NibblePath.FromKey([0xAB, 0xC0]).Slice(0, 3);
        var encoded = original.ToCompactEncoding(isLeaf: false);
        var (decoded, isLeaf) = NibblePath.FromCompactEncoding(encoded);

        isLeaf.Should().BeFalse();
        decoded.Length.Should().Be(original.Length);
        for (int i = 0; i < original.Length; i++)
            decoded[i].Should().Be(original[i]);
    }

    [Fact]
    public void Equals_SamePaths()
    {
        var a = NibblePath.FromKey([0xAB, 0xCD]);
        var b = NibblePath.FromKey([0xAB, 0xCD]);

        a.Equals(b).Should().BeTrue();
    }

    [Fact]
    public void Equals_DifferentPaths()
    {
        var a = NibblePath.FromKey([0xAB, 0xCD]);
        var b = NibblePath.FromKey([0xAB, 0xCE]);

        a.Equals(b).Should().BeFalse();
    }

    [Fact]
    public void ToString_ShowsNibbles()
    {
        var path = NibblePath.FromKey([0xAB, 0xCD]);
        path.ToString().Should().Be("abcd");
    }

    // -----------------------------------------------------------------------
    // Edge cases for FromKey
    // -----------------------------------------------------------------------

    [Fact]
    public void FromKey_EmptyArray_ProducesEmptyPath()
    {
        var path = NibblePath.FromKey([]);
        path.Length.Should().Be(0);
        path.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void FromKey_SingleByte_ProducesTwoNibbles()
    {
        var path = NibblePath.FromKey([0x5A]);
        path.Length.Should().Be(2);
        path[0].Should().Be(0x05);
        path[1].Should().Be(0x0A);
    }

    [Fact]
    public void FromKey_AllZeros_ProducesZeroNibbles()
    {
        var path = NibblePath.FromKey([0x00, 0x00]);
        path.Length.Should().Be(4);
        for (int i = 0; i < 4; i++)
            path[i].Should().Be(0);
    }

    [Fact]
    public void FromKey_AllFF_ProducesAllFNibbles()
    {
        var path = NibblePath.FromKey([0xFF, 0xFF]);
        path.Length.Should().Be(4);
        for (int i = 0; i < 4; i++)
            path[i].Should().Be(0x0F);
    }

    [Fact]
    public void FromKey_LongKey_CorrectLength()
    {
        var key = new byte[32]; // 32 bytes = 64 nibbles
        var path = NibblePath.FromKey(key);
        path.Length.Should().Be(64);
    }

    // -----------------------------------------------------------------------
    // FromSpan
    // -----------------------------------------------------------------------

    [Fact]
    public void FromSpan_ProducesSameResultAsFromKey()
    {
        byte[] key = [0xDE, 0xAD, 0xBE, 0xEF];
        var fromKey = NibblePath.FromKey(key);
        var fromSpan = NibblePath.FromSpan(key.AsSpan());

        fromSpan.Length.Should().Be(fromKey.Length);
        for (int i = 0; i < fromKey.Length; i++)
            fromSpan[i].Should().Be(fromKey[i]);
    }

    // -----------------------------------------------------------------------
    // IsEmpty
    // -----------------------------------------------------------------------

    [Fact]
    public void IsEmpty_NonEmptyPath_ReturnsFalse()
    {
        var path = NibblePath.FromKey([0x01]);
        path.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void IsEmpty_SlicedToZeroLength_ReturnsTrue()
    {
        var path = NibblePath.FromKey([0xAB]);
        var sliced = path.Slice(2); // Skip both nibbles
        sliced.Length.Should().Be(0);
        sliced.IsEmpty.Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // Slice with offset and length
    // -----------------------------------------------------------------------

    [Fact]
    public void Slice_WithLength_ProducesCorrectSubpath()
    {
        var path = NibblePath.FromKey([0xAB, 0xCD, 0xEF]);
        // Full path: A, B, C, D, E, F
        var sliced = path.Slice(1, 3); // B, C, D

        sliced.Length.Should().Be(3);
        sliced[0].Should().Be(0x0B);
        sliced[1].Should().Be(0x0C);
        sliced[2].Should().Be(0x0D);
    }

    [Fact]
    public void Slice_ZeroLength_ProducesEmptyPath()
    {
        var path = NibblePath.FromKey([0xAB]);
        var sliced = path.Slice(0, 0);
        sliced.Length.Should().Be(0);
        sliced.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Slice_FullLength_ProducesSamePath()
    {
        var path = NibblePath.FromKey([0xAB, 0xCD]);
        var sliced = path.Slice(0, 4);

        sliced.Length.Should().Be(4);
        for (int i = 0; i < 4; i++)
            sliced[i].Should().Be(path[i]);
    }

    [Fact]
    public void Slice_ChainedSlices_ProducesCorrectResult()
    {
        var path = NibblePath.FromKey([0xAB, 0xCD, 0xEF]);
        // Full path: A, B, C, D, E, F
        var first = path.Slice(1); // B, C, D, E, F
        var second = first.Slice(2); // D, E, F

        second.Length.Should().Be(3);
        second[0].Should().Be(0x0D);
        second[1].Should().Be(0x0E);
        second[2].Should().Be(0x0F);
    }

    // -----------------------------------------------------------------------
    // CommonPrefixLength edge cases
    // -----------------------------------------------------------------------

    [Fact]
    public void CommonPrefixLength_EmptyPaths_ReturnsZero()
    {
        var a = NibblePath.FromKey([]);
        var b = NibblePath.FromKey([]);
        a.CommonPrefixLength(b).Should().Be(0);
    }

    [Fact]
    public void CommonPrefixLength_OneEmpty_ReturnsZero()
    {
        var a = NibblePath.FromKey([0xAB]);
        var b = NibblePath.FromKey([]);
        a.CommonPrefixLength(b).Should().Be(0);
    }

    [Fact]
    public void CommonPrefixLength_DifferentLengths_PartialMatch()
    {
        var a = NibblePath.FromKey([0xAB, 0xCD]);   // A, B, C, D
        var b = NibblePath.FromKey([0xAB, 0xCD, 0xEF]); // A, B, C, D, E, F
        a.CommonPrefixLength(b).Should().Be(4); // All of 'a' matches
    }

    [Fact]
    public void CommonPrefixLength_DifferentLengths_NoMatch()
    {
        var a = NibblePath.FromKey([0x12]);
        var b = NibblePath.FromKey([0x34, 0x56]);
        a.CommonPrefixLength(b).Should().Be(0);
    }

    [Fact]
    public void CommonPrefixLength_SingleNibbleMatch()
    {
        var a = NibblePath.FromKey([0xA0]);  // A, 0
        var b = NibblePath.FromKey([0xAF]);  // A, F
        a.CommonPrefixLength(b).Should().Be(1);
    }

    // -----------------------------------------------------------------------
    // Compact encoding: additional roundtrip tests
    // -----------------------------------------------------------------------

    [Fact]
    public void CompactEncoding_EmptyPath_Leaf_Roundtrips()
    {
        var original = new NibblePath([], 0, 0);
        var encoded = original.ToCompactEncoding(isLeaf: true);
        var (decoded, isLeaf) = NibblePath.FromCompactEncoding(encoded);

        isLeaf.Should().BeTrue();
        decoded.Length.Should().Be(0);
    }

    [Fact]
    public void CompactEncoding_EmptyPath_Extension_Roundtrips()
    {
        var original = new NibblePath([], 0, 0);
        var encoded = original.ToCompactEncoding(isLeaf: false);
        var (decoded, isLeaf) = NibblePath.FromCompactEncoding(encoded);

        isLeaf.Should().BeFalse();
        decoded.Length.Should().Be(0);
    }

    [Fact]
    public void CompactEncoding_SingleNibble_Leaf_Roundtrips()
    {
        // Create a path with a single nibble (odd length)
        var original = NibblePath.FromKey([0xA0]).Slice(0, 1);
        original.Length.Should().Be(1);
        original[0].Should().Be(0x0A);

        var encoded = original.ToCompactEncoding(isLeaf: true);
        var (decoded, isLeaf) = NibblePath.FromCompactEncoding(encoded);

        isLeaf.Should().BeTrue();
        decoded.Length.Should().Be(1);
        decoded[0].Should().Be(0x0A);
    }

    [Fact]
    public void CompactEncoding_SingleNibble_Extension_Roundtrips()
    {
        var original = NibblePath.FromKey([0x50]).Slice(0, 1);
        var encoded = original.ToCompactEncoding(isLeaf: false);
        var (decoded, isLeaf) = NibblePath.FromCompactEncoding(encoded);

        isLeaf.Should().BeFalse();
        decoded.Length.Should().Be(1);
        decoded[0].Should().Be(0x05);
    }

    [Fact]
    public void CompactEncoding_LongOddPath_Roundtrips()
    {
        // 5 nibbles (odd): A, B, C, D, E
        var original = NibblePath.FromKey([0xAB, 0xCD, 0xE0]).Slice(0, 5);
        original.Length.Should().Be(5);

        var encoded = original.ToCompactEncoding(isLeaf: true);
        var (decoded, isLeaf) = NibblePath.FromCompactEncoding(encoded);

        isLeaf.Should().BeTrue();
        decoded.Length.Should().Be(5);
        for (int i = 0; i < 5; i++)
            decoded[i].Should().Be(original[i]);
    }

    [Fact]
    public void CompactEncoding_LongEvenPath_Roundtrips()
    {
        // 6 nibbles (even): A, B, C, D, E, F
        var original = NibblePath.FromKey([0xAB, 0xCD, 0xEF]);
        original.Length.Should().Be(6);

        var encoded = original.ToCompactEncoding(isLeaf: false);
        var (decoded, isLeaf) = NibblePath.FromCompactEncoding(encoded);

        isLeaf.Should().BeFalse();
        decoded.Length.Should().Be(6);
        for (int i = 0; i < 6; i++)
            decoded[i].Should().Be(original[i]);
    }

    [Fact]
    public void CompactEncoding_EmptyInput_ReturnsEmptyExtension()
    {
        var (decoded, isLeaf) = NibblePath.FromCompactEncoding([]);
        decoded.Length.Should().Be(0);
        isLeaf.Should().BeFalse();
    }

    [Fact]
    public void CompactEncoding_AllNibbleValues_Roundtrip()
    {
        // Path with all 16 nibble values: 0, 1, 2, ..., F
        var data = new byte[] { 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF };
        var original = NibblePath.FromKey(data);
        original.Length.Should().Be(16);

        var encoded = original.ToCompactEncoding(isLeaf: true);
        var (decoded, isLeaf) = NibblePath.FromCompactEncoding(encoded);

        isLeaf.Should().BeTrue();
        decoded.Length.Should().Be(16);
        for (int i = 0; i < 16; i++)
            decoded[i].Should().Be(original[i]);
    }

    // -----------------------------------------------------------------------
    // Equals edge cases
    // -----------------------------------------------------------------------

    [Fact]
    public void Equals_EmptyPaths_AreEqual()
    {
        var a = NibblePath.FromKey([]);
        var b = NibblePath.FromKey([]);
        a.Equals(b).Should().BeTrue();
    }

    [Fact]
    public void Equals_DifferentLengths_AreNotEqual()
    {
        var a = NibblePath.FromKey([0xAB]);
        var b = NibblePath.FromKey([0xAB, 0xCD]);
        a.Equals(b).Should().BeFalse();
    }

    [Fact]
    public void Equals_SlicedPathsWithSameContent_AreEqual()
    {
        var a = NibblePath.FromKey([0xAB, 0xCD]).Slice(2); // C, D
        var b = NibblePath.FromKey([0xCD]);                  // C, D

        a.Equals(b).Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // ToString edge cases
    // -----------------------------------------------------------------------

    [Fact]
    public void ToString_EmptyPath_ReturnsEmptyString()
    {
        var path = NibblePath.FromKey([]);
        path.ToString().Should().Be("");
    }

    [Fact]
    public void ToString_AllZeros_ReturnsZeros()
    {
        var path = NibblePath.FromKey([0x00]);
        path.ToString().Should().Be("00");
    }

    [Fact]
    public void ToString_AllF_ReturnsAllF()
    {
        var path = NibblePath.FromKey([0xFF]);
        path.ToString().Should().Be("ff");
    }

    [Fact]
    public void ToString_SlicedPath_ReturnsCorrect()
    {
        var path = NibblePath.FromKey([0xAB, 0xCD]);
        var sliced = path.Slice(1, 2); // B, C
        sliced.ToString().Should().Be("bc");
    }
}
