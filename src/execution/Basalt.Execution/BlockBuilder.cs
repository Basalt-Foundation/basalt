using Basalt.Core;
using Basalt.Crypto;
using Basalt.Storage;
using Microsoft.Extensions.Logging;

namespace Basalt.Execution;

/// <summary>
/// Builds blocks by selecting transactions from a pool, executing them, and computing the block header.
/// </summary>
public sealed class BlockBuilder
{
    private readonly ChainParameters _chainParams;
    private readonly TransactionValidator _validator;
    private readonly TransactionExecutor _executor;
    private readonly ILogger<BlockBuilder>? _logger;

    public BlockBuilder(ChainParameters chainParams, ILogger<BlockBuilder>? logger = null)
    {
        _chainParams = chainParams;
        _validator = new TransactionValidator(chainParams);
        _executor = new TransactionExecutor(chainParams);
        _logger = logger;
    }

    /// <summary>
    /// Build a block from the pending transactions.
    /// </summary>
    public Block BuildBlock(
        IReadOnlyList<Transaction> pendingTransactions,
        IStateDatabase stateDb,
        BlockHeader parentHeader,
        Address proposer)
    {
        var validTxs = new List<Transaction>();
        var receipts = new List<TransactionReceipt>();
        ulong totalGasUsed = 0;

        // Compute EIP-1559 base fee from parent block
        var baseFee = BaseFeeCalculator.Calculate(
            parentHeader.BaseFee, parentHeader.GasUsed, parentHeader.GasLimit, _chainParams);

        // Create a preliminary header for receipt generation
        var blockNumber = parentHeader.Number + 1;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var preliminaryHeader = new BlockHeader
        {
            Number = blockNumber,
            ParentHash = parentHeader.Hash,
            StateRoot = Hash256.Zero, // Will be computed after execution
            TransactionsRoot = Hash256.Zero,
            ReceiptsRoot = Hash256.Zero,
            Timestamp = timestamp,
            Proposer = proposer,
            ChainId = _chainParams.ChainId,
            GasUsed = 0,
            GasLimit = _chainParams.BlockGasLimit,
            BaseFee = baseFee,
        };

        foreach (var tx in pendingTransactions)
        {
            if (validTxs.Count >= (int)_chainParams.MaxTransactionsPerBlock)
                break;

            if (totalGasUsed + tx.GasLimit > _chainParams.BlockGasLimit)
                continue;

            var validation = _validator.Validate(tx, stateDb, baseFee);
            if (!validation.IsSuccess)
            {
                _logger?.LogWarning("BuildBlock skipped tx {Hash}: {Error}",
                    tx.Hash.ToHexString()[..18] + "...", validation.Message);
                continue;
            }

            var receipt = _executor.Execute(tx, stateDb, preliminaryHeader, validTxs.Count);
            validTxs.Add(tx);
            receipts.Add(receipt);
            totalGasUsed += receipt.GasUsed;
        }

        // Compute roots
        var stateRoot = stateDb.ComputeStateRoot();
        var txRoot = ComputeTransactionsRoot(validTxs);
        var receiptsRoot = ComputeReceiptsRoot(receipts);

        var header = new BlockHeader
        {
            Number = blockNumber,
            ParentHash = parentHeader.Hash,
            StateRoot = stateRoot,
            TransactionsRoot = txRoot,
            ReceiptsRoot = receiptsRoot,
            Timestamp = timestamp,
            Proposer = proposer,
            ChainId = _chainParams.ChainId,
            GasUsed = totalGasUsed,
            GasLimit = _chainParams.BlockGasLimit,
            BaseFee = baseFee,
        };

        return new Block
        {
            Header = header,
            Transactions = validTxs,
            Receipts = receipts,
        };
    }

    /// <summary>
    /// Compute the Merkle root of transaction hashes.
    /// </summary>
    public static Hash256 ComputeTransactionsRoot(List<Transaction> transactions)
    {
        if (transactions.Count == 0)
            return Hash256.Zero;

        var hashes = transactions.Select(tx => tx.Hash).ToList();
        return ComputeMerkleRoot(hashes);
    }

    /// <summary>
    /// Compute the Merkle root of receipt hashes.
    /// Each receipt is hashed from its content (status + gasUsed + logs hash) rather than
    /// the transaction hash, ensuring receipt integrity is independently verifiable.
    /// </summary>
    public static Hash256 ComputeReceiptsRoot(List<TransactionReceipt> receipts)
    {
        if (receipts.Count == 0)
            return Hash256.Zero;

        var hashes = receipts.Select(ComputeReceiptHash).ToList();
        return ComputeMerkleRoot(hashes);
    }

    private static Hash256 ComputeReceiptHash(TransactionReceipt receipt)
    {
        // Fixed-size format: [1 byte success][8 bytes gasUsed][32 bytes txHash][32 bytes logsHash]
        // logsHash is Hash256.Zero when there are no logs — eliminates variable-length ambiguity.
        Hash256 logsHash = Hash256.Zero;
        if (receipt.Logs.Count > 0)
        {
            using var logsHasher = Blake3Hasher.CreateIncremental();
            Span<byte> logEntry = stackalloc byte[Address.Size + Hash256.Size];
            foreach (var log in receipt.Logs)
            {
                log.Contract.WriteTo(logEntry[..Address.Size]);
                log.EventSignature.WriteTo(logEntry[Address.Size..]);
                logsHasher.Update(logEntry);
                if (log.Data.Length > 0)
                    logsHasher.Update(log.Data);
            }
            logsHash = logsHasher.Finalize();
        }

        // Fixed-size buffer: 1 + 8 + 32 + 32 = 73 bytes (always)
        Span<byte> buffer = stackalloc byte[1 + 8 + Hash256.Size + Hash256.Size];
        buffer[0] = receipt.Success ? (byte)1 : (byte)0;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buffer[1..], receipt.GasUsed);
        receipt.TransactionHash.WriteTo(buffer[9..]);
        logsHash.WriteTo(buffer[(9 + Hash256.Size)..]);

        return Blake3Hasher.Hash(buffer);
    }

    private static Hash256 ComputeMerkleRoot(List<Hash256> hashes)
    {
        if (hashes.Count == 0)
            return Hash256.Zero;
        if (hashes.Count == 1)
            return hashes[0];

        while (hashes.Count > 1)
        {
            var next = new List<Hash256>();
            for (int i = 0; i < hashes.Count; i += 2)
            {
                if (i + 1 < hashes.Count)
                    next.Add(Blake3Hasher.HashPair(hashes[i], hashes[i + 1]));
                else
                    next.Add(hashes[i]);
            }
            hashes = next;
        }

        return hashes[0];
    }
}
