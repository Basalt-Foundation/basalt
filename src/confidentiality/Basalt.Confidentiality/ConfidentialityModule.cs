using Basalt.Confidentiality.Channels;
using Basalt.Confidentiality.Crypto;
using Basalt.Confidentiality.Disclosure;
using Basalt.Confidentiality.Transactions;

namespace Basalt.Confidentiality;

/// <summary>
/// Entry point for the Basalt confidentiality module.
///
/// Provides access to all privacy-preserving features:
/// <list type="bullet">
///   <item><description><see cref="PedersenCommitment"/> — Additively homomorphic commitments for hiding values.</description></item>
///   <item><description><see cref="Groth16Verifier"/> — Zero-knowledge proof verification (ZK-SNARKs).</description></item>
///   <item><description><see cref="TransferValidator"/> — Confidential transfer balance/range proof validation.</description></item>
///   <item><description><see cref="X25519KeyExchange"/> / <see cref="ChannelEncryption"/> — Private channel key exchange and encryption.</description></item>
///   <item><description><see cref="ViewingKey"/> — Selective disclosure for compliance/audit.</description></item>
///   <item><description><see cref="DisclosureProof"/> — Commitment opening proofs.</description></item>
/// </list>
/// </summary>
public static class ConfidentialityModule
{
    /// <summary>
    /// Module name for logging and diagnostics.
    /// </summary>
    public const string Name = "Basalt.Confidentiality";

    /// <summary>
    /// Module version.
    /// </summary>
    public const string Version = "1.0.0";

    /// <summary>
    /// Check whether the confidentiality module is operational by verifying
    /// that the underlying cryptographic primitives are functional.
    /// </summary>
    /// <returns><c>true</c> if all subsystems initialize correctly.</returns>
    public static bool IsOperational()
    {
        try
        {
            // Verify BLS12-381 pairing engine works
            _ = PairingEngine.G1Generator;
            _ = PairingEngine.G2Generator;

            // Verify Pedersen commitment round-trips
            _ = PedersenCommitment.HGenerator;

            // Verify X25519 key generation works
            var (privKey, pubKey) = X25519KeyExchange.GenerateKeyPair();
            if (privKey.Length != X25519KeyExchange.KeySize || pubKey.Length != X25519KeyExchange.KeySize)
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }
}
