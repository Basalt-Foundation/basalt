using Basalt.Core;
using Basalt.Crypto;
using Basalt.Storage;

namespace Basalt.Execution;

/// <summary>
/// Builds blocks by selecting transactions from a pool, executing them, and computing the block header.
/// </summary>
public sealed class BlockBuilder
{
    private readonly ChainParameters _chainParams;
    private readonly TransactionValidator _validator;
    private readonly TransactionExecutor _executor;

    public BlockBuilder(ChainParameters chainParams)
    {
        _chainParams = chainParams;
        _validator = new TransactionValidator(chainParams);
        _executor = new TransactionExecutor(chainParams);
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
        };

        foreach (var tx in pendingTransactions)
        {
            if (validTxs.Count >= (int)_chainParams.MaxTransactionsPerBlock)
                break;

            if (totalGasUsed + tx.GasLimit > _chainParams.BlockGasLimit)
                continue;

            var validation = _validator.Validate(tx, stateDb);
            if (!validation.IsSuccess)
                continue;

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
    /// </summary>
    public static Hash256 ComputeReceiptsRoot(List<TransactionReceipt> receipts)
    {
        if (receipts.Count == 0)
            return Hash256.Zero;

        var hashes = receipts.Select(r => r.TransactionHash).ToList();
        return ComputeMerkleRoot(hashes);
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
