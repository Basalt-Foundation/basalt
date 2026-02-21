using Basalt.Core;
using Basalt.Crypto;
using FluentAssertions;
using Xunit;

namespace Basalt.Crypto.Tests;

public class Blake3Tests
{
    [Fact]
    public void Hash_EmptyInput_ProducesKnownHash()
    {
        var hash = Blake3Hasher.Hash(ReadOnlySpan<byte>.Empty);
        hash.IsZero.Should().BeFalse();
        // BLAKE3 of empty input: af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262
        hash.ToHexString().Should().Be("0xaf1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262");
    }

    [Fact]
    public void Hash_DeterministicForSameInput()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var hash1 = Blake3Hasher.Hash(data);
        var hash2 = Blake3Hasher.Hash(data);
        hash1.Should().Be(hash2);
    }

    [Fact]
    public void Hash_DifferentInputs_DifferentHashes()
    {
        var hash1 = Blake3Hasher.Hash(new byte[] { 1 });
        var hash2 = Blake3Hasher.Hash(new byte[] { 2 });
        hash1.Should().NotBe(hash2);
    }

    [Fact]
    public void HashPair_IsDeterministic()
    {
        var a = Blake3Hasher.Hash(new byte[] { 1 });
        var b = Blake3Hasher.Hash(new byte[] { 2 });
        var result1 = Blake3Hasher.HashPair(a, b);
        var result2 = Blake3Hasher.HashPair(a, b);
        result1.Should().Be(result2);
    }

    [Fact]
    public void HashPair_OrderMatters()
    {
        var a = Blake3Hasher.Hash(new byte[] { 1 });
        var b = Blake3Hasher.Hash(new byte[] { 2 });
        var ab = Blake3Hasher.HashPair(a, b);
        var ba = Blake3Hasher.HashPair(b, a);
        ab.Should().NotBe(ba);
    }

    [Fact]
    public void IncrementalHasher_MatchesSingleShot()
    {
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var singleShot = Blake3Hasher.Hash(data);

        using var incremental = Blake3Hasher.CreateIncremental();
        incremental.Update(data[..4]);
        incremental.Update(data[4..]);
        var incrementalResult = incremental.Finalize();

        incrementalResult.Should().Be(singleShot);
    }

    // ===== AUDIT M-11: Disposed guard on IncrementalHasher =====

    [Fact]
    public void IncrementalHasher_UpdateAfterDispose_ThrowsObjectDisposedException()
    {
        var hasher = Blake3Hasher.CreateIncremental();
        hasher.Dispose();

        var act = () => hasher.Update(new byte[] { 1, 2, 3 });
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void IncrementalHasher_FinalizeAfterDispose_ThrowsObjectDisposedException()
    {
        var hasher = Blake3Hasher.CreateIncremental();
        hasher.Dispose();

        var act = () => hasher.Finalize();
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void IncrementalHasher_DoubleDispose_DoesNotThrow()
    {
        var hasher = Blake3Hasher.CreateIncremental();
        hasher.Dispose();

        var act = () => hasher.Dispose();
        act.Should().NotThrow();
    }
}
