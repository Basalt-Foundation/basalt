using Basalt.Core;
using Basalt.Crypto;

namespace Basalt.Network;

/// <summary>
/// Unique identifier for a network peer. Derived from the peer's public key.
/// </summary>
public readonly struct PeerId : IEquatable<PeerId>, IComparable<PeerId>
{
    private readonly Hash256 _id;

    public PeerId(Hash256 id) => _id = id;

    /// <summary>
    /// Derive a peer ID from a public key using BLAKE3.
    /// </summary>
    public static PeerId FromPublicKey(PublicKey publicKey)
    {
        Span<byte> pubKeyBytes = stackalloc byte[PublicKey.Size];
        publicKey.WriteTo(pubKeyBytes);
        return new PeerId(Blake3Hasher.Hash(pubKeyBytes));
    }

    public Hash256 AsHash256() => _id;
    public string ToHexString() => _id.ToHexString();

    public bool Equals(PeerId other) => _id.Equals(other._id);
    public override bool Equals(object? obj) => obj is PeerId other && Equals(other);
    public override int GetHashCode() => _id.GetHashCode();
    public int CompareTo(PeerId other) => _id.CompareTo(other._id);

    public static bool operator ==(PeerId left, PeerId right) => left.Equals(right);
    public static bool operator !=(PeerId left, PeerId right) => !left.Equals(right);

    public override string ToString() => $"Peer({ToHexString()[..16]}...)";
}
