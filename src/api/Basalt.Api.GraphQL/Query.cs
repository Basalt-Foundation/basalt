using Basalt.Core;
using Basalt.Execution;
using Basalt.Storage;

namespace Basalt.Api.GraphQL;

public class Query
{
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

        var count = Math.Min(last, 100); // Cap at 100
        for (ulong i = latest.Number; i > 0 && results.Count < count; i--)
        {
            var block = chainManager.GetBlockByNumber(i);
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
