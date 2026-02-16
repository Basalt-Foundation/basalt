using System.Security.Cryptography;
using System.Text;

namespace Basalt.Sdk.Wallet.HdWallet;

/// <summary>
/// BIP-39 mnemonic phrase generation, validation, and seed derivation.
/// Implemented from scratch using System.Security.Cryptography — no external dependencies.
/// </summary>
public static class Mnemonic
{
    /// <summary>Number of PBKDF2 iterations for seed derivation (BIP-39 spec).</summary>
    private const int Pbkdf2Iterations = 2048;

    /// <summary>Seed output length in bytes.</summary>
    private const int SeedLength = 64;

    /// <summary>
    /// Generate a new BIP-39 mnemonic phrase.
    /// </summary>
    /// <param name="wordCount">Number of words (12 = 128-bit entropy, 24 = 256-bit entropy).</param>
    /// <returns>A space-separated mnemonic phrase.</returns>
    public static string Generate(int wordCount = 24)
    {
        var entropyBits = wordCount switch
        {
            12 => 128,
            15 => 160,
            18 => 192,
            21 => 224,
            24 => 256,
            _ => throw new ArgumentException($"Invalid word count: {wordCount}. Must be 12, 15, 18, 21, or 24.", nameof(wordCount)),
        };

        var entropyBytes = entropyBits / 8;
        var entropy = RandomNumberGenerator.GetBytes(entropyBytes);
        return EntropyToMnemonic(entropy);
    }

    /// <summary>
    /// Convert entropy bytes to a BIP-39 mnemonic phrase.
    /// </summary>
    /// <param name="entropy">Entropy bytes (16, 20, 24, 28, or 32 bytes).</param>
    /// <returns>A space-separated mnemonic phrase.</returns>
    public static string EntropyToMnemonic(ReadOnlySpan<byte> entropy)
    {
        var entropyBits = entropy.Length * 8;
        var checksumBits = entropyBits / 32;
        var totalBits = entropyBits + checksumBits;
        var wordCount = totalBits / 11;

        // Compute SHA-256 checksum
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(entropy, hash);

        // Combine entropy + checksum into a bit stream
        // We work with the raw bytes and extract 11-bit groups
        var combined = new byte[entropy.Length + 1];
        entropy.CopyTo(combined);
        combined[entropy.Length] = hash[0]; // Only first byte needed (max 8 checksum bits)

        var words = new string[wordCount];
        for (var i = 0; i < wordCount; i++)
        {
            var index = Extract11Bits(combined, i * 11);
            words[i] = Bip39Wordlist.English[index];
        }

        return string.Join(' ', words);
    }

    /// <summary>
    /// Validate a BIP-39 mnemonic phrase (word count + checksum).
    /// </summary>
    /// <param name="mnemonic">Space-separated mnemonic phrase.</param>
    /// <returns>True if the mnemonic is valid.</returns>
    public static bool Validate(string mnemonic)
    {
        if (string.IsNullOrWhiteSpace(mnemonic))
            return false;

        var words = mnemonic.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is not (12 or 15 or 18 or 21 or 24))
            return false;

        // Check all words are in the wordlist and get their indices
        var indices = new int[words.Length];
        for (var i = 0; i < words.Length; i++)
        {
            var idx = Bip39Wordlist.IndexOf(words[i]);
            if (idx < 0)
                return false;
            indices[i] = idx;
        }

        // Reconstruct entropy + checksum bits
        var totalBits = words.Length * 11;
        var checksumBits = words.Length / 3; // CS = ENT / 32, and ENT = (words * 11) - CS
        var entropyBits = totalBits - checksumBits;
        var entropyBytes = entropyBits / 8;

        // Pack 11-bit indices into byte array
        var allBytes = new byte[(totalBits + 7) / 8];
        for (var i = 0; i < words.Length; i++)
        {
            var value = indices[i];
            var bitPos = i * 11;
            Write11Bits(allBytes, bitPos, value);
        }

        // Extract entropy
        var entropy = allBytes[..entropyBytes];

        // Compute expected checksum
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(entropy, hash);

        // Compare checksum bits
        var expectedChecksum = hash[0] >> (8 - checksumBits);
        var actualChecksum = allBytes[entropyBytes] >> (8 - checksumBits);

        return expectedChecksum == actualChecksum;
    }

    /// <summary>
    /// Derive a 64-byte seed from a BIP-39 mnemonic using PBKDF2-HMAC-SHA512.
    /// </summary>
    /// <param name="mnemonic">Space-separated mnemonic phrase.</param>
    /// <param name="passphrase">Optional passphrase (default: empty).</param>
    /// <returns>64-byte seed suitable for HD key derivation.</returns>
    public static byte[] ToSeed(string mnemonic, string passphrase = "")
    {
        var password = Encoding.UTF8.GetBytes(mnemonic.Normalize(NormalizationForm.FormKD));
        var salt = Encoding.UTF8.GetBytes("mnemonic" + passphrase.Normalize(NormalizationForm.FormKD));

        return Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA512, SeedLength);
    }

    /// <summary>
    /// Extract 11 bits from a byte array at the given bit offset.
    /// </summary>
    private static int Extract11Bits(ReadOnlySpan<byte> data, int bitOffset)
    {
        var byteIndex = bitOffset / 8;
        var bitIndex = bitOffset % 8;

        // Read up to 3 bytes to cover the 11-bit span
        var value = data[byteIndex] << 16;
        if (byteIndex + 1 < data.Length)
            value |= data[byteIndex + 1] << 8;
        if (byteIndex + 2 < data.Length)
            value |= data[byteIndex + 2];

        // Shift right to align the 11 bits and mask
        var shift = 24 - 11 - bitIndex;
        return (value >> shift) & 0x7FF;
    }

    /// <summary>
    /// Write 11 bits into a byte array at the given bit offset.
    /// </summary>
    private static void Write11Bits(Span<byte> data, int bitOffset, int value)
    {
        var byteIndex = bitOffset / 8;
        var bitIndex = bitOffset % 8;

        // We need to write 11 bits starting at bitIndex within byteIndex
        // This can span up to 3 bytes
        var shift = 24 - 11 - bitIndex;
        var masked = (value & 0x7FF) << shift;

        data[byteIndex] |= (byte)(masked >> 16);
        if (byteIndex + 1 < data.Length)
            data[byteIndex + 1] |= (byte)(masked >> 8);
        if (byteIndex + 2 < data.Length)
            data[byteIndex + 2] |= (byte)masked;
    }
}
