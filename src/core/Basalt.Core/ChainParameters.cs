namespace Basalt.Core;

/// <summary>
/// Immutable chain configuration parameters. A record so callers can derive a variant with a single
/// overridden field via <c>with</c> (e.g. enabling trie pruning from an environment toggle at startup).
/// </summary>
public sealed record ChainParameters
{
    /// <summary>Chain ID for replay protection.</summary>
    public required uint ChainId { get; init; }

    /// <summary>Well-known chain id of the public incentivized testnet.</summary>
    public const uint IncentivizedTestnetChainId = 4242;

    /// <summary>
    /// Whether this is a public network, subject to the stricter startup guards (explicit faucet key, no
    /// debug CORS, required data dir and validator key). True for mainnet (1), the built-in testnet (2), and
    /// the public incentivized testnet (4242). Private devnets (e.g. 31337) are exempt. Without this, a
    /// public testnet on a chain id above 2 would silently run with devnet-relaxed guards.
    /// </summary>
    public bool IsPublicNetwork => ChainId is 1 or 2 or IncentivizedTestnetChainId;

    /// <summary>Human-readable network name.</summary>
    public required string NetworkName { get; init; }

    /// <summary>Target block time in milliseconds.</summary>
    public uint BlockTimeMs { get; init; } = 2000;

    /// <summary>Maximum block size in bytes.</summary>
    public uint MaxBlockSizeBytes { get; init; } = 2 * 1024 * 1024; // 2 MB

    /// <summary>Maximum transactions per block.</summary>
    public uint MaxTransactionsPerBlock { get; init; } = 10_000;

    /// <summary>Maximum transaction data size in bytes.</summary>
    public uint MaxTransactionDataBytes { get; init; } = 128 * 1024; // 128 KB

    /// <summary>H-6: Maximum extra data size in block headers (bytes). Increased to 256 for TWAP oracle data.</summary>
    public uint MaxExtraDataBytes { get; init; } = 256;

    /// <summary>Minimum gas price in smallest unit.</summary>
    public UInt256 MinGasPrice { get; init; } = new(1);

    /// <summary>Block gas limit.</summary>
    public ulong BlockGasLimit { get; init; } = 100_000_000;

    /// <summary>Initial base fee for the genesis block (EIP-1559).</summary>
    public UInt256 InitialBaseFee { get; init; } = new UInt256(1_000_000_000); // 1 gwei

    /// <summary>Denominator for base fee adjustment. 8 = max 12.5% change per block (EIP-1559).</summary>
    public uint BaseFeeChangeDenominator { get; init; } = 8;

    /// <summary>Elasticity multiplier. Target gas = BlockGasLimit / ElasticityMultiplier (EIP-1559).</summary>
    public uint ElasticityMultiplier { get; init; } = 2;

    /// <summary>Base transfer gas cost.</summary>
    public ulong TransferGasCost { get; init; } = 21_000;

    /// <summary>Contract deploy base gas cost.</summary>
    public ulong ContractDeployGasCost { get; init; } = 500_000;

    /// <summary>Contract call base gas cost.</summary>
    public ulong ContractCallGasCost { get; init; } = 50_000;

    /// <summary>
    /// Maximum validator set size supported by the ulong commit voter bitmap.
    /// The consensus vote bitmap is a 64-bit ulong where each bit represents one validator.
    /// Supporting more than 64 validators requires migrating to a variable-length bitmap
    /// (e.g., byte[] with length = ceil(validatorCount/8)) and a corresponding wire format change.
    /// </summary>
    public const uint MaxValidatorSetSize = 64;

    /// <summary>Number of validators in the active set (max 64 due to bitmap representation).</summary>
    public uint ValidatorSetSize { get; init; } = MaxValidatorSetSize;

    /// <summary>Minimum stake required to become a validator.</summary>
    public UInt256 MinValidatorStake { get; init; } = UInt256.Parse("100000000000000000000000"); // 100,000 tokens

    /// <summary>Epoch length in blocks.</summary>
    public uint EpochLength { get; init; } = 1000;

    /// <summary>Unbonding period in blocks.</summary>
    public uint UnbondingPeriod { get; init; } = 907_200; // ~21 days at 2s blocks

    /// <summary>
    /// Minimum block-signing participation percentage required to avoid inactivity slashing.
    /// Validators signing fewer than this percentage of blocks in an epoch are slashed.
    /// </summary>
    public uint InactivityThresholdPercent { get; init; } = 50;

    // ── Caldera Fusion DEX Parameters ──

    /// <summary>Gas cost for DEX pool creation.</summary>
    public ulong DexCreatePoolGas { get; init; } = 100_000;

    /// <summary>Gas cost for DEX add/remove liquidity.</summary>
    public ulong DexLiquidityGas { get; init; } = 80_000;

    /// <summary>Gas cost for DEX single swap.</summary>
    public ulong DexSwapGas { get; init; } = 80_000;

    /// <summary>Gas cost for placing a limit order.</summary>
    public ulong DexLimitOrderGas { get; init; } = 60_000;

    /// <summary>Gas cost for canceling a limit order.</summary>
    public ulong DexCancelOrderGas { get; init; } = 40_000;

    /// <summary>Gas cost for LP token transfer.</summary>
    public ulong DexTransferLpGas { get; init; } = 40_000;

    /// <summary>Gas cost for LP token approval.</summary>
    public ulong DexApproveLpGas { get; init; } = 30_000;

    /// <summary>Gas cost for minting a concentrated liquidity position.</summary>
    public ulong DexMintPositionGas { get; init; } = 120_000;

    /// <summary>Gas cost for burning a concentrated liquidity position.</summary>
    public ulong DexBurnPositionGas { get; init; } = 100_000;

    /// <summary>Gas cost for collecting fees from a concentrated position.</summary>
    public ulong DexCollectFeesGas { get; init; } = 60_000;

    /// <summary>Gas cost for encrypted swap intent (includes decryption overhead).</summary>
    public ulong DexEncryptedSwapIntentGas { get; init; } = 100_000;

    /// <summary>Maximum number of swap intents per batch auction per block.</summary>
    public uint DexMaxIntentsPerBatch { get; init; } = 500;

    /// <summary>Duration in milliseconds that the proposer waits for external solver solutions.</summary>
    public int SolverWindowMs { get; init; } = 500;

    /// <summary>Maximum number of registered solvers.</summary>
    public int MaxSolvers { get; init; } = 32;

    /// <summary>Fraction of swap fees rewarded to the winning solver (basis points, e.g. 1000 = 10%).</summary>
    public uint SolverRewardBps { get; init; } = 500;

    /// <summary>Admin address authorized to pause the DEX and set governance parameters. Null means no admin.</summary>
    public Address? DexAdminAddress { get; init; }

    /// <summary>TWAP oracle window in blocks (~4 hours at 2s blocks). Governance-overridable.</summary>
    public ulong TwapWindowBlocks { get; init; } = 7200;

    /// <summary>Maximum pool creations per block (0 = unlimited). Governance-overridable.</summary>
    public uint MaxPoolCreationsPerBlock { get; init; } = 10;

    /// <summary>Number of blocks to retain nullifiers for cross-block replay prevention.</summary>
    public uint NullifierWindowBlocks { get; init; } = 256;

    // ── Configurable Timeouts (M10, M11) ──

    /// <summary>M10: Consensus round timeout in milliseconds. Default 2000ms (2s).</summary>
    public uint ConsensusTimeoutMs { get; init; } = 2000;

    /// <summary>M11: P2P handshake timeout in milliseconds.</summary>
    public uint P2PHandshakeTimeoutMs { get; init; } = 5000;

    /// <summary>M11: P2P frame read timeout in milliseconds.</summary>
    public uint P2PFrameReadTimeoutMs { get; init; } = 120_000;

    /// <summary>M11: P2P connect timeout in milliseconds.</summary>
    public uint P2PConnectTimeoutMs { get; init; } = 10_000;

    /// <summary>Token decimals (18 like Ethereum).</summary>
    public byte TokenDecimals { get; init; } = 18;

    /// <summary>Token symbol.</summary>
    public string TokenSymbol { get; init; } = "BSLT";

    /// <summary>Protocol version.</summary>
    public uint ProtocolVersion { get; init; } = 1;

    // ── State-trie pruning (Phase 1.6) ──

    /// <summary>
    /// Enables the background trie prune sweep that bounds <c>trie_nodes</c> disk growth. Off by default
    /// because it is consensus-critical and is validated by the disk-flattening soak before being turned
    /// on. When off, the node never sweeps and relies on the testnet reset escape hatch.
    /// </summary>
    public bool EnableTriePruning { get; init; } = false;

    /// <summary>
    /// Number of recent canonical blocks whose state roots a prune sweep retains. Must be at least the
    /// fork rollback depth (1000) so any legal rollback re-roots onto a state whose nodes still exist.
    /// </summary>
    public ulong TriePruneWindowSize { get; init; } = 1000;

    /// <summary>Run a prune sweep every this many finalized blocks. Default 1000 (one epoch).</summary>
    public uint TriePruneIntervalBlocks { get; init; } = 1000;

    /// <summary>
    /// Fraction of scanned nodes a single sweep may delete before it aborts as implausible (guard 4).
    ///
    /// The default is deliberately strict: on a node that has pruned all along, a sweep deleting nearly
    /// everything means the reachability mark is broken, and refusing to sweep is the only safe answer.
    /// It has to be raisable because that reading is wrong in one legitimate case: the first sweep on a
    /// node with a long unpruned history really does delete almost everything, and correctly so. Raise it
    /// only when you know that is the situation, and put it back afterwards.
    /// </summary>
    public double TriePruneMaxDeleteFraction { get; init; } = 0.95;

    /// <summary>
    /// Validates that all chain parameters are within acceptable ranges.
    /// Should be called at node startup to catch misconfigurations early.
    /// </summary>
    public void Validate()
    {
        if (BlockTimeMs == 0)
            throw new InvalidOperationException("BlockTimeMs must be greater than zero.");
        if (BaseFeeChangeDenominator == 0)
            throw new InvalidOperationException("BaseFeeChangeDenominator must be greater than zero.");
        if (ElasticityMultiplier == 0)
            throw new InvalidOperationException("ElasticityMultiplier must be greater than zero.");
        if (EpochLength == 0)
            throw new InvalidOperationException("EpochLength must be greater than zero.");
        if (ValidatorSetSize == 0)
            throw new InvalidOperationException("ValidatorSetSize must be greater than zero.");
        if (EnableTriePruning)
        {
            if (TriePruneIntervalBlocks == 0)
                throw new InvalidOperationException("TriePruneIntervalBlocks must be greater than zero when pruning is enabled.");
            if (TriePruneMaxDeleteFraction is <= 0 or > 1)
                throw new InvalidOperationException("TriePruneMaxDeleteFraction must be greater than zero and at most 1.");
            if (TriePruneWindowSize < 1000)
                throw new InvalidOperationException(
                    $"TriePruneWindowSize ({TriePruneWindowSize}) must be at least the fork rollback depth (1000).");
        }
        // MEDIUM-02: Consensus vote bitmap is ulong (64 bits), so >64 validators silently
        // corrupts quorum detection. Enforce at validation time.
        if (ValidatorSetSize > MaxValidatorSetSize)
            throw new InvalidOperationException(
                $"ValidatorSetSize ({ValidatorSetSize}) exceeds maximum ({MaxValidatorSetSize}). " +
                "Consensus vote bitmap is ulong (64 bits).");
        if (BlockGasLimit == 0)
            throw new InvalidOperationException("BlockGasLimit must be greater than zero.");
        if (MaxBlockSizeBytes == 0)
            throw new InvalidOperationException("MaxBlockSizeBytes must be greater than zero.");
        if (MaxTransactionsPerBlock == 0)
            throw new InvalidOperationException("MaxTransactionsPerBlock must be greater than zero.");
        if (string.IsNullOrEmpty(NetworkName))
            throw new InvalidOperationException("NetworkName must not be empty.");
        if (IsPublicNetwork && DexAdminAddress == null)
            throw new InvalidOperationException(
                "DexAdminAddress must be set for a public network. DEX governance cannot function without an admin.");
    }

    private static Address MakeDexGovernanceAddress()
    {
        var bytes = new byte[20];
        bytes[18] = 0x10;
        bytes[19] = 0x0A; // 0x...100A — dedicated DEX governance address
        return new Address(bytes);
    }

    private static readonly ChainParameters _mainnet = new()
    {
        ChainId = 1,
        NetworkName = "basalt-mainnet",
        BlockTimeMs = 2000,
        MaxBlockSizeBytes = 2 * 1024 * 1024,
        MaxTransactionsPerBlock = 10_000,
        MaxTransactionDataBytes = 128 * 1024,
        InitialBaseFee = new UInt256(1_000_000_000),
        BaseFeeChangeDenominator = 8,
        ElasticityMultiplier = 2,
        BlockGasLimit = 100_000_000,
        ValidatorSetSize = 64,
        MinValidatorStake = UInt256.Parse("100000000000000000000000"),
        EpochLength = 1000,
        UnbondingPeriod = 907_200,
        InactivityThresholdPercent = 50,
        NullifierWindowBlocks = 256,
        DexAdminAddress = MakeDexGovernanceAddress(),
        TwapWindowBlocks = 7200,
        MaxPoolCreationsPerBlock = 10,
        SolverRewardBps = 500,
        DexMaxIntentsPerBatch = 500,
    };

    private static readonly ChainParameters _testnet = new()
    {
        ChainId = 2,
        NetworkName = "basalt-testnet",
        BlockTimeMs = 2000,
        InitialBaseFee = new UInt256(100_000_000),
        ValidatorSetSize = 32,
        MinValidatorStake = UInt256.Parse("10000000000000000000000"),
        EpochLength = 500,
        UnbondingPeriod = 43_200,
        InactivityThresholdPercent = 50,
        NullifierWindowBlocks = 128,
        DexAdminAddress = MakeDexGovernanceAddress(),
        TwapWindowBlocks = 3600,
        MaxPoolCreationsPerBlock = 20,
    };

    /// <summary>Pre-defined Basalt mainnet parameters.</summary>
    public static ChainParameters Mainnet => _mainnet;

    /// <summary>Pre-defined Basalt testnet parameters.</summary>
    public static ChainParameters Testnet => _testnet;

    /// <summary>Pre-defined Basalt devnet parameters for local development.</summary>
    public static ChainParameters Devnet => new()
    {
        ChainId = 31337,
        NetworkName = "basalt-devnet",
        BlockTimeMs = 2000,
        ValidatorSetSize = 4,
        MinValidatorStake = new UInt256(1000),
        EpochLength = 100,
        InitialBaseFee = new UInt256(1),
        InactivityThresholdPercent = 50,
    };

    /// <summary>
    /// Creates chain parameters from node configuration, selecting the appropriate
    /// security profile based on chain ID. Falls back to devnet parameters for
    /// unrecognized chain IDs.
    /// </summary>
    public static ChainParameters FromConfiguration(uint chainId, string networkName)
    {
        return chainId switch
        {
            1 => new ChainParameters
            {
                ChainId = chainId,
                NetworkName = networkName,
                BlockTimeMs = 2000,
                MaxBlockSizeBytes = 2 * 1024 * 1024,
                MaxTransactionsPerBlock = 10_000,
                MaxTransactionDataBytes = 128 * 1024,
                InitialBaseFee = new UInt256(1_000_000_000),
                BaseFeeChangeDenominator = 8,
                ElasticityMultiplier = 2,
                BlockGasLimit = 100_000_000,
                ValidatorSetSize = 64,
                MinValidatorStake = UInt256.Parse("100000000000000000000000"),
                EpochLength = 1000,
                UnbondingPeriod = 907_200,
                InactivityThresholdPercent = 50,
                NullifierWindowBlocks = 256,
                DexAdminAddress = MakeDexGovernanceAddress(),
                TwapWindowBlocks = 7200,
                MaxPoolCreationsPerBlock = 10,
                SolverRewardBps = 500,
                DexMaxIntentsPerBatch = 500,
            },
            2 => new ChainParameters
            {
                ChainId = chainId,
                NetworkName = networkName,
                BlockTimeMs = 2000,
                InitialBaseFee = new UInt256(100_000_000),
                ValidatorSetSize = 32,
                MinValidatorStake = UInt256.Parse("10000000000000000000000"),
                EpochLength = 500,
                UnbondingPeriod = 43_200,
                InactivityThresholdPercent = 50,
                NullifierWindowBlocks = 128,
                DexAdminAddress = MakeDexGovernanceAddress(),
                TwapWindowBlocks = 3600,
                MaxPoolCreationsPerBlock = 20,
            },
            IncentivizedTestnetChainId => new ChainParameters
            {
                // Public incentivized testnet: testnet-scale parameters, and a DexAdminAddress so it passes
                // the public-network validation (IsPublicNetwork requires one).
                ChainId = chainId,
                NetworkName = networkName,
                BlockTimeMs = 2000,
                InitialBaseFee = new UInt256(100_000_000),
                ValidatorSetSize = 32,
                MinValidatorStake = UInt256.Parse("10000000000000000000000"),
                EpochLength = 500,
                UnbondingPeriod = 43_200,
                InactivityThresholdPercent = 50,
                NullifierWindowBlocks = 128,
                DexAdminAddress = MakeDexGovernanceAddress(),
                TwapWindowBlocks = 3600,
                MaxPoolCreationsPerBlock = 20,
            },
            _ => new ChainParameters
            {
                ChainId = chainId,
                NetworkName = networkName,
                // Devnet / local development parameters
                BlockTimeMs = 2000,
                ValidatorSetSize = 4,
                MinValidatorStake = new UInt256(1000),
                EpochLength = 100,
                InitialBaseFee = new UInt256(1),
                InactivityThresholdPercent = 50,
                NullifierWindowBlocks = 16,
            },
        };
    }
}
