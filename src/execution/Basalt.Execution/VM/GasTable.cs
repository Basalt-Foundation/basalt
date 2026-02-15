namespace Basalt.Execution.VM;

/// <summary>
/// Gas costs for BasaltVM operations.
/// Phase 1: Simplified flat costs.
/// </summary>
public static class GasTable
{
    // Base costs
    public const ulong TxBase = 21_000;
    public const ulong TxDataZeroByte = 4;
    public const ulong TxDataNonZeroByte = 16;
    public const ulong ContractCreation = 32_000;

    // Storage
    public const ulong StorageRead = 200;
    public const ulong StorageWrite = 5_000;
    public const ulong StorageWriteNew = 20_000;
    public const ulong StorageDelete = 5_000;
    public const ulong StorageDeleteRefund = 15_000;

    // Compute
    public const ulong Blake3Hash = 30;
    public const ulong Blake3HashPerWord = 6;
    public const ulong Keccak256 = 30;
    public const ulong Keccak256PerWord = 6;
    public const ulong Ed25519Verify = 3_000;

    // Memory
    public const ulong MemoryPerByte = 3;
    public const ulong CopyPerByte = 3;

    // Calls
    public const ulong Call = 700;
    public const ulong CallValueTransfer = 9_000;
    public const ulong CallNewAccount = 25_000;

    // Events
    public const ulong Log = 375;
    public const ulong LogTopic = 375;
    public const ulong LogDataPerByte = 8;

    // ZK / Confidentiality operations
    public const ulong PedersenCommit = 10_000;
    public const ulong Groth16Verify = 300_000;
    public const ulong G1ScalarMult = 5_000;
    public const ulong G1Add = 500;
    public const ulong Pairing = 75_000;

    // System
    public const ulong Balance = 400;
    public const ulong BlockHash = 20;
    public const ulong Timestamp = 2;
    public const ulong BlockNumber = 2;
    public const ulong Caller = 2;

    /// <summary>
    /// Compute gas cost for transaction data.
    /// </summary>
    public static ulong ComputeDataGas(ReadOnlySpan<byte> data)
    {
        ulong cost = 0;
        foreach (byte b in data)
            cost += b == 0 ? TxDataZeroByte : TxDataNonZeroByte;
        return cost;
    }
}
