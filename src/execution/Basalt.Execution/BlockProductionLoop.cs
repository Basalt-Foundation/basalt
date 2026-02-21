using Basalt.Core;
using Basalt.Storage;
using Microsoft.Extensions.Logging;

namespace Basalt.Execution;

/// <summary>
/// Background loop that produces blocks on a timer.
/// Drains the mempool, builds blocks, and advances the chain.
/// </summary>
public sealed class BlockProductionLoop
{
    private readonly ChainParameters _chainParams;
    private readonly ChainManager _chainManager;
    private readonly Mempool _mempool;
    private readonly IStateDatabase _stateDb;
    private readonly BlockBuilder _blockBuilder;
    private readonly Address _proposer;
    private readonly ILogger<BlockProductionLoop> _logger;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public event Action<Block>? OnBlockProduced;

    public BlockProductionLoop(
        ChainParameters chainParams,
        ChainManager chainManager,
        Mempool mempool,
        IStateDatabase stateDb,
        Address proposer,
        ILogger<BlockProductionLoop> logger)
    {
        _chainParams = chainParams;
        _chainManager = chainManager;
        _mempool = mempool;
        _stateDb = stateDb;
        _blockBuilder = new BlockBuilder(chainParams);
        _proposer = proposer;
        _logger = logger;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loopTask = RunLoop(_cts.Token);
        _logger.LogInformation("Block production started. Block time: {BlockTime}ms, Proposer: {Proposer}",
            _chainParams.BlockTimeMs, _proposer);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_loopTask != null)
            await _loopTask;
        _logger.LogInformation("Block production stopped.");
    }

    private async Task RunLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay((int)_chainParams.BlockTimeMs, ct);
                ProduceBlock();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during block production.");
            }
        }
    }

    private void ProduceBlock()
    {
        var parentBlock = _chainManager.LatestBlock;
        if (parentBlock == null)
        {
            _logger.LogWarning("No genesis block found. Cannot produce blocks.");
            return;
        }

        // C-4: Fork the state database before building to prevent canonical state corruption
        // if AddBlock fails (e.g., due to a concurrent consensus finalization).
        var proposalState = _stateDb.Fork();

        // M-8: Pass stateDb to GetPending for nonce-gap filtering
        var pendingTxs = _mempool.GetPending((int)_chainParams.MaxTransactionsPerBlock, proposalState);
        var block = _blockBuilder.BuildBlock(pendingTxs, proposalState, parentBlock.Header, _proposer);

        var result = _chainManager.AddBlock(block, block.Header.StateRoot);
        if (result.IsSuccess)
        {
            _mempool.RemoveConfirmed(block.Transactions);

            _logger.LogInformation(
                "Block #{Number} produced. Hash: {Hash}, Txs: {TxCount}, Gas: {GasUsed}",
                block.Number,
                block.Hash.ToHexString()[..18] + "...",
                block.Transactions.Count,
                block.Header.GasUsed);

            OnBlockProduced?.Invoke(block);
        }
        else
        {
            // C-4: Fork is discarded — canonical state is unaffected
            _logger.LogError("Failed to add block: {Error}", result.Message);
        }
    }
}
