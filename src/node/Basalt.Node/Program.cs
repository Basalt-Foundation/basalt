using Basalt.Core;
using Basalt.Consensus.Staking;
using Basalt.Execution;
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
try
{
    var config = NodeConfiguration.FromEnvironment();

    Log.Information("Starting Basalt Node v0.1 ({Mode} mode)",
        config.IsConsensusMode ? "consensus" : "standalone");

    var chainParams = ChainParameters.Devnet;
    var chainManager = new ChainManager();
    var mempool = new Mempool();
    var validator = new TransactionValidator(chainParams);

    // Storage initialization — RocksDB if DataDir is set, otherwise in-memory
    IStateDatabase stateDb;
    BlockStore? blockStore = null;
    ReceiptStore? receiptStore = null;

    var faucetAddr = Address.FromHexString("0x00000000000000000000000000000000000000FF");
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
                stateDb = new TrieStateDb(trieNodeStore, latestBlock.Header.StateRoot);
                chainManager.ResumeFromBlock(genesisBlock, latestBlock);
                Log.Information("Recovered from persistent storage. Latest block: #{Number}, Hash: {Hash}",
                    latestBlockNumber.Value, latestBlock.Hash.ToHexString()[..18] + "...");
            }
            else
            {
                // Data corrupted — start fresh
                Log.Warning("Persistent data corrupted, starting fresh");
                stateDb = new TrieStateDb(trieNodeStore);
                var genesisBlock = chainManager.CreateGenesisBlock(chainParams, genesisBalances, stateDb);
                PersistBlock(blockStore, genesisBlock);
                Log.Information("Genesis block created. Hash: {Hash}", genesisBlock.Hash.ToHexString()[..18] + "...");
            }
        }
        else
        {
            // Fresh start with RocksDB
            stateDb = new TrieStateDb(trieNodeStore);
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

    // Build the host
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    // Configure JSON serialization for AOT-compatible Minimal APIs
    builder.Services.ConfigureHttpJsonOptions(options =>
    {
        options.SerializerOptions.TypeInfoResolverChain.Add(BasaltApiJsonContext.Default);
        options.SerializerOptions.TypeInfoResolverChain.Add(WsJsonContext.Default);
    });

    // Register services
    builder.Services.AddSingleton(chainParams);
    builder.Services.AddSingleton<IStateDatabase>(stateDb);
    builder.Services.AddSingleton(chainManager);
    builder.Services.AddSingleton(mempool);
    builder.Services.AddSingleton(validator);
    builder.Services.AddGrpc();

    var app = builder.Build();

    // Map REST endpoints
    RestApiEndpoints.MapBasaltEndpoints(app, chainManager, mempool, validator, stateDb);

    // Map faucet endpoint
    FaucetEndpoint.FaucetAddress = faucetAddr;
    FaucetEndpoint.MapFaucetEndpoint(app, stateDb);

    // Map WebSocket endpoint
    app.UseWebSockets();
    var wsHandler = new WebSocketHandler(chainManager);
    app.MapWebSocketEndpoint(wsHandler);

    // Map gRPC service
    app.MapGrpcService<BasaltNodeService>();

    // Map Prometheus metrics endpoint
    MetricsEndpoint.MapMetricsEndpoint(app, chainManager, mempool);

    if (config.IsConsensusMode)
    {
        // === CONSENSUS MODE ===
        // Multi-node operation with P2P networking and BFT consensus
        var slashingEngine = new SlashingEngine(
            stakingState,
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<SlashingEngine>());

        var coordinator = new NodeCoordinator(
            config, chainParams, chainManager, mempool, stateDb, validator, wsHandler,
            app.Services.GetRequiredService<ILoggerFactory>(),
            blockStore, receiptStore,
            stakingState, slashingEngine);

        Log.Information("Basalt Node listening on {Urls}", string.Join(", ", app.Urls.DefaultIfEmpty($"http://localhost:{config.HttpPort}")));
        Log.Information("Chain: {Network} (ChainId={ChainId})", chainParams.NetworkName, chainParams.ChainId);
        Log.Information("Validator: index={Index}, address={Address}, P2P port={P2PPort}",
            config.ValidatorIndex, config.ValidatorAddress, config.P2PPort);
        Log.Information("Peers: {Peers}", string.Join(", ", config.Peers));

        app.Lifetime.ApplicationStarted.Register(() =>
        {
            _ = coordinator.StartAsync(app.Lifetime.ApplicationStopping);
        });

        app.Lifetime.ApplicationStopping.Register(() =>
        {
            Log.Information("Shutting down consensus coordinator...");
            coordinator.StopAsync().GetAwaiter().GetResult();
        });
    }
    else
    {
        // === STANDALONE MODE ===
        // Single-node block production on a timer (existing behavior)
        var proposer = Address.FromHexString("0x0000000000000000000000000000000000000001");
        var blockProduction = new BlockProductionLoop(
            chainParams, chainManager, mempool, stateDb, proposer,
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
            blockProduction.StopAsync().GetAwaiter().GetResult();
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
