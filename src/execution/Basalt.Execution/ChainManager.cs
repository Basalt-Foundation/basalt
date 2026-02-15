using Basalt.Core;

namespace Basalt.Execution;

/// <summary>
/// Manages the canonical chain, providing block lookup and chain advancement.
/// </summary>
public sealed class ChainManager
{
    private readonly object _lock = new();
    private readonly Dictionary<Hash256, Block> _blocksByHash = new();
    private readonly Dictionary<ulong, Block> _blocksByNumber = new();
    private Block? _latestBlock;

    public Block? LatestBlock
    {
        get { lock (_lock) return _latestBlock; }
    }

    public ulong LatestBlockNumber
    {
        get { lock (_lock) return _latestBlock?.Number ?? 0; }
    }

    /// <summary>
    /// Add a block to the chain. Must be a valid extension of the current chain tip.
    /// </summary>
    public BasaltResult AddBlock(Block block)
    {
        lock (_lock)
        {
            if (_latestBlock != null)
            {
                if (block.Header.ParentHash != _latestBlock.Hash)
                    return BasaltResult.Error(BasaltErrorCode.InvalidParentHash,
                        "Block parent hash does not match current chain tip.");

                if (block.Number != _latestBlock.Number + 1)
                    return BasaltResult.Error(BasaltErrorCode.InvalidBlockNumber,
                        $"Expected block number {_latestBlock.Number + 1}, got {block.Number}.");
            }
            else
            {
                // Genesis block
                if (block.Number != 0)
                    return BasaltResult.Error(BasaltErrorCode.InvalidBlockNumber,
                        "First block must be genesis (number 0).");
            }

            _blocksByHash[block.Hash] = block;
            _blocksByNumber[block.Number] = block;
            _latestBlock = block;

            return BasaltResult.Ok;
        }
    }

    public Block? GetBlockByHash(Hash256 hash)
    {
        lock (_lock)
            return _blocksByHash.TryGetValue(hash, out var block) ? block : null;
    }

    public Block? GetBlockByNumber(ulong number)
    {
        lock (_lock)
            return _blocksByNumber.TryGetValue(number, out var block) ? block : null;
    }

    /// <summary>
    /// Resume from a known genesis and latest block without replaying the full chain.
    /// Used on startup when recovering from persistent storage.
    /// </summary>
    public void ResumeFromBlock(Block genesisBlock, Block latestBlock)
    {
        lock (_lock)
        {
            _blocksByHash[genesisBlock.Hash] = genesisBlock;
            _blocksByNumber[genesisBlock.Number] = genesisBlock;

            if (latestBlock.Number != genesisBlock.Number)
            {
                _blocksByHash[latestBlock.Hash] = latestBlock;
                _blocksByNumber[latestBlock.Number] = latestBlock;
            }

            _latestBlock = latestBlock;
        }
    }

    /// <summary>
    /// Create and add a genesis block with initial account balances.
    /// </summary>
    public Block CreateGenesisBlock(ChainParameters chainParams, Dictionary<Address, UInt256>? initialBalances = null,
        Storage.IStateDatabase? stateDb = null, long? genesisTimestamp = null)
    {
        if (initialBalances != null && stateDb != null)
        {
            foreach (var (address, balance) in initialBalances)
            {
                stateDb.SetAccount(address, new Storage.AccountState
                {
                    Balance = balance,
                    Nonce = 0,
                    AccountType = Storage.AccountType.ExternallyOwned,
                });
            }
        }

        var stateRoot = stateDb?.ComputeStateRoot() ?? Hash256.Zero;

        var genesisHeader = new BlockHeader
        {
            Number = 0,
            ParentHash = Hash256.Zero,
            StateRoot = stateRoot,
            TransactionsRoot = Hash256.Zero,
            ReceiptsRoot = Hash256.Zero,
            Timestamp = genesisTimestamp ?? 0,
            Proposer = Address.Zero,
            ChainId = chainParams.ChainId,
            GasUsed = 0,
            GasLimit = chainParams.BlockGasLimit,
        };

        var genesis = new Block
        {
            Header = genesisHeader,
            Transactions = [],
        };

        AddBlock(genesis);
        return genesis;
    }
}
