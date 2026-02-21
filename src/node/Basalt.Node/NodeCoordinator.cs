using System.Collections.Concurrent;
using System.Security.Cryptography;
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
    // N-05: StateDbRef allows fork-and-swap during sync while keeping the
    // API layer (which shares this reference) in sync with canonical state.
    private readonly StateDbRef _stateDb;
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

    // Compliance
    private readonly IComplianceVerifier? _complianceVerifier;
    private WeightedLeaderSelector? _leaderSelector;

    // Network components
    private TcpTransport? _transport;
    private PeerManager? _peerManager;
    private EpisubService? _episub;
    private GossipService? _gossip;
    /// <summary>
    /// Creates a fresh HandshakeProtocol per connection to avoid sharing ephemeral key state.
    /// Each handshake generates its own X25519 key pair, so concurrent connections must not
    /// share the same instance.
    /// </summary>
    private HandshakeProtocol CreateHandshake() => new(
        _config.ChainId,
        _privateKey,
        _publicKey,
        _localBlsPublicKey,
        _localPeerId,
        _config.P2PPort,
        () => _chainManager.LatestBlockNumber,
        () => _chainManager.LatestBlock?.Hash ?? Hash256.Zero,
        () => _chainManager.GetBlockByNumber(0)?.Hash ?? Hash256.Zero,
        _loggerFactory.CreateLogger<HandshakeProtocol>(),
        $"validator-{_config.ValidatorIndex}");

    // Consensus
    private BasaltBft? _consensus;
    private PipelinedConsensus? _pipelinedConsensus;
    private ValidatorSet? _validatorSet;
    private IBlsSigner _blsSigner = new BlsSigner();
    private EpochManager? _epochManager;

    // Block production (consensus-driven — no BlockProductionLoop)
    private BlockBuilder? _blockBuilder;
    private TransactionExecutor? _txExecutor;
    private Address _proposerAddress;

    // Runtime
    private CancellationTokenSource? _cts;
    private Task? _consensusLoop;
    private long _lastBlockFinalizedAtMs;

    // Sync state
    // N-09: Atomic sync guard — use int + Interlocked.CompareExchange instead of bool
    private int _isSyncing;
    private TaskCompletionSource<bool>? _syncBatchTcs;
    private const int SyncBatchSize = 50;

    // N-03: Cap sync response to prevent a peer from requesting unbounded blocks
    private const int MaxSyncResponseBlocks = 100;

    // N-14: Cap block request count to prevent resource exhaustion
    private const int MaxBlockRequestCount = 100;

    // N-17: Thread-safe double-sign detection: keyed by (view, block, proposer).
    // Block number is included because view numbers can collide across blocks:
    // after a view change bumps view to V, and then StartRound(V) reuses the same
    // view number for the next block, old entries from the previous block would
    // cause false double-sign detection without the block dimension.
    private readonly ConcurrentDictionary<(ulong View, ulong Block, PeerId Proposer), Hash256> _proposalsByView = new();

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
        StateDbRef stateDb,
        TransactionValidator txValidator,
        WebSocketHandler wsHandler,
        ILoggerFactory loggerFactory,
        BlockStore? blockStore = null,
        ReceiptStore? receiptStore = null,
        StakingState? stakingState = null,
        SlashingEngine? slashingEngine = null,
        IComplianceVerifier? complianceVerifier = null)
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
        _complianceVerifier = complianceVerifier;
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
            // N-01: Use cryptographically secure RNG for dev mode key generation
            _privateKey = new byte[32];
            RandomNumberGenerator.Fill(_privateKey);
            _logger.LogWarning("No validator key configured, using random key (dev mode)");
        }
        else
        {
            var hex = _config.ValidatorKeyHex.StartsWith("0x")
                ? _config.ValidatorKeyHex[2..]
                : _config.ValidatorKeyHex;
            _privateKey = Convert.FromHexString(hex);

            // N-13: Validate key length — Ed25519 private keys must be exactly 32 bytes
            if (_privateKey.Length != 32)
                throw new InvalidOperationException($"BASALT_VALIDATOR_KEY must be exactly 32 bytes (64 hex chars), got {_privateKey.Length} bytes");

            // N-07: Reject trivially weak validator keys
            ValidateKeyEntropy(_privateKey, "BASALT_VALIDATOR_KEY");
        }

        _publicKey = Ed25519Signer.GetPublicKey(_privateKey);
        _localBlsPublicKey = new BlsPublicKey(_blsSigner.GetPublicKey(_privateKey));
        _localPeerId = PeerId.FromPublicKey(_publicKey);
    }

    // N-07: Reject trivially weak validator keys (all zeros, all same byte, sequential)
    private static void ValidateKeyEntropy(byte[] key, string keyName)
    {
        if (key.All(b => b == 0))
            throw new InvalidOperationException($"{keyName} is all zeros — this is not a valid key");
        if (key.Distinct().Count() <= 2)
            throw new InvalidOperationException($"{keyName} has very low entropy (<=2 distinct bytes) — generate a proper key");
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
        // HandshakeProtocol is now created per-connection via CreateHandshake()
        // to avoid sharing ephemeral X25519 key state across concurrent handshakes.

        // Wire transport → message processing
        _transport.OnMessageReceived += HandleRawMessage;
        _transport.OnPeerConnected += HandleNewConnection;
        _transport.OnPeerDisconnected += HandlePeerDisconnected;

        // NET-C04: Wire peer ban → transport disconnect
        _peerManager.OnPeerBanned += peerId => _transport.DisconnectPeer(peerId);

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
            _leaderSelector = new WeightedLeaderSelector(_validatorSet!);
            _validatorSet!.SetLeaderSelector(view => _leaderSelector.SelectLeader(view));
            _logger.LogInformation("Consensus: using stake-weighted leader selection");

            // Setup epoch manager for dynamic validator set transitions
            _epochManager = new EpochManager(_chainParams, _stakingState, _validatorSet, _blsSigner,
                _slashingEngine, _loggerFactory.CreateLogger<EpochManager>());

            // Seed epoch state from chain height and replay persisted commit bitmaps
            // so that epoch-boundary slashing is deterministic across restarts
            var chainHeight = _chainManager.LatestBlockNumber;
            if (chainHeight > 0)
                _epochManager.SeedFromChainHeight(chainHeight, blockNum => _blockStore?.GetCommitBitmap(blockNum));

            _logger.LogInformation("Epoch manager: epoch length={EpochLength}, validator set size={SetSize}",
                _chainParams.EpochLength, _chainParams.ValidatorSetSize);
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
            _blsSigner,
            chainId: _chainParams.ChainId);

        // When a block is finalized by consensus, apply it
        _consensus.OnBlockFinalized += HandleBlockFinalized;

        // When the leader builds an aggregate QC, broadcast to all peers
        _consensus.OnAggregateVote += aggregate =>
            _gossip!.BroadcastConsensusMessage(aggregate);

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
            lastFinalized,
            _chainParams.ChainId);

        // When a block is finalized by pipelined consensus, apply it
        _pipelinedConsensus.OnBlockFinalized += HandleBlockFinalized;

        _pipelinedConsensus.OnViewChange += (view) =>
        {
            _logger.LogInformation("Pipelined consensus view changed to {View}", view);
            PruneProposalsByView(view);
        };

        _pipelinedConsensus.OnBehindDetected += (blockNumber) =>
        {
            _logger.LogWarning("Pipelined consensus detected we are behind (need block #{Block}). Triggering sync.",
                blockNumber);
            _ = Task.Run(() => TrySyncFromPeers(_cts?.Token ?? CancellationToken.None));
        };

        _logger.LogInformation("Consensus: pipelined mode (PipelinedConsensus)");
    }

    private void HandleBlockFinalized(Hash256 hash, byte[] blockData, ulong commitBitmap)
    {
        try
        {
            var block = BlockCodec.DeserializeBlock(blockData);

            // COMPL-07: Reset nullifiers at block boundary to bound memory and prevent same-block replay
            _complianceVerifier?.ResetNullifiers();

            // All validators execute finalized transactions against canonical state.
            // Proposals use a forked state, so the leader's live state is never speculatively mutated.
            if (block.Transactions.Count > 0)
            {
                var receipts = new List<TransactionReceipt>(block.Transactions.Count);
                for (int i = 0; i < block.Transactions.Count; i++)
                {
                    var receipt = _txExecutor!.Execute(block.Transactions[i], _stateDb, block.Header, i);
                    receipts.Add(receipt);
                }
                block.Receipts = receipts;
            }

            var result = _chainManager.AddBlock(block);
            if (result.IsSuccess)
            {
                _mempool.RemoveConfirmed(block.Transactions);

                // Prune stale/underpriced transactions that can no longer be included
                var pruned = _mempool.PruneStale(_stateDb, block.Header.BaseFee);
                if (pruned > 0)
                    _logger.LogInformation("Pruned {Count} stale/underpriced transactions from mempool", pruned);

                MetricsEndpoint.RecordBlock(block.Transactions.Count, block.Header.Timestamp);
                _ = _wsHandler.BroadcastNewBlock(block);

                _lastBlockFinalizedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                // N-10: Sliding window — retain evidence for last 10 views instead of clearing entirely.
                // This preserves recent evidence for double-sign detection while preventing unbounded growth.
                {
                    var currentView = block.Number;
                    var cutoff = currentView > 10 ? currentView - 10 : 0;
                    var oldKeys = _proposalsByView.Keys.ToArray().Where(k => k.View < cutoff);
                    foreach (var key in oldKeys)
                        _proposalsByView.TryRemove(key, out _);
                }

                // Announce finalized block to peers so their BestBlockNumber stays
                // current.  Without this, TrySyncFromPeers can't find an up-to-date
                // peer and validators that fall behind are unable to catch up.
                _gossip!.BroadcastBlock(block.Number, block.Hash, block.Header.ParentHash);

                // Persist block + commit bitmap atomically to RocksDB
                PersistBlock(block, blockData, commitBitmap);
                PersistReceipts(block.Receipts);

                // Record commit participation for deterministic epoch-boundary slashing
                _epochManager?.RecordBlockSigners(block.Number, commitBitmap);

                // Check for epoch transition — rebuild validator set if at boundary
                var newSet = _epochManager?.OnBlockFinalized(block.Number);
                if (newSet != null)
                    ApplyEpochTransition(newSet, block.Number);

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

        _txExecutor = new TransactionExecutor(_chainParams, contractRuntime, _stakingState, _complianceVerifier);

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

        var pendingTxs = _mempool.GetPending((int)_chainParams.MaxTransactionsPerBlock, _stateDb);
        var proposalState = _stateDb.Fork();
        var block = _blockBuilder!.BuildBlock(pendingTxs, proposalState, parentBlock.Header, _proposerAddress);

        var blockData = BlockCodec.SerializeBlock(block);
        var proposal = _consensus.ProposeBlock(blockData, block.Hash);

        if (proposal != null)
        {
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

        // After a view change, MinNextView advances so a different leader is selected.
        // Use the effective view (max of block number and MinNextView) for leader selection.
        var effectiveView = Math.Max(nextBlock, _pipelinedConsensus.MinNextView);
        var leader = _validatorSet!.GetLeader(effectiveView);
        if (leader.PeerId != _localPeerId)
            return;

        var parentBlock = _chainManager.LatestBlock;
        if (parentBlock == null)
            return;

        var pendingTxs = _mempool.GetPending((int)_chainParams.MaxTransactionsPerBlock, _stateDb);
        var proposalState = _stateDb.Fork();
        var block = _blockBuilder!.BuildBlock(pendingTxs, proposalState, parentBlock.Header, _proposerAddress);

        var blockData = BlockCodec.SerializeBlock(block);
        var proposal = _pipelinedConsensus.StartRound(nextBlock, blockData, block.Hash);

        if (proposal != null)
        {
            _gossip!.BroadcastConsensusMessage(proposal);
            _logger.LogInformation("Proposed pipelined block #{Number}. Active rounds: {Active}",
                nextBlock, _pipelinedConsensus.ActiveRoundCount);
        }
    }

    private void SendVoteToLeader(ConsensusVoteMessage vote)
    {
        var leader = _validatorSet!.GetLeader(vote.ViewNumber);
        if (leader.PeerId == _localPeerId)
            _consensus!.HandleVote(vote);
        else
            _gossip!.SendToPeer(leader.PeerId, vote);
    }

    private async void HandleNewConnection(PeerConnection connection)
    {
        try
        {
            // Perform handshake as responder
            var result = await CreateHandshake().RespondAsync(connection, _cts!.Token);
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

            // NET-C02: Enable transport encryption if shared secret was established
            if (result.SharedSecret != null)
            {
                var enc = new Basalt.Network.Transport.TransportEncryption(result.SharedSecret, result.IsInitiator);
                result.ZeroSharedSecret(); // M-2: Wipe shared secret after key derivation
                connection.EnableEncryption(enc);
                _logger.LogDebug("Transport encryption enabled for inbound peer {PeerId}", result.PeerId);
            }

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

            case AggregateVoteMessage aggregateVote:
                HandleAggregateVote(sender, aggregateVote);
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
                var pong = new PongMessage { SenderId = _localPeerId, Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
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
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
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
                var baseFee = _chainManager.LatestBlock?.Header.BaseFee ?? UInt256.Zero;
                var validationResult = _txValidator.Validate(tx, _stateDb, baseFee);
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
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Transactions = txDataList.ToArray(),
            };
            _gossip!.SendToPeer(sender, payload);
        }
    }

    private void HandleBlockRequest(PeerId sender, BlockRequestMessage request)
    {
        // N-14: Cap block request count to prevent resource exhaustion
        var count = Math.Min(request.Count, MaxBlockRequestCount);
        var blockDataList = new List<byte[]>();
        var bitmapList = new List<ulong>();
        for (ulong i = request.StartNumber; i < request.StartNumber + (ulong)count; i++)
        {
            var block = _chainManager.GetBlockByNumber(i);
            if (block != null)
            {
                blockDataList.Add(BlockCodec.SerializeBlock(block));
                bitmapList.Add(_blockStore?.GetCommitBitmap(i) ?? 0UL);
            }
            else
                break;
        }

        if (blockDataList.Count > 0)
        {
            var payload = new BlockPayloadMessage
            {
                SenderId = _localPeerId,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Blocks = blockDataList.ToArray(),
                CommitBitmaps = bitmapList.ToArray(),
            };
            _gossip!.SendToPeer(sender, payload);
        }
    }

    private void HandleBlockAnnounce(PeerId sender, BlockAnnounceMessage announce)
    {
        _peerManager!.UpdatePeerBestBlock(sender, announce.BlockNumber, announce.BlockHash);

        // If we're behind, trigger a full sync.  Previously this sent a BlockRequestMessage,
        // but the BlockPayloadMessage response was rejected by the N-04 anti-injection guard
        // when not in sync mode, causing validators to get permanently stuck after falling behind.
        if (announce.BlockNumber > _chainManager.LatestBlockNumber)
        {
            _ = Task.Run(() => TrySyncFromPeers(_cts?.Token ?? CancellationToken.None));
        }
    }

    private void HandleBlockPayload(PeerId sender, BlockPayloadMessage payload)
    {
        // N-04: Only accept block payloads during active sync to prevent unsolicited block injection
        if (Volatile.Read(ref _isSyncing) == 0)
        {
            _logger.LogWarning("Received unsolicited block payload from {Sender}; ignoring", sender);
            return;
        }

        for (int idx = 0; idx < payload.Blocks.Length; idx++)
        {
            var blockBytes = payload.Blocks[idx];
            try
            {
                var block = BlockCodec.DeserializeBlock(blockBytes);

                // Only apply if it's the next block in our chain
                if (block.Number != _chainManager.LatestBlockNumber + 1)
                    continue;

                // Execute transactions and capture receipts
                if (block.Transactions.Count > 0)
                {
                    var receipts = new List<TransactionReceipt>(block.Transactions.Count);
                    for (int i = 0; i < block.Transactions.Count; i++)
                    {
                        var receipt = _txExecutor!.Execute(block.Transactions[i], _stateDb, block.Header, i);
                        receipts.Add(receipt);
                    }
                    block.Receipts = receipts;
                }

                var result = _chainManager.AddBlock(block);
                if (result.IsSuccess)
                {
                    _mempool.RemoveConfirmed(block.Transactions);
                    var bitmap = idx < payload.CommitBitmaps.Length ? payload.CommitBitmaps[idx] : 0UL;
                    PersistBlock(block, blockBytes, bitmap);
                    PersistReceipts(block.Receipts);

                    // Use propagated commit bitmap from the serving peer
                    _epochManager?.RecordBlockSigners(block.Number, bitmap);

                    // Apply epoch transitions for blocks received via gossip
                    var newSet = _epochManager?.OnBlockFinalized(block.Number);
                    if (newSet != null)
                        ApplyEpochTransition(newSet, block.Number);

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
        // N-03: Cap sync response to prevent a peer from requesting unbounded blocks
        var maxBlocks = Math.Min(request.MaxBlocks, MaxSyncResponseBlocks);
        var blockDataList = new List<byte[]>();
        var bitmapList = new List<ulong>();
        for (ulong i = request.FromBlock; i < request.FromBlock + (ulong)maxBlocks; i++)
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
            bitmapList.Add(_blockStore?.GetCommitBitmap(i) ?? 0UL);
        }

        if (blockDataList.Count > 0)
        {
            var response = new SyncResponseMessage
            {
                SenderId = _localPeerId,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Blocks = blockDataList.ToArray(),
                CommitBitmaps = bitmapList.ToArray(),
            };
            _gossip!.SendToPeer(sender, response);
            _logger.LogInformation("Served {Count} blocks to syncing peer {PeerId} (from #{From})",
                blockDataList.Count, sender, request.FromBlock);
        }
    }

    private void HandleSyncResponse(PeerId sender, SyncResponseMessage response)
    {
        var applied = 0;

        // N-05: Fork state for sync — execute all blocks on a forked state database.
        // Only replace canonical state if all blocks in the batch succeed.
        var forkedState = _stateDb.Fork();
        var blocksToApply = new List<(Block Block, byte[] Raw, int OrigIdx)>();

        for (int idx = 0; idx < response.Blocks.Length; idx++)
        {
            var blockBytes = response.Blocks[idx];
            try
            {
                var block = BlockCodec.DeserializeBlock(blockBytes);

                if (block.Number != _chainManager.LatestBlockNumber + (ulong)blocksToApply.Count + 1)
                {
                    _logger.LogDebug("Skipping synced block #{Number} (expected #{Expected})",
                        block.Number, _chainManager.LatestBlockNumber + (ulong)blocksToApply.Count + 1);
                    continue;
                }

                // Execute transactions against the forked state
                if (block.Transactions.Count > 0)
                {
                    var receipts = new List<TransactionReceipt>(block.Transactions.Count);
                    for (int i = 0; i < block.Transactions.Count; i++)
                    {
                        var receipt = _txExecutor!.Execute(block.Transactions[i], forkedState, block.Header, i);
                        receipts.Add(receipt);
                    }
                    block.Receipts = receipts;
                }

                blocksToApply.Add((block, blockBytes, idx));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process synced block from {Sender}", sender);
                break;
            }
        }

        // Apply all successfully executed blocks to the chain
        foreach (var (block, blockBytes, origIdx) in blocksToApply)
        {
            var result = _chainManager.AddBlock(block);
            if (result.IsSuccess)
            {
                _mempool.RemoveConfirmed(block.Transactions);
                var bitmap = origIdx < response.CommitBitmaps.Length ? response.CommitBitmaps[origIdx] : 0UL;
                PersistBlock(block, blockBytes, bitmap);
                PersistReceipts(block.Receipts);
                applied++;

                // Use propagated commit bitmap from the serving peer
                _epochManager?.RecordBlockSigners(block.Number, bitmap);

                // Apply epoch transitions for synced blocks — without this,
                // nodes that sync across epoch boundaries would have a stale
                // ValidatorSet and disagree on leader selection.
                var newSet = _epochManager?.OnBlockFinalized(block.Number);
                if (newSet != null)
                    ApplyEpochTransition(newSet, block.Number);
            }
            else
            {
                _logger.LogWarning("Failed to apply synced block #{Number}: {Error}", block.Number, result.Message);
                break;
            }
        }

        // N-05: Only adopt the forked state if all blocks were applied successfully.
        // Swap() updates the shared StateDbRef so the API layer sees the new state.
        if (applied == blocksToApply.Count && applied > 0)
        {
            _stateDb.Swap(forkedState);
            _logger.LogInformation("Synced {Count} blocks, now at #{Height}", applied, _chainManager.LatestBlockNumber);
        }
        else if (applied > 0)
        {
            _logger.LogWarning("Partial sync: applied {Applied}/{Total} blocks — discarding forked state", applied, blocksToApply.Count);
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
            var proposalKey = (proposal.ViewNumber, proposal.BlockNumber, proposal.SenderId);
            if (_slashingEngine != null && _proposalsByView.TryGetValue(proposalKey, out var existingHash))
            {
                if (existingHash != proposal.BlockHash)
                {
                    var proposerInfo = _validatorSet?.GetByPeerId(proposal.SenderId);
                    if (proposerInfo != null)
                    {
                        _slashingEngine.SlashDoubleSign(proposerInfo.Address, proposal.BlockNumber, existingHash, proposal.BlockHash);
                        _logger.LogWarning("Double-sign detected from validator {Address} at view {View} block {Block}",
                            proposerInfo.Address, proposal.ViewNumber, proposal.BlockNumber);
                    }
                }
            }
            _proposalsByView[proposalKey] = proposal.BlockHash;
        }

        ConsensusVoteMessage? vote;
        if (_config.UsePipelining)
        {
            vote = _pipelinedConsensus!.HandleProposal(proposal);
            if (vote != null)
                _gossip!.BroadcastConsensusMessage(vote);
        }
        else
        {
            vote = _consensus!.HandleProposal(proposal);
            if (vote != null)
                SendVoteToLeader(vote);
        }
    }

    private void HandleConsensusVote(PeerId sender, ConsensusVoteMessage vote)
    {
        if (_config.UsePipelining)
        {
            var response = _pipelinedConsensus!.HandleVote(vote);
            if (response != null)
                _gossip!.BroadcastConsensusMessage(response);
        }
        else
        {
            // In sequential mode, HandleVote returns null (leader builds aggregate QCs
            // internally and fires OnAggregateVote). No response to route.
            _consensus!.HandleVote(vote);
        }
    }

    private void HandleAggregateVote(PeerId sender, AggregateVoteMessage aggregate)
    {
        if (_config.UsePipelining)
            return;

        var response = _consensus!.HandleAggregateVote(aggregate);
        if (response != null)
            SendVoteToLeader(response);
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
                var result = await CreateHandshake().InitiateAsync(connection, _cts!.Token);
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

                // NET-C02: Enable transport encryption if shared secret was established
                if (result.SharedSecret != null)
                {
                    var enc = new Basalt.Network.Transport.TransportEncryption(result.SharedSecret, result.IsInitiator);
                    result.ZeroSharedSecret(); // M-2: Wipe shared secret after key derivation
                    connection.EnableEncryption(enc);
                    _logger.LogDebug("Transport encryption enabled for outbound peer {PeerId}", result.PeerId);
                }

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

                // N-09: Use Volatile.Read for atomic sync check
                if (Volatile.Read(ref _isSyncing) != 0)
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

                // N-09: Use Volatile.Read for atomic sync check
                if (Volatile.Read(ref _isSyncing) != 0)
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
        // N-09: Atomic sync guard — use Interlocked.CompareExchange to prevent TOCTOU race
        if (Interlocked.CompareExchange(ref _isSyncing, 1, 0) != 0) return;

        // Find the best block among connected peers
        var bestPeer = GetBestPeer();
        if (bestPeer == null)
        {
            Interlocked.Exchange(ref _isSyncing, 0);
            return;
        }

        var localHeight = _chainManager.LatestBlockNumber;
        var peerHeight = bestPeer.BestBlockNumber;

        if (peerHeight <= localHeight)
        {
            Interlocked.Exchange(ref _isSyncing, 0);
            return;
        }

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
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
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
            Interlocked.Exchange(ref _isSyncing, 0);
            _syncBatchTcs = null;

            // Restart consensus from the correct block — HandleSyncResponse applies
            // blocks without updating the consensus engine's _currentBlockNumber,
            // so without this restart, consensus would be stuck trying to decide a
            // block that was already applied via sync.
            var nextBlock = _chainManager.LatestBlockNumber + 1;
            if (!_config.UsePipelining)
            {
                _consensus!.StartRound(nextBlock);
                _logger.LogInformation("Consensus restarted at block #{Block} after sync", nextBlock);
            }
            else
            {
                // Pipelined consensus tracks _lastFinalizedBlock internally.
                // Clear stale rounds and let the pipeline restart from current height.
                _pipelinedConsensus!.UpdateLastFinalizedBlock(_chainManager.LatestBlockNumber);
                _logger.LogInformation("Pipelined consensus updated to block #{Block} after sync", _chainManager.LatestBlockNumber);
            }
        }
    }

    /// <summary>
    /// Remove proposal entries for views older than <paramref name="currentView"/>
    /// to prevent unbounded growth and avoid false double-sign detection when
    /// a view number is reused after a view change.
    /// </summary>
    private void PruneProposalsByView(ulong currentView)
    {
        // N-17: Thread-safe iteration with .ToArray() on ConcurrentDictionary
        var staleKeys = _proposalsByView.Keys.ToArray().Where(k => k.View < currentView);
        foreach (var key in staleKeys)
            _proposalsByView.TryRemove(key, out _);
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

    /// <summary>
    /// Apply an epoch transition: swap the validator set, rewire consensus and leader selection.
    /// </summary>
    private void ApplyEpochTransition(ValidatorSet newSet, ulong blockNumber)
    {
        var oldCount = _validatorSet?.Count ?? 0;
        _validatorSet = newSet;

        // Rewire leader selector with new set (reads snapshotted stakes from ValidatorInfo.Stake)
        if (_stakingState != null)
        {
            _leaderSelector = new WeightedLeaderSelector(_validatorSet);
            _validatorSet.SetLeaderSelector(view => _leaderSelector.SelectLeader(view));
        }

        // Update consensus engine
        if (_config.UsePipelining)
            _pipelinedConsensus?.UpdateValidatorSet(newSet);
        else
            _consensus?.UpdateValidatorSet(newSet);

        // N-10: Sliding window — retain evidence for last 10 views on epoch transition
        {
            var cutoff = blockNumber > 10 ? blockNumber - 10 : 0;
            var oldKeys = _proposalsByView.Keys.ToArray().Where(k => k.View < cutoff).ToList();
            foreach (var key in oldKeys)
                _proposalsByView.TryRemove(key, out _);
        }

        _logger.LogInformation("Epoch transition at block #{Block}: {OldCount} → {NewCount} validators, quorum: {Quorum}",
            blockNumber, oldCount, newSet.Count, newSet.QuorumThreshold);
    }

    private void PersistBlock(Block block, byte[] serializedBlockData, ulong? commitBitmap = null)
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
                BaseFee = block.Header.BaseFee,
                ProtocolVersion = block.Header.ProtocolVersion,
                ExtraData = block.Header.ExtraData,
                TransactionHashes = block.Transactions.Select(t => t.Hash).ToArray(),
            };
            _blockStore.PutFullBlock(blockData, serializedBlockData, commitBitmap);
            _blockStore.SetLatestBlockNumber(block.Number);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist block #{Number}", block.Number);
        }
    }

    private void PersistReceipts(List<TransactionReceipt>? receipts)
    {
        if (_receiptStore == null || receipts == null || receipts.Count == 0)
            return;

        try
        {
            var receiptDataList = receipts.Select(r => new ReceiptData
            {
                TransactionHash = r.TransactionHash,
                BlockHash = r.BlockHash,
                BlockNumber = r.BlockNumber,
                TransactionIndex = r.TransactionIndex,
                From = r.From,
                To = r.To,
                GasUsed = r.GasUsed,
                Success = r.Success,
                ErrorCode = (int)r.ErrorCode,
                PostStateRoot = r.PostStateRoot,
                EffectiveGasPrice = r.EffectiveGasPrice,
                Logs = (r.Logs ?? []).Select(l => new LogData
                {
                    Contract = l.Contract,
                    EventSignature = l.EventSignature,
                    Topics = l.Topics ?? [],
                    Data = l.Data ?? [],
                }).ToArray(),
            });
            _receiptStore.PutReceipts(receiptDataList);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist {Count} receipts", receipts.Count);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();

        // N-16: Zero private key on disposal to prevent memory scraping
        CryptographicOperations.ZeroMemory(_privateKey);
    }
}
