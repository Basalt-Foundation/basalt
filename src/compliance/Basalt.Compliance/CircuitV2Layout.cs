using System.Buffers.Binary;

namespace Basalt.Compliance;

/// <summary>
/// Public-input layout for compliance circuit v2, the request-bound flagship circuit. v2 keeps the five v1
/// indices (issuerRoot, nullifier, expiry, issuerTier, revocationRoot) at the same positions and appends
/// two request-binding inputs: <see cref="CidField"/> and <see cref="RecipientKeyHash"/>. The circuit folds
/// both into the nullifier (nullifier = MiMC(secret, cidField, recipientKeyHash)), so they are genuinely
/// constrained: a captured proof cannot be redirected to a different content id or recipient key without
/// changing the proven nullifier and failing the pairing check.
/// <para>
/// The on-chain verifier (COMPL-C03) reads only the first five indices, which are byte-for-byte identical
/// to <see cref="CircuitV1Layout"/>, so the expiry/tier/nullifier enforcement is unchanged. The cid and
/// recipient bindings are checked by the consumer that knows their semantics (Trilith.KeyGate), which
/// recomputes CidField/RecipientKeyHash from the request and compares them to these public inputs.
/// </para>
/// Each public input is a 32-byte big-endian BLS12-381 scalar. Changing an index is a breaking change that
/// requires a new circuit and re-registering every verification key.
/// </summary>
public static class CircuitV2Layout
{
    /// <summary>Merkle root of the issuer's credential accumulator.</summary>
    public const int IssuerRoot = 0;

    /// <summary>Request-scoped nullifier = MiMC(secret, cidField, recipientKeyHash). Must equal <c>ComplianceProof.Nullifier</c>.</summary>
    public const int Nullifier = 1;

    /// <summary>Credential expiry as unix seconds (big-endian). Enforced against the block timestamp.</summary>
    public const int Expiry = 2;

    /// <summary>Issuer trust tier. Enforced against the schema's minimum required tier.</summary>
    public const int IssuerTier = 3;

    /// <summary>Revocation accumulator root.</summary>
    public const int RevocationRoot = 4;

    /// <summary>Field element binding the proof to one content id: SHA256("trilith-keygate-cid-v2" || cid) mod r.</summary>
    public const int CidField = 5;

    /// <summary>Field element binding the wrapped key to one recipient: SHA256("trilith-keygate-recipient-v2" || pubkey) mod r.</summary>
    public const int RecipientKeyHash = 6;

    /// <summary>Number of public inputs a v2 proof exposes. Equals <c>vk.IC.Length - 1</c>.</summary>
    public const int PublicInputCount = 7;

    /// <summary>
    /// Reads a public input (32-byte big-endian scalar) as a <see cref="ulong"/>, used for the expiry and
    /// tier fields. Throws if the value does not fit in 64 bits (the top 24 bytes must be zero), which
    /// signals a malformed or out-of-layout proof.
    /// </summary>
    public static ulong ReadUInt64(byte[] publicInput)
    {
        ArgumentNullException.ThrowIfNull(publicInput);
        if (publicInput.Length != 32)
            throw new ArgumentException($"Public input must be 32 bytes, got {publicInput.Length}.");
        for (int i = 0; i < 24; i++)
        {
            if (publicInput[i] != 0)
                throw new ArgumentException("Public input exceeds 64 bits.");
        }
        return BinaryPrimitives.ReadUInt64BigEndian(publicInput.AsSpan(24, 8));
    }
}
