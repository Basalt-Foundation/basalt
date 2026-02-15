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
}
