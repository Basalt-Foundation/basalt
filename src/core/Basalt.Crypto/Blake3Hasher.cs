using Basalt.Core;

namespace Basalt.Crypto;

/// <summary>
/// BLAKE3 hash function wrapper for Basalt.
/// Used as the primary hash function throughout the protocol.
/// </summary>
public static class Blake3Hasher
{
    /// <summary>
    /// Compute a 256-bit BLAKE3 hash of the input data.
    /// </summary>
    public static Hash256 Hash(ReadOnlySpan<byte> data)
    {
        Span<byte> output = stackalloc byte[32];
        Blake3.Hasher.Hash(data, output);
        return new Hash256(output);
    }

    /// <summary>
    /// Compute a BLAKE3 hash and write directly to output span.
    /// </summary>
    public static void Hash(ReadOnlySpan<byte> data, Span<byte> output)
    {
        Blake3.Hasher.Hash(data, output);
    }

    /// <summary>
    /// Compute BLAKE3 hash of concatenated inputs (useful for Merkle tree nodes).
    /// </summary>
    public static Hash256 HashPair(Hash256 left, Hash256 right)
    {
        Span<byte> combined = stackalloc byte[64];
        left.WriteTo(combined[..32]);
        right.WriteTo(combined[32..]);
        return Hash(combined);
    }

    /// <summary>
    /// Incremental BLAKE3 hasher for streaming data.
    /// </summary>
    public static IncrementalHasher CreateIncremental() => new();
}

/// <summary>
/// Incremental BLAKE3 hasher for streaming hash computation.
/// </summary>
public sealed class IncrementalHasher : IDisposable
{
    private readonly Blake3.Hasher _hasher;
    private bool _disposed;

    internal IncrementalHasher()
    {
        _hasher = Blake3.Hasher.New();
    }

    public void Update(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _hasher.Update(data);
    }

    public Hash256 Finalize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Span<byte> output = stackalloc byte[32];
        _hasher.Finalize(output);
        return new Hash256(output);
    }

    public void FinalizeInto(Span<byte> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _hasher.Finalize(output);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _hasher.Dispose();
        }
    }
}
