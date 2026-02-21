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
    private readonly ChainParameters? _chainParams;

    // H-9: Maximum number of blocks to keep in memory
    private const int MaxInMemoryBlocks = 10_000;

    public ChainManager() { }

    public ChainManager(ChainParameters chainParams)
    {
        _chainParams = chainParams;
    }

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
    public BasaltResult AddBlock(Block block) => AddBlock(block, null);

    /// <summary>
    /// Add a block to the chain with optional state root validation.
    /// If computedStateRoot is provided, it must match the block header's StateRoot.
    /// </summary>
    public BasaltResult AddBlock(Block block, Hash256? computedStateRoot)
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

                // L-8: Validate timestamp monotonicity with correct error code
                if (block.Header.Timestamp <= _latestBlock.Header.Timestamp)
                    return BasaltResult.Error(BasaltErrorCode.InvalidTimestamp,
                        $"Block timestamp {block.Header.Timestamp} must be after parent timestamp {_latestBlock.Header.Timestamp}.");
            }
            else
            {
                // Genesis block
                if (block.Number != 0)
                    return BasaltResult.Error(BasaltErrorCode.InvalidBlockNumber,
                        "First block must be genesis (number 0).");
            }

            // H-7: Validate state root if the caller provides a computed one
            if (computedStateRoot.HasValue && block.Header.StateRoot != computedStateRoot.Value)
                return BasaltResult.Error(BasaltErrorCode.InvalidStateRoot,
                    $"Block state root mismatch: header={block.Header.StateRoot.ToHexString()}, computed={computedStateRoot.Value.ToHexString()}.");

            // H-8: Validate header fields when chain parameters are available
            if (_chainParams != null && _latestBlock != null)
            {
                if (block.Header.ChainId != _chainParams.ChainId)
                    return BasaltResult.Error(BasaltErrorCode.InvalidChainId,
                        $"Block chain ID {block.Header.ChainId} does not match expected {_chainParams.ChainId}.");

                if (block.Header.GasUsed > block.Header.GasLimit)
                    return BasaltResult.Error(BasaltErrorCode.GasLimitExceeded,
                        $"Block gas used ({block.Header.GasUsed}) exceeds gas limit ({block.Header.GasLimit}).");

                // H-6: Validate ExtraData size
                if ((uint)(block.Header.ExtraData?.Length ?? 0) > _chainParams.MaxExtraDataBytes)
                    return BasaltResult.Error(BasaltErrorCode.DataTooLarge,
                        $"Block ExtraData size ({block.Header.ExtraData?.Length}) exceeds limit ({_chainParams.MaxExtraDataBytes}).");
            }

            _blocksByHash[block.Hash] = block;
            _blocksByNumber[block.Number] = block;
            _latestBlock = block;

            // H-9: Evict old blocks beyond the retention window
            EvictOldBlocks();

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

        // Deploy system contracts (WBSLT, BNS, Governance, Escrow, StakingPool)
        if (stateDb != null)
            GenesisContractDeployer.DeployAll(stateDb, chainParams.ChainId);

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
            BaseFee = chainParams.InitialBaseFee,
        };

        var genesis = new Block
        {
            Header = genesisHeader,
            Transactions = [],
        };

        // M-10: Check AddBlock result and throw on failure
        var addResult = AddBlock(genesis);
        if (!addResult.IsSuccess)
            throw new InvalidOperationException($"Failed to add genesis block: {addResult.Message}");
        return genesis;
    }

    /// <summary>
    /// H-9: Evict old blocks beyond the retention window to prevent unbounded memory growth.
    /// Retains genesis (block 0) and the most recent MaxInMemoryBlocks blocks.
    /// Must be called under _lock.
    /// </summary>
    private void EvictOldBlocks()
    {
        if (_latestBlock == null || _blocksByNumber.Count <= MaxInMemoryBlocks)
            return;

        var cutoff = _latestBlock.Number > (ulong)MaxInMemoryBlocks
            ? _latestBlock.Number - (ulong)MaxInMemoryBlocks
            : 0;

        var toRemove = new List<ulong>();
        foreach (var number in _blocksByNumber.Keys)
        {
            // Keep genesis and blocks within the retention window
            if (number > 0 && number < cutoff)
                toRemove.Add(number);
        }

        foreach (var number in toRemove)
        {
            if (_blocksByNumber.Remove(number, out var block))
                _blocksByHash.Remove(block.Hash);
        }
    }
}
