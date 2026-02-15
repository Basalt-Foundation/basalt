using Basalt.Core;

namespace Basalt.Execution.VM.Sandbox;

/// <summary>
/// Provides a clean boundary between contract code loaded in an isolated
/// <see cref="ContractAssemblyContext"/> and the host <see cref="HostInterface"/>.
///
/// All methods expose only primitive types and byte arrays so that contracts
/// loaded in a separate ALC do not need direct references to core value types.
/// Every method that produces output tracks the allocation through the
/// <see cref="ResourceLimiter"/> so memory pressure is accounted for.
/// </summary>
public sealed class SandboxedHostBridge
{
    private readonly HostInterface _host;
    private readonly ResourceLimiter _limiter;

    public SandboxedHostBridge(HostInterface host, ResourceLimiter limiter)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _limiter = limiter ?? throw new ArgumentNullException(nameof(limiter));
    }

    // =========================================================================
    // Storage
    // =========================================================================

    /// <summary>
    /// Read a value from contract storage.
    /// </summary>
    /// <param name="key">32-byte storage key.</param>
    /// <returns>The stored value, or null if the key does not exist.</returns>
    public byte[]? StorageRead(byte[] key)
    {
        ValidateKeyLength(key);
        var hash = new Hash256(key.AsSpan(0, Hash256.Size));
        var result = _host.StorageRead(hash);

        if (result != null)
            _limiter.Allocate(result.Length);

        return result;
    }

    /// <summary>
    /// Write a value to contract storage.
    /// </summary>
    /// <param name="key">32-byte storage key.</param>
    /// <param name="value">The value to store.</param>
    public void StorageWrite(byte[] key, byte[] value)
    {
        ValidateKeyLength(key);
        var hash = new Hash256(key.AsSpan(0, Hash256.Size));
        _host.StorageWrite(hash, value);
    }

    /// <summary>
    /// Delete a key from contract storage.
    /// </summary>
    /// <param name="key">32-byte storage key.</param>
    public void StorageDelete(byte[] key)
    {
        ValidateKeyLength(key);
        var hash = new Hash256(key.AsSpan(0, Hash256.Size));
        _host.StorageDelete(hash);
    }

    // =========================================================================
    // Cryptographic
    // =========================================================================

    /// <summary>
    /// Compute the BLAKE3 hash of the given data.
    /// </summary>
    /// <param name="data">Input bytes to hash.</param>
    /// <returns>32-byte hash result.</returns>
    public byte[] Blake3Hash(byte[] data)
    {
        var hash = _host.Blake3Hash(data);
        var result = hash.ToArray();
        _limiter.Allocate(result.Length);
        return result;
    }

    /// <summary>
    /// Compute the Keccak-256 hash of the given data.
    /// </summary>
    /// <param name="data">Input bytes to hash.</param>
    /// <returns>32-byte hash result.</returns>
    public byte[] Keccak256(byte[] data)
    {
        var result = _host.Keccak256(data);
        _limiter.Allocate(result.Length);
        return result;
    }

    /// <summary>
    /// Verify an Ed25519 signature.
    /// </summary>
    /// <param name="publicKey">32-byte Ed25519 public key.</param>
    /// <param name="message">The signed message bytes.</param>
    /// <param name="signature">64-byte Ed25519 signature.</param>
    /// <returns><c>true</c> if the signature is valid.</returns>
    public bool Ed25519Verify(byte[] publicKey, byte[] message, byte[] signature)
    {
        if (publicKey.Length != PublicKey.Size)
            throw new ArgumentException($"Public key must be {PublicKey.Size} bytes.", nameof(publicKey));
        if (signature.Length != Signature.Size)
            throw new ArgumentException($"Signature must be {Signature.Size} bytes.", nameof(signature));

        var pk = new PublicKey(publicKey);
        var sig = new Signature(signature);
        return _host.Ed25519Verify(pk, message, sig);
    }

    // =========================================================================
    // Context
    // =========================================================================

    /// <summary>
    /// Get the address of the caller (msg.sender equivalent).
    /// </summary>
    /// <returns>20-byte caller address.</returns>
    public byte[] GetCaller()
    {
        var address = _host.GetCaller();
        var result = address.ToArray();
        _limiter.Allocate(result.Length);
        return result;
    }

    /// <summary>
    /// Get the value (native token amount) sent with the call.
    /// </summary>
    /// <returns>32-byte big-endian UInt256 representation.</returns>
    public byte[] GetValue()
    {
        var value = _host.GetValue();
        var result = value.ToArray(isBigEndian: true);
        _limiter.Allocate(result.Length);
        return result;
    }

    /// <summary>
    /// Get the current block timestamp.
    /// </summary>
    public ulong GetBlockTimestamp()
    {
        return _host.GetBlockTimestamp();
    }

    /// <summary>
    /// Get the current block number.
    /// </summary>
    public ulong GetBlockNumber()
    {
        return _host.GetBlockNumber();
    }

    /// <summary>
    /// Get the balance of an address.
    /// </summary>
    /// <param name="address">20-byte account address.</param>
    /// <returns>32-byte big-endian UInt256 balance.</returns>
    public byte[] GetBalance(byte[] address)
    {
        if (address.Length != Address.Size)
            throw new ArgumentException($"Address must be {Address.Size} bytes.", nameof(address));

        var addr = new Address(address);
        var balance = _host.GetBalance(addr);
        var result = balance.ToArray(isBigEndian: true);
        _limiter.Allocate(result.Length);
        return result;
    }

    // =========================================================================
    // Events
    // =========================================================================

    /// <summary>
    /// Emit a log event.
    /// </summary>
    /// <param name="eventSignature">32-byte event signature hash.</param>
    /// <param name="topics">Array of 32-byte indexed topic values.</param>
    /// <param name="data">Unindexed event data payload.</param>
    public void EmitEvent(byte[] eventSignature, byte[][] topics, byte[] data)
    {
        ValidateKeyLength(eventSignature);
        var sig = new Hash256(eventSignature.AsSpan(0, Hash256.Size));

        var topicHashes = new Hash256[topics.Length];
        for (int i = 0; i < topics.Length; i++)
        {
            if (topics[i].Length < Hash256.Size)
                throw new ArgumentException($"Topic {i} must be at least {Hash256.Size} bytes.");
            topicHashes[i] = new Hash256(topics[i].AsSpan(0, Hash256.Size));
        }

        _host.EmitEvent(sig, topicHashes, data);
    }

    // =========================================================================
    // Control Flow
    // =========================================================================

    /// <summary>
    /// Revert execution with the given reason message.
    /// </summary>
    /// <param name="message">Human-readable revert reason.</param>
    public void Revert(string message)
    {
        _host.Revert(message);
    }

    /// <summary>
    /// Assert a condition; reverts with the given message if the condition is false.
    /// </summary>
    /// <param name="condition">The condition to assert.</param>
    /// <param name="message">Revert reason if the condition fails.</param>
    public void Require(bool condition, string message)
    {
        _host.Require(condition, message);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static void ValidateKeyLength(byte[] key)
    {
        if (key == null || key.Length < Hash256.Size)
            throw new ArgumentException($"Key must be at least {Hash256.Size} bytes.");
    }
}
