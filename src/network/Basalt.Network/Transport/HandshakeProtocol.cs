using System.Security.Cryptography;
using Basalt.Core;
using Basalt.Crypto;
using Microsoft.Extensions.Logging;

namespace Basalt.Network.Transport;

/// <summary>
/// Handshake protocol for establishing peer identity after TCP connection.
/// Exchanges Hello/HelloAck messages and validates chain compatibility.
/// NET-C01: Mutual Ed25519 challenge-response authentication.
/// NET-C02: Ephemeral X25519 key exchange for AES-256-GCM transport encryption.
/// NET-H03: Genesis hash cross-validation.
/// </summary>
public sealed class HandshakeProtocol
{
    private readonly uint _chainId;
    private readonly byte[] _localPrivateKey;
    private readonly PublicKey _localPublicKey;
    private readonly BlsPublicKey _localBlsPublicKey;
    private readonly PeerId _localPeerId;
    private readonly Func<ulong> _getBestBlockNumber;
    private readonly Func<Hash256> _getBestBlockHash;
    private readonly Func<Hash256> _getGenesisHash;
    private readonly int _listenPort;
    private readonly string _listenAddress;
    private readonly ILogger _logger;

    /// <summary>NET-C01: Domain separation prefix for Hello auth signatures.</summary>
    private static readonly byte[] HelloDomain = "basalt-hello-v1"u8.ToArray();

    /// <summary>NET-C01: Domain separation prefix for HelloAck challenge-response signatures.</summary>
    private static readonly byte[] AckDomain = "basalt-ack-v1"u8.ToArray();

    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>NET-C02: Ephemeral X25519 private key for this handshake instance.</summary>
    private byte[]? _ephemeralPrivateKey;

    /// <summary>NET-C02: Ephemeral X25519 public key for this handshake instance.</summary>
    private byte[]? _ephemeralPublicKey;

    public HandshakeProtocol(
        uint chainId,
        byte[] localPrivateKey,
        PublicKey localPublicKey,
        BlsPublicKey localBlsPublicKey,
        PeerId localPeerId,
        int listenPort,
        Func<ulong> getBestBlockNumber,
        Func<Hash256> getBestBlockHash,
        Func<Hash256> getGenesisHash,
        ILogger logger,
        string? listenAddress = null)
    {
        _chainId = chainId;
        _localPrivateKey = localPrivateKey;
        _localPublicKey = localPublicKey;
        _localBlsPublicKey = localBlsPublicKey;
        _localPeerId = localPeerId;
        _listenPort = listenPort;
        _listenAddress = listenAddress ?? "";
        _getBestBlockNumber = getBestBlockNumber;
        _getBestBlockHash = getBestBlockHash;
        _getGenesisHash = getGenesisHash;
        _logger = logger;
    }

    /// <summary>
    /// Perform handshake as the initiator (outbound connection).
    /// Sends Hello (with challenge nonce + auth signature + X25519 key), waits for HelloAck.
    /// NET-C01: Verifies responder's challenge-response signature.
    /// NET-C02: Derives shared secret for transport encryption.
    /// NET-H03: Verifies responder's genesis hash.
    /// </summary>
    public async Task<HandshakeResult> InitiateAsync(
        PeerConnection connection,
        CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(HandshakeTimeout);

        try
        {
            // NET-C02: Generate ephemeral X25519 key pair
            GenerateEphemeralKeys();

            // Send Hello with challenge nonce + X25519 key
            var hello = CreateHello();
            var helloBytes = MessageCodec.Serialize(hello);
            await connection.SendAsync(helloBytes, timeoutCts.Token);

            // Wait for HelloAck
            var response = await connection.ReceiveOneAsync(timeoutCts.Token);
            if (response == null)
                return HandshakeResult.Failed("No response received");

            var msg = MessageCodec.Deserialize(response);
            if (msg is HelloAckMessage ack)
            {
                if (!ack.Accepted)
                    return HandshakeResult.Failed($"Rejected: {ack.RejectReason}");

                // NET-C01: Verify responder signed our challenge nonce
                var ackPayload = BuildChallengePayload(AckDomain, hello.ChallengeNonce, _chainId);
                if (!Ed25519Signer.Verify(ack.NodePublicKey, ackPayload, ack.ChallengeResponse))
                {
                    _logger.LogWarning("HelloAck challenge-response verification failed");
                    return HandshakeResult.Failed("Challenge-response verification failed");
                }

                // NET-H03: Verify genesis hash matches
                var localGenesis = _getGenesisHash();
                if (ack.GenesisHash != localGenesis)
                {
                    _logger.LogWarning("Genesis hash mismatch in HelloAck: local={Local}, remote={Remote}",
                        localGenesis.ToHexString()[..16], ack.GenesisHash.ToHexString()[..16]);
                    return HandshakeResult.Failed("Genesis hash mismatch");
                }

                // NET-C02: Derive shared secret from X25519 key exchange
                var sharedSecret = DeriveSharedSecret(ack.X25519PublicKey, ack.NodePublicKey, ack.X25519KeySignature);

                var remotePeerId = PeerId.FromPublicKey(ack.NodePublicKey);
                return HandshakeResult.Success(
                    remotePeerId,
                    ack.NodePublicKey,
                    ack.BlsPublicKey,
                    "",
                    ack.ListenPort,
                    ack.BestBlockNumber,
                    ack.BestBlockHash,
                    sharedSecret,
                    isInitiator: true);
            }

            if (msg is HelloMessage peerHello)
            {
                // Peer also sent Hello (simultaneous connect) — validate and send Ack
                var validation = ValidateHello(peerHello);
                if (!validation.IsAccepted)
                {
                    var rejectAck = CreateAck(false, validation.RejectReason, peerHello.ChallengeNonce);
                    await connection.SendAsync(MessageCodec.Serialize(rejectAck), timeoutCts.Token);
                    return HandshakeResult.Failed(validation.RejectReason);
                }

                var acceptAck = CreateAck(true, "", peerHello.ChallengeNonce);
                await connection.SendAsync(MessageCodec.Serialize(acceptAck), timeoutCts.Token);

                // NET-C02: Derive shared secret from peer's Hello X25519 key
                var sharedSecret = DeriveSharedSecret(
                    peerHello.X25519PublicKey, peerHello.NodePublicKey, peerHello.X25519KeySignature);

                return HandshakeResult.Success(
                    PeerId.FromPublicKey(peerHello.NodePublicKey),
                    peerHello.NodePublicKey,
                    peerHello.BlsPublicKey,
                    peerHello.ListenAddress,
                    peerHello.ListenPort,
                    peerHello.BestBlockNumber,
                    peerHello.BestBlockHash,
                    sharedSecret,
                    isInitiator: true);
            }

            return HandshakeResult.Failed($"Unexpected message type: {msg.Type}");
        }
        catch (OperationCanceledException)
        {
            return HandshakeResult.Failed("Handshake timed out");
        }
        finally
        {
            ZeroEphemeralKeys();
        }
    }

    /// <summary>
    /// Perform handshake as the responder (inbound connection).
    /// Waits for Hello, validates (including NET-C01 auth), sends HelloAck.
    /// NET-C02: Includes X25519 key exchange for transport encryption.
    /// </summary>
    public async Task<HandshakeResult> RespondAsync(
        PeerConnection connection,
        CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(HandshakeTimeout);

        try
        {
            // NET-C02: Generate ephemeral X25519 key pair
            GenerateEphemeralKeys();

            // Wait for Hello
            var data = await connection.ReceiveOneAsync(timeoutCts.Token);
            if (data == null)
                return HandshakeResult.Failed("No Hello received");

            var msg = MessageCodec.Deserialize(data);
            if (msg is not HelloMessage hello)
                return HandshakeResult.Failed($"Expected Hello, got {msg.Type}");

            // Validate (includes NET-C01 auth verification + NET-H03 genesis hash)
            var validation = ValidateHello(hello);
            if (!validation.IsAccepted)
            {
                var rejectAck = CreateAck(false, validation.RejectReason, hello.ChallengeNonce);
                await connection.SendAsync(MessageCodec.Serialize(rejectAck), timeoutCts.Token);
                return HandshakeResult.Failed(validation.RejectReason);
            }

            // Send HelloAck with challenge-response (sign initiator's nonce) + X25519 key
            var ack = CreateAck(true, "", hello.ChallengeNonce);
            await connection.SendAsync(MessageCodec.Serialize(ack), timeoutCts.Token);

            // NET-C02: Derive shared secret from initiator's X25519 key
            var sharedSecret = DeriveSharedSecret(
                hello.X25519PublicKey, hello.NodePublicKey, hello.X25519KeySignature);

            var peerId = PeerId.FromPublicKey(hello.NodePublicKey);
            return HandshakeResult.Success(
                peerId,
                hello.NodePublicKey,
                hello.BlsPublicKey,
                hello.ListenAddress,
                hello.ListenPort,
                hello.BestBlockNumber,
                hello.BestBlockHash,
                sharedSecret,
                isInitiator: false);
        }
        catch (OperationCanceledException)
        {
            return HandshakeResult.Failed("Handshake timed out");
        }
        finally
        {
            ZeroEphemeralKeys();
        }
    }

    private HelloMessage CreateHello()
    {
        // NET-C01: Generate 32-byte random challenge nonce
        var nonce = new byte[32];
        RandomNumberGenerator.Fill(nonce);

        // NET-C01: Sign BLAKE3("basalt-hello-v1" || nonce || chainId) to prove identity
        var payload = BuildChallengePayload(HelloDomain, nonce, _chainId);
        var authSig = Ed25519Signer.Sign(_localPrivateKey, payload);

        // NET-C02: Sign the ephemeral X25519 public key with Ed25519 identity
        var x25519Sig = X25519.SignPublicKey(_ephemeralPublicKey!, _localPrivateKey);

        return new HelloMessage
        {
            SenderId = _localPeerId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ProtocolVersion = 1,
            ChainId = _chainId,
            BestBlockNumber = _getBestBlockNumber(),
            BestBlockHash = _getBestBlockHash(),
            GenesisHash = _getGenesisHash(),
            NodePublicKey = _localPublicKey,
            BlsPublicKey = _localBlsPublicKey,
            ListenAddress = _listenAddress,
            ListenPort = _listenPort,
            ChallengeNonce = nonce,
            AuthSignature = authSig,
            X25519PublicKey = _ephemeralPublicKey!,
            X25519KeySignature = x25519Sig,
        };
    }

    /// <summary>
    /// Create a HelloAck message, optionally signing the initiator's challenge nonce.
    /// NET-C02: Includes ephemeral X25519 key + signature.
    /// </summary>
    private HelloAckMessage CreateAck(bool accepted, string reason, byte[]? initiatorNonce = null)
    {
        // NET-C01: Sign the initiator's nonce to prove our identity
        var challengeResponse = default(Signature);
        if (accepted && initiatorNonce is { Length: > 0 })
        {
            var payload = BuildChallengePayload(AckDomain, initiatorNonce, _chainId);
            challengeResponse = Ed25519Signer.Sign(_localPrivateKey, payload);
        }

        // NET-C02: Sign the ephemeral X25519 public key with Ed25519 identity
        var x25519Sig = default(Signature);
        var x25519PubKey = Array.Empty<byte>();
        if (accepted && _ephemeralPublicKey != null)
        {
            x25519Sig = X25519.SignPublicKey(_ephemeralPublicKey, _localPrivateKey);
            x25519PubKey = _ephemeralPublicKey;
        }

        return new HelloAckMessage
        {
            SenderId = _localPeerId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Accepted = accepted,
            RejectReason = reason,
            NodePublicKey = _localPublicKey,
            BlsPublicKey = _localBlsPublicKey,
            ListenPort = _listenPort,
            BestBlockNumber = _getBestBlockNumber(),
            BestBlockHash = _getBestBlockHash(),
            ChallengeResponse = challengeResponse,
            GenesisHash = _getGenesisHash(),
            X25519PublicKey = x25519PubKey,
            X25519KeySignature = x25519Sig,
        };
    }

    private HelloValidation ValidateHello(HelloMessage hello)
    {
        if (hello.ChainId != _chainId)
            return new HelloValidation(false, $"Chain ID mismatch: expected {_chainId}, got {hello.ChainId}");

        if (hello.ProtocolVersion < 1)
            return new HelloValidation(false, $"Unsupported protocol version: {hello.ProtocolVersion}");

        // NET-H03: Validate genesis hash
        var localGenesis = _getGenesisHash();
        if (hello.GenesisHash != localGenesis)
            return new HelloValidation(false,
                $"Genesis hash mismatch: expected {localGenesis.ToHexString()[..16]}..., got {hello.GenesisHash.ToHexString()[..16]}...");

        // NET-C01: Verify initiator's auth signature proves ownership of NodePublicKey
        if (hello.ChallengeNonce is not { Length: 32 })
            return new HelloValidation(false, "Missing or invalid challenge nonce (expected 32 bytes)");

        var helloPayload = BuildChallengePayload(HelloDomain, hello.ChallengeNonce, hello.ChainId);
        if (!Ed25519Signer.Verify(hello.NodePublicKey, helloPayload, hello.AuthSignature))
            return new HelloValidation(false, "Hello auth signature verification failed");

        // NET-C02: Validate X25519 key exchange fields
        if (hello.X25519PublicKey is not { Length: 32 })
            return new HelloValidation(false, "Missing or invalid X25519 public key (expected 32 bytes)");

        if (!X25519.VerifyPublicKey(hello.X25519PublicKey, hello.NodePublicKey, hello.X25519KeySignature))
            return new HelloValidation(false, "X25519 public key signature verification failed");

        return new HelloValidation(true, "");
    }

    /// <summary>
    /// NET-C02: Generate a fresh ephemeral X25519 key pair for this connection.
    /// </summary>
    private void GenerateEphemeralKeys()
    {
        (_ephemeralPrivateKey, _ephemeralPublicKey) = X25519.GenerateKeyPair();
    }

    /// <summary>
    /// NET-C02: Derive shared secret from peer's X25519 public key.
    /// Verifies the X25519 key signature and performs DH.
    /// </summary>
    private byte[]? DeriveSharedSecret(byte[] peerX25519PubKey, PublicKey peerEd25519PubKey, Signature x25519Sig)
    {
        if (peerX25519PubKey is not { Length: 32 })
        {
            _logger.LogWarning("Peer X25519 public key missing or invalid length");
            return null;
        }

        if (!X25519.VerifyPublicKey(peerX25519PubKey, peerEd25519PubKey, x25519Sig))
        {
            _logger.LogWarning("Peer X25519 public key signature verification failed");
            return null;
        }

        try
        {
            return X25519.DeriveSharedSecret(_ephemeralPrivateKey!, peerX25519PubKey);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "X25519 key agreement failed");
            return null;
        }
    }

    /// <summary>
    /// NET-C02: Securely zero ephemeral key material after handshake.
    /// </summary>
    private void ZeroEphemeralKeys()
    {
        if (_ephemeralPrivateKey != null)
        {
            CryptographicOperations.ZeroMemory(_ephemeralPrivateKey);
            _ephemeralPrivateKey = null;
        }
        _ephemeralPublicKey = null;
    }

    /// <summary>
    /// NET-C01: Build the BLAKE3 challenge payload: domain || nonce || chainId (LE 4 bytes).
    /// </summary>
    private static byte[] BuildChallengePayload(byte[] domain, byte[] nonce, uint chainId)
    {
        var payload = new byte[domain.Length + nonce.Length + 4];
        domain.CopyTo(payload, 0);
        nonce.CopyTo(payload, domain.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(domain.Length + nonce.Length), chainId);
        return Blake3Hasher.Hash(payload).ToArray();
    }

    private readonly record struct HelloValidation(bool IsAccepted, string RejectReason);
}

/// <summary>
/// Result of a handshake attempt.
/// </summary>
public sealed class HandshakeResult
{
    public bool IsSuccess { get; init; }
    public string? Error { get; init; }
    public PeerId PeerId { get; init; }
    public PublicKey PeerPublicKey { get; init; }
    public BlsPublicKey PeerBlsPublicKey { get; init; }
    public string PeerHost { get; init; } = "";
    public int PeerPort { get; init; }
    public ulong PeerBestBlock { get; init; }
    public Hash256 PeerBestBlockHash { get; init; }

    /// <summary>NET-C02: Shared secret for transport encryption (null if key exchange failed).</summary>
    public byte[]? SharedSecret { get; init; }

    /// <summary>NET-C02: True if this side initiated the connection (sent Hello first).</summary>
    public bool IsInitiator { get; init; }

    public static HandshakeResult Failed(string reason) => new() { IsSuccess = false, Error = reason };

    public static HandshakeResult Success(
        PeerId peerId,
        PublicKey publicKey,
        BlsPublicKey blsPublicKey,
        string host,
        int port,
        ulong bestBlock,
        Hash256 bestBlockHash,
        byte[]? sharedSecret = null,
        bool isInitiator = false) =>
        new()
        {
            IsSuccess = true,
            PeerId = peerId,
            PeerPublicKey = publicKey,
            PeerBlsPublicKey = blsPublicKey,
            PeerHost = host,
            PeerPort = port,
            PeerBestBlock = bestBlock,
            PeerBestBlockHash = bestBlockHash,
            SharedSecret = sharedSecret,
            IsInitiator = isInitiator,
        };
}
