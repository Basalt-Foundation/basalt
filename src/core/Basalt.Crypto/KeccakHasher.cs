using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Basalt.Core;

namespace Basalt.Crypto;

/// <summary>
/// Keccak-256 hash function used for address derivation.
/// Software implementation (no platform dependency on SHA3 support).
/// </summary>
public static class KeccakHasher
{
    /// <summary>
    /// Compute Keccak-256 hash.
    /// </summary>
    public static byte[] Hash(ReadOnlySpan<byte> data)
    {
        var result = new byte[32];
        HashInto(data, result);
        return result;
    }

    /// <summary>
    /// Compute Keccak-256 hash into a destination span.
    /// </summary>
    public static void Hash(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        HashInto(data, destination);
    }

    /// <summary>
    /// Derive an Address from a public key.
    /// Takes the Keccak-256 hash of the public key and uses the last 20 bytes.
    /// </summary>
    public static Address DeriveAddress(PublicKey publicKey)
    {
        Span<byte> pubKeyBytes = stackalloc byte[PublicKey.Size];
        publicKey.WriteTo(pubKeyBytes);

        Span<byte> hash = stackalloc byte[32];
        HashInto(pubKeyBytes, hash);

        return new Address(hash[12..]);
    }

    /// <summary>
    /// Derive an Address from raw public key bytes.
    /// </summary>
    public static Address DeriveAddress(ReadOnlySpan<byte> publicKeyBytes)
    {
        Span<byte> hash = stackalloc byte[32];
        HashInto(publicKeyBytes, hash);
        return new Address(hash[12..]);
    }

    private static void HashInto(ReadOnlySpan<byte> data, Span<byte> output)
    {
        // Keccak-256: rate=136, capacity=64, output=32
        const int Rate = 136;
        var state = new ulong[25];

        // Absorb
        int offset = 0;
        while (offset + Rate <= data.Length)
        {
            for (int i = 0; i < Rate / 8; i++)
            {
                state[i] ^= BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset + i * 8, 8));
            }
            KeccakF1600(state);
            offset += Rate;
        }

        // Absorb remaining + padding
        Span<byte> lastBlock = stackalloc byte[Rate];
        lastBlock.Clear();
        data[offset..].CopyTo(lastBlock);
        lastBlock[data.Length - offset] = 0x01; // Keccak padding (NOT SHA-3's 0x06)
        lastBlock[Rate - 1] |= 0x80;

        for (int i = 0; i < Rate / 8; i++)
        {
            state[i] ^= BinaryPrimitives.ReadUInt64LittleEndian(lastBlock.Slice(i * 8, 8));
        }
        KeccakF1600(state);

        // Squeeze
        for (int i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(output.Slice(i * 8, 8), state[i]);
        }

        // AUDIT L-10: Clear state to prevent leaking hash internals
        Array.Clear(state);
    }

    private static readonly ulong[] RoundConstants =
    [
        0x0000000000000001UL, 0x0000000000008082UL, 0x800000000000808AUL, 0x8000000080008000UL,
        0x000000000000808BUL, 0x0000000080000001UL, 0x8000000080008081UL, 0x8000000000008009UL,
        0x000000000000008AUL, 0x0000000000000088UL, 0x0000000080008009UL, 0x000000008000000AUL,
        0x000000008000808BUL, 0x800000000000008BUL, 0x8000000000008089UL, 0x8000000000008003UL,
        0x8000000000008002UL, 0x8000000000000080UL, 0x000000000000800AUL, 0x800000008000000AUL,
        0x8000000080008081UL, 0x8000000000008080UL, 0x0000000080000001UL, 0x8000000080008008UL,
    ];

    private static readonly int[] RotationOffsets =
    [
        0, 1, 62, 28, 27, 36, 44, 6, 55, 20, 3, 10, 43, 25, 39, 41, 45, 15, 21, 8, 18, 2, 61, 56, 14,
    ];

    private static readonly int[] PiLane =
    [
        0, 6, 12, 18, 24, 3, 9, 10, 16, 22, 1, 7, 13, 19, 20, 4, 5, 11, 17, 23, 2, 8, 14, 15, 21,
    ];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong RotL(ulong x, int n) => (x << n) | (x >> (64 - n));

    private static void KeccakF1600(ulong[] state)
    {
        Span<ulong> C = stackalloc ulong[5];
        Span<ulong> D = stackalloc ulong[5];
        Span<ulong> B = stackalloc ulong[25];

        for (int round = 0; round < 24; round++)
        {
            // θ (theta)
            for (int x = 0; x < 5; x++)
                C[x] = state[x] ^ state[x + 5] ^ state[x + 10] ^ state[x + 15] ^ state[x + 20];

            for (int x = 0; x < 5; x++)
                D[x] = C[(x + 4) % 5] ^ RotL(C[(x + 1) % 5], 1);

            for (int x = 0; x < 5; x++)
                for (int y = 0; y < 5; y++)
                    state[x + 5 * y] ^= D[x];

            // ρ (rho) and π (pi)
            for (int i = 0; i < 25; i++)
                B[i] = RotL(state[PiLane[i]], RotationOffsets[PiLane[i]]);

            // χ (chi)
            for (int y = 0; y < 5; y++)
                for (int x = 0; x < 5; x++)
                    state[x + 5 * y] = B[x + 5 * y] ^ (~B[(x + 1) % 5 + 5 * y] & B[(x + 2) % 5 + 5 * y]);

            // ι (iota)
            state[0] ^= RoundConstants[round];
        }
    }
}
