using Basalt.Core;
using Basalt.Execution;
using Basalt.Storage;
using Basalt.Storage.RocksDb;

namespace Basalt.Api.GraphQL;

public class Query
{
    /// <summary>L-6: Shared helper — convert in-memory TransactionReceipt to ReceiptData.</summary>
    private static ReceiptData MapToReceiptData(TransactionReceipt r) => new()
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
    };

    /// <summary>L-6: Shared helper — convert ReceiptData to ReceiptResult.</summary>
    private static ReceiptResult MapToReceiptResult(ReceiptData receipt) => new()
    {
        TransactionHash = receipt.TransactionHash.ToHexString(),
        BlockHash = receipt.BlockHash.ToHexString(),
        BlockNumber = receipt.BlockNumber,
        TransactionIndex = receipt.TransactionIndex,
        From = receipt.From.ToHexString(),
        To = receipt.To.ToHexString(),
        GasUsed = receipt.GasUsed,
        Success = receipt.Success,
        ErrorCode = ((BasaltErrorCode)receipt.ErrorCode).ToString(),
        PostStateRoot = receipt.PostStateRoot.ToHexString(),
        EffectiveGasPrice = receipt.EffectiveGasPrice.ToString(),
        Logs = receipt.Logs.Select(l => new EventLogResult
        {
            Contract = l.Contract.ToHexString(),
            EventSignature = l.EventSignature.ToHexString(),
            Topics = l.Topics.Select(t => t.ToHexString()).ToList(),
            Data = l.Data.Length > 0 ? Convert.ToHexString(l.Data) : null,
        }).ToList(),
    };

    /// <summary>L-6: Shared receipt lookup — persistent store first, then in-memory fallback.</summary>
    private static ReceiptData? LookupReceipt(Hash256 hash, ChainManager chainManager, ReceiptStore? receiptStore)
    {
        var receipt = receiptStore?.GetReceipt(hash);
        if (receipt != null) return receipt;

        var latestNum = chainManager.LatestBlockNumber;
        var scanDepth = Math.Min(latestNum + 1, 1000UL);
        for (ulong i = 0; i < scanDepth; i++)
        {
            var block = chainManager.GetBlockByNumber(latestNum - i);
            if (block?.Receipts == null) continue;
            var r = block.Receipts.FirstOrDefault(r => r.TransactionHash == hash);
            if (r != null) return MapToReceiptData(r);
        }
        return null;
    }

    public ReceiptResult? GetReceipt(string txHash,
        [Service] ChainManager chainManager,
        [Service] IServiceProvider serviceProvider)
    {
        if (!Hash256.TryFromHexString(txHash, out var hash))
            return null;

        var receiptStore = serviceProvider.GetService(typeof(ReceiptStore)) as ReceiptStore;
        var receipt = LookupReceipt(hash, chainManager, receiptStore);
        return receipt != null ? MapToReceiptResult(receipt) : null;
    }

    public TransactionDetailResult? GetTransaction(string txHash,
        [Service] ChainManager chainManager,
        [Service] IServiceProvider serviceProvider)
    {
        if (!Hash256.TryFromHexString(txHash, out var hash))
            return null;

        var receiptStore = serviceProvider.GetService(typeof(ReceiptStore)) as ReceiptStore;
        var latestNum = chainManager.LatestBlockNumber;
        var scanDepth = Math.Min(latestNum + 1, 1000UL);

        for (ulong i = 0; i < scanDepth; i++)
        {
            var block = chainManager.GetBlockByNumber(latestNum - i);
            if (block == null) continue;
            for (int j = 0; j < block.Transactions.Count; j++)
            {
                var tx = block.Transactions[j];
                if (tx.Hash != hash) continue;

                var result = new TransactionDetailResult
                {
                    Hash = tx.Hash.ToHexString(),
                    Type = tx.Type.ToString(),
                    Nonce = tx.Nonce,
                    Sender = tx.Sender.ToHexString(),
                    To = tx.To.ToHexString(),
                    Value = tx.Value.ToString(),
                    GasLimit = tx.GasLimit,
                    GasPrice = tx.GasPrice.ToString(),
                    MaxFeePerGas = tx.IsEip1559 ? tx.MaxFeePerGas.ToString() : null,
                    MaxPriorityFeePerGas = tx.IsEip1559 ? tx.MaxPriorityFeePerGas.ToString() : null,
                    BlockNumber = block.Number,
                    BlockHash = block.Hash.ToHexString(),
                    TransactionIndex = j,
                };

                // Try to get receipt from store or in-memory
                ReceiptData? receipt = receiptStore?.GetReceipt(hash);
                if (receipt == null && block.Receipts != null && j < block.Receipts.Count)
                    receipt = MapToReceiptData(block.Receipts[j]);

                if (receipt != null)
                {
                    result.GasUsed = receipt.GasUsed;
                    result.Success = receipt.Success;
                    result.ErrorCode = ((BasaltErrorCode)receipt.ErrorCode).ToString();
                    result.EffectiveGasPrice = receipt.EffectiveGasPrice.ToString();
                    result.Logs = receipt.Logs.Select(l => new EventLogResult
                    {
                        Contract = l.Contract.ToHexString(),
                        EventSignature = l.EventSignature.ToHexString(),
                        Topics = l.Topics.Select(t => t.ToHexString()).ToList(),
                        Data = l.Data.Length > 0 ? Convert.ToHexString(l.Data) : null,
                    }).ToList();
                }

                return result;
            }
        }
        return null;
    }

    public StatusResult GetStatus(
        [Service] ChainManager chainManager,
        [Service] Mempool mempool)
    {
        var latest = chainManager.LatestBlock;
        return new StatusResult
        {
            BlockHeight = latest?.Number ?? 0,
            LatestBlockHash = latest?.Hash.ToHexString() ?? Hash256.Zero.ToHexString(),
            MempoolSize = mempool.Count,
            ProtocolVersion = 1,
        };
    }

    public BlockResult? GetBlock(string id, [Service] ChainManager chainManager)
    {
        Block? block;
        if (ulong.TryParse(id, out var number))
            block = chainManager.GetBlockByNumber(number);
        else if (Hash256.TryFromHexString(id, out var hash))
            block = chainManager.GetBlockByHash(hash);
        else
            return null;

        return block != null ? BlockResult.FromBlock(block) : null;
    }

    public BlockResult? GetLatestBlock([Service] ChainManager chainManager)
    {
        var block = chainManager.LatestBlock;
        return block != null ? BlockResult.FromBlock(block) : null;
    }

    public List<BlockResult> GetBlocks(int last, [Service] ChainManager chainManager)
    {
        var results = new List<BlockResult>();
        var latest = chainManager.LatestBlock;
        if (latest == null) return results;

        var count = Math.Clamp(last, 1, 100);
        // M-6: Include genesis block (i >= 0) — use long to avoid ulong underflow
        for (long i = (long)latest.Number; i >= 0 && results.Count < count; i--)
        {
            var block = chainManager.GetBlockByNumber((ulong)i);
            if (block != null)
                results.Add(BlockResult.FromBlock(block));
        }
        return results;
    }

    public AccountResult? GetAccount(string address, [Service] IStateDatabase stateDb)
    {
        if (!Address.TryFromHexString(address, out var addr))
            return null;

        var account = stateDb.GetAccount(addr);
        if (account == null) return null;

        return new AccountResult
        {
            Address = addr.ToHexString(),
            Balance = account.Value.Balance.ToString(),
            Nonce = account.Value.Nonce,
            AccountType = account.Value.AccountType.ToString(),
        };
    }
}
