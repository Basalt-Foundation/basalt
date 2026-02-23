using System.Numerics;
using Basalt.Core;
using Basalt.Crypto;

namespace Basalt.Execution.Dex;

/// <summary>
/// An encrypted swap intent submitted via <see cref="TransactionType.DexEncryptedSwapIntent"/>.
/// The intent payload is encrypted so the block proposer cannot see it before settlement.
///
/// <para><b>Encryption scheme (simplified prototype):</b>
/// The intent is XOR-encrypted with a key derived from the DKG group public key and a
/// random nonce. Production deployment should replace this with BLS threshold IBE
/// (e.g. Boneh-Franklin scheme) or ECIES when BLS point arithmetic is exposed by the
/// underlying library.</para>
///
/// <para><b>Transaction data format:</b>
/// <c>[8B epoch][32B nonce][encrypted_payload (114B)]</c>
/// where encrypted_payload is a standard swap intent (version + tokenIn + tokenOut + amounts + deadline + flags)
/// encrypted with BLAKE3("basalt-intent-v1" || groupPubKey || nonce).</para>
/// </summary>
public readonly struct EncryptedIntent
{
    /// <summary>Expected minimum transaction data length: 8 (epoch) + 32 (nonce) + 114 (intent).</summary>
    public const int MinDataLength = 154;

    /// <summary>The DKG epoch this intent was encrypted for.</summary>
    public ulong EpochNumber { get; init; }

    /// <summary>Random nonce used for encryption key derivation (32 bytes).</summary>
    public byte[] Nonce { get; init; }

    /// <summary>The encrypted intent payload.</summary>
    public byte[] Ciphertext { get; init; }

    /// <summary>The sender address (from the transaction).</summary>
    public Address Sender { get; init; }

    /// <summary>The original transaction hash.</summary>
    public Hash256 TxHash { get; init; }

    /// <summary>The original transaction.</summary>
    public Transaction OriginalTx { get; init; }

    /// <summary>
    /// Encrypt a plaintext swap intent for a specific DKG epoch.
    /// </summary>
    /// <param name="intentPayload">The raw intent bytes (114 bytes: version + tokenIn + tokenOut + amounts + deadline + flags).</param>
    /// <param name="groupPubKey">The DKG group public key for the target epoch.</param>
    /// <param name="epochNumber">The DKG epoch number.</param>
    /// <returns>Transaction data bytes suitable for a DexEncryptedSwapIntent transaction.</returns>
    public static byte[] Encrypt(ReadOnlySpan<byte> intentPayload, BlsPublicKey groupPubKey, ulong epochNumber)
    {
        var nonce = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);
        return EncryptWithNonce(intentPayload, groupPubKey, epochNumber, nonce);
    }

    /// <summary>
    /// Encrypt with a specific nonce (for deterministic testing).
    /// </summary>
    public static byte[] EncryptWithNonce(ReadOnlySpan<byte> intentPayload, BlsPublicKey groupPubKey, ulong epochNumber, byte[] nonce)
    {
        var key = DeriveKey(groupPubKey, nonce);
        var ciphertext = new byte[intentPayload.Length];
        for (int i = 0; i < intentPayload.Length; i++)
            ciphertext[i] = (byte)(intentPayload[i] ^ key[i % 32]);

        // Build transaction data: [8B epoch][32B nonce][ciphertext]
        var data = new byte[8 + 32 + ciphertext.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(0, 8), epochNumber);
        nonce.CopyTo(data.AsSpan(8));
        ciphertext.CopyTo(data.AsSpan(40));
        return data;
    }

    /// <summary>
    /// Parse an encrypted intent from a transaction.
    /// </summary>
    public static EncryptedIntent? Parse(Transaction tx)
    {
        if (tx.Data.Length < MinDataLength)
            return null;

        var epoch = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(tx.Data.AsSpan(0, 8));
        var nonce = tx.Data[8..40];
        var ciphertext = tx.Data[40..];

        return new EncryptedIntent
        {
            EpochNumber = epoch,
            Nonce = nonce,
            Ciphertext = ciphertext,
            Sender = tx.Sender,
            TxHash = tx.Hash,
            OriginalTx = tx,
        };
    }

    /// <summary>
    /// Decrypt an encrypted intent using the DKG group public key.
    /// The group public key is derived from the reconstructed group secret.
    /// </summary>
    /// <param name="groupPubKey">The DKG group public key for the encrypted epoch.</param>
    /// <returns>A parsed intent, or null if decryption produces malformed data.</returns>
    public ParsedIntent? Decrypt(BlsPublicKey groupPubKey)
    {
        var key = DeriveKey(groupPubKey, Nonce);
        var plaintext = new byte[Ciphertext.Length];
        for (int i = 0; i < Ciphertext.Length; i++)
            plaintext[i] = (byte)(Ciphertext[i] ^ key[i % 32]);

        // Parse as a standard swap intent
        if (plaintext.Length < 114)
            return null;

        return new ParsedIntent
        {
            Sender = Sender,
            TokenIn = new Address(plaintext.AsSpan(1, 20)),
            TokenOut = new Address(plaintext.AsSpan(21, 20)),
            AmountIn = new UInt256(plaintext.AsSpan(41, 32)),
            MinAmountOut = new UInt256(plaintext.AsSpan(73, 32)),
            Deadline = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(plaintext.AsSpan(105, 8)),
            AllowPartialFill = (plaintext[113] & 0x01) != 0,
            TxHash = TxHash,
            OriginalTx = OriginalTx,
        };
    }

    /// <summary>
    /// Derive the encryption/decryption key from the group public key and nonce.
    /// </summary>
    private static byte[] DeriveKey(BlsPublicKey groupPubKey, byte[] nonce)
    {
        Span<byte> input = stackalloc byte[16 + BlsPublicKey.Size + 32];
        "basalt-intent-v1"u8.CopyTo(input);
        groupPubKey.WriteTo(input[16..]);
        nonce.CopyTo(input[(16 + BlsPublicKey.Size)..]);
        var hash = Blake3Hasher.Hash(input);
        return hash.ToArray();
    }
}
