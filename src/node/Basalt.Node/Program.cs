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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

RocksDbStore? rocksDbStore = null;
IStateDatabase? stateDb = null;
StateDbRef? stateDbRef = null;
try
{
    var config = NodeConfiguration.FromEnvironment();

    Log.Information("Starting Basalt Node v0.1 ({Mode} mode)",
        config.IsConsensusMode ? "consensus" : "standalone");

    var chainParams = ChainParameters.FromConfiguration(config.ChainId, config.NetworkName);
    var chainManager = new ChainManager();
    var mempool = new Mempool();
    var validator = new TransactionValidator(chainParams);
    BlockStore? blockStore = null;
    ReceiptStore? receiptStore = null;

    // N-06: Load faucet key from environment or fallback with warning
    var faucetKeyHex = Environment.GetEnvironmentVariable("BASALT_FAUCET_KEY");
    byte[] faucetPrivateKey;
    if (!string.IsNullOrEmpty(faucetKeyHex))
    {
        faucetPrivateKey = Convert.FromHexString(faucetKeyHex);
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
    var validatorAddresses = new[]
    {
        Address.FromHexString("0x0000000000000000000000000000000000000100"),
        Address.FromHexString("0x0000000000000000000000000000000000000101"),
        Address.FromHexString("0x0000000000000000000000000000000000000102"),
        Address.FromHexString("0x0000000000000000000000000000000000000103"),
    };
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
                Log.Information("Recovered from persistent storage. Latest block: #{Number}, Hash: {Hash}",
                    latestBlockNumber.Value, latestBlock.Hash.ToHexString()[..18] + "...");
            }
            else
            {
                // Data corrupted — start fresh
                Log.Warning("Persistent data corrupted, starting fresh");
                stateDb = new FlatStateDb(new TrieStateDb(trieNodeStore), new RocksDbFlatStatePersistence(rocksDbStore));
                var genesisBlock = chainManager.CreateGenesisBlock(chainParams, genesisBalances, stateDb);
                PersistBlock(blockStore, genesisBlock);
                Log.Information("Genesis block created. Hash: {Hash}", genesisBlock.Hash.ToHexString()[..18] + "...");
            }
        }
        else
        {
            // Fresh start with RocksDB
            stateDb = new FlatStateDb(new TrieStateDb(trieNodeStore), new RocksDbFlatStatePersistence(rocksDbStore));
            var genesisBlock = chainManager.CreateGenesisBlock(chainParams, genesisBalances, stateDb);
            PersistBlock(blockStore, genesisBlock);
            Log.Information("Genesis block created (persistent). Hash: {Hash}", genesisBlock.Hash.ToHexString()[..18] + "...");
        }

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
    builder.Services.AddGrpc();

    var app = builder.Build();

    // Map REST endpoints (with read-only call support via ManagedContractRuntime)
    var contractRuntime = new ManagedContractRuntime();
    RestApiEndpoints.MapBasaltEndpoints(app, chainManager, mempool, validator, stateDbRef, contractRuntime, receiptStore);

    // Map faucet endpoint
    var faucetLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Basalt.Faucet");
    FaucetEndpoint.MapFaucetEndpoint(app, stateDbRef, mempool, chainParams, faucetPrivateKey, faucetLogger, chainManager);

    // Map WebSocket endpoint
    app.UseWebSockets();
    var wsHandler = new WebSocketHandler(chainManager);
    app.MapWebSocketEndpoint(wsHandler);

    // Map gRPC service
    app.MapGrpcService<BasaltNodeService>();

    // Map Prometheus metrics endpoint
    MetricsEndpoint.MapMetricsEndpoint(app, chainManager, mempool);

    // N-19: Health endpoint with meaningful status info (AOT-safe string formatting)
    app.MapGet("/v1/health", (HttpContext ctx) =>
    {
        var lastBlock = chainManager.LatestBlock;
        var blockAge = lastBlock != null
            ? (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lastBlock.Header.Timestamp) / 1000.0
            : -1;
        var healthy = blockAge >= 0 && blockAge < 60;
        ctx.Response.StatusCode = healthy ? 200 : 503;
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync(
            "{\"status\":\"" + (healthy ? "healthy" : "degraded") +
            "\",\"lastBlockNumber\":" + (lastBlock?.Header.Number ?? 0) +
            ",\"lastBlockAgeSeconds\":" + blockAge.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
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

    if (config.IsConsensusMode)
    {
        // === CONSENSUS MODE ===
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
        var complianceEngine = new Basalt.Compliance.ComplianceEngine(
            new Basalt.Compliance.IdentityRegistry(),
            new Basalt.Compliance.SanctionsList(),
            zkVerifier);

        var coordinator = new NodeCoordinator(
            config, chainParams, chainManager, mempool, stateDbRef, validator, wsHandler,
            app.Services.GetRequiredService<ILoggerFactory>(),
            blockStore, receiptStore,
            stakingState, slashingEngine,
            complianceEngine);

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
            Log.Information("Shutting down consensus coordinator...");
            // N-18: Timeout to prevent shutdown deadlock
            if (!coordinator.StopAsync().Wait(TimeSpan.FromSeconds(10)))
            {
                Log.Warning("Node coordinator did not stop within 10 seconds; forcing exit");
            }
        });
    }
    else
    {
        // === STANDALONE MODE ===
        // Single-node block production on a timer (existing behavior)
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
            Log.Information("Shutting down block production...");
            // N-18: Timeout to prevent shutdown deadlock
            if (!blockProduction.StopAsync().Wait(TimeSpan.FromSeconds(10)))
            {
                Log.Warning("Block production did not stop within 10 seconds; forcing exit");
            }
        });
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
    // Flush the current canonical state — after sync swaps this may differ
    // from the original stateDb variable.
    var canonical = stateDbRef?.Inner ?? stateDb;
    if (canonical is FlatStateDb flatState)
        flatState.FlushToPersistence();
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
