using Basalt.Core;

namespace Basalt.Confidentiality.Channels;

/// <summary>
/// An encrypted message sent over a private channel.
/// </summary>
public sealed class ChannelMessage
{
    /// <summary>Identifier of the channel this message belongs to.</summary>
    public required Hash256 ChannelId { get; init; }

    /// <summary>
    /// Monotonically increasing sequence number used as the encryption nonce.
    /// Prevents replay attacks.
    /// </summary>
    public required ulong Nonce { get; init; }

    /// <summary>
    /// The encrypted payload (ciphertext + AES-GCM auth tag).
    /// </summary>
    public required byte[] EncryptedPayload { get; init; }

    /// <summary>
    /// Ed25519 signature over (ChannelId || Nonce || EncryptedPayload)
    /// by the sender, proving message authenticity.
    /// </summary>
    public required Signature SenderSignature { get; init; }
}
