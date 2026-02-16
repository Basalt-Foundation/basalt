using Basalt.Execution;
using Basalt.Sdk.Wallet.Rpc.Models;

namespace Basalt.Sdk.Wallet.Rpc;

/// <summary>
/// Client interface for interacting with a Basalt node via its REST API.
/// </summary>
public interface IBasaltClient : IDisposable
{
    /// <summary>
    /// Retrieves the current node status including block height, latest block hash,
    /// mempool size, and protocol version.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The current node status.</returns>
    Task<NodeStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Retrieves account information for the given address.
    /// Returns null if the account does not exist on-chain.
    /// </summary>
    /// <param name="address">The account address in "0x..." hex format.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Account information, or null if not found.</returns>
    Task<AccountInfo?> GetAccountAsync(string address, CancellationToken ct = default);

    /// <summary>
    /// Retrieves the latest finalized block.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The latest block.</returns>
    Task<BlockInfo> GetLatestBlockAsync(CancellationToken ct = default);

    /// <summary>
    /// Retrieves a block by its number or hash.
    /// Returns null if the block does not exist.
    /// </summary>
    /// <param name="blockId">Block number (as string) or block hash in "0x..." hex format.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Block information, or null if not found.</returns>
    Task<BlockInfo?> GetBlockAsync(string blockId, CancellationToken ct = default);

    /// <summary>
    /// Retrieves a transaction by its hash.
    /// Returns null if the transaction is not found within the scan depth.
    /// </summary>
    /// <param name="hash">The transaction hash in "0x..." hex format.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Transaction information, or null if not found.</returns>
    Task<TransactionInfo?> GetTransactionAsync(string hash, CancellationToken ct = default);

    /// <summary>
    /// Retrieves the most recent transactions across the latest blocks.
    /// </summary>
    /// <param name="count">Maximum number of transactions to return (1-200, default 50).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An array of recent transactions.</returns>
    Task<TransactionInfo[]> GetRecentTransactionsAsync(int count = 50, CancellationToken ct = default);

    /// <summary>
    /// Submits a signed transaction to the node's mempool.
    /// </summary>
    /// <param name="signedTx">The signed transaction to submit.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The submission result containing the transaction hash and pending status.</returns>
    Task<TransactionSubmitResult> SendTransactionAsync(Transaction signedTx, CancellationToken ct = default);

    /// <summary>
    /// Requests testnet/devnet tokens from the faucet for the given address.
    /// </summary>
    /// <param name="address">The recipient address in "0x..." hex format.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The faucet result indicating success or failure.</returns>
    Task<FaucetResult> RequestFaucetAsync(string address, CancellationToken ct = default);

    /// <summary>
    /// Retrieves the list of registered validators.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An array of validator information.</returns>
    Task<ValidatorInfo[]> GetValidatorsAsync(CancellationToken ct = default);

    /// <summary>
    /// Executes a read-only contract call without submitting a transaction.
    /// Equivalent to eth_call — executes against current state and returns the result.
    /// </summary>
    /// <param name="to">The contract address in "0x..." hex format.</param>
    /// <param name="data">The call data in "0x..." hex format.</param>
    /// <param name="from">Optional caller address in "0x..." hex format.</param>
    /// <param name="gasLimit">Gas limit for the call. Default: 1,000,000.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The call result containing return data and gas used.</returns>
    Task<CallResult> CallReadOnlyAsync(string to, string data, string? from = null, ulong gasLimit = 1_000_000, CancellationToken ct = default);
}
