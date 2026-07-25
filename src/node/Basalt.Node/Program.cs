using System.Threading.RateLimiting;
using Basalt.Core;
using Basalt.Consensus.Staking;
using Basalt.Crypto;
using Basalt.Execution;
using Basalt.Execution.VM;
using Basalt.Network;
using Basalt.Storage;
using Basalt.Storage.RocksDb;
using Basalt.Api.Grpc;
using Basalt.Api.Rest;
using Basalt.Node;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

// L17: Configurable log level via BASALT_LOG_LEVEL environment variable
var logLevelStr = Environment.GetEnvironmentVariable("BASALT_LOG_LEVEL") ?? "Information";
var logLevel = logLevelStr.ToLowerInvariant() switch
{
    "verbose" or "trace" => Serilog.Events.LogEventLevel.Verbose,
    "debug" => Serilog.Events.LogEventLevel.Debug,
    "information" or "info" => Serilog.Events.LogEventLevel.Information,
    "warning" or "warn" => Serilog.Events.LogEventLevel.Warning,
    "error" => Serilog.Events.LogEventLevel.Error,
    "fatal" => Serilog.Events.LogEventLevel.Fatal,
    _ => Serilog.Events.LogEventLevel.Information,
};

// L18: File logging when BASALT_DATA_DIR is set
var logConfig = new LoggerConfiguration()
    .MinimumLevel.Is(logLevel)
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}");

var dataDir = Environment.GetEnvironmentVariable("BASALT_DATA_DIR");
if (!string.IsNullOrEmpty(dataDir))
{
    logConfig.WriteTo.File(
        Path.Combine(dataDir, "basalt-.log"),
        rollingInterval: Serilog.RollingInterval.Day,
        retainedFileCountLimit: 14,
        fileSizeLimitBytes: 100 * 1024 * 1024); // 100MB per file
}

Log.Logger = logConfig.CreateLogger();

RocksDbStore? rocksDbStore = null;
IStateDatabase? stateDb = null;
StateDbRef? stateDbRef = null;
// LOW-N05: Declare outside try so finally can zero it on all exit paths.
byte[]? faucetPrivateKey = null;
// B1: Declare outside try so finally block can flush staking state.
Basalt.Consensus.Staking.StakingState? stakingStateForShutdown = null;
Basalt.Consensus.Staking.IStakingPersistence? stakingPersistenceForShutdown = null;
try
{
    var config = NodeConfiguration.FromEnvironment();

    Log.Information("Starting Basalt Node v0.1 ({Mode} mode)",
        config.IsConsensusMode ? "consensus" : "standalone");

    var chainParams = ChainParameters.FromConfiguration(config.ChainId, config.NetworkName);

    // Phase 1.6: opt into background trie pruning via env (used by the testnet soak). Off unless set.
    // BASALT_TRIE_PRUNE_WINDOW / _INTERVAL override the defaults (window must stay >= rollback depth).
    if (Environment.GetEnvironmentVariable("BASALT_ENABLE_TRIE_PRUNING") == "1")
    {
        var pruneWindow = ulong.TryParse(Environment.GetEnvironmentVariable("BASALT_TRIE_PRUNE_WINDOW"), out var w)
            ? w : chainParams.TriePruneWindowSize;
        var pruneInterval = uint.TryParse(Environment.GetEnvironmentVariable("BASALT_TRIE_PRUNE_INTERVAL"), out var iv)
            ? iv : chainParams.TriePruneIntervalBlocks;
        // Raising this is how an operator says "I know this node has a long unpruned backlog, and a
        // near-total first sweep is expected", which is the one case where guard 4's reading is wrong.
        var pruneMaxDelete = double.TryParse(
            Environment.GetEnvironmentVariable("BASALT_TRIE_PRUNE_MAX_DELETE_FRACTION"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var mdf) ? mdf : chainParams.TriePruneMaxDeleteFraction;

        chainParams = chainParams with
        {
            EnableTriePruning = true,
            TriePruneWindowSize = pruneWindow,
            TriePruneIntervalBlocks = pruneInterval,
            TriePruneMaxDeleteFraction = pruneMaxDelete,
        };
        // Only a consensus node builds the pruner (NodeCoordinator.SetupBlockProduction), and only a
        // consensus node runs the sweep loop. Announcing "enabled" on a standalone or RPC node would tell
        // an operator their archive is reclaiming disk when nothing is, which is the dangerous direction
        // to be wrong in: the disk fills silently.
        if (config.IsConsensusMode)
        {
            Log.Information("Trie pruning enabled via env: window={Window} blocks, interval={Interval} blocks",
                pruneWindow, pruneInterval);
        }
        else
        {
            // A sync node prunes from its own loop (see the RPC setup below), so the only mode left with
            // no sweeper is a standalone node that never syncs and never produces.
            if (string.IsNullOrEmpty(config.SyncSource))
            {
                Log.Warning(
                    "BASALT_ENABLE_TRIE_PRUNING is set but this node neither produces nor syncs blocks, so "
                    + "nothing will prune and trie_nodes will grow without bound.");
            }
        }
    }

    // B4: Refuse BASALT_DEBUG=1 on public networks — debug mode enables AllowAnyOrigin CORS
    var isDebugMode = Environment.GetEnvironmentVariable("BASALT_DEBUG") == "1";
    if (isDebugMode && chainParams.IsPublicNetwork)
    {
        Log.Fatal("BASALT_DEBUG=1 is not allowed on a public network. Remove this flag.");
        return 1;
    }

    // H7: Public-network configuration guards (mainnet, built-in testnet, and the incentivized testnet 4242)
    if (chainParams.IsPublicNetwork)
    {
        if (chainParams.ChainId == 1 && chainParams.NetworkName != "basalt-mainnet")
            throw new InvalidOperationException("ChainId 1 requires network name 'basalt-mainnet'");
        if (chainParams.ChainId == 2 && chainParams.NetworkName != "basalt-testnet")
            throw new InvalidOperationException("ChainId 2 requires network name 'basalt-testnet'");
        if (config.DataDir == null)
            throw new InvalidOperationException("BASALT_DATA_DIR must be set for mainnet/testnet");
        if (config.IsConsensusMode && string.IsNullOrEmpty(config.ValidatorKeyHex))
            throw new InvalidOperationException("BASALT_VALIDATOR_KEY must be set for mainnet/testnet validators");
    }

    var chainManager = new ChainManager();
    var mempool = new Mempool();
    var validator = new TransactionValidator(chainParams);
    BlockStore? blockStore = null;
    ReceiptStore? receiptStore = null;

    // N-06: Load faucet key from environment or fallback with warning
    var faucetKeyHex = Environment.GetEnvironmentVariable("BASALT_FAUCET_KEY");
    if (!string.IsNullOrEmpty(faucetKeyHex))
    {
        faucetPrivateKey = Convert.FromHexString(faucetKeyHex);
    }
    else if (chainParams.IsPublicNetwork)
    {
        // B2: Reject startup on a public network without an explicit faucet key
        Log.Fatal("BASALT_FAUCET_KEY must be set for a public network. Cannot use deterministic dev key.");
        return 1;
    }
    else
    {
        Log.Warning("N-06: BASALT_FAUCET_KEY not set; using deterministic dev-mode faucet key. DO NOT use in production.");
        faucetPrivateKey = new byte[32];
        faucetPrivateKey[31] = 0xFF;
    }
    var faucetPublicKey = Ed25519Signer.GetPublicKey(faucetPrivateKey);
    var faucetAddr = Ed25519Signer.DeriveAddress(faucetPublicKey);

    var genesisBalances = new Dictionary<Address, UInt256>
    {
        [Address.FromHexString("0x0000000000000000000000000000000000000001")] = UInt256.Parse("1000000000000000000000000"),
        [Address.FromHexString("0x0000000000000000000000000000000000000002")] = UInt256.Parse("1000000000000000000000000"),
        [Address.FromHexString("0x0000000000000000000000000000000000000100")] = UInt256.Parse("200000000000000000000000"),
        [Address.FromHexString("0x0000000000000000000000000000000000000101")] = UInt256.Parse("200000000000000000000000"),
        [Address.FromHexString("0x0000000000000000000000000000000000000102")] = UInt256.Parse("200000000000000000000000"),
        [Address.FromHexString("0x0000000000000000000000000000000000000103")] = UInt256.Parse("200000000000000000000000"),
        [faucetAddr] = UInt256.Parse("500000000000000000000000000"),
    };

    // Initialize staking state with genesis validators
    var stakingState = new StakingState();
    stakingStateForShutdown = stakingState;
    Basalt.Consensus.Staking.IStakingPersistence? stakingPersistence = null;
    // The genesis validator set. It defaults to the deterministic devnet addresses, whose private keys
    // are published in the devnet compose, which is fine for a devnet and disqualifying for anything
    // else: a network staking four addresses that anyone can sign for is not a network anyone should
    // trust. BASALT_GENESIS_VALIDATORS lets a real deployment stake the keys its operators actually
    // hold. It must list every validator, in the same order on every node, because leader election
    // walks this set: a node whose own address is absent proposes blocks that everyone else rejects as
    // coming from a non-leader, and the chain limps along on view timeouts instead of failing loudly.
    var genesisValidatorsEnv = Environment.GetEnvironmentVariable("BASALT_GENESIS_VALIDATORS");
    Address[] validatorAddresses;
    if (!string.IsNullOrWhiteSpace(genesisValidatorsEnv))
    {
        try
        {
            validatorAddresses = genesisValidatorsEnv
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Address.FromHexString)
                .ToArray();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "BASALT_GENESIS_VALIDATORS is not a comma-separated list of hex addresses");
            return 1;
        }

        if (validatorAddresses.Length == 0)
        {
            Log.Fatal("BASALT_GENESIS_VALIDATORS was set but lists no addresses");
            return 1;
        }

        if (validatorAddresses.Distinct().Count() != validatorAddresses.Length)
        {
            Log.Fatal("BASALT_GENESIS_VALIDATORS contains a duplicate address");
            return 1;
        }

        if (chainParams.IsPublicNetwork && config.IsConsensusMode
            && config.ValidatorAddress is { } ownAddress
            && !validatorAddresses.Contains(Address.FromHexString(ownAddress)))
        {
            // Catch the mismatch at startup rather than letting it show up as a slow chain.
            Log.Fatal(
                "This validator's address {Own} is not in BASALT_GENESIS_VALIDATORS. It could never be "
                + "elected leader and the chain would stall on view timeouts.", ownAddress);
            return 1;
        }

        Log.Information("Genesis validator set from environment: {Count} validators", validatorAddresses.Length);
    }
    else
    {
        if (chainParams.IsPublicNetwork)
        {
            Log.Fatal(
                "A public network must set BASALT_GENESIS_VALIDATORS. The built-in set is the devnet one, "
                + "whose private keys are public.");
            return 1;
        }

        validatorAddresses = new[]
        {
            Address.FromHexString("0x0000000000000000000000000000000000000100"),
            Address.FromHexString("0x0000000000000000000000000000000000000101"),
            Address.FromHexString("0x0000000000000000000000000000000000000102"),
            Address.FromHexString("0x0000000000000000000000000000000000000103"),
        };
    }

    // Genesis validators need a stake to be counted, so fund whatever set we ended up with.
    foreach (var validatorAddr in validatorAddresses)
    {
        genesisBalances[validatorAddr] = UInt256.Parse("200000000000000000000000");
    }
    foreach (var validatorAddr in validatorAddresses)
    {
        stakingState.RegisterValidator(validatorAddr, UInt256.Parse("200000000000000000000000"));
    }
    Log.Information("Staking: {Count} genesis validators registered", validatorAddresses.Length);

    if (config.DataDir != null)
    {
        Directory.CreateDirectory(config.DataDir);
        rocksDbStore = new RocksDbStore(config.DataDir);
        var trieNodeStore = new RocksDbTrieNodeStore(rocksDbStore);
        blockStore = new BlockStore(rocksDbStore);
        receiptStore = new ReceiptStore(rocksDbStore);

        var latestBlockNumber = blockStore.GetLatestBlockNumber();
        if (latestBlockNumber.HasValue)
        {
            // Recover from persistent storage
            var genesisRaw = blockStore.GetRawBlockByNumber(0);
            var latestRaw = blockStore.GetRawBlockByNumber(latestBlockNumber.Value);

            if (genesisRaw != null && latestRaw != null)
            {
                var genesisBlock = BlockCodec.DeserializeBlock(genesisRaw);
                var latestBlock = BlockCodec.DeserializeBlock(latestRaw);

                // N-20: Verify genesis block matches configured chain
                if (genesisBlock.Header.ChainId != chainParams.ChainId)
                {
                    Log.Fatal("Genesis block chain ID {Stored} does not match configured chain ID {Configured}",
                        genesisBlock.Header.ChainId, chainParams.ChainId);
                    throw new InvalidOperationException("Data directory contains genesis from a different chain");
                }

                var recoveredTrie = new TrieStateDb(trieNodeStore, latestBlock.Header.StateRoot);
                var recoveredFlat = new FlatStateDb(recoveredTrie, new RocksDbFlatStatePersistence(rocksDbStore));
                recoveredFlat.LoadFromPersistence();

                // N-11: Verify state root consistency after recovery
                var computedRoot = recoveredFlat.ComputeStateRoot();
                var expectedRoot = latestBlock.Header.StateRoot;
                if (computedRoot != expectedRoot)
                {
                    Log.Fatal("State root mismatch after recovery: computed={ComputedRoot}, expected={ExpectedRoot}",
                        computedRoot, expectedRoot);
                    throw new InvalidOperationException("Corrupted state: state root does not match latest block after recovery");
                }

                stateDb = recoveredFlat;
                chainManager.ResumeFromBlock(genesisBlock, latestBlock);

                // B1: Load persisted staking state (overwrites genesis defaults with real data)
                stakingPersistence = new Basalt.Node.RocksDbStakingPersistence(rocksDbStore);
                stakingState.LoadFromPersistence(stakingPersistence);
                Log.Information("Staking: loaded persisted staking state");

                Log.Information("Recovered from persistent storage. Latest block: #{Number}, Hash: {Hash}",
                    latestBlockNumber.Value, latestBlock.Hash.ToHexString()[..18] + "...");
            }
            else
            {
                // Data corrupted — start fresh
                Log.Warning("Persistent data corrupted, starting fresh");
                stakingPersistence = new Basalt.Node.RocksDbStakingPersistence(rocksDbStore);
                stateDb = new FlatStateDb(new TrieStateDb(trieNodeStore), new RocksDbFlatStatePersistence(rocksDbStore));
                var genesisBlock = chainManager.CreateGenesisBlock(chainParams, genesisBalances, stateDb);
                PersistBlock(blockStore, genesisBlock);
                Log.Information("Genesis block created. Hash: {Hash}", genesisBlock.Hash.ToHexString()[..18] + "...");
            }
        }
        else
        {
            // Fresh start with RocksDB
            stakingPersistence = new Basalt.Node.RocksDbStakingPersistence(rocksDbStore);
            stateDb = new FlatStateDb(new TrieStateDb(trieNodeStore), new RocksDbFlatStatePersistence(rocksDbStore));
            var genesisBlock = chainManager.CreateGenesisBlock(chainParams, genesisBalances, stateDb);
            PersistBlock(blockStore, genesisBlock);
            Log.Information("Genesis block created (persistent). Hash: {Hash}", genesisBlock.Hash.ToHexString()[..18] + "...");
        }

        stakingPersistenceForShutdown = stakingPersistence;
        Log.Information("Storage: RocksDB at {DataDir}", config.DataDir);
    }
    else
    {
        // In-memory storage (development / standalone)
        stateDb = new InMemoryStateDb();
        var genesisBlock = chainManager.CreateGenesisBlock(chainParams, genesisBalances, stateDb);
        Log.Information("Genesis block created. Hash: {Hash}", genesisBlock.Hash.ToHexString()[..18] + "...");
    }

    // Wrap the state database in a mutable reference so that all consumers
    // (API, faucet, consensus) share the same canonical view.  When the
    // consensus layer swaps state after a sync, the API sees it immediately.
    stateDbRef = new StateDbRef(stateDb);

    // Build the host
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    // INFO-2: Limit request body size to 512KB
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Limits.MaxRequestBodySize = 512 * 1024;
    });

    // Configure JSON serialization for AOT-compatible Minimal APIs
    builder.Services.ConfigureHttpJsonOptions(options =>
    {
        options.SerializerOptions.TypeInfoResolverChain.Add(BasaltApiJsonContext.Default);
        options.SerializerOptions.TypeInfoResolverChain.Add(WsJsonContext.Default);
    });

    // Register services
    builder.Services.AddSingleton(chainParams);
    builder.Services.AddSingleton<IStateDatabase>(stateDbRef);
    builder.Services.AddSingleton(chainManager);
    builder.Services.AddSingleton(mempool);
    builder.Services.AddSingleton(validator);
    // TxForwarderRef: inner forwarder is set later in the RPC branch; DI resolves lazily
    var txForwarderRef = new TxForwarderRef();
    builder.Services.AddSingleton<ITxForwarder>(txForwarderRef);
    builder.Services.AddGrpc();

    // R3-NEW-1: Use GlobalLimiter instead of named policy. Named policies require
    // explicit .RequireRateLimiting("per-ip") on each endpoint group; a global limiter
    // applies to all requests automatically without per-endpoint opt-in.
    // RPC nodes get much higher limits since they are the public-facing API layer.
    var isRpcMode = config.ResolvedMode == NodeMode.Rpc;
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = 429;
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = isRpcMode ? 1000 : 100,
                    Window = TimeSpan.FromMinutes(1),
                }));
    });

    // R3-NEW-2: Restrict CORS to known origins. AllowAnyOrigin enables localhost CSRF where
    // a malicious website uses a visitor's browser as a proxy to a locally-running node.
    // Allow any origin when BASALT_DEBUG=1 is set (development mode) or in RPC mode
    // (public-facing API serving Explorer, Caldera, and third-party consumers).
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            if (Environment.GetEnvironmentVariable("BASALT_DEBUG") == "1" || isRpcMode)
            {
                policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
            }
            else
            {
                policy.WithOrigins("https://explorer.basalt.network")
                      .AllowAnyHeader()
                      .WithMethods("GET", "POST");
            }
        });
    });

    var app = builder.Build();

    // NEW-5: Wire rate limiter middleware
    app.UseRateLimiter();

    // NEW-6: Wire CORS middleware
    app.UseCors();

    // R3-NEW-3: GraphQL API (AddBasaltGraphQL / MapBasaltGraphQL) is intentionally not registered.
    // The GraphQL schema (Basalt.Api.GraphQL) exists but is not yet fully integrated with the
    // node's consensus and state layers. Enable it once subscriptions and auth are wired up.

    // Map REST endpoints (with read-only call support via ManagedContractRuntime)
    var contractRuntime = new ManagedContractRuntime();
    var solverInfoAdapter = new Basalt.Node.Solver.SolverInfoAdapter();
    solverInfoAdapter.SetMempool(mempool);
    RestApiEndpoints.MapBasaltEndpoints(app, chainManager, mempool, validator, stateDbRef, contractRuntime, receiptStore, chainParams: chainParams, solverProvider: solverInfoAdapter, blockStore: blockStore, txForwarder: txForwarderRef);

    // Map faucet endpoint (txForwarderRef set later in RPC branch)
    var faucetLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Basalt.Faucet");
    FaucetEndpoint.MapFaucetEndpoint(app, stateDbRef, mempool, chainParams, faucetPrivateKey, faucetLogger, chainManager, txForwarder: txForwarderRef);

    // Map WebSocket endpoint (keep-alive prevents Cloudflare Tunnel idle disconnects)
    app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
    var wsHandler = new WebSocketHandler(chainManager);
    app.MapWebSocketEndpoint(wsHandler);

    // Map gRPC service
    app.MapGrpcService<BasaltNodeService>();

    // Map Prometheus metrics endpoint
    MetricsEndpoint.MapMetricsEndpoint(app, chainManager, mempool);

    // Sync status (set by RPC mode — null for other modes)
    ISyncStatus? syncStatus = null;

    // N-19: Health endpoint with meaningful status info (AOT-safe string formatting)
    ulong lastHealthCheckBlock = 0;
    long lastHealthCheckTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    app.MapGet("/v1/health", (HttpContext ctx) =>
    {
        var lastBlock = chainManager.LatestBlock;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var blockAge = lastBlock != null
            ? (now - lastBlock.Header.Timestamp) / 1000.0
            : -1;

        // MED-04: Supplement clock-based health with block height progress check.
        // If block number has increased since last health check, the node is making progress
        // regardless of clock skew.
        var currentBlockNumber = lastBlock?.Header.Number ?? 0;
        var makingProgress = currentBlockNumber > lastHealthCheckBlock;
        lastHealthCheckBlock = currentBlockNumber;
        lastHealthCheckTime = now;

        var healthy = makingProgress || (blockAge >= 0 && blockAge < 60);

        // RPC nodes are unhealthy if too far behind the sync source
        var currentSyncLag = syncStatus?.SyncLag ?? 0;
        var modeName = config.ResolvedMode switch
        {
            NodeMode.Validator => "validator",
            NodeMode.Rpc => "rpc",
            _ => "standalone",
        };

        if (config.ResolvedMode == NodeMode.Rpc && currentSyncLag > 50)
            healthy = false;

        ctx.Response.StatusCode = healthy ? 200 : 503;
        ctx.Response.ContentType = "application/json";
        var syncLagField = config.ResolvedMode == NodeMode.Rpc
            ? ",\"syncLag\":" + currentSyncLag
            : "";
        return ctx.Response.WriteAsync(
            "{\"status\":\"" + (healthy ? "healthy" : "degraded") +
            "\",\"mode\":\"" + modeName +
            "\",\"lastBlockNumber\":" + currentBlockNumber +
            ",\"lastBlockAgeSeconds\":" + blockAge.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
            ",\"makingProgress\":" + (makingProgress ? "true" : "false") +
            syncLagField +
            ",\"chainId\":" + chainParams.ChainId + "}");
    });

    // Map validators endpoint (uses stakingState from consensus layer)
    app.MapGet("/v1/validators", () =>
    {
        var validators = stakingState.GetActiveValidators();
        var response = validators.Select(v => new ValidatorInfoResponse
        {
            Address = v.Address.ToHexString(),
            Stake = v.TotalStake.ToString(),
            SelfStake = v.SelfStake.ToString(),
            DelegatedStake = v.DelegatedStake.ToString(),
            Status = v.IsActive ? "active" : "inactive",
        }).ToArray();
        return Microsoft.AspNetCore.Http.Results.Ok(response);
    });

    switch (config.ResolvedMode)
    {
        case NodeMode.Validator:
        {
            // === VALIDATOR MODE ===
            // Multi-node operation with P2P networking and BFT consensus
            var slashingEngine = new SlashingEngine(
                stakingState,
                app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<SlashingEngine>());

            // ZK compliance verifier — reads VKs from SchemaRegistry contract storage (COMPL-17)
            var schemaRegistryAddress = Basalt.Execution.GenesisContractDeployer.Addresses.SchemaRegistry;
            var zkVerifier = new Basalt.Compliance.ZkComplianceVerifier(schemaId =>
            {
                // StorageMap key: "scr_vk:{schemaIdHex}", hashed to Hash256 via BLAKE3
                var storageKey = "scr_vk:" + schemaId.ToHexString();
                var slot = Basalt.Crypto.Blake3Hasher.Hash(System.Text.Encoding.UTF8.GetBytes(storageKey));
                var raw = stateDbRef.GetStorage(schemaRegistryAddress, slot);
                if (raw == null || raw.Length < 2 || raw[0] != 0x07) // 0x07 = TagString
                    return null;
                var hexVk = System.Text.Encoding.UTF8.GetString(raw.AsSpan(1));
                if (string.IsNullOrEmpty(hexVk))
                    return null;
                try { return Convert.FromHexString(hexVk); }
                catch { return null; }
            });
            // H9: No MockKycProvider in consensus mode — only governance-approved
            // providers can issue attestations on mainnet/testnet.
            // COMPL-C02: bind both registries to the Governance system-contract address (0x1003) so their
            // admin guards enforce that only Governance can approve KYC providers or edit the sanctions
            // list. The parameterless ctors leave the guard address null, which DISABLES that access
            // control (any/null caller passes). No consensus path calls those admin methods today, so this
            // is defense-in-depth for when a governance handler is wired, with no runtime behavior change.
            var governanceAddress = Basalt.Execution.GenesisContractDeployer.Addresses.Governance.ToArray();
            var complianceEngine = new Basalt.Compliance.ComplianceEngine(
                new Basalt.Compliance.IdentityRegistry(governanceAddress),
                new Basalt.Compliance.SanctionsList(governanceAddress),
                zkVerifier);

            var coordinator = new NodeCoordinator(
                config, chainParams, chainManager, mempool, stateDbRef, validator, wsHandler,
                app.Services.GetRequiredService<ILoggerFactory>(),
                blockStore, receiptStore,
                stakingState, slashingEngine,
                complianceEngine,
                stakingPersistence,
                rocksDbStore);

            // E4: Wire solver manager into REST API adapter after NodeCoordinator is initialized
            if (coordinator.SolverManager != null)
                solverInfoAdapter.SetSolverManager(coordinator.SolverManager);

            Log.Information("Basalt Node listening on {Urls}", string.Join(", ", app.Urls.DefaultIfEmpty($"http://localhost:{config.HttpPort}")));
            Log.Information("Chain: {Network} (ChainId={ChainId})", chainParams.NetworkName, chainParams.ChainId);
            Log.Information("Validator: index={Index}, address={Address}, P2P port={P2PPort}",
                config.ValidatorIndex, config.ValidatorAddress, config.P2PPort);
            Log.Information("Peers: {Peers}", string.Join(", ", config.Peers));

            app.Lifetime.ApplicationStarted.Register(() =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await coordinator.StartAsync(app.Lifetime.ApplicationStopping);
                    }
                    catch (Exception ex)
                    {
                        Log.Fatal(ex, "Consensus coordinator failed to start");
                    }
                });
            });

            app.Lifetime.ApplicationStopping.Register(() =>
            {
                // L20: Add random jitter to stagger validator restarts and avoid thundering herd
                var jitterMs = Random.Shared.Next(0, 3000);
                Thread.Sleep(jitterMs);

                Log.Information("Shutting down consensus coordinator...");
                // N-18: Timeout to prevent shutdown deadlock
                if (!coordinator.StopAsync().Wait(TimeSpan.FromSeconds(10)))
                {
                    Log.Warning("Node coordinator did not stop within 10 seconds; forcing exit");
                }
            });
            break;
        }

        case NodeMode.Rpc:
        {
            // === RPC MODE ===
            // Syncs finalized blocks from a trusted source via HTTP. Serves the full API
            // without participating in consensus or P2P networking.

            if (blockStore == null)
            {
                Log.Fatal("RPC mode requires BASALT_DATA_DIR for block persistence");
                return 1;
            }

            var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();

            // Create execution components for block replay
            IContractRuntime rpcContractRuntime = config.UseSandbox
                ? new Basalt.Execution.VM.Sandbox.SandboxedContractRuntime(new Basalt.Execution.VM.Sandbox.SandboxConfiguration())
                : new ManagedContractRuntime();
            var rpcTxExecutor = new TransactionExecutor(chainParams, rpcContractRuntime, stakingState);
            var rpcBlockBuilder = new BlockBuilder(chainParams, rpcTxExecutor, loggerFactory.CreateLogger<BlockBuilder>());

            // H4: after a sync batch, persist the batch's new trie nodes to CF.TrieNodes and adopt a
            // fresh disk-backed canonical state at the new root — so a synced RPC node survives a restart
            // (its state-root check would otherwise fail on missing nodes) and does not accumulate an
            // unbounded in-memory overlay stack.
            Func<IStateDatabase, Hash256, IStateDatabase> rpcSyncStateCommit = (forked, newRoot) =>
            {
                var persistent = new RocksDbTrieNodeStore(rocksDbStore!);
                if (forked is FlatStateDb fsd)
                    fsd.InnerTrie.FlushOverlayTo(persistent);
                return new FlatStateDb(new TrieStateDb(persistent, newRoot), new RocksDbFlatStatePersistence(rocksDbStore!));
            };

            var rpcBlockApplier = new BlockApplier(
                chainParams, chainManager, mempool, rpcTxExecutor, rpcBlockBuilder,
                blockStore, receiptStore,
                epochManager: null, // No epoch transitions in RPC mode (no consensus)
                stakingState: stakingState,
                stakingPersistence: stakingPersistence,
                wsHandler,
                loggerFactory.CreateLogger<BlockApplier>(),
                syncStateCommit: rpcSyncStateCommit);

            // Phase 1.6 on a sync-only node. Pruning used to be impossible here: the pruner is built by
            // NodeCoordinator, which an RPC node never runs, so the feature announced itself and then did
            // nothing while trie_nodes grew without bound. Archive nodes accumulate the most state and
            // are exactly the ones that need this.
            //
            // The sweep is driven from the sync loop's caught-up branch rather than from a background
            // task. That loop is the only thing that mutates state here, so running the sweep from it is
            // serialised against block application with no lock at all, and being caught up means it
            // delays no sync.
            Action<ulong>? rpcPruneMaintenance = null;
            if (chainParams.EnableTriePruning && rocksDbStore != null && blockStore != null)
            {
                var rpcPruner = new RocksDbTriePruner(
                    rocksDbStore,
                    new TriePrunerOptions { MaxDeleteFraction = chainParams.TriePruneMaxDeleteFraction });
                var pruneLogger = loggerFactory.CreateLogger("TriePrune");
                ulong lastSweptTip = 0;
                rpcPruneMaintenance = tip =>
                {
                    if (tip < chainParams.TriePruneWindowSize || tip < lastSweptTip + chainParams.TriePruneIntervalBlocks)
                        return;

                    var retained = TrieRetention.CollectRetainedRoots(blockStore, chainParams.TriePruneWindowSize);
                    var stats = rpcPruner.Prune(retained);
                    lastSweptTip = tip;
                    MetricsEndpoint.RecordTriePrune(stats.TotalScanned, stats.Deleted, stats.Retained, (long)tip);
                    pruneLogger.LogInformation(
                        "Trie prune sweep at #{Tip}: scanned {Scanned}, deleted {Deleted}, retained {Retained}",
                        tip, stats.TotalScanned, stats.Deleted, stats.Retained);
                    rpcPruner.Compact();
                };

                Log.Information(
                    "Trie pruning ENABLED on this sync node: window={Window} blocks, interval={Interval} blocks",
                    chainParams.TriePruneWindowSize, chainParams.TriePruneIntervalBlocks);
            }

            var rpcSyncService = new BlockSyncService(
                config.SyncSource!,
                rpcBlockApplier,
                chainManager,
                stateDbRef,
                chainParams,
                loggerFactory.CreateLogger<BlockSyncService>(),
                onCaughtUp: rpcPruneMaintenance);

            syncStatus = rpcSyncService;

            var txForwarder = new HttpTxForwarder(
                config.SyncSource!,
                loggerFactory.CreateLogger<HttpTxForwarder>());
            txForwarderRef.Set(txForwarder);

            Log.Information("Basalt RPC Node listening on {Urls}", string.Join(", ", app.Urls.DefaultIfEmpty($"http://localhost:{config.HttpPort}")));
            Log.Information("Chain: {Network} (ChainId={ChainId})", chainParams.NetworkName, chainParams.ChainId);
            Log.Information("Sync source: {Source}", config.SyncSource);

            app.Lifetime.ApplicationStarted.Register(() =>
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await rpcSyncService.RunAsync(app.Lifetime.ApplicationStopping);
                    }
                    catch (Exception ex)
                    {
                        Log.Fatal(ex, "Block sync service failed");
                    }
                });
            });

            app.Lifetime.ApplicationStopping.Register(() =>
            {
                Log.Information("Shutting down RPC sync service...");
                txForwarder.Dispose();
                rpcSyncService.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            });
            break;
        }

        default:
        {
            // === STANDALONE MODE ===
            // Single-node block production on a timer (existing behavior)

            // LOW-06: Warn if DataDir is set but blocks are not persisted in standalone mode
            if (config.DataDir != null)
                Log.Warning("DataDir is set but standalone mode does not persist blocks. State will be lost on restart.");

            var proposer = Address.FromHexString("0x0000000000000000000000000000000000000001");
            var blockProduction = new BlockProductionLoop(
                chainParams, chainManager, mempool, stateDbRef, proposer,
                app.Services.GetRequiredService<ILogger<BlockProductionLoop>>());

            // Wire metrics and WebSocket to block production
            blockProduction.OnBlockProduced += block =>
            {
                MetricsEndpoint.RecordBlock(block.Transactions.Count, block.Header.Timestamp);
                _ = wsHandler.BroadcastNewBlock(block);
            };

            blockProduction.Start();

            Log.Information("Basalt Node listening on {Urls}", string.Join(", ", app.Urls.DefaultIfEmpty("http://localhost:5000")));
            Log.Information("Chain: {Network} (ChainId={ChainId})", chainParams.NetworkName, chainParams.ChainId);
            Log.Information("Block time: {BlockTime}ms", chainParams.BlockTimeMs);

            app.Lifetime.ApplicationStopping.Register(() =>
            {
                // L20: Add random jitter to stagger restarts
                var jitterMs = Random.Shared.Next(0, 3000);
                Thread.Sleep(jitterMs);

                Log.Information("Shutting down block production...");
                // N-18: Timeout to prevent shutdown deadlock
                if (!blockProduction.StopAsync().Wait(TimeSpan.FromSeconds(10)))
                {
                    Log.Warning("Block production did not stop within 10 seconds; forcing exit");
                }
            });
            break;
        }
    }

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Basalt Node terminated unexpectedly");
    return 1;
}
finally
{
    // LOW-N05: Zero faucet private key on shutdown for both consensus and standalone modes
    if (faucetPrivateKey != null)
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(faucetPrivateKey);

    // CR-7: Wrap persistence flushes in try/catch so I/O errors don't prevent
    // subsequent cleanup (RocksDB dispose, log flush)
    try
    {
        // B1: Flush staking state to persistent storage on shutdown
        if (stakingPersistenceForShutdown != null && stakingStateForShutdown != null)
        {
            stakingStateForShutdown.FlushToPersistence(stakingPersistenceForShutdown);
            Log.Information("Staking state flushed to persistence");
        }

        // Flush the current canonical state — after sync swaps this may differ
        // from the original stateDb variable.
        var canonical = stateDbRef?.Inner ?? stateDb;
        if (canonical is FlatStateDb flatState)
            flatState.FlushToPersistence();
    }
    catch (Exception flushEx)
    {
        Log.Error(flushEx, "Failed to flush state to persistence during shutdown");
    }
    rocksDbStore?.Dispose();
    Log.CloseAndFlush();
}

return 0;

/// <summary>
/// Persist a block to the BlockStore with full serialized data.
/// </summary>
static void PersistBlock(BlockStore blockStore, Block block)
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
    var serialized = BlockCodec.SerializeBlock(block);
    blockStore.PutFullBlock(blockData, serialized);
    blockStore.SetLatestBlockNumber(block.Number);
}
