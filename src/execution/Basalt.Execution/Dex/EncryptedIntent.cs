using System.Security.Cryptography;
using Basalt.Core;
using Basalt.Crypto;

namespace Basalt.Execution.Dex;

/// <summary>
/// An encrypted swap intent submitted via <see cref="TransactionType.DexEncryptedSwapIntent"/>.
/// The intent payload is encrypted so the block proposer cannot see it before settlement.
///
/// <para><b>Encryption scheme: EC-ElGamal in G1 + AES-256-GCM</b>
/// Provides IND-CCA2 security with threshold decryption.</para>
///
/// <para><b>Encryption:</b>
/// 1. Generate random scalar r
/// 2. Compute ephemeral public key C1 = r * G1 (48 bytes compressed)
/// 3. Compute shared point SharedPoint = r * GPK (EC-ElGamal shared secret)
/// 4. Derive symmetric key symKey = BLAKE3("basalt-ecies-v1" || SharedPoint) (32 bytes)
/// 5. Encrypt payload with AES-256-GCM: (nonce, ciphertext, tag)</para>
///
/// <para><b>Decryption</b> (requires group secret key s):
/// 1. Compute SharedPoint = s * C1 (since s * r * G1 = r * s * G1 = r * GPK)
/// 2. Derive same symmetric key
/// 3. Decrypt + authenticate with AES-256-GCM</para>
///
/// <para><b>Transaction data format:</b>
/// <c>[8B epoch][48B C1][12B GCM_nonce][encrypted_payload][16B GCM_tag]</c>
/// where encrypted_payload is a standard swap intent (114 bytes).</para>
/// </summary>
public readonly struct EncryptedIntent
{
    /// <summary>GCM nonce size in bytes.</summary>
    public const int GcmNonceSize = 12;

    /// <summary>GCM authentication tag size in bytes.</summary>
    public const int GcmTagSize = 16;

    /// <summary>Ephemeral G1 point size (compressed).</summary>
    public const int EphemeralKeySize = 48;

    /// <summary>
    /// Expected minimum transaction data length:
    /// 8 (epoch) + 48 (C1) + 12 (GCM nonce) + 114 (intent payload) + 16 (GCM tag) = 198.
    /// </summary>
    public const int MinDataLength = 198;

    /// <summary>Header size before the ciphertext: 8 + 48 + 12 = 68.</summary>
    private const int HeaderSize = 8 + EphemeralKeySize + GcmNonceSize;

    /// <summary>The DKG epoch this intent was encrypted for.</summary>
    public ulong EpochNumber { get; init; }

    /// <summary>The ephemeral G1 public key C1 = r * G1 (48 bytes compressed).</summary>
    public byte[] EphemeralKey { get; init; }

    /// <summary>The AES-256-GCM nonce (12 bytes).</summary>
    public byte[] GcmNonce { get; init; }

    /// <summary>The encrypted intent payload.</summary>
    public byte[] Ciphertext { get; init; }

    /// <summary>The AES-256-GCM authentication tag (16 bytes).</summary>
    public byte[] GcmTag { get; init; }

    /// <summary>The sender address (from the transaction).</summary>
    public Address Sender { get; init; }

    /// <summary>The original transaction hash.</summary>
    public Hash256 TxHash { get; init; }

    /// <summary>The original transaction.</summary>
    public Transaction OriginalTx { get; init; }

    /// <summary>
    /// Encrypt a plaintext swap intent for a specific DKG epoch using EC-ElGamal + AES-256-GCM.
    /// </summary>
    /// <param name="intentPayload">The raw intent bytes (114 bytes).</param>
    /// <param name="groupPubKey">The DKG group public key (48-byte compressed G1 point).</param>
    /// <param name="epochNumber">The DKG epoch number.</param>
    /// <returns>Transaction data bytes suitable for a DexEncryptedSwapIntent transaction.</returns>
    public static byte[] Encrypt(ReadOnlySpan<byte> intentPayload, BlsPublicKey groupPubKey, ulong epochNumber)
    {
        // Generate random scalar r
        var rBytes = new byte[32];
        RandomNumberGenerator.Fill(rBytes);
        rBytes[0] &= 0x3F; // Ensure scalar < field order
        if (rBytes[0] == 0 && rBytes[1] == 0) rBytes[1] = 1; // Avoid zero scalar

        return EncryptWithScalar(intentPayload, groupPubKey, epochNumber, rBytes);
    }

    /// <summary>
    /// L-16: Internal visibility — only used for deterministic testing.
    /// </summary>
    internal static byte[] EncryptWithScalar(ReadOnlySpan<byte> intentPayload, BlsPublicKey groupPubKey, ulong epochNumber, byte[] rScalar)
    {
        byte[]? sharedPoint = null;
        byte[]? symKey = null;
        try
        {
            // C1 = r * G1 (ephemeral public key)
            var g1Gen = BlsCrypto.G1Generator();
            var c1 = BlsCrypto.ScalarMultG1(g1Gen, rScalar);

            // SharedPoint = r * GPK
            Span<byte> gpkBytes = stackalloc byte[BlsPublicKey.Size];
            groupPubKey.WriteTo(gpkBytes);
            sharedPoint = BlsCrypto.ScalarMultG1(gpkBytes, rScalar);

            // Derive AES key
            symKey = DeriveSymmetricKey(sharedPoint);

            // Generate random GCM nonce
            var gcmNonce = new byte[GcmNonceSize];
            RandomNumberGenerator.Fill(gcmNonce);

            // Encrypt with AES-256-GCM
            var ciphertext = new byte[intentPayload.Length];
            var tag = new byte[GcmTagSize];

            using var aes = new AesGcm(symKey, GcmTagSize);
            aes.Encrypt(gcmNonce, intentPayload, ciphertext, tag);

            // Build transaction data: [8B epoch][48B C1][12B nonce][ciphertext][16B tag]
            var data = new byte[8 + EphemeralKeySize + GcmNonceSize + ciphertext.Length + GcmTagSize];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(0, 8), epochNumber);
            c1.CopyTo(data.AsSpan(8));
            gcmNonce.CopyTo(data.AsSpan(8 + EphemeralKeySize));
            ciphertext.CopyTo(data.AsSpan(HeaderSize));
            tag.CopyTo(data.AsSpan(HeaderSize + ciphertext.Length));

            return data;
        }
        finally
        {
            // L-12: Zero all sensitive material
            if (symKey != null) CryptographicOperations.ZeroMemory(symKey);
            if (sharedPoint != null) CryptographicOperations.ZeroMemory(sharedPoint);
            CryptographicOperations.ZeroMemory(rScalar);
        }
    }

    /// <summary>
    /// Parse an encrypted intent from a transaction.
    /// </summary>
    public static EncryptedIntent? Parse(Transaction tx)
    {
        if (tx.Data.Length < MinDataLength)
            return null;

        var epoch = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(tx.Data.AsSpan(0, 8));
        var ephemeralKey = tx.Data[8..(8 + EphemeralKeySize)];
        var gcmNonce = tx.Data[(8 + EphemeralKeySize)..HeaderSize];
        var ciphertextLen = tx.Data.Length - HeaderSize - GcmTagSize;
        if (ciphertextLen < 114) return null; // Too short for a swap intent

        var ciphertext = tx.Data[HeaderSize..(HeaderSize + ciphertextLen)];
        var gcmTag = tx.Data[(HeaderSize + ciphertextLen)..];

        return new EncryptedIntent
        {
            EpochNumber = epoch,
            EphemeralKey = ephemeralKey,
            GcmNonce = gcmNonce,
            Ciphertext = ciphertext,
            GcmTag = gcmTag,
            Sender = tx.Sender,
            TxHash = tx.Hash,
            OriginalTx = tx,
        };
    }

    /// <summary>
    /// Decrypt an encrypted intent using the reconstructed DKG group secret key,
    /// with optional epoch validation.
    /// </summary>
    /// <param name="groupSecretKey">The DKG group secret key (32-byte BLS scalar, big-endian).</param>
    /// <param name="expectedEpoch">The expected DKG epoch. If > 0 and doesn't match, returns null.</param>
    /// <returns>A parsed intent, or null if epoch mismatch or decryption/authentication fails.</returns>
    public ParsedIntent? Decrypt(byte[] groupSecretKey, ulong expectedEpoch)
    {
        if (expectedEpoch > 0 && EpochNumber != expectedEpoch)
            return null;
        return Decrypt(groupSecretKey);
    }

    /// <summary>
    /// Decrypt an encrypted intent using the reconstructed DKG group secret key.
    /// This requires the group secret (threshold-reconstructed from validator shares),
    /// NOT the group public key — providing real threshold security.
    /// </summary>
    /// <param name="groupSecretKey">The DKG group secret key (32-byte BLS scalar, big-endian).</param>
    /// <returns>A parsed intent, or null if decryption/authentication fails.</returns>
    public ParsedIntent? Decrypt(byte[] groupSecretKey)
    {
        byte[]? sharedPoint = null;
        byte[]? symKey = null;
        try
        {
            // SharedPoint = s * C1 = s * r * G1 = r * GPK
            sharedPoint = BlsCrypto.ScalarMultG1(EphemeralKey, groupSecretKey);

            // Derive AES key
            symKey = DeriveSymmetricKey(sharedPoint);

            // Decrypt with AES-256-GCM (authenticates ciphertext + tag)
            var plaintext = new byte[Ciphertext.Length];
            using var aes = new AesGcm(symKey, GcmTagSize);
            aes.Decrypt(GcmNonce, Ciphertext, GcmTag, plaintext);

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
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            // L-12: Zero all sensitive material
            if (symKey != null) CryptographicOperations.ZeroMemory(symKey);
            if (sharedPoint != null) CryptographicOperations.ZeroMemory(sharedPoint);
        }
    }

    /// <summary>
    /// Derive the AES-256 symmetric key from the EC-ElGamal shared point.
    /// </summary>
    private static byte[] DeriveSymmetricKey(byte[] sharedPoint)
    {
        Span<byte> input = stackalloc byte[16 + BlsCrypto.G1CompressedSize];
        "basalt-ecies-v1\0"u8.CopyTo(input);
        sharedPoint.AsSpan().CopyTo(input[16..]);
        var hash = Blake3Hasher.Hash(input);
        return hash.ToArray();
    }
}
