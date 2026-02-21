using Basalt.Core;
using Basalt.Execution;
using Basalt.Sdk.Wallet.Accounts;
using Basalt.Sdk.Wallet.Contracts;
using Basalt.Sdk.Wallet.Rpc;
using Basalt.Sdk.Wallet.Rpc.Models;
using Basalt.Sdk.Wallet.Subscriptions;
using Basalt.Sdk.Wallet.Transactions;

namespace Basalt.Sdk.Wallet;

/// <summary>
/// High-level facade combining RPC client, nonce management, contract interaction,
/// and block subscriptions into a single developer-friendly entry point.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BasaltProvider"/> is the main entry point for interacting with a Basalt node.
/// It wraps an <see cref="IBasaltClient"/>, <see cref="NonceManager"/>, and optional
/// <see cref="IBlockSubscription"/> to provide convenience methods that auto-fill nonces,
/// sign transactions, and submit them in a single call.
/// </para>
/// <example>
/// <code>
/// using var provider = new BasaltProvider("http://localhost:5100");
/// using var wallet = HdWallet.HdWallet.FromMnemonic("...");
/// var account = wallet.GetAccount(0);
///
/// // Simple transfer
/// var hash = await provider.TransferAsync(account, recipient, amount);
///
/// // Check balance
/// var balance = await provider.GetBalanceAsync(account.Address);
/// </code>
/// </example>
/// </remarks>
public sealed class BasaltProvider : IDisposable
{
    private readonly IBasaltClient _client;
    private readonly bool _ownsClient;
    private readonly NonceManager _nonceManager;
    private readonly uint _chainId;
    private readonly string _baseUrl;

    /// <summary>
    /// Gets the underlying RPC client for advanced operations.
    /// </summary>
    public IBasaltClient Client => _client;

    /// <summary>
    /// Gets the nonce manager used by this provider.
    /// </summary>
    public NonceManager NonceManager => _nonceManager;

    /// <summary>
    /// Gets the chain ID used for transaction signing.
    /// </summary>
    public uint ChainId => _chainId;

    /// <summary>
    /// Creates a new provider connecting to the specified Basalt node URL.
    /// </summary>
    /// <param name="baseUrl">The base URL of the Basalt node REST API.</param>
    /// <param name="chainId">The chain ID for replay protection. Default: 1.</param>
    public BasaltProvider(string baseUrl, uint chainId = 1)
    {
        _client = new BasaltClient(baseUrl);
        _ownsClient = true;
        _nonceManager = new NonceManager();
        _chainId = chainId;
        _baseUrl = baseUrl;
    }

    /// <summary>
    /// Creates a new provider using an existing RPC client.
    /// </summary>
    /// <param name="client">The RPC client to use. Caller retains ownership.</param>
    /// <param name="chainId">The chain ID for replay protection. Default: 1.</param>
    public BasaltProvider(IBasaltClient client, uint chainId = 1)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _ownsClient = false;
        _nonceManager = new NonceManager();
        _chainId = chainId;
        _baseUrl = "http://localhost:5100";
    }

    // ── Query Methods ──────────────────────────────────────────────────

    /// <summary>
    /// Gets the current node status.
    /// </summary>
    public Task<NodeStatus> GetStatusAsync(CancellationToken ct = default)
        => _client.GetStatusAsync(ct);

    /// <summary>
    /// Gets the native token balance for an address.
    /// Returns "0" if the account does not exist.
    /// </summary>
    /// <param name="address">The address to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The balance as a decimal string.</returns>
    public async Task<string> GetBalanceAsync(Address address, CancellationToken ct = default)
    {
        var hex = "0x" + Convert.ToHexString(address.ToArray()).ToLowerInvariant();
        var info = await _client.GetAccountAsync(hex, ct).ConfigureAwait(false);
        return info?.Balance ?? "0";
    }

    /// <summary>
    /// Gets the current nonce for an address.
    /// Returns 0 if the account does not exist.
    /// </summary>
    /// <param name="address">The address to query.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ulong> GetNonceAsync(Address address, CancellationToken ct = default)
    {
        var hex = "0x" + Convert.ToHexString(address.ToArray()).ToLowerInvariant();
        var info = await _client.GetAccountAsync(hex, ct).ConfigureAwait(false);
        return info?.Nonce ?? 0;
    }

    /// <summary>
    /// Gets the latest finalized block.
    /// </summary>
    public Task<BlockInfo> GetLatestBlockAsync(CancellationToken ct = default)
        => _client.GetLatestBlockAsync(ct);

    /// <summary>
    /// Gets a block by number or hash.
    /// </summary>
    public Task<BlockInfo?> GetBlockAsync(string blockId, CancellationToken ct = default)
        => _client.GetBlockAsync(blockId, ct);

    /// <summary>
    /// Gets a transaction by its hash.
    /// </summary>
    public Task<TransactionInfo?> GetTransactionAsync(string hash, CancellationToken ct = default)
        => _client.GetTransactionAsync(hash, ct);

    /// <summary>
    /// Gets the list of registered validators.
    /// </summary>
    public Task<ValidatorInfo[]> GetValidatorsAsync(CancellationToken ct = default)
        => _client.GetValidatorsAsync(ct);

    /// <summary>
    /// Requests testnet tokens from the faucet.
    /// </summary>
    /// <param name="address">The recipient address.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<FaucetResult> RequestFaucetAsync(Address address, CancellationToken ct = default)
    {
        var hex = "0x" + Convert.ToHexString(address.ToArray()).ToLowerInvariant();
        return _client.RequestFaucetAsync(hex, ct);
    }

    /// <summary>
    /// Executes a read-only contract call without submitting a transaction.
    /// </summary>
    /// <param name="to">The contract address.</param>
    /// <param name="data">The call data bytes.</param>
    /// <param name="from">Optional caller address.</param>
    /// <param name="gasLimit">Gas limit for the call. Default: 1,000,000.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<CallResult> CallReadOnlyAsync(
        Address to,
        byte[] data,
        Address? from = null,
        ulong gasLimit = 1_000_000,
        CancellationToken ct = default)
    {
        var toHex = "0x" + Convert.ToHexString(to.ToArray()).ToLowerInvariant();
        var dataHex = "0x" + Convert.ToHexString(data).ToLowerInvariant();
        var fromHex = from.HasValue ? "0x" + Convert.ToHexString(from.Value.ToArray()).ToLowerInvariant() : null;
        return _client.CallReadOnlyAsync(toHex, dataHex, fromHex, gasLimit, ct);
    }

    // ── Transaction Methods ────────────────────────────────────────────

    /// <summary>
    /// Signs and submits an unsigned transaction using the specified account.
    /// Automatically fills the nonce and sender address.
    /// </summary>
    /// <param name="account">The account to sign with.</param>
    /// <param name="unsignedTx">The unsigned transaction (nonce and sender will be overwritten).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The submission result containing the transaction hash.</returns>
    public async Task<TransactionSubmitResult> SendTransactionAsync(
        IAccount account,
        Transaction unsignedTx,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(unsignedTx);

        var addressHex = "0x" + Convert.ToHexString(account.Address.ToArray()).ToLowerInvariant();
        var nonce = await _nonceManager.GetNextNonceAsync(addressHex, _client, ct).ConfigureAwait(false);

        var tx = new Transaction
        {
            Type = unsignedTx.Type,
            Nonce = nonce,
            Sender = account.Address,
            To = unsignedTx.To,
            Value = unsignedTx.Value,
            GasLimit = unsignedTx.GasLimit,
            GasPrice = unsignedTx.GasPrice,
            MaxFeePerGas = unsignedTx.MaxFeePerGas,
            MaxPriorityFeePerGas = unsignedTx.MaxPriorityFeePerGas,
            Data = unsignedTx.Data,
            Priority = unsignedTx.Priority,
            ChainId = _chainId,
        };

        var signedTx = account.SignTransaction(tx);
        try
        {
            var result = await _client.SendTransactionAsync(signedTx, ct).ConfigureAwait(false);
            _nonceManager.IncrementNonce(addressHex);
            return result;
        }
        catch
        {
            // Reset nonce cache on any submission error so the next attempt
            // re-fetches the on-chain nonce instead of staying out of sync.
            _nonceManager.Reset(addressHex);
            throw;
        }
    }

    /// <summary>
    /// Convenience method for transferring native tokens.
    /// Auto-fills nonce, signs, and submits.
    /// </summary>
    /// <param name="account">The sending account.</param>
    /// <param name="to">The recipient address.</param>
    /// <param name="value">The amount to transfer.</param>
    /// <param name="gasLimit">Gas limit. Default: 21,000.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The submission result containing the transaction hash.</returns>
    public Task<TransactionSubmitResult> TransferAsync(
        IAccount account,
        Address to,
        UInt256 value,
        ulong gasLimit = 21_000,
        CancellationToken ct = default)
    {
        var tx = TransactionBuilder.Transfer()
            .WithTo(to)
            .WithValue(value)
            .WithGasLimit(gasLimit)
            .Build();

        return SendTransactionAsync(account, tx, ct);
    }

    /// <summary>
    /// Deploys a smart contract, returning the submission result.
    /// </summary>
    /// <param name="account">The deploying account.</param>
    /// <param name="bytecode">The contract bytecode.</param>
    /// <param name="gasLimit">Gas limit. Default: 500,000.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The submission result containing the transaction hash.</returns>
    public Task<TransactionSubmitResult> DeployContractAsync(
        IAccount account,
        byte[] bytecode,
        ulong gasLimit = 500_000,
        CancellationToken ct = default)
    {
        var tx = new ContractDeployBuilder(bytecode)
            .WithGasLimit(gasLimit)
            .Build();

        return SendTransactionAsync(account, tx, ct);
    }

    /// <summary>
    /// Creates a <see cref="ContractClient"/> targeting the specified contract address
    /// using this provider's RPC client and nonce manager.
    /// </summary>
    /// <param name="contractAddress">The deployed contract address.</param>
    /// <returns>A contract client for calling methods on the contract.</returns>
    public ContractClient GetContract(Address contractAddress)
        => new(_client, _nonceManager, contractAddress, _chainId);

    /// <summary>
    /// Deploys an SDK contract using a 0xBA5A manifest.
    /// Use <see cref="SdkContractEncoder"/> to build manifests.
    /// </summary>
    /// <param name="account">The deploying account.</param>
    /// <param name="manifest">The SDK contract manifest (0xBA5A + typeId + constructor args).</param>
    /// <param name="gasLimit">Gas limit. Default: 500,000.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<TransactionSubmitResult> DeploySdkContractAsync(
        IAccount account,
        byte[] manifest,
        ulong gasLimit = 500_000,
        CancellationToken ct = default)
    {
        return DeployContractAsync(account, manifest, gasLimit, ct);
    }

    /// <summary>
    /// Well-known system contract addresses deployed at genesis.
    /// </summary>
    public static class SystemContracts
    {
        public static readonly Address WBSLT = MakeSystemAddress(0x1001);
        public static readonly Address NameService = MakeSystemAddress(0x1002);
        public static readonly Address Governance = MakeSystemAddress(0x1003);
        public static readonly Address Escrow = MakeSystemAddress(0x1004);
        public static readonly Address StakingPool = MakeSystemAddress(0x1005);

        private static Address MakeSystemAddress(ushort id)
        {
            var bytes = new byte[20];
            bytes[18] = (byte)(id >> 8);
            bytes[19] = (byte)(id & 0xFF);
            return new Address(bytes);
        }
    }

    // ── Subscription Methods ───────────────────────────────────────────

    /// <summary>
    /// Creates a new block subscription to this node's WebSocket endpoint.
    /// </summary>
    /// <param name="options">Subscription options, or null for defaults.</param>
    /// <returns>A block subscription that can be used to stream new block events.</returns>
    public IBlockSubscription SubscribeToBlocks(SubscriptionOptions? options = null)
    {
        // M-20: Use stored base URL instead of broken ToString() cast
        return new BlockSubscription(_baseUrl, options);
    }

    /// <summary>
    /// Creates a block subscription to a specific WebSocket URL.
    /// </summary>
    /// <param name="wsBaseUrl">The base URL (e.g. "http://localhost:5100").</param>
    /// <param name="options">Subscription options, or null for defaults.</param>
    /// <returns>A block subscription.</returns>
    public static IBlockSubscription CreateBlockSubscription(string wsBaseUrl, SubscriptionOptions? options = null)
        => new BlockSubscription(wsBaseUrl, options);

    // ── Disposal ───────────────────────────────────────────────────────

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsClient)
            _client.Dispose();
    }
}
