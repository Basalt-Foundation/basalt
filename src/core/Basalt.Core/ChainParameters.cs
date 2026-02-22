namespace Basalt.Core;

/// <summary>
/// Immutable chain configuration parameters.
/// </summary>
public sealed class ChainParameters
{
    /// <summary>Chain ID for replay protection.</summary>
    public required uint ChainId { get; init; }

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

    /// <summary>H-6: Maximum extra data size in block headers (bytes).</summary>
    public uint MaxExtraDataBytes { get; init; } = 32;

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

    /// <summary>Token decimals (18 like Ethereum).</summary>
    public byte TokenDecimals { get; init; } = 18;

    /// <summary>Token symbol.</summary>
    public string TokenSymbol { get; init; } = "BSLT";

    /// <summary>Protocol version.</summary>
    public uint ProtocolVersion { get; init; } = 1;

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
    }

    private static readonly ChainParameters _mainnet = new()
    {
        ChainId = 1,
        NetworkName = "basalt-mainnet",
    };

    private static readonly ChainParameters _testnet = new()
    {
        ChainId = 2,
        NetworkName = "basalt-testnet",
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
                // Mainnet security parameters (defaults from property initializers)
            },
            2 => new ChainParameters
            {
                ChainId = chainId,
                NetworkName = networkName,
                // Testnet: same security profile as mainnet
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
            },
        };
    }
}
