using Basalt.Consensus;
using Basalt.Consensus.Staking;
using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution;
using Basalt.Execution.VM;
using Basalt.Execution.VM.Sandbox;
using Basalt.Network;
using Basalt.Network.Gossip;
using Basalt.Network.Transport;
using Basalt.Storage;
using Basalt.Storage.RocksDb;
using Basalt.Api.Rest;
using Microsoft.Extensions.Logging;

namespace Basalt.Node;

/// <summary>
/// Orchestrates the multi-node operation of a Basalt validator.
/// Wires: TCP Transport ↔ Handshake ↔ PeerManager ↔ Episub ↔ Consensus ↔ Block Production.
/// </summary>
public sealed class NodeCoordinator : IAsyncDisposable
{
    private readonly NodeConfiguration _config;
    private readonly ChainParameters _chainParams;
    private readonly ChainManager _chainManager;
    private readonly Mempool _mempool;
    private readonly IStateDatabase _stateDb;
    private readonly TransactionValidator _txValidator;
    private readonly WebSocketHandler _wsHandler;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<NodeCoordinator> _logger;

    // Persistent storage (optional — null when using in-memory)
    private readonly BlockStore? _blockStore;
    private readonly ReceiptStore? _receiptStore;

    // Staking / Slashing
    private readonly StakingState? _stakingState;
    private readonly SlashingEngine? _slashingEngine;
    private WeightedLeaderSelector? _leaderSelector;

    // Network components
    private TcpTransport? _transport;
    private PeerManager? _peerManager;
    private EpisubService? _episub;
    private GossipService? _gossip;
    private HandshakeProtocol? _handshake;

    // Consensus
    private BasaltBft? _consensus;
    private PipelinedConsensus? _pipelinedConsensus;
    private ValidatorSet? _validatorSet;
    private IBlsSigner _blsSigner = new BlsSigner();

    // Block production (consensus-driven — no BlockProductionLoop)
    private BlockBuilder? _blockBuilder;
    private TransactionExecutor? _txExecutor;
    private Address _proposerAddress;
    private Hash256 _myProposedBlockHash = Hash256.Zero;

    // Runtime
    private CancellationTokenSource? _cts;
    private Task? _consensusLoop;
    private long _lastBlockFinalizedAtMs;

    // Sync state
    private bool _isSyncing;
    private TaskCompletionSource<bool>? _syncBatchTcs;
    private const int SyncBatchSize = 50;

    // Validator activity tracking (for inactivity slashing)
    private readonly Dictionary<Address, ulong> _lastActiveBlock = new();
    private const ulong InactivityThresholdBlocks = 100; // ~40 seconds at 400ms

    // Double-sign detection: keyed by (view, proposer) to avoid false positives
    // when different proposers propose for the same view after a view change.
    private readonly Dictionary<(ulong View, PeerId Proposer), Hash256> _proposalsByView = new();

    // Identity
    private byte[] _privateKey = [];
    private PublicKey _publicKey;
    private BlsPublicKey _localBlsPublicKey;
    private PeerId _localPeerId;

    public NodeCoordinator(
        NodeConfiguration config,
        ChainParameters chainParams,
        ChainManager chainManager,
        Mempool mempool,
        IStateDatabase stateDb,
        TransactionValidator txValidator,
        WebSocketHandler wsHandler,
        ILoggerFactory loggerFactory,
        BlockStore? blockStore = null,
        ReceiptStore? receiptStore = null,
        StakingState? stakingState = null,
        SlashingEngine? slashingEngine = null)
    {
        _config = config;
        _chainParams = chainParams;
        _chainManager = chainManager;
        _mempool = mempool;
        _stateDb = stateDb;
        _txValidator = txValidator;
        _wsHandler = wsHandler;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<NodeCoordinator>();
        _blockStore = blockStore;
        _receiptStore = receiptStore;
        _stakingState = stakingState;
        _slashingEngine = slashingEngine;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // 1. Setup identity
        SetupIdentity();

        // 2. Setup validator set
        SetupValidatorSet();

        // 3. Setup network components
        SetupNetworking();

        // 4. Setup consensus
        SetupConsensus();

        // 5. Setup block production
        SetupBlockProduction();

        // 6. Start TCP transport
        await _transport!.StartAsync(_config.P2PPort, _cts.Token);
        _logger.LogInformation("P2P transport listening on port {Port}", _config.P2PPort);

        // 7. Connect to static peers
        await ConnectToStaticPeers();

        // 7.5. Subscribe to mempool events for tx gossip
        _mempool.OnTransactionAdded += tx =>
        {
            _gossip!.BroadcastTransaction(tx.Hash);
        };

        // 8. Start consensus loop
        _lastBlockFinalizedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _chainParams.BlockTimeMs;
        _consensusLoop = RunConsensusLoop(_cts.Token);

        // 9. Start peer reconnection loop
        _ = ReconnectLoop(_cts.Token);

        _logger.LogInformation("Node coordinator started. Validator index: {Index}, PeerId: {PeerId}",
            _config.ValidatorIndex, _localPeerId);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();

        if (_consensusLoop != null)
        {
            try { await _consensusLoop; }
            catch (OperationCanceledException) { }
        }

        if (_transport != null)
            await _transport.DisposeAsync();

        _logger.LogInformation("Node coordinator stopped.");
    }

    private void SetupIdentity()
    {
        if (string.IsNullOrEmpty(_config.ValidatorKeyHex))
        {
            // Generate a random key for development
            _privateKey = new byte[32];
            Random.Shared.NextBytes(_privateKey);
            _logger.LogWarning("No validator key configured, using random key (dev mode)");
        }
        else
        {
            var hex = _config.ValidatorKeyHex.StartsWith("0x")
                ? _config.ValidatorKeyHex[2..]
                : _config.ValidatorKeyHex;
            _privateKey = Convert.FromHexString(hex);
        }

        _publicKey = Ed25519Signer.GetPublicKey(_privateKey);
        _localBlsPublicKey = new BlsPublicKey(_blsSigner.GetPublicKey(_privateKey));
        _localPeerId = PeerId.FromPublicKey(_publicKey);
    }

    private void SetupValidatorSet()
    {
        // For devnet: Build validator set from peer configuration
        // Each validator's PeerId is derived from their public key
        // In Phase 1, we use the validator index to assign identities
        var validators = new List<ValidatorInfo>();

        // Add self
        var selfAddress = Address.FromHexString(_config.ValidatorAddress.Length > 0
            ? _config.ValidatorAddress
            : $"0x{_config.ValidatorIndex:X40}");
        var selfStake = _stakingState?.GetStakeInfo(selfAddress)?.TotalStake ?? (UInt256)100_000;

        validators.Add(new ValidatorInfo
        {
            PeerId = _localPeerId,
            PublicKey = _publicKey,
            BlsPublicKey = _localBlsPublicKey,
            Address = selfAddress,
            Index = _config.ValidatorIndex,
            Stake = selfStake,
        });

        // For a 4-validator devnet, we need all validators in the set.
        // Other validators' identities will be learned during handshake.
        // For now, create placeholder entries that will be updated.
        var totalValidators = _config.Peers.Length + 1;
        for (int i = 0; i < totalValidators; i++)
        {
            if (i == _config.ValidatorIndex)
                continue;

            // Placeholder — real PeerId will be set after handshake
            // Use deterministic placeholder based on index
            // Byte 31 = 1 ensures the key is never all-zeros (invalid for BLS12-381)
            var placeholderKey = new byte[32];
            placeholderKey[0] = (byte)i;
            placeholderKey[31] = 1;
            var pk = Ed25519Signer.GetPublicKey(placeholderKey);
            var addr = $"0x{i + 0x0100:X40}";
            var peerAddress = Address.FromHexString(addr);
            var peerStake = _stakingState?.GetStakeInfo(peerAddress)?.TotalStake ?? (UInt256)100_000;

            validators.Add(new ValidatorInfo
            {
                PeerId = PeerId.FromPublicKey(pk),
                PublicKey = pk,
                BlsPublicKey = new BlsPublicKey(_blsSigner.GetPublicKey(placeholderKey)),
                Address = peerAddress,
                Index = i,
                Stake = peerStake,
            });
        }

        // Sort by index for deterministic leader selection
        validators.Sort((a, b) => a.Index.CompareTo(b.Index));
        _validatorSet = new ValidatorSet(validators);

        _logger.LogInformation("Validator set: {Count} validators, quorum: {Quorum}",
            _validatorSet.Count, _validatorSet.QuorumThreshold);
    }

    private void SetupNetworking()
    {
        _peerManager = new PeerManager(_loggerFactory.CreateLogger<PeerManager>());
        _gossip = new GossipService(_peerManager, _loggerFactory.CreateLogger<GossipService>());
        _episub = new EpisubService(_peerManager, _loggerFactory.CreateLogger<EpisubService>());
        _transport = new TcpTransport(_loggerFactory.CreateLogger<TcpTransport>());

        // Send our validator identity as "validator-N" so peers can map us in their ValidatorSet.
        var listenAddress = $"validator-{_config.ValidatorIndex}";

        _handshake = new HandshakeProtocol(
            _config.ChainId,
            _publicKey,
            _localBlsPublicKey,
            _localPeerId,
            _config.P2PPort,
            () => _chainManager.LatestBlockNumber,
            () => _chainManager.LatestBlock?.Hash ?? Hash256.Zero,
            () => _chainManager.GetBlockByNumber(0)?.Hash ?? Hash256.Zero,
            _loggerFactory.CreateLogger<HandshakeProtocol>(),
            listenAddress);

        // Wire transport → message processing
        _transport.OnMessageReceived += HandleRawMessage;
        _transport.OnPeerConnected += HandleNewConnection;
        _transport.OnPeerDisconnected += HandlePeerDisconnected;

        // Wire episub → transport (outbound messages)
        _episub.OnSendMessage += (peerId, data) =>
        {
            _ = _transport.SendAsync(peerId, data);
        };

        // Wire gossip → transport (outbound messages)
        _gossip.OnSendMessage += (peerId, data) =>
        {
            _ = _transport.SendAsync(peerId, data);
        };

        // Wire episub → message processing
        _episub.OnMessageReceived += HandleNetworkMessage;
    }

    private void SetupConsensus()
    {
        // Wire weighted leader selection if staking is available
        if (_stakingState != null)
        {
            _leaderSelector = new WeightedLeaderSelector(_validatorSet!, _stakingState);
            _validatorSet!.SetLeaderSelector(view => _leaderSelector.SelectLeader(view));
            _logger.LogInformation("Consensus: using stake-weighted leader selection");
        }

        if (_config.UsePipelining)
        {
            SetupPipelinedConsensus();
        }
        else
        {
            SetupSequentialConsensus();
        }
    }

    private void SetupSequentialConsensus()
    {
        _consensus = new BasaltBft(
            _validatorSet!,
            _localPeerId,
            _privateKey,
            _loggerFactory.CreateLogger<BasaltBft>(),
            _blsSigner);

        // When a block is finalized by consensus, apply it
        _consensus.OnBlockFinalized += HandleBlockFinalized;

        _consensus.OnViewChange += (view) =>
        {
            _logger.LogInformation("Consensus view changed to {View}", view);
            // Clear stale proposals to prevent false double-sign detection
            // after view changes (a new leader may propose a different block
            // for a view that was previously abandoned).
            PruneProposalsByView(view);
        };

        _consensus.OnBehindDetected += (blockNumber) =>
        {
            _logger.LogWarning("Consensus detected we are behind (need block #{Block}). Triggering sync.",
                blockNumber);
            _ = Task.Run(() => TrySyncFromPeers(_cts?.Token ?? CancellationToken.None));
        };

        _logger.LogInformation("Consensus: sequential mode (BasaltBft)");
    }

    private void SetupPipelinedConsensus()
    {
        var lastFinalized = _chainManager.LatestBlockNumber;
        _pipelinedConsensus = new PipelinedConsensus(
            _validatorSet!,
            _localPeerId,
            _privateKey,
            _blsSigner,
            _loggerFactory.CreateLogger<PipelinedConsensus>(),
            lastFinalized);

        // When a block is finalized by pipelined consensus, apply it
        _pipelinedConsensus.OnBlockFinalized += HandleBlockFinalized;

        _pipelinedConsensus.OnViewChange += (view) =>
        {
            _logger.LogInformation("Pipelined consensus view changed to {View}", view);
            PruneProposalsByView(view);
        };

        _logger.LogInformation("Consensus: pipelined mode (PipelinedConsensus)");
    }

    private void HandleBlockFinalized(Hash256 hash, byte[] blockData)
    {
        try
        {
            var block = BlockCodec.DeserializeBlock(blockData);

            // Execute transactions if we didn't propose this block
            var weProposed = hash == _myProposedBlockHash;
            if (!weProposed && block.Transactions.Count > 0)
            {
                _logger.LogInformation("Executing {TxCount} txs from block #{Number} (proposed by other validator)",
                    block.Transactions.Count, block.Number);
                for (int i = 0; i < block.Transactions.Count; i++)
                {
                    _txExecutor!.Execute(block.Transactions[i], _stateDb, block.Header, i);
                }
            }
            else if (weProposed && block.Transactions.Count > 0)
            {
                _logger.LogInformation("Block #{Number} finalized with {TxCount} txs (we proposed, state already applied)",
                    block.Number, block.Transactions.Count);
            }
            _myProposedBlockHash = Hash256.Zero;

            var result = _chainManager.AddBlock(block);
            if (result.IsSuccess)
            {
                _mempool.RemoveConfirmed(block.Transactions);
                MetricsEndpoint.RecordBlock(block.Transactions.Count, block.Header.Timestamp);
                _ = _wsHandler.BroadcastNewBlock(block);

                _lastBlockFinalizedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                // Clear proposal history to prevent false double-sign detection.
                // View numbers reset with each new block (StartRound sets view = blockNumber),
                // so proposals from previous blocks' view changes must not carry over.
                _proposalsByView.Clear();

                // Announce finalized block to peers so their BestBlockNumber stays
                // current.  Without this, TrySyncFromPeers can't find an up-to-date
                // peer and validators that fall behind are unable to catch up.
                _gossip!.BroadcastBlock(block.Number, block.Hash, block.Header.ParentHash);

                // Persist to RocksDB if available
                PersistBlock(block, blockData);

                // Check for inactive validators and slash
                CheckInactiveValidators(block.Number);

                _logger.LogInformation(
                    "Block #{Number} finalized via consensus. Hash: {Hash}, Txs: {TxCount}",
                    block.Number, hash.ToHexString()[..18] + "...", block.Transactions.Count);

                // In sequential mode, start next round
                if (!_config.UsePipelining)
                    _consensus!.StartRound(block.Number + 1);

                // In pipelined mode, cleanup finalized rounds
                _pipelinedConsensus?.CleanupFinalizedRounds();
            }
            else
            {
                _logger.LogError("Failed to add consensus-finalized block #{Number}: {Error}",
                    block.Number, result.Message);

                // If we're behind (block number > our tip + 1), trigger a sync
                // to catch up on missed blocks before the next round.
                if (block.Number > _chainManager.LatestBlockNumber + 1)
                {
                    _logger.LogWarning(
                        "We are behind (at #{Local}, need #{Need}). Triggering sync.",
                        _chainManager.LatestBlockNumber, block.Number);
                    _ = Task.Run(() => TrySyncFromPeers(_cts?.Token ?? CancellationToken.None));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying finalized block");
        }
    }

    private void SetupBlockProduction()
    {
        _proposerAddress = Address.FromHexString(_config.ValidatorAddress.Length > 0
            ? _config.ValidatorAddress
            : $"0x{_config.ValidatorIndex:X40}");

        _blockBuilder = new BlockBuilder(_chainParams, _loggerFactory.CreateLogger<BlockBuilder>());

        IContractRuntime contractRuntime = _config.UseSandbox
            ? new SandboxedContractRuntime(new SandboxConfiguration())
            : new ManagedContractRuntime();

        _txExecutor = new TransactionExecutor(_chainParams, contractRuntime);

        if (_config.UseSandbox)
            _logger.LogInformation("Contract execution: sandboxed mode (AssemblyLoadContext isolation)");

        // No BlockProductionLoop in consensus mode — blocks are built
        // on-demand by the leader and finalized through BFT consensus.
    }

    /// <summary>
    /// Builds a block (leader only) and proposes it for consensus.
    /// Does NOT add to ChainManager — that happens only on finalization.
    /// </summary>
    private void TryProposeBlock()
    {
        if (_config.UsePipelining)
        {
            TryProposeBlockPipelined();
        }
        else
        {
            TryProposeBlockSequential();
        }
    }

    private void TryProposeBlockSequential()
    {
        if (!_consensus!.IsLeader || _consensus.State != ConsensusState.Proposing)
            return;

        // Block time pacing: don't propose until BlockTimeMs has elapsed since last finalization
        var elapsedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _lastBlockFinalizedAtMs;
        if (elapsedMs < _chainParams.BlockTimeMs)
            return;

        var parentBlock = _chainManager.LatestBlock;
        if (parentBlock == null)
            return;

        var pendingTxs = _mempool.GetPending((int)_chainParams.MaxTransactionsPerBlock);
        var block = _blockBuilder!.BuildBlock(pendingTxs, _stateDb, parentBlock.Header, _proposerAddress);

        var blockData = BlockCodec.SerializeBlock(block);
        var proposal = _consensus.ProposeBlock(blockData, block.Hash);

        if (proposal != null)
        {
            _myProposedBlockHash = block.Hash;
            _gossip!.BroadcastConsensusMessage(proposal);
            _logger.LogInformation("Proposed block #{Number} for consensus. Hash: {Hash}, Mempool: {MempoolCount}, BlockTxs: {BlockTxs}",
                block.Number, block.Hash.ToHexString()[..18] + "...", pendingTxs.Count, block.Transactions.Count);
        }
    }

    private void TryProposeBlockPipelined()
    {
        // Block time pacing
        var elapsedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _lastBlockFinalizedAtMs;
        if (elapsedMs < _chainParams.BlockTimeMs)
            return;

        // Determine next block number for pipeline
        var nextBlock = _chainManager.LatestBlockNumber + 1 + (ulong)_pipelinedConsensus!.ActiveRoundCount;

        // Check if we're the leader for this block
        var leader = _validatorSet!.GetLeader(nextBlock);
        if (leader.PeerId != _localPeerId)
            return;

        var parentBlock = _chainManager.LatestBlock;
        if (parentBlock == null)
            return;

        var pendingTxs = _mempool.GetPending((int)_chainParams.MaxTransactionsPerBlock);
        var block = _blockBuilder!.BuildBlock(pendingTxs, _stateDb, parentBlock.Header, _proposerAddress);

        var blockData = BlockCodec.SerializeBlock(block);
        var proposal = _pipelinedConsensus.StartRound(nextBlock, blockData, block.Hash);

        if (proposal != null)
        {
            _myProposedBlockHash = block.Hash;
            _gossip!.BroadcastConsensusMessage(proposal);
            _logger.LogInformation("Proposed pipelined block #{Number}. Active rounds: {Active}",
                nextBlock, _pipelinedConsensus.ActiveRoundCount);
        }
    }

    private async void HandleNewConnection(PeerConnection connection)
    {
        try
        {
            // Perform handshake as responder
            var result = await _handshake!.RespondAsync(connection, _cts!.Token);
            if (!result.IsSuccess)
            {
                _logger.LogWarning("Inbound handshake failed: {Error}", result.Error);
                connection.Dispose();
                return;
            }

            // Update connection with real PeerId — may fail if we already
            // have an outbound connection to this peer (simultaneous connect)
            var oldId = connection.PeerId;
            if (!_transport!.UpdatePeerId(oldId, result.PeerId))
            {
                _logger.LogInformation("Already connected to peer {PeerId} via outbound; skipping duplicate", result.PeerId);
                return;
            }

            // Register in peer manager
            _peerManager!.RegisterPeer(new PeerInfo
            {
                Id = result.PeerId,
                PublicKey = result.PeerPublicKey,
                Host = result.PeerHost,
                Port = result.PeerPort,
            });

            _peerManager.UpdatePeerBestBlock(result.PeerId, result.PeerBestBlock, result.PeerBestBlockHash);
            _episub!.OnPeerConnected(result.PeerId);

            // Update validator set with real PeerId (replaces placeholder)
            // PeerHost comes from the peer's Hello.ListenAddress (Docker hostname, e.g. "validator-1")
            if (TryParseValidatorIndex(result.PeerHost, out var peerValidatorIndex))
            {
                _validatorSet!.UpdateValidatorIdentity(peerValidatorIndex, result.PeerId, result.PeerPublicKey, result.PeerBlsPublicKey);
                _logger.LogInformation("Updated validator {Index} identity (inbound): {PeerId}", peerValidatorIndex, result.PeerId);
            }

            _logger.LogInformation("Peer {PeerId} connected (inbound) from {Host}, best block: #{BestBlock}",
                result.PeerId, result.PeerHost, result.PeerBestBlock);

            // Start reading messages
            _transport!.StartReadLoop(connection);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling inbound connection");
            connection.Dispose();
        }
    }

    private void HandlePeerDisconnected(PeerId peerId)
    {
        _peerManager!.DisconnectPeer(peerId, "Connection closed");
        _episub!.OnPeerDisconnected(peerId);
        _logger.LogInformation("Peer {PeerId} disconnected", peerId);
    }

    /// <summary>
    /// Extract validator index from Docker-style hostname (e.g., "validator-2" → 2).
    /// </summary>
    private static bool TryParseValidatorIndex(string hostname, out int index)
    {
        index = -1;
        const string prefix = "validator-";
        if (!hostname.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        return int.TryParse(hostname.AsSpan(prefix.Length), out index);
    }

    private void HandleRawMessage(PeerId sender, byte[] data)
    {
        try
        {
            var message = MessageCodec.Deserialize(data);
            HandleNetworkMessage(sender, message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize message from {PeerId}", sender);
        }
    }

    private void HandleNetworkMessage(PeerId sender, NetworkMessage message)
    {
        _logger.LogDebug("Received {Type} from {Sender}", message.Type, sender);

        switch (message)
        {
            case TxAnnounceMessage txAnnounce:
                HandleTxAnnounce(sender, txAnnounce);
                break;

            case TxPayloadMessage txPayload:
                HandleTxPayload(sender, txPayload);
                break;

            case BlockAnnounceMessage blockAnnounce:
                HandleBlockAnnounce(sender, blockAnnounce);
                break;

            case BlockPayloadMessage blockPayload:
                HandleBlockPayload(sender, blockPayload);
                break;

            case TxRequestMessage txRequest:
                HandleTxRequest(sender, txRequest);
                break;

            case BlockRequestMessage blockRequest:
                HandleBlockRequest(sender, blockRequest);
                break;

            case SyncRequestMessage syncRequest:
                HandleSyncRequest(sender, syncRequest);
                break;

            case SyncResponseMessage syncResponse:
                HandleSyncResponse(sender, syncResponse);
                break;

            case ConsensusProposalMessage proposal:
                HandleConsensusProposal(sender, proposal);
                break;

            case ConsensusVoteMessage vote:
                HandleConsensusVote(sender, vote);
                break;

            case ViewChangeMessage viewChange:
                ViewChangeMessage? autoJoinVc;
                if (_config.UsePipelining)
                    autoJoinVc = _pipelinedConsensus!.HandleViewChange(viewChange);
                else
                    autoJoinVc = _consensus!.HandleViewChange(viewChange);
                // Broadcast auto-join so other nodes can count our vote
                if (autoJoinVc != null)
                    _gossip!.BroadcastConsensusMessage(autoJoinVc);
                break;

            case IHaveMessage ihave:
                foreach (var id in ihave.MessageIds)
                    _episub!.HandleIHave(sender, id);
                break;

            case IWantMessage iwant:
                foreach (var (id, data) in _episub!.HandleIWant(sender, iwant.MessageIds))
                    _ = _transport!.SendAsync(sender, data);
                break;

            case GraftMessage:
                _episub!.GraftPeer(sender);
                break;

            case PruneMessage:
                _episub!.PrunePeer(sender);
                break;

            case PingMessage:
                var pong = new PongMessage { SenderId = _localPeerId };
                _ = _transport!.SendAsync(sender, MessageCodec.Serialize(pong));
                break;

            default:
                _logger.LogDebug("Unhandled message type {Type} from {Sender}", message.Type, sender);
                break;
        }
    }

    private void HandleTxAnnounce(PeerId sender, TxAnnounceMessage announce)
    {
        // Request full transactions we don't have
        var missing = new List<Hash256>();
        foreach (var hash in announce.TransactionHashes)
        {
            if (!_mempool.Contains(hash))
                missing.Add(hash);
        }

        if (missing.Count > 0)
        {
            var request = new TxRequestMessage
            {
                SenderId = _localPeerId,
                TransactionHashes = missing.ToArray(),
            };
            _gossip!.SendToPeer(sender, request);
        }
    }

    private void HandleTxPayload(PeerId sender, TxPayloadMessage payload)
    {
        foreach (var txBytes in payload.Transactions)
        {
            try
            {
                var tx = BlockCodec.DeserializeTransaction(txBytes);
                var validationResult = _txValidator.Validate(tx, _stateDb);
                if (validationResult.IsSuccess)
                {
                    if (_mempool.Add(tx, raiseEvent: false))
                    {
                        // Re-announce to other peers (skip sender)
                        _gossip!.BroadcastTransaction(tx.Hash, sender);
                    }
                }
                else
                {
                    _logger.LogWarning("Rejected gossipped tx {Hash} from {Sender}: {Error}",
                        tx.Hash.ToHexString()[..18] + "...", sender, validationResult.Message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to process transaction from {Sender}", sender);
            }
        }
    }

    private void HandleTxRequest(PeerId sender, TxRequestMessage request)
    {
        var txDataList = new List<byte[]>();
        foreach (var hash in request.TransactionHashes)
        {
            var tx = _mempool.Get(hash);
            if (tx != null)
                txDataList.Add(BlockCodec.SerializeTransaction(tx));
        }

        if (txDataList.Count > 0)
        {
            var payload = new TxPayloadMessage
            {
                SenderId = _localPeerId,
                Transactions = txDataList.ToArray(),
            };
            _gossip!.SendToPeer(sender, payload);
        }
    }

    private void HandleBlockRequest(PeerId sender, BlockRequestMessage request)
    {
        var blockDataList = new List<byte[]>();
        for (ulong i = request.StartNumber; i < request.StartNumber + (ulong)request.Count; i++)
        {
            var block = _chainManager.GetBlockByNumber(i);
            if (block != null)
                blockDataList.Add(BlockCodec.SerializeBlock(block));
            else
                break;
        }

        if (blockDataList.Count > 0)
        {
            var payload = new BlockPayloadMessage
            {
                SenderId = _localPeerId,
                Blocks = blockDataList.ToArray(),
            };
            _gossip!.SendToPeer(sender, payload);
        }
    }

    private void HandleBlockAnnounce(PeerId sender, BlockAnnounceMessage announce)
    {
        // If we don't have this block, request it
        if (_chainManager.GetBlockByHash(announce.BlockHash) == null)
        {
            var request = new BlockRequestMessage
            {
                SenderId = _localPeerId,
                StartNumber = announce.BlockNumber,
                Count = 1,
            };
            _gossip!.SendToPeer(sender, request);
        }

        _peerManager!.UpdatePeerBestBlock(sender, announce.BlockNumber, announce.BlockHash);
    }

    private void HandleBlockPayload(PeerId sender, BlockPayloadMessage payload)
    {
        foreach (var blockBytes in payload.Blocks)
        {
            try
            {
                var block = BlockCodec.DeserializeBlock(blockBytes);

                // Only apply if it's the next block in our chain
                if (block.Number != _chainManager.LatestBlockNumber + 1)
                    continue;

                // Execute transactions
                for (int i = 0; i < block.Transactions.Count; i++)
                {
                    _txExecutor!.Execute(block.Transactions[i], _stateDb, block.Header, i);
                }

                var result = _chainManager.AddBlock(block);
                if (result.IsSuccess)
                {
                    _mempool.RemoveConfirmed(block.Transactions);
                    PersistBlock(block, blockBytes);
                    _logger.LogInformation("Applied block #{Number} from peer", block.Number);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to process block from {Sender}", sender);
            }
        }
    }

    private void HandleSyncRequest(PeerId sender, SyncRequestMessage request)
    {
        var blockDataList = new List<byte[]>();
        for (ulong i = request.FromBlock; i < request.FromBlock + (ulong)request.MaxBlocks; i++)
        {
            // Try to serve from BlockStore (raw bytes) first, fall back to ChainManager
            byte[]? rawBlock = _blockStore?.GetRawBlockByNumber(i);
            if (rawBlock != null)
            {
                blockDataList.Add(rawBlock);
            }
            else
            {
                var block = _chainManager.GetBlockByNumber(i);
                if (block != null)
                    blockDataList.Add(BlockCodec.SerializeBlock(block));
                else
                    break;
            }
        }

        if (blockDataList.Count > 0)
        {
            var response = new SyncResponseMessage
            {
                SenderId = _localPeerId,
                Blocks = blockDataList.ToArray(),
            };
            _gossip!.SendToPeer(sender, response);
            _logger.LogInformation("Served {Count} blocks to syncing peer {PeerId} (from #{From})",
                blockDataList.Count, sender, request.FromBlock);
        }
    }

    private void HandleSyncResponse(PeerId sender, SyncResponseMessage response)
    {
        var applied = 0;
        foreach (var blockBytes in response.Blocks)
        {
            try
            {
                var block = BlockCodec.DeserializeBlock(blockBytes);

                if (block.Number != _chainManager.LatestBlockNumber + 1)
                    continue;

                // Execute transactions
                for (int i = 0; i < block.Transactions.Count; i++)
                {
                    _txExecutor!.Execute(block.Transactions[i], _stateDb, block.Header, i);
                }

                var result = _chainManager.AddBlock(block);
                if (result.IsSuccess)
                {
                    _mempool.RemoveConfirmed(block.Transactions);
                    PersistBlock(block, blockBytes);
                    applied++;
                }
                else
                {
                    _logger.LogWarning("Failed to apply synced block #{Number}: {Error}", block.Number, result.Message);
                    break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process synced block from {Sender}", sender);
                break;
            }
        }

        if (applied > 0)
        {
            _logger.LogInformation("Synced {Count} blocks, now at #{Height}", applied, _chainManager.LatestBlockNumber);
        }

        // Signal the sync loop: true = progress made, false = stalled (no blocks applied)
        _syncBatchTcs?.TrySetResult(applied > 0);
    }

    private void HandleConsensusProposal(PeerId sender, ConsensusProposalMessage proposal)
    {
        // Double-sign detection: only for proposals matching the current block number.
        // View numbers are reused across blocks (StartRound sets view = blockNumber),
        // so delayed proposals from previous blocks can arrive after _proposalsByView
        // was cleared and collide with current-round entries, causing false positives.
        var currentBlock = _config.UsePipelining
            ? _pipelinedConsensus!.LastFinalizedBlock + 1
            : _consensus!.CurrentBlockNumber;

        if (proposal.BlockNumber == currentBlock)
        {
            var proposalKey = (proposal.ViewNumber, proposal.SenderId);
            if (_slashingEngine != null && _proposalsByView.TryGetValue(proposalKey, out var existingHash))
            {
                if (existingHash != proposal.BlockHash)
                {
                    var proposerInfo = _validatorSet?.GetByPeerId(proposal.SenderId);
                    if (proposerInfo != null)
                    {
                        _slashingEngine.SlashDoubleSign(proposerInfo.Address, proposal.BlockNumber, existingHash, proposal.BlockHash);
                        _logger.LogWarning("Double-sign detected from validator {Address} at view {View}",
                            proposerInfo.Address, proposal.ViewNumber);
                    }
                }
            }
            _proposalsByView[proposalKey] = proposal.BlockHash;
        }

        ConsensusVoteMessage? vote;
        if (_config.UsePipelining)
            vote = _pipelinedConsensus!.HandleProposal(proposal);
        else
            vote = _consensus!.HandleProposal(proposal);

        if (vote != null)
        {
            _gossip!.BroadcastConsensusMessage(vote);
        }
    }

    private void HandleConsensusVote(PeerId sender, ConsensusVoteMessage vote)
    {
        // Track validator activity for inactivity slashing
        var voterInfo = _validatorSet?.GetByPeerId(vote.SenderId);
        if (voterInfo != null)
            _lastActiveBlock[voterInfo.Address] = _chainManager.LatestBlockNumber;

        ConsensusVoteMessage? response;
        if (_config.UsePipelining)
            response = _pipelinedConsensus!.HandleVote(vote);
        else
            response = _consensus!.HandleVote(vote);

        if (response != null)
        {
            _gossip!.BroadcastConsensusMessage(response);
        }
    }

    private async Task ConnectToStaticPeers()
    {
        foreach (var peerEndpoint in _config.Peers)
        {
            var parts = peerEndpoint.Split(':');
            var host = parts[0];
            var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 30303;

            try
            {
                // Skip peers we're already connected to (by checking transport connections)
                var connectedPeers = _transport!.ConnectedPeerIds;
                bool alreadyConnected = false;
                foreach (var existingPeerId in connectedPeers)
                {
                    var peerInfo = _peerManager!.GetPeer(existingPeerId);
                    if (peerInfo != null && peerInfo.Host == host && peerInfo.Port == port)
                    {
                        alreadyConnected = true;
                        break;
                    }
                }
                if (alreadyConnected)
                    continue;

                var connection = await _transport!.ConnectAsync(host, port);
                if (connection == null)
                {
                    _logger.LogWarning("Failed to connect to {Host}:{Port}", host, port);
                    continue;
                }

                // Perform handshake as initiator
                var result = await _handshake!.InitiateAsync(connection, _cts!.Token);
                if (!result.IsSuccess)
                {
                    _logger.LogWarning("Handshake with {Host}:{Port} failed: {Error}", host, port, result.Error);
                    connection.Dispose();
                    continue;
                }

                // Update connection PeerId — may fail if we already have an
                // inbound connection from this peer (simultaneous connect)
                var oldId = connection.PeerId;
                if (!_transport.UpdatePeerId(oldId, result.PeerId))
                {
                    _logger.LogInformation("Already connected to peer {PeerId} via inbound; skipping duplicate", result.PeerId);
                    continue;
                }

                // Register peer
                _peerManager!.RegisterPeer(new PeerInfo
                {
                    Id = result.PeerId,
                    PublicKey = result.PeerPublicKey,
                    Host = host,
                    Port = result.PeerPort > 0 ? result.PeerPort : port,
                });

                _peerManager.UpdatePeerBestBlock(result.PeerId, result.PeerBestBlock, result.PeerBestBlockHash);
                _episub!.OnPeerConnected(result.PeerId);

                // Update validator set with real PeerId (replaces placeholder)
                if (TryParseValidatorIndex(host, out var peerValidatorIndex))
                {
                    _validatorSet!.UpdateValidatorIdentity(peerValidatorIndex, result.PeerId, result.PeerPublicKey, result.PeerBlsPublicKey);
                    _logger.LogInformation("Updated validator {Index} identity: {PeerId}", peerValidatorIndex, result.PeerId);
                }

                _logger.LogInformation("Connected to peer {PeerId} at {Host}:{Port}, best block: #{BestBlock}",
                    result.PeerId, host, port, result.PeerBestBlock);

                // Start reading messages
                _transport!.StartReadLoop(connection);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to connect to {Host}:{Port}", host, port);
            }
        }
    }

    private async Task ReconnectLoop(CancellationToken ct)
    {
        // Wait for initial connections to settle
        await Task.Delay(3000, ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var expectedPeerCount = _config.Peers.Length;
                var connectedCount = _peerManager!.ConnectedCount;

                if (connectedCount < expectedPeerCount)
                {
                    _logger.LogInformation("Only {Connected}/{Expected} peers connected, reconnecting...",
                        connectedCount, expectedPeerCount);
                    await ConnectToStaticPeers();
                }

                await Task.Delay(5000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in reconnection loop");
            }
        }
    }

    private async Task RunConsensusLoop(CancellationToken ct)
    {
        // Wait for peers to connect
        await Task.Delay(2000, ct);

        // Check if we need to sync before joining consensus
        await TrySyncFromPeers(ct);

        if (_config.UsePipelining)
        {
            await RunPipelinedConsensusLoop(ct);
        }
        else
        {
            await RunSequentialConsensusLoop(ct);
        }
    }

    private async Task RunSequentialConsensusLoop(CancellationToken ct)
    {
        // Start first consensus round
        var nextBlock = _chainManager.LatestBlockNumber + 1;
        _consensus!.StartRound(nextBlock);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(200, ct);

                if (_isSyncing)
                    continue;

                TryProposeBlock();

                var viewChange = _consensus.CheckViewTimeout();
                if (viewChange != null)
                {
                    var autoJoin = _consensus.HandleViewChange(viewChange);
                    _gossip!.BroadcastConsensusMessage(viewChange);
                    if (autoJoin != null)
                        _gossip.BroadcastConsensusMessage(autoJoin);
                }

                _episub!.RebalanceTiers();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in consensus loop");
            }
        }
    }

    private async Task RunPipelinedConsensusLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(200, ct);

                if (_isSyncing)
                    continue;

                // In pipelined mode, try to propose next block if pipeline has capacity
                TryProposeBlock();

                // Check for round timeouts
                var viewChange = _pipelinedConsensus!.CheckViewTimeout();
                if (viewChange != null)
                {
                    var autoJoin = _pipelinedConsensus.HandleViewChange(viewChange);
                    _gossip!.BroadcastConsensusMessage(viewChange);
                    if (autoJoin != null)
                        _gossip.BroadcastConsensusMessage(autoJoin);
                }

                // Cleanup finalized rounds periodically
                _pipelinedConsensus.CleanupFinalizedRounds();

                _episub!.RebalanceTiers();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in pipelined consensus loop");
            }
        }
    }

    private async Task TrySyncFromPeers(CancellationToken ct)
    {
        // Find the best block among connected peers
        var bestPeer = GetBestPeer();
        if (bestPeer == null)
            return;

        var localHeight = _chainManager.LatestBlockNumber;
        var peerHeight = bestPeer.BestBlockNumber;

        if (peerHeight <= localHeight)
            return;

        _isSyncing = true;
        _logger.LogInformation("Starting sync from block #{Local} to #{Peer} from peer {PeerId}",
            localHeight, peerHeight, bestPeer.Id);

        try
        {
            var currentBlock = localHeight + 1;
            while (currentBlock <= peerHeight && !ct.IsCancellationRequested)
            {
                var batchCount = (int)Math.Min(SyncBatchSize, peerHeight - currentBlock + 1);

                _syncBatchTcs = new TaskCompletionSource<bool>();
                var request = new SyncRequestMessage
                {
                    SenderId = _localPeerId,
                    FromBlock = currentBlock,
                    MaxBlocks = batchCount,
                };
                _gossip!.SendToPeer(bestPeer.Id, request);

                // Wait for response with timeout
                var completed = await Task.WhenAny(
                    _syncBatchTcs.Task,
                    Task.Delay(TimeSpan.FromSeconds(10), ct));

                if (completed != _syncBatchTcs.Task)
                {
                    _logger.LogWarning("Sync batch timed out at block #{Block}", currentBlock);
                    break;
                }

                // Check if the batch made progress (applied at least 1 block)
                var madeProgress = await _syncBatchTcs.Task;
                if (!madeProgress)
                {
                    _logger.LogWarning(
                        "Sync stalled at block #{Block} — possible chain fork or invalid blocks from peer {Peer}",
                        currentBlock, bestPeer.Id);
                    break;
                }

                // Update current position after successful batch
                currentBlock = _chainManager.LatestBlockNumber + 1;

                // Re-check if peer has more blocks
                bestPeer = GetBestPeer();
                if (bestPeer == null || bestPeer.BestBlockNumber <= _chainManager.LatestBlockNumber)
                    break;
                peerHeight = bestPeer.BestBlockNumber;
            }

            _logger.LogInformation("Sync complete. Local chain at block #{Height}", _chainManager.LatestBlockNumber);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Sync cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during sync");
        }
        finally
        {
            _isSyncing = false;
            _syncBatchTcs = null;
        }
    }

    /// <summary>
    /// Remove proposal entries for views older than <paramref name="currentView"/>
    /// to prevent unbounded growth and avoid false double-sign detection when
    /// a view number is reused after a view change.
    /// </summary>
    private void PruneProposalsByView(ulong currentView)
    {
        var staleKeys = new List<(ulong, PeerId)>();
        foreach (var key in _proposalsByView.Keys)
        {
            if (key.View < currentView)
                staleKeys.Add(key);
        }
        foreach (var key in staleKeys)
            _proposalsByView.Remove(key);
    }

    private PeerInfo? GetBestPeer()
    {
        PeerInfo? best = null;
        foreach (var peer in _peerManager!.ConnectedPeers)
        {
            if (best == null || peer.BestBlockNumber > best.BestBlockNumber)
                best = peer;
        }
        return best;
    }

    private void CheckInactiveValidators(ulong currentBlock)
    {
        if (_slashingEngine == null || _validatorSet == null || currentBlock < InactivityThresholdBlocks)
            return;

        foreach (var validator in _validatorSet.Validators)
        {
            if (validator.PeerId == _localPeerId)
                continue; // Don't slash ourselves

            if (!_lastActiveBlock.TryGetValue(validator.Address, out var lastActive))
                lastActive = 0;

            if (currentBlock - lastActive > InactivityThresholdBlocks)
            {
                _slashingEngine.SlashInactivity(validator.Address, lastActive, currentBlock);
            }
        }
    }

    private void PersistBlock(Block block, byte[] serializedBlockData)
    {
        if (_blockStore == null)
            return;

        try
        {
            var blockData = new BlockData
            {
                Number = block.Number,
                Hash = block.Hash,
                ParentHash = block.Header.ParentHash,
                StateRoot = block.Header.StateRoot,
                TransactionsRoot = block.Header.TransactionsRoot,
                ReceiptsRoot = block.Header.ReceiptsRoot,
                Timestamp = block.Header.Timestamp,
                Proposer = block.Header.Proposer,
                ChainId = block.Header.ChainId,
                GasUsed = block.Header.GasUsed,
                GasLimit = block.Header.GasLimit,
                ProtocolVersion = block.Header.ProtocolVersion,
                ExtraData = block.Header.ExtraData,
                TransactionHashes = block.Transactions.Select(t => t.Hash).ToArray(),
            };
            _blockStore.PutFullBlock(blockData, serializedBlockData);
            _blockStore.SetLatestBlockNumber(block.Number);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist block #{Number}", block.Number);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
