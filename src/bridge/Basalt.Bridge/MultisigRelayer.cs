using Basalt.Core;
using Basalt.Crypto;

namespace Basalt.Bridge;

/// <summary>
/// Multisig relayer model for testnet bridge.
/// Requires M-of-N relayer signatures to validate cross-chain messages.
/// In production, this would be replaced by a trustless light-client bridge.
/// </summary>
public sealed class MultisigRelayer
{
    private readonly Dictionary<string, byte[]> _relayers = new(); // pubKeyHex -> pubKey bytes
    private readonly int _threshold;
    private readonly object _lock = new();

    /// <summary>
    /// Create a multisig relayer with the specified threshold.
    /// </summary>
    /// <param name="threshold">Minimum signatures required (M-of-N).</param>
    public MultisigRelayer(int threshold)
    {
        if (threshold < 1)
            throw new ArgumentException("Threshold must be at least 1", nameof(threshold));
        _threshold = threshold;
    }

    /// <summary>Number of signatures required.</summary>
    public int Threshold => _threshold;

    /// <summary>Number of registered relayers.</summary>
    public int RelayerCount
    {
        get { lock (_lock) return _relayers.Count; }
    }

    /// <summary>Register a relayer's public key.</summary>
    public void AddRelayer(byte[] publicKey)
    {
        lock (_lock)
            _relayers[Convert.ToHexString(publicKey)] = publicKey;
    }

    /// <summary>Remove a relayer.</summary>
    public void RemoveRelayer(byte[] publicKey)
    {
        lock (_lock)
            _relayers.Remove(Convert.ToHexString(publicKey));
    }

    /// <summary>Check if a public key is a registered relayer.</summary>
    public bool IsRelayer(byte[] publicKey)
    {
        lock (_lock)
            return _relayers.ContainsKey(Convert.ToHexString(publicKey));
    }

    /// <summary>
    /// Sign a message hash with a relayer's private key.
    /// </summary>
    public static RelayerSignature Sign(byte[] messageHash, byte[] privateKey, byte[] publicKey)
    {
        var signature = Ed25519Signer.Sign(privateKey, messageHash);
        return new RelayerSignature
        {
            PublicKey = publicKey,
            Signature = signature.ToArray(),
        };
    }

    /// <summary>
    /// Verify that a message has sufficient valid relayer signatures.
    /// </summary>
    public bool VerifyMessage(byte[] messageHash, IReadOnlyList<RelayerSignature> signatures)
    {
        lock (_lock)
        {
            if (signatures.Count < _threshold)
                return false;

            var validCount = 0;
            var seenRelayers = new HashSet<string>();

            foreach (var sig in signatures)
            {
                var pubKeyHex = Convert.ToHexString(sig.PublicKey);

                // Must be a registered relayer
                if (!_relayers.ContainsKey(pubKeyHex))
                    continue;

                // Prevent duplicate signatures from same relayer
                if (!seenRelayers.Add(pubKeyHex))
                    continue;

                // Verify Ed25519 signature
                var pubKey = new PublicKey(sig.PublicKey);
                var coreSig = new Basalt.Core.Signature(sig.Signature);
                if (Ed25519Signer.Verify(pubKey, messageHash, coreSig))
                    validCount++;

                if (validCount >= _threshold)
                    return true;
            }

            return false;
        }
    }

    /// <summary>Get all registered relayer public keys.</summary>
    public IReadOnlyList<byte[]> GetRelayers()
    {
        lock (_lock)
            return _relayers.Values.ToList();
    }
}
