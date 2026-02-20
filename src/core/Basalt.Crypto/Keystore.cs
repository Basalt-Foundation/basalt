using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Konscious.Security.Cryptography;

namespace Basalt.Crypto;

/// <summary>
/// AES-256-GCM encrypted keystore with Argon2id key derivation.
/// Protects Ed25519 private keys at rest.
/// </summary>
public static class Keystore
{
    private const int SaltSize = 32;
    private const int NonceSize = 12; // AES-GCM standard nonce
    private const int TagSize = 16; // AES-GCM tag
    private const int KeySize = 32; // AES-256

    // Argon2id parameters (OWASP recommended)
    private const int Argon2Iterations = 3;
    private const int Argon2MemoryKB = 65536; // 64 MB
    private const int Argon2Parallelism = 4;

    /// <summary>
    /// Encrypt a private key with a password.
    /// </summary>
    public static KeystoreFile Encrypt(ReadOnlySpan<byte> privateKey, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);

        var derivedKey = DeriveKey(password, salt);
        try
        {
            var ciphertext = new byte[privateKey.Length];
            var tag = new byte[TagSize];

            using var aes = new AesGcm(derivedKey, TagSize);
            aes.Encrypt(nonce, privateKey, ciphertext, tag);

            return new KeystoreFile
            {
                Version = 1,
                Crypto = new KeystoreCrypto
                {
                    Cipher = "aes-256-gcm",
                    CipherText = Convert.ToHexString(ciphertext).ToLowerInvariant(),
                    Nonce = Convert.ToHexString(nonce).ToLowerInvariant(),
                    Tag = Convert.ToHexString(tag).ToLowerInvariant(),
                    Kdf = "argon2id",
                    KdfParams = new KdfParams
                    {
                        Salt = Convert.ToHexString(salt).ToLowerInvariant(),
                        Iterations = Argon2Iterations,
                        MemoryKB = Argon2MemoryKB,
                        Parallelism = Argon2Parallelism,
                    },
                },
            };
        }
        finally
        {
            // CORE-07: Zero derived key material after use
            CryptographicOperations.ZeroMemory(derivedKey);
        }
    }

    /// <summary>
    /// Decrypt a private key from a keystore file.
    /// </summary>
    public static byte[] Decrypt(KeystoreFile keystore, string password)
    {
        var crypto = keystore.Crypto;
        var salt = Convert.FromHexString(crypto.KdfParams.Salt);
        var nonce = Convert.FromHexString(crypto.Nonce);
        var tag = Convert.FromHexString(crypto.Tag);
        var ciphertext = Convert.FromHexString(crypto.CipherText);

        var derivedKey = DeriveKey(password, salt,
            crypto.KdfParams.Iterations,
            crypto.KdfParams.MemoryKB,
            crypto.KdfParams.Parallelism);

        try
        {
            var plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(derivedKey, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            return plaintext;
        }
        finally
        {
            // CORE-07: Zero derived key material after use
            CryptographicOperations.ZeroMemory(derivedKey);
        }
    }

    /// <summary>
    /// Serialize keystore to JSON string.
    /// </summary>
    public static string ToJson(KeystoreFile keystore) =>
        JsonSerializer.Serialize(keystore, KeystoreJsonContext.Default.KeystoreFile);

    /// <summary>
    /// Deserialize keystore from JSON string.
    /// </summary>
    public static KeystoreFile? FromJson(string json) =>
        JsonSerializer.Deserialize(json, KeystoreJsonContext.Default.KeystoreFile);

    private static byte[] DeriveKey(string password, byte[] salt,
        int iterations = Argon2Iterations,
        int memoryKB = Argon2MemoryKB,
        int parallelism = Argon2Parallelism)
    {
        var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = parallelism,
            MemorySize = memoryKB,
            Iterations = iterations,
        };

        return argon2.GetBytes(KeySize);
    }
}

public sealed class KeystoreFile
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("crypto")]
    public required KeystoreCrypto Crypto { get; set; }
}

public sealed class KeystoreCrypto
{
    [JsonPropertyName("cipher")]
    public required string Cipher { get; set; }

    [JsonPropertyName("ciphertext")]
    public required string CipherText { get; set; }

    [JsonPropertyName("nonce")]
    public required string Nonce { get; set; }

    [JsonPropertyName("tag")]
    public required string Tag { get; set; }

    [JsonPropertyName("kdf")]
    public required string Kdf { get; set; }

    [JsonPropertyName("kdfparams")]
    public required KdfParams KdfParams { get; set; }
}

public sealed class KdfParams
{
    [JsonPropertyName("salt")]
    public required string Salt { get; set; }

    [JsonPropertyName("iterations")]
    public int Iterations { get; set; }

    [JsonPropertyName("memory_kb")]
    public int MemoryKB { get; set; }

    [JsonPropertyName("parallelism")]
    public int Parallelism { get; set; }
}

[JsonSerializable(typeof(KeystoreFile))]
internal partial class KeystoreJsonContext : JsonSerializerContext;
