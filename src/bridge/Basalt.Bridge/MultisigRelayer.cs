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

    /// <summary>
    /// Remove a relayer.
    /// LOW-05: Prevents removal if it would make the threshold unreachable.
    /// </summary>
    /// <exception cref="InvalidOperationException">If removal would leave fewer relayers than the threshold.</exception>
    public void RemoveRelayer(byte[] publicKey)
    {
        lock (_lock)
        {
            var key = Convert.ToHexString(publicKey);
            if (!_relayers.ContainsKey(key))
                return;

            // LOW-05: prevent threshold from becoming unreachable
            if (_relayers.Count - 1 < _threshold)
                throw new InvalidOperationException(
                    $"Cannot remove relayer: would leave {_relayers.Count - 1} relayers, below threshold {_threshold}.");

            _relayers.Remove(key);
        }
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
    /// HIGH-03: Enforces that threshold represents a strict majority (M &gt; N/2).
    /// </summary>
    public bool VerifyMessage(byte[] messageHash, IReadOnlyList<RelayerSignature> signatures)
    {
        lock (_lock)
        {
            if (signatures.Count < _threshold)
                return false;

            // HIGH-03: Enforce minimum quorum — threshold must be strict majority
            if (_relayers.Count > 0 && _threshold * 2 <= _relayers.Count)
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
