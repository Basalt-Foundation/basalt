namespace Basalt.Core;

/// <summary>
/// A compliance requirement specifying which credential schema must be proved
/// and the minimum issuer trust tier accepted.
/// Token issuers define requirements in terms of schemas and tiers,
/// not specific issuers — the user chooses their provider.
/// </summary>
public readonly struct ProofRequirement
{
    /// <summary>BLAKE3 hash of the required schema name.</summary>
    public Hash256 SchemaId { get; init; }

    /// <summary>
    /// Minimum issuer tier accepted (0=self, 1=regulated, 2=accredited, 3=sovereign).
    /// </summary>
    public byte MinIssuerTier { get; init; }
}
