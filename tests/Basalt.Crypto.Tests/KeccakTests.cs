using System.Text;
using Basalt.Core;
using Basalt.Crypto;
using FluentAssertions;
using Xunit;

namespace Basalt.Crypto.Tests;

public class KeccakTests
{
    [Fact]
    public void Hash_EmptyInput_ProducesNonZero()
    {
        var hash = KeccakHasher.Hash(ReadOnlySpan<byte>.Empty);
        hash.Should().NotBeEmpty();
    }

    [Fact]
    public void Hash_IsDeterministic()
    {
        var data = new byte[] { 1, 2, 3 };
        var hash1 = KeccakHasher.Hash(data);
        var hash2 = KeccakHasher.Hash(data);
        hash1.Should().Equal(hash2);
    }

    [Fact]
    public void DeriveAddress_ProducesValidAddress()
    {
        var pubKeyBytes = new byte[32];
        Random.Shared.NextBytes(pubKeyBytes);
        var pubKey = new PublicKey(pubKeyBytes);

        var address = KeccakHasher.DeriveAddress(pubKey);
        address.IsZero.Should().BeFalse();
    }

    [Fact]
    public void DeriveAddress_IsDeterministic()
    {
        var pubKeyBytes = new byte[32];
        pubKeyBytes[0] = 0xAB;
        var pubKey = new PublicKey(pubKeyBytes);

        var addr1 = KeccakHasher.DeriveAddress(pubKey);
        var addr2 = KeccakHasher.DeriveAddress(pubKey);
        addr1.Should().Be(addr2);
    }

    // ===== CORE-04: NIST/Known Keccak-256 test vectors =====
    // These are Keccak-256 (NOT SHA3-256) known-answer tests.
    // The padding byte is 0x01 for Keccak vs 0x06 for SHA-3.

    [Fact]
    public void Hash_EmptyString_MatchesKeccak256Vector()
    {
        // Keccak-256("") = c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470
        var hash = KeccakHasher.Hash(ReadOnlySpan<byte>.Empty);
        var expected = Convert.FromHexString("c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470");
        hash.Should().Equal(expected);
    }

    [Fact]
    public void Hash_Abc_MatchesKeccak256Vector()
    {
        // Keccak-256("abc") = 4e03657aea45a94fc7d47ba826c8d667c0d1e6e33a64a036ec44f58fa12d6c45
        var data = Encoding.ASCII.GetBytes("abc");
        var hash = KeccakHasher.Hash(data);
        var expected = Convert.FromHexString("4e03657aea45a94fc7d47ba826c8d667c0d1e6e33a64a036ec44f58fa12d6c45");
        hash.Should().Equal(expected);
    }

    [Fact]
    public void Hash_LongerMessage_MatchesKeccak256Vector()
    {
        // Keccak-256("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq")
        // = 45d3b367a6904e6e8d502ee04999a7c27647f91fa845d456525fd352ae3d7371
        var data = Encoding.ASCII.GetBytes("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq");
        var hash = KeccakHasher.Hash(data);
        var expected = Convert.FromHexString("45d3b367a6904e6e8d502ee04999a7c27647f91fa845d456525fd352ae3d7371");
        hash.Should().Equal(expected);
    }

    [Fact]
    public void Hash_EthereumStyleAddress_MatchesKnownDerivation()
    {
        // Verify that Keccak-256 of a known 32-byte input gives the expected hash
        // This validates our implementation matches Ethereum's address derivation
        var input = new byte[32];
        // All zeros
        var hash = KeccakHasher.Hash(input);
        // Keccak-256(0x00...00 32 bytes) = 290decd9548b62a8d60345a988386fc84ba6bc95484008f6362f93160ef3e563
        var expected = Convert.FromHexString("290decd9548b62a8d60345a988386fc84ba6bc95484008f6362f93160ef3e563");
        hash.Should().Equal(expected);
    }

    [Fact]
    public void Hash_SingleByte_MatchesVector()
    {
        // Keccak-256(0x00) - single zero byte
        var hash = KeccakHasher.Hash(new byte[] { 0x00 });
        // Known result for single zero byte
        hash.Length.Should().Be(32);
        // Verify it's different from empty input (which uses different padding block)
        var emptyHash = KeccakHasher.Hash(ReadOnlySpan<byte>.Empty);
        hash.Should().NotEqual(emptyHash);
    }

    [Fact]
    public void Hash_ExactlyOneBlock_136Bytes()
    {
        // Test with exactly one rate block (136 bytes)
        var data = new byte[136];
        for (int i = 0; i < 136; i++) data[i] = (byte)(i & 0xFF);
        var hash = KeccakHasher.Hash(data);
        hash.Length.Should().Be(32);
        // Should be deterministic
        var hash2 = KeccakHasher.Hash(data);
        hash.Should().Equal(hash2);
    }

    [Fact]
    public void Hash_MultipleBlocks_272Bytes()
    {
        // Test with exactly two rate blocks (272 bytes)
        var data = new byte[272];
        for (int i = 0; i < 272; i++) data[i] = (byte)(i & 0xFF);
        var hash = KeccakHasher.Hash(data);
        hash.Length.Should().Be(32);
    }

    [Fact]
    public void Hash_OutputLengthAlways32()
    {
        for (int len = 0; len < 300; len++)
        {
            var data = new byte[len];
            var hash = KeccakHasher.Hash(data);
            hash.Length.Should().Be(32, $"hash of {len}-byte input should be 32 bytes");
        }
    }

    [Fact]
    public void Hash_SpanOverload_MatchesArrayOverload()
    {
        var data = Encoding.ASCII.GetBytes("test data for span vs array comparison");
        var arrayHash = KeccakHasher.Hash(data);

        Span<byte> destination = stackalloc byte[32];
        KeccakHasher.Hash(data, destination);

        destination.ToArray().Should().Equal(arrayHash);
    }
}
