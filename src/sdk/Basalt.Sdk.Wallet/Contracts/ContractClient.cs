using Basalt.Core;
using Basalt.Execution;
using Basalt.Sdk.Wallet.Accounts;
using Basalt.Sdk.Wallet.Rpc;
using Basalt.Sdk.Wallet.Rpc.Models;
using Basalt.Sdk.Wallet.Transactions;

namespace Basalt.Sdk.Wallet.Contracts;

/// <summary>
/// High-level client for interacting with a deployed Basalt smart contract.
/// Provides methods to call contract functions and deploy new contracts.
/// </summary>
public sealed class ContractClient
{
    private readonly IBasaltClient _client;
    private readonly NonceManager _nonceManager;
    private readonly Address _contractAddress;
    private readonly uint _chainId;

    /// <summary>
    /// Gets the address of the contract this client targets.
    /// </summary>
    public Address ContractAddress => _contractAddress;

    /// <summary>
    /// Creates a new contract client targeting a specific contract address.
    /// </summary>
    /// <param name="client">The RPC client for submitting transactions.</param>
    /// <param name="nonceManager">The nonce manager for tracking account nonces.</param>
    /// <param name="contractAddress">The address of the deployed contract.</param>
    /// <param name="chainId">The chain ID for replay protection.</param>
    public ContractClient(IBasaltClient client, NonceManager nonceManager, Address contractAddress, uint chainId = 1)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(nonceManager);

        _client = client;
        _nonceManager = nonceManager;
        _contractAddress = contractAddress;
        _chainId = chainId;
    }

    /// <summary>
    /// Calls a contract method, signing and submitting the transaction.
    /// </summary>
    /// <param name="account">The account to sign the transaction with.</param>
    /// <param name="methodName">The method name to call.</param>
    /// <param name="gasLimit">Gas limit for the call.</param>
    /// <param name="value">Native tokens to send with the call.</param>
    /// <param name="args">Encoded arguments (use <see cref="AbiEncoder"/> methods).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The transaction submission result.</returns>
    public async Task<TransactionSubmitResult> CallAsync(
        IAccount account,
        string methodName,
        ulong gasLimit = 100_000,
        UInt256 value = default,
        byte[][]? args = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(methodName);

        var data = AbiEncoder.EncodeCall(methodName, args ?? []);
        var addressHex = "0x" + Convert.ToHexString(account.Address.ToArray()).ToLowerInvariant();
        var nonce = await _nonceManager.GetNextNonceAsync(addressHex, _client, ct).ConfigureAwait(false);

        var tx = TransactionBuilder.ContractCall()
            .WithNonce(nonce)
            .WithSender(account.Address)
            .WithTo(_contractAddress)
            .WithValue(value)
            .WithGasLimit(gasLimit)
            .WithGasPrice(UInt256.One)
            .WithData(data)
            .WithChainId(_chainId)
            .Build();

        var signedTx = account.SignTransaction(tx);
        var result = await _client.SendTransactionAsync(signedTx, ct).ConfigureAwait(false);
        _nonceManager.IncrementNonce(addressHex);
        return result;
    }

    /// <summary>
    /// Executes a read-only contract call without submitting a transaction.
    /// No account or signing required — the call is executed against current state.
    /// </summary>
    /// <param name="methodName">The method name to call.</param>
    /// <param name="gasLimit">Gas limit for the call.</param>
    /// <param name="args">Encoded arguments (use <see cref="AbiEncoder"/> methods).</param>
    /// <param name="from">Optional caller address in "0x..." hex format.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The call result containing return data.</returns>
    public async Task<CallResult> ReadAsync(
        string methodName,
        ulong gasLimit = 100_000,
        byte[][]? args = null,
        string? from = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(methodName);

        var data = AbiEncoder.EncodeCall(methodName, args ?? []);
        var dataHex = "0x" + Convert.ToHexString(data).ToLowerInvariant();
        var toHex = "0x" + Convert.ToHexString(_contractAddress.ToArray()).ToLowerInvariant();
        return await _client.CallReadOnlyAsync(toHex, dataHex, from, gasLimit, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Calls an SDK contract method using FNV-1a selectors and BasaltWriter encoding.
    /// </summary>
    /// <param name="account">The account to sign the transaction with.</param>
    /// <param name="methodName">The method name (PascalCase, e.g. "Transfer", "BalanceOf").</param>
    /// <param name="gasLimit">Gas limit for the call.</param>
    /// <param name="value">Native tokens to send with the call.</param>
    /// <param name="args">Encoded arguments (use <see cref="SdkContractEncoder"/> methods).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The transaction submission result.</returns>
    public async Task<TransactionSubmitResult> CallSdkAsync(
        IAccount account,
        string methodName,
        ulong gasLimit = 100_000,
        UInt256 value = default,
        byte[][]? args = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(methodName);

        var data = SdkContractEncoder.EncodeSdkCall(methodName, args ?? []);
        var addressHex = "0x" + Convert.ToHexString(account.Address.ToArray()).ToLowerInvariant();
        var nonce = await _nonceManager.GetNextNonceAsync(addressHex, _client, ct).ConfigureAwait(false);

        var tx = TransactionBuilder.ContractCall()
            .WithNonce(nonce)
            .WithSender(account.Address)
            .WithTo(_contractAddress)
            .WithValue(value)
            .WithGasLimit(gasLimit)
            .WithGasPrice(UInt256.One)
            .WithData(data)
            .WithChainId(_chainId)
            .Build();

        var signedTx = account.SignTransaction(tx);
        var result = await _client.SendTransactionAsync(signedTx, ct).ConfigureAwait(false);
        _nonceManager.IncrementNonce(addressHex);
        return result;
    }

    /// <summary>
    /// Executes a read-only SDK contract call using FNV-1a selectors.
    /// No account or signing required.
    /// </summary>
    /// <param name="methodName">The method name (PascalCase, e.g. "Name", "BalanceOf").</param>
    /// <param name="gasLimit">Gas limit for the call.</param>
    /// <param name="args">Encoded arguments (use <see cref="SdkContractEncoder"/> methods).</param>
    /// <param name="from">Optional caller address in "0x..." hex format.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The call result containing return data.</returns>
    public async Task<CallResult> ReadSdkAsync(
        string methodName,
        ulong gasLimit = 100_000,
        byte[][]? args = null,
        string? from = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(methodName);

        var data = SdkContractEncoder.EncodeSdkCall(methodName, args ?? []);
        var dataHex = "0x" + Convert.ToHexString(data).ToLowerInvariant();
        var toHex = "0x" + Convert.ToHexString(_contractAddress.ToArray()).ToLowerInvariant();
        return await _client.CallReadOnlyAsync(toHex, dataHex, from, gasLimit, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deploys a new contract, returning the submission result.
    /// </summary>
    /// <param name="account">The account to deploy from.</param>
    /// <param name="bytecode">The contract bytecode.</param>
    /// <param name="client">The RPC client.</param>
    /// <param name="nonceManager">The nonce manager.</param>
    /// <param name="gasLimit">Gas limit for the deployment.</param>
    /// <param name="chainId">The chain ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The transaction submission result.</returns>
    public static async Task<TransactionSubmitResult> DeployAsync(
        IAccount account,
        byte[] bytecode,
        IBasaltClient client,
        NonceManager nonceManager,
        ulong gasLimit = 500_000,
        uint chainId = 1,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(bytecode);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(nonceManager);

        var addressHex = "0x" + Convert.ToHexString(account.Address.ToArray()).ToLowerInvariant();
        var nonce = await nonceManager.GetNextNonceAsync(addressHex, client, ct).ConfigureAwait(false);

        var tx = new ContractDeployBuilder(bytecode)
            .WithNonce(nonce)
            .WithSender(account.Address)
            .WithGasLimit(gasLimit)
            .WithChainId(chainId)
            .Build();

        var signedTx = account.SignTransaction(tx);
        var result = await client.SendTransactionAsync(signedTx, ct).ConfigureAwait(false);
        nonceManager.IncrementNonce(addressHex);
        return result;
    }
}
