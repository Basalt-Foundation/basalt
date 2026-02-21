using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Basalt.Sdk.Wallet.HdWallet;

/// <summary>
/// SLIP-0010 hierarchical deterministic key derivation for Ed25519.
/// Derives Ed25519 private keys from a BIP-39 seed using HMAC-SHA512.
/// Only hardened derivation is supported (required by Ed25519 SLIP-0010).
/// </summary>
public static class HdKeyDerivation
{
    private static readonly byte[] MasterKeyLabel = "ed25519 seed"u8.ToArray();

    /// <summary>
    /// Derive a master key and chain code from a BIP-39 seed.
    /// </summary>
    /// <param name="seed">64-byte seed from BIP-39 mnemonic.</param>
    /// <returns>32-byte private key and 32-byte chain code.</returns>
    public static (byte[] Key, byte[] ChainCode) DeriveMasterKey(ReadOnlySpan<byte> seed)
    {
        Span<byte> hmac = stackalloc byte[64];
        HMACSHA512.HashData(MasterKeyLabel, seed, hmac);

        var key = hmac[..32].ToArray();
        var chainCode = hmac[32..].ToArray();
        return (key, chainCode);
    }

    /// <summary>
    /// Derive a child key from a parent key and chain code (hardened only).
    /// </summary>
    /// <param name="parentKey">32-byte parent private key.</param>
    /// <param name="parentChainCode">32-byte parent chain code.</param>
    /// <param name="index">Child index (must include hardened offset 0x80000000).</param>
    /// <returns>32-byte child private key and 32-byte child chain code.</returns>
    public static (byte[] Key, byte[] ChainCode) DeriveChild(
        ReadOnlySpan<byte> parentKey,
        ReadOnlySpan<byte> parentChainCode,
        uint index)
    {
        // SLIP-0010 Ed25519: data = 0x00 || parentKey (32 bytes) || index (4 bytes BE)
        Span<byte> data = stackalloc byte[1 + 32 + 4];
        data[0] = 0x00;
        parentKey.CopyTo(data[1..]);
        BinaryPrimitives.WriteUInt32BigEndian(data[33..], index);

        Span<byte> hmac = stackalloc byte[64];
        HMACSHA512.HashData(parentChainCode, data, hmac);

        var childKey = hmac[..32].ToArray();
        var childChainCode = hmac[32..].ToArray();
        return (childKey, childChainCode);
    }

    /// <summary>
    /// Derive a private key at the specified derivation path from a seed.
    /// </summary>
    /// <param name="seed">64-byte BIP-39 seed.</param>
    /// <param name="path">Derivation path (all levels must be hardened).</param>
    /// <returns>32-byte Ed25519 private key.</returns>
    /// <remarks>
    /// H-15: Intermediate key and chain code arrays are zeroed after each derivation step
    /// to minimize the window for memory forensics extraction.
    /// </remarks>
    public static byte[] DerivePath(ReadOnlySpan<byte> seed, DerivationPath path)
    {
        var (key, chainCode) = DeriveMasterKey(seed);

        foreach (var index in path.Indices)
        {
            var previousKey = key;
            var previousChainCode = chainCode;
            (key, chainCode) = DeriveChild(key, chainCode, index);
            // H-15: Zero previous intermediate key material
            CryptographicOperations.ZeroMemory(previousKey);
            CryptographicOperations.ZeroMemory(previousChainCode);
        }

        // Zero the final chain code — caller only needs the key
        CryptographicOperations.ZeroMemory(chainCode);

        return key;
    }
}
