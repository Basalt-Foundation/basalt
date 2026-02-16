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
    public uint BlockTimeMs { get; init; } = 400;

    /// <summary>Maximum block size in bytes.</summary>
    public uint MaxBlockSizeBytes { get; init; } = 2 * 1024 * 1024; // 2 MB

    /// <summary>Maximum transactions per block.</summary>
    public uint MaxTransactionsPerBlock { get; init; } = 10_000;

    /// <summary>Maximum transaction data size in bytes.</summary>
    public uint MaxTransactionDataBytes { get; init; } = 128 * 1024; // 128 KB

    /// <summary>Minimum gas price in smallest unit.</summary>
    public UInt256 MinGasPrice { get; init; } = new(1);

    /// <summary>Block gas limit.</summary>
    public ulong BlockGasLimit { get; init; } = 100_000_000;

    /// <summary>Base transfer gas cost.</summary>
    public ulong TransferGasCost { get; init; } = 21_000;

    /// <summary>Contract deploy base gas cost.</summary>
    public ulong ContractDeployGasCost { get; init; } = 500_000;

    /// <summary>Contract call base gas cost.</summary>
    public ulong ContractCallGasCost { get; init; } = 50_000;

    /// <summary>Number of validators in the active set.</summary>
    public uint ValidatorSetSize { get; init; } = 100;

    /// <summary>Minimum stake required to become a validator.</summary>
    public UInt256 MinValidatorStake { get; init; } = UInt256.Parse("100000000000000000000000"); // 100,000 tokens

    /// <summary>Epoch length in blocks.</summary>
    public uint EpochLength { get; init; } = 1000;

    /// <summary>Unbonding period in blocks.</summary>
    public uint UnbondingPeriod { get; init; } = 907_200; // ~21 days at 2s blocks

    /// <summary>Token decimals (18 like Ethereum).</summary>
    public byte TokenDecimals { get; init; } = 18;

    /// <summary>Token symbol.</summary>
    public string TokenSymbol { get; init; } = "BSLT";

    /// <summary>Protocol version.</summary>
    public uint ProtocolVersion { get; init; } = 1;

    /// <summary>Pre-defined Basalt mainnet parameters.</summary>
    public static ChainParameters Mainnet => new()
    {
        ChainId = 1,
        NetworkName = "basalt-mainnet",
    };

    /// <summary>Pre-defined Basalt testnet parameters.</summary>
    public static ChainParameters Testnet => new()
    {
        ChainId = 2,
        NetworkName = "basalt-testnet",
    };

    /// <summary>Pre-defined Basalt devnet parameters for local development.</summary>
    public static ChainParameters Devnet => new()
    {
        ChainId = 31337,
        NetworkName = "basalt-devnet",
        BlockTimeMs = 400,
        ValidatorSetSize = 4,
        MinValidatorStake = new UInt256(1000),
        EpochLength = 100,
    };

    /// <summary>
    /// Creates chain parameters from node configuration, using Devnet defaults
    /// for any unspecified parameters but honoring the configured chain ID and network name.
    /// </summary>
    public static ChainParameters FromConfiguration(uint chainId, string networkName)
    {
        return new ChainParameters
        {
            ChainId = chainId,
            NetworkName = networkName,
            BlockTimeMs = 400,
            ValidatorSetSize = 4,
            MinValidatorStake = new UInt256(1000),
            EpochLength = 100,
        };
    }
}
