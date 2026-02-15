using Basalt.Core;
using Microsoft.Extensions.Logging;

namespace Basalt.Network.Transport;

/// <summary>
/// Handshake protocol for establishing peer identity after TCP connection.
/// Exchanges Hello/HelloAck messages and validates chain compatibility.
/// </summary>
public sealed class HandshakeProtocol
{
    private readonly uint _chainId;
    private readonly PublicKey _localPublicKey;
    private readonly PeerId _localPeerId;
    private readonly Func<ulong> _getBestBlockNumber;
    private readonly Func<Hash256> _getBestBlockHash;
    private readonly Func<Hash256> _getGenesisHash;
    private readonly int _listenPort;
    private readonly ILogger _logger;

    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    public HandshakeProtocol(
        uint chainId,
        PublicKey localPublicKey,
        PeerId localPeerId,
        int listenPort,
        Func<ulong> getBestBlockNumber,
        Func<Hash256> getBestBlockHash,
        Func<Hash256> getGenesisHash,
        ILogger logger)
    {
        _chainId = chainId;
        _localPublicKey = localPublicKey;
        _localPeerId = localPeerId;
        _listenPort = listenPort;
        _getBestBlockNumber = getBestBlockNumber;
        _getBestBlockHash = getBestBlockHash;
        _getGenesisHash = getGenesisHash;
        _logger = logger;
    }

    /// <summary>
    /// Perform handshake as the initiator (outbound connection).
    /// Sends Hello, waits for HelloAck.
    /// </summary>
    public async Task<HandshakeResult> InitiateAsync(
        PeerConnection connection,
        CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(HandshakeTimeout);

        try
        {
            // Send Hello
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

                var remotePeerId = PeerId.FromPublicKey(ack.NodePublicKey);
                return HandshakeResult.Success(
                    remotePeerId,
                    ack.NodePublicKey,
                    "",
                    ack.ListenPort,
                    ack.BestBlockNumber,
                    ack.BestBlockHash);
            }

            if (msg is HelloMessage peerHello)
            {
                // Peer also sent Hello (simultaneous connect) — validate and send Ack
                var validation = ValidateHello(peerHello);
                if (!validation.IsAccepted)
                {
                    var rejectAck = CreateAck(false, validation.RejectReason);
                    await connection.SendAsync(MessageCodec.Serialize(rejectAck), timeoutCts.Token);
                    return HandshakeResult.Failed(validation.RejectReason);
                }

                var acceptAck = CreateAck(true, "");
                await connection.SendAsync(MessageCodec.Serialize(acceptAck), timeoutCts.Token);

                return HandshakeResult.Success(
                    PeerId.FromPublicKey(peerHello.NodePublicKey),
                    peerHello.NodePublicKey,
                    peerHello.ListenAddress,
                    peerHello.ListenPort,
                    peerHello.BestBlockNumber,
                    peerHello.BestBlockHash);
            }

            return HandshakeResult.Failed($"Unexpected message type: {msg.Type}");
        }
        catch (OperationCanceledException)
        {
            return HandshakeResult.Failed("Handshake timed out");
        }
    }

    /// <summary>
    /// Perform handshake as the responder (inbound connection).
    /// Waits for Hello, validates, sends HelloAck.
    /// </summary>
    public async Task<HandshakeResult> RespondAsync(
        PeerConnection connection,
        CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(HandshakeTimeout);

        try
        {
            // Wait for Hello
            var data = await connection.ReceiveOneAsync(timeoutCts.Token);
            if (data == null)
                return HandshakeResult.Failed("No Hello received");

            var msg = MessageCodec.Deserialize(data);
            if (msg is not HelloMessage hello)
                return HandshakeResult.Failed($"Expected Hello, got {msg.Type}");

            // Validate
            var validation = ValidateHello(hello);
            if (!validation.IsAccepted)
            {
                var rejectAck = CreateAck(false, validation.RejectReason);
                await connection.SendAsync(MessageCodec.Serialize(rejectAck), timeoutCts.Token);
                return HandshakeResult.Failed(validation.RejectReason);
            }

            // Send HelloAck
            var ack = CreateAck(true, "");
            await connection.SendAsync(MessageCodec.Serialize(ack), timeoutCts.Token);

            var peerId = PeerId.FromPublicKey(hello.NodePublicKey);
            return HandshakeResult.Success(
                peerId,
                hello.NodePublicKey,
                hello.ListenAddress,
                hello.ListenPort,
                hello.BestBlockNumber,
                hello.BestBlockHash);
        }
        catch (OperationCanceledException)
        {
            return HandshakeResult.Failed("Handshake timed out");
        }
    }

    private HelloMessage CreateHello()
    {
        return new HelloMessage
        {
            SenderId = _localPeerId,
            ProtocolVersion = 1,
            ChainId = _chainId,
            BestBlockNumber = _getBestBlockNumber(),
            BestBlockHash = _getBestBlockHash(),
            GenesisHash = _getGenesisHash(),
            NodePublicKey = _localPublicKey,
            ListenAddress = "",
            ListenPort = _listenPort,
        };
    }

    private HelloAckMessage CreateAck(bool accepted, string reason)
    {
        return new HelloAckMessage
        {
            SenderId = _localPeerId,
            Accepted = accepted,
            RejectReason = reason,
            NodePublicKey = _localPublicKey,
            ListenPort = _listenPort,
            BestBlockNumber = _getBestBlockNumber(),
            BestBlockHash = _getBestBlockHash(),
        };
    }

    private HelloValidation ValidateHello(HelloMessage hello)
    {
        if (hello.ChainId != _chainId)
            return new HelloValidation(false, $"Chain ID mismatch: expected {_chainId}, got {hello.ChainId}");

        if (hello.ProtocolVersion < 1)
            return new HelloValidation(false, $"Unsupported protocol version: {hello.ProtocolVersion}");

        return new HelloValidation(true, "");
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
    public string PeerHost { get; init; } = "";
    public int PeerPort { get; init; }
    public ulong PeerBestBlock { get; init; }
    public Hash256 PeerBestBlockHash { get; init; }

    public static HandshakeResult Failed(string reason) => new() { IsSuccess = false, Error = reason };

    public static HandshakeResult Success(
        PeerId peerId,
        PublicKey publicKey,
        string host,
        int port,
        ulong bestBlock,
        Hash256 bestBlockHash) =>
        new()
        {
            IsSuccess = true,
            PeerId = peerId,
            PeerPublicKey = publicKey,
            PeerHost = host,
            PeerPort = port,
            PeerBestBlock = bestBlock,
            PeerBestBlockHash = bestBlockHash,
        };
}
