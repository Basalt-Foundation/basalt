using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Basalt.Network.Transport;

/// <summary>
/// NET-C02: Per-connection AES-256-GCM encryption for post-handshake transport security.
/// Derives directional send/receive keys via HKDF-SHA256 from the X25519 shared secret.
/// Nonces are monotonically increasing counters transmitted explicitly for anti-replay.
/// </summary>
public sealed class TransportEncryption : IDisposable
{
    /// <summary>AES-GCM nonce size in bytes.</summary>
    public const int NonceSize = 12;

    /// <summary>AES-GCM authentication tag size in bytes.</summary>
    public const int TagSize = 16;

    /// <summary>Overhead added to each encrypted frame: nonce + tag.</summary>
    public const int Overhead = NonceSize + TagSize;

    private static readonly byte[] InitiatorInfo = "basalt-transport-v1-init"u8.ToArray();
    private static readonly byte[] ResponderInfo = "basalt-transport-v1-resp"u8.ToArray();

    private readonly AesGcm _sendCipher;
    private readonly AesGcm _recvCipher;
    private ulong _sendNonce;
    private ulong _recvNonce;
    private int _disposed;

    /// <summary>
    /// Create a new transport encryption instance from a shared secret and role.
    /// </summary>
    /// <param name="sharedSecret">32-byte shared secret from X25519 DH + HKDF.</param>
    /// <param name="isInitiator">True if this side initiated the connection (sent Hello first).</param>
    public TransportEncryption(byte[] sharedSecret, bool isInitiator)
    {
        if (sharedSecret.Length != 32)
            throw new ArgumentException("Shared secret must be 32 bytes.", nameof(sharedSecret));

        // Derive two directional 32-byte keys from the shared secret
        var initKey = new byte[32];
        var respKey = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, initKey, ReadOnlySpan<byte>.Empty, InitiatorInfo);
        HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, respKey, ReadOnlySpan<byte>.Empty, ResponderInfo);

        byte[] sendKeyBytes, recvKeyBytes;
        if (isInitiator)
        {
            sendKeyBytes = initKey;
            recvKeyBytes = respKey;
        }
        else
        {
            sendKeyBytes = respKey;
            recvKeyBytes = initKey;
        }

        _sendCipher = new AesGcm(sendKeyBytes, TagSize);
        _recvCipher = new AesGcm(recvKeyBytes, TagSize);

        // Zero key material after creating ciphers
        CryptographicOperations.ZeroMemory(sendKeyBytes);
        CryptographicOperations.ZeroMemory(recvKeyBytes);
        // Zero whichever wasn't used as send/recv (the other was already zeroed above)
        if (isInitiator)
            CryptographicOperations.ZeroMemory(respKey);
        else
            CryptographicOperations.ZeroMemory(initKey);
    }

    /// <summary>
    /// Encrypt plaintext for sending to the peer.
    /// Returns [12-byte nonce || ciphertext || 16-byte GCM tag].
    /// </summary>
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // Increment nonce (pre-increment so first nonce is 1)
        var nonceCounter = Interlocked.Increment(ref _sendNonce);

        // Build 12-byte nonce: 4 zero bytes + 8-byte counter (big-endian)
        Span<byte> nonce = stackalloc byte[NonceSize];
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], nonceCounter);

        // Output: [nonce || ciphertext || tag]
        var result = new byte[NonceSize + plaintext.Length + TagSize];
        nonce.CopyTo(result);

        var ciphertext = result.AsSpan(NonceSize, plaintext.Length);
        var tag = result.AsSpan(NonceSize + plaintext.Length, TagSize);

        _sendCipher.Encrypt(nonce, plaintext, ciphertext, tag);

        return result;
    }

    /// <summary>
    /// Decrypt an encrypted envelope received from the peer.
    /// Input format: [12-byte nonce || ciphertext || 16-byte GCM tag].
    /// Validates nonce monotonicity for anti-replay protection.
    /// </summary>
    public byte[] Decrypt(ReadOnlySpan<byte> envelope)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (envelope.Length < Overhead)
            throw new CryptographicException("Encrypted envelope too short.");

        var nonce = envelope[..NonceSize];
        var ciphertextLength = envelope.Length - Overhead;
        var ciphertext = envelope.Slice(NonceSize, ciphertextLength);
        var tag = envelope.Slice(NonceSize + ciphertextLength, TagSize);

        // Extract counter from nonce (bytes 4-11, big-endian)
        var receivedCounter = BinaryPrimitives.ReadUInt64BigEndian(nonce[4..]);

        // Anti-replay: nonce must be strictly monotonically increasing
        var expectedMin = Volatile.Read(ref _recvNonce) + 1;
        if (receivedCounter < expectedMin)
            throw new CryptographicException(
                $"Nonce replay detected: received {receivedCounter}, expected >= {expectedMin}.");

        var plaintext = new byte[ciphertextLength];
        _recvCipher.Decrypt(nonce, ciphertext, tag, plaintext);

        // Update receive nonce after successful decryption
        Volatile.Write(ref _recvNonce, receivedCounter);

        return plaintext;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _sendCipher.Dispose();
        _recvCipher.Dispose();
    }
}
