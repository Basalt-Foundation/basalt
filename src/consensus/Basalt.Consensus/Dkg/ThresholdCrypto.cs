using System.Numerics;
using System.Security.Cryptography;
using Basalt.Core;
using Basalt.Crypto;
using AesGcm = System.Security.Cryptography.AesGcm;

namespace Basalt.Consensus.Dkg;

/// <summary>
/// Threshold cryptography primitives for Feldman VSS over BLS12-381.
/// Implements polynomial generation, share evaluation, share verification,
/// and Lagrange interpolation for threshold secret reconstruction.
/// </summary>
public static class ThresholdCrypto
{
    /// <summary>
    /// The BLS12-381 scalar field order (group order of G1/G2).
    /// All polynomial arithmetic is performed modulo this prime.
    /// </summary>
    public static readonly BigInteger ScalarFieldOrder = BigInteger.Parse(
        "73eda753299d7d483339d80809a1d80553bda402fffe5bfeffffffff00000001",
        System.Globalization.NumberStyles.HexNumber);

    /// <summary>
    /// Generate a random polynomial of degree t with coefficients in the BLS scalar field.
    /// The constant term (a_0) is the secret.
    /// </summary>
    /// <param name="threshold">Degree of the polynomial (t). The scheme requires t+1 shares to reconstruct.</param>
    /// <returns>Array of t+1 coefficients [a_0, a_1, ..., a_t].</returns>
    public static BigInteger[] GeneratePolynomial(int threshold)
    {
        var coefficients = new BigInteger[threshold + 1];
        for (int i = 0; i <= threshold; i++)
        {
            coefficients[i] = GenerateRandomScalar();
        }
        return coefficients;
    }

    /// <summary>
    /// Evaluate a polynomial at a given point modulo the scalar field order.
    /// f(x) = a_0 + a_1*x + a_2*x^2 + ... + a_t*x^t (mod p)
    /// </summary>
    /// <param name="coefficients">Polynomial coefficients [a_0, a_1, ..., a_t].</param>
    /// <param name="x">The evaluation point (typically the validator index + 1).</param>
    /// <returns>f(x) mod p.</returns>
    public static BigInteger EvaluatePolynomial(BigInteger[] coefficients, int x)
    {
        var result = BigInteger.Zero;
        var xBig = new BigInteger(x);
        var xPow = BigInteger.One;

        for (int i = 0; i < coefficients.Length; i++)
        {
            result = (result + coefficients[i] * xPow) % ScalarFieldOrder;
            xPow = xPow * xBig % ScalarFieldOrder;
        }

        // Ensure positive
        if (result < 0) result += ScalarFieldOrder;
        return result;
    }

    /// <summary>
    /// Compute Feldman commitments: C_j = a_j * G1 for each polynomial coefficient.
    /// M-01: Uses big-endian scalars with BlsCrypto.ScalarMultG1 for correct G1 point computation.
    /// </summary>
    /// <param name="coefficients">Polynomial coefficients.</param>
    /// <returns>Array of BLS public keys (G1 points) serving as commitments.</returns>
    public static BlsPublicKey[] ComputeCommitments(BigInteger[] coefficients)
    {
        var g1Gen = BlsCrypto.G1Generator();
        var commitments = new BlsPublicKey[coefficients.Length];
        for (int i = 0; i < coefficients.Length; i++)
        {
            var scalarBytesBE = ScalarToBytesBE(coefficients[i]);
            var point = BlsCrypto.ScalarMultG1(g1Gen, scalarBytesBE);
            commitments[i] = new BlsPublicKey(point);
        }
        return commitments;
    }

    /// <summary>
    /// Verify that a share is consistent with the Feldman commitment vector.
    /// C-01: Real Feldman VSS verification using G1 point arithmetic.
    /// Checks: share * G1 == sum(C_j * i^j) for j = 0..t
    /// </summary>
    /// <param name="share">The share value f(i).</param>
    /// <param name="validatorIndex">The validator index (1-based).</param>
    /// <param name="commitments">The Feldman commitment vector [C_0, C_1, ..., C_t].</param>
    /// <returns>True if the share is consistent with the commitments.</returns>
    public static bool VerifyShare(BigInteger share, int validatorIndex, BlsPublicKey[] commitments)
    {
        if (share <= 0 || share >= ScalarFieldOrder) return false;
        if (commitments.Length == 0) return false;

        // Compute share * G1 (the left side of the equation)
        var shareBytesBE = ScalarToBytesBE(share);
        var sharePoint = BlsCrypto.ScalarMultG1(BlsCrypto.G1Generator(), shareBytesBE);

        // Compute sum(C_j * i^j) for j = 0..t (the right side)
        // Start with C_0 * i^0 = C_0 * 1 = C_0
        byte[]? expectedPoint = null;
        var iPow = BigInteger.One; // i^j, starting with i^0 = 1
        var iBig = new BigInteger(validatorIndex);

        for (int j = 0; j < commitments.Length; j++)
        {
            // Compute C_j * i^j
            var scalarBE = ScalarToBytesBE(iPow);
            var term = BlsCrypto.ScalarMultG1(commitments[j].ToArray(), scalarBE);

            expectedPoint = expectedPoint == null ? term : BlsCrypto.AddG1(expectedPoint, term);

            iPow = iPow * iBig % ScalarFieldOrder;
            if (iPow < 0) iPow += ScalarFieldOrder;
        }

        if (expectedPoint == null) return false;

        // Compare the two G1 points
        return sharePoint.AsSpan().SequenceEqual(expectedPoint);
    }

    /// <summary>
    /// C-03: Encrypt a share for a specific recipient using ECDH + AES-256-GCM.
    /// Derives a shared secret via BLS scalar multiplication (ECDH in G1),
    /// then uses BLAKE3-derived key for AES-GCM authenticated encryption.
    /// </summary>
    /// <param name="share">The share to encrypt (as a BigInteger scalar).</param>
    /// <param name="senderPrivateKey">Sender's BLS private key (32 bytes, LE).</param>
    /// <param name="recipientPubKey">Recipient's BLS public key.</param>
    /// <returns>Encrypted share bytes: [12B nonce][32B ciphertext][16B tag] = 60 bytes.</returns>
    public static byte[] EncryptShare(BigInteger share, byte[] senderPrivateKey, BlsPublicKey recipientPubKey)
    {
        var symKey = DeriveEcdhKey(senderPrivateKey, recipientPubKey);
        try
        {
            var shareBytes = ScalarToBytes(share);
            var nonce = new byte[12];
            RandomNumberGenerator.Fill(nonce);

            var ciphertext = new byte[32];
            var tag = new byte[16];

            using var aes = new AesGcm(symKey, 16);
            aes.Encrypt(nonce, shareBytes, ciphertext, tag);

            // [12B nonce][32B ciphertext][16B tag]
            var result = new byte[60];
            Array.Copy(nonce, 0, result, 0, 12);
            Array.Copy(ciphertext, 0, result, 12, 32);
            Array.Copy(tag, 0, result, 44, 16);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(symKey);
        }
    }

    /// <summary>
    /// C-03: Legacy overload for backward compatibility (uses public keys only, XOR).
    /// Kept for tests that don't have access to private keys.
    /// </summary>
    [Obsolete("Use ECDH-based EncryptShare(share, senderPrivateKey, recipientPubKey) instead")]
    public static byte[] EncryptShare(BigInteger share, BlsPublicKey senderPubKey, BlsPublicKey recipientPubKey)
    {
        var key = DeriveSharedKey(senderPubKey, recipientPubKey);
        var shareBytes = ScalarToBytes(share);
        var encrypted = new byte[32];
        for (int i = 0; i < 32; i++)
            encrypted[i] = (byte)(shareBytes[i] ^ key[i]);
        return encrypted;
    }

    /// <summary>
    /// C-03: Decrypt a share using ECDH + AES-256-GCM.
    /// </summary>
    /// <param name="encrypted">Encrypted share: [12B nonce][32B ciphertext][16B tag] = 60 bytes.</param>
    /// <param name="recipientPrivateKey">Recipient's BLS private key (32 bytes, LE).</param>
    /// <param name="senderPubKey">Sender's BLS public key.</param>
    /// <returns>The decrypted share scalar.</returns>
    public static BigInteger DecryptShare(byte[] encrypted, byte[] recipientPrivateKey, BlsPublicKey senderPubKey)
    {
        if (encrypted.Length == 60)
        {
            // AES-GCM format: [12B nonce][32B ciphertext][16B tag]
            var symKey = DeriveEcdhKey(recipientPrivateKey, senderPubKey);
            try
            {
                var nonce = encrypted.AsSpan(0, 12);
                var ciphertext = encrypted.AsSpan(12, 32);
                var tag = encrypted.AsSpan(44, 16);

                var shareBytes = new byte[32];
                using var aes = new AesGcm(symKey, 16);
                aes.Decrypt(nonce, ciphertext, tag, shareBytes);

                var share = new BigInteger(shareBytes, isUnsigned: true, isBigEndian: false);
                return share % ScalarFieldOrder;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(symKey);
            }
        }

        // Legacy 32-byte XOR format
        return DecryptShare(encrypted, new BlsPublicKey(BlsSigner.GetPublicKeyStatic(recipientPrivateKey)),
            senderPubKey);
    }

    /// <summary>
    /// Legacy overload for backward compatibility (uses public keys only, XOR).
    /// </summary>
    public static BigInteger DecryptShare(byte[] encrypted, BlsPublicKey senderPubKey, BlsPublicKey recipientPubKey)
    {
        var key = DeriveSharedKey(senderPubKey, recipientPubKey);
        var shareBytes = new byte[32];
        for (int i = 0; i < 32; i++)
            shareBytes[i] = (byte)(encrypted[i] ^ key[i]);
        var share = new BigInteger(shareBytes, isUnsigned: true, isBigEndian: false);
        return share % ScalarFieldOrder;
    }

    /// <summary>
    /// Compute the Lagrange coefficient for participant i among a set of participants.
    /// lambda_i = product((x_j) / (x_j - x_i)) for j != i, where x_j = participant index.
    /// Used for threshold secret reconstruction.
    /// </summary>
    /// <param name="participantIndex">The participant's 1-based index (i).</param>
    /// <param name="allIndices">All participating 1-based indices.</param>
    /// <returns>The Lagrange coefficient modulo the scalar field order.</returns>
    public static BigInteger LagrangeCoefficient(int participantIndex, int[] allIndices)
    {
        var xi = new BigInteger(participantIndex);
        var num = BigInteger.One;
        var den = BigInteger.One;

        foreach (var j in allIndices)
        {
            if (j == participantIndex) continue;
            var xj = new BigInteger(j);
            num = num * xj % ScalarFieldOrder;
            var diff = (xj - xi) % ScalarFieldOrder;
            if (diff < 0) diff += ScalarFieldOrder;
            den = den * diff % ScalarFieldOrder;
        }

        // Compute modular inverse of denominator using Fermat's little theorem
        var denInv = BigInteger.ModPow(den, ScalarFieldOrder - 2, ScalarFieldOrder);
        return num * denInv % ScalarFieldOrder;
    }

    /// <summary>
    /// Reconstruct the secret from a threshold number of shares using Lagrange interpolation.
    /// secret = sum(share_i * lambda_i) mod p
    /// </summary>
    /// <param name="shares">Pairs of (1-based index, share value).</param>
    /// <returns>The reconstructed secret.</returns>
    public static BigInteger ReconstructSecret(IReadOnlyList<(int Index, BigInteger Share)> shares)
    {
        var indices = shares.Select(s => s.Index).ToArray();
        var secret = BigInteger.Zero;

        foreach (var (index, share) in shares)
        {
            var lambda = LagrangeCoefficient(index, indices);
            secret = (secret + share * lambda) % ScalarFieldOrder;
        }

        if (secret < 0) secret += ScalarFieldOrder;
        return secret;
    }

    /// <summary>
    /// Generate a random scalar in the BLS12-381 scalar field [1, p-1].
    /// </summary>
    public static BigInteger GenerateRandomScalar()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        // Mask high byte to ensure it fits and reduce modulo field order
        bytes[31] &= 0x3F;
        var scalar = new BigInteger(bytes, isUnsigned: true, isBigEndian: false);
        scalar %= ScalarFieldOrder;
        if (scalar.IsZero) scalar = BigInteger.One;
        return scalar;
    }

    /// <summary>
    /// Convert a BigInteger scalar to a 32-byte little-endian representation.
    /// The scalar is already reduced mod the field order, so it's a valid BLS secret key.
    /// </summary>
    public static byte[] ScalarToBytes(BigInteger scalar)
    {
        scalar %= ScalarFieldOrder;
        if (scalar < 0) scalar += ScalarFieldOrder;
        if (scalar.IsZero) scalar = BigInteger.One;
        var bytes = scalar.ToByteArray(isUnsigned: true, isBigEndian: false);
        var padded = new byte[32];
        Array.Copy(bytes, padded, Math.Min(bytes.Length, 32));
        return padded;
    }

    /// <summary>
    /// M-01: Convert a BigInteger scalar to a 32-byte big-endian representation.
    /// This is what BlsCrypto.ScalarMultG1 expects for scalar arguments.
    /// </summary>
    public static byte[] ScalarToBytesBE(BigInteger scalar)
    {
        scalar %= ScalarFieldOrder;
        if (scalar < 0) scalar += ScalarFieldOrder;
        if (scalar.IsZero) scalar = BigInteger.One;
        var bytes = scalar.ToByteArray(isUnsigned: true, isBigEndian: true);
        var padded = new byte[32];
        // Right-align in 32-byte buffer (big-endian padding)
        Array.Copy(bytes, 0, padded, 32 - bytes.Length, Math.Min(bytes.Length, 32));
        return padded;
    }

    /// <summary>
    /// C-03: Derive symmetric key via ECDH in G1: BLAKE3("basalt-dkg-share-v1" || sk * recipientPK).
    /// </summary>
    private static byte[] DeriveEcdhKey(byte[] privateKey, BlsPublicKey recipientPubKey)
    {
        // Convert LE private key to BE for ScalarMultG1
        var skBE = new byte[32];
        for (int i = 0; i < 32; i++)
            skBE[i] = privateKey[31 - i];

        var sharedPoint = BlsCrypto.ScalarMultG1(recipientPubKey.ToArray(), skBE);
        try
        {
            var prefix = System.Text.Encoding.UTF8.GetBytes("basalt-dkg-share-v1");
            var input = new byte[prefix.Length + sharedPoint.Length];
            Array.Copy(prefix, input, prefix.Length);
            Array.Copy(sharedPoint, 0, input, prefix.Length, sharedPoint.Length);
            var hash = Blake3Hasher.Hash(input);
            return hash.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedPoint);
            CryptographicOperations.ZeroMemory(skBE);
        }
    }

    private static byte[] DeriveSharedKey(BlsPublicKey a, BlsPublicKey b)
    {
        Span<byte> input = stackalloc byte[BlsPublicKey.Size * 2];
        a.WriteTo(input[..BlsPublicKey.Size]);
        b.WriteTo(input[BlsPublicKey.Size..]);
        var hash = Blake3Hasher.Hash(input);
        return hash.ToArray();
    }
}
