using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution.VM;
using Basalt.Storage;

namespace Basalt.Execution;

/// <summary>
/// Executes transactions and applies state changes.
/// Supports Transfer (Type=0), ContractDeploy (Type=1), ContractCall (Type=2),
/// StakeDeposit (Type=3), StakeWithdraw (Type=4), ValidatorRegister (Type=5),
/// and ValidatorExit (Type=6).
/// </summary>
public sealed class TransactionExecutor
{
    private readonly ChainParameters _chainParams;
    private readonly IContractRuntime _contractRuntime;
    private readonly IStakingState? _stakingState;

    public TransactionExecutor(ChainParameters chainParams)
        : this(chainParams, new ManagedContractRuntime(), null) { }

    public TransactionExecutor(ChainParameters chainParams, IContractRuntime contractRuntime)
        : this(chainParams, contractRuntime, null) { }

    public TransactionExecutor(ChainParameters chainParams, IContractRuntime contractRuntime, IStakingState? stakingState)
    {
        _chainParams = chainParams;
        _contractRuntime = contractRuntime;
        _stakingState = stakingState;
    }

    /// <summary>
    /// Execute a single transaction against the state database.
    /// </summary>
    public TransactionReceipt Execute(Transaction tx, IStateDatabase stateDb, BlockHeader blockHeader, int txIndex)
    {
        return tx.Type switch
        {
            TransactionType.Transfer => ExecuteTransfer(tx, stateDb, blockHeader, txIndex),
            TransactionType.ContractDeploy => ExecuteContractDeploy(tx, stateDb, blockHeader, txIndex),
            TransactionType.ContractCall => ExecuteContractCall(tx, stateDb, blockHeader, txIndex),
            TransactionType.StakeDeposit => ExecuteStakeDeposit(tx, stateDb, blockHeader, txIndex),
            TransactionType.StakeWithdraw => ExecuteStakeWithdraw(tx, stateDb, blockHeader, txIndex),
            TransactionType.ValidatorRegister => ExecuteValidatorRegister(tx, stateDb, blockHeader, txIndex),
            TransactionType.ValidatorExit => ExecuteValidatorExit(tx, stateDb, blockHeader, txIndex),
            _ => ExecuteStub(tx, stateDb, blockHeader, txIndex, BasaltErrorCode.InvalidTransactionType),
        };
    }

    private TransactionReceipt ExecuteTransfer(Transaction tx, IStateDatabase stateDb, BlockHeader blockHeader, int txIndex)
    {
        var gasUsed = _chainParams.TransferGasCost;
        var gasFee = tx.GasPrice * new UInt256(gasUsed);

        // Debit sender: value + gas fee
        var senderState = stateDb.GetAccount(tx.Sender) ?? AccountState.Empty;
        var totalDebit = tx.Value + gasFee;

        if (senderState.Balance < totalDebit)
        {
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.InsufficientBalance, stateDb);
        }

        senderState = senderState with
        {
            Balance = senderState.Balance - totalDebit,
            Nonce = senderState.Nonce + 1,
        };
        stateDb.SetAccount(tx.Sender, senderState);

        // Credit recipient
        var recipientState = stateDb.GetAccount(tx.To) ?? AccountState.Empty;
        recipientState = recipientState with
        {
            Balance = recipientState.Balance + tx.Value,
        };
        stateDb.SetAccount(tx.To, recipientState);

        return CreateReceipt(tx, blockHeader, txIndex, gasUsed, true, BasaltErrorCode.Success, stateDb);
    }

    private TransactionReceipt ExecuteContractDeploy(Transaction tx, IStateDatabase stateDb, BlockHeader blockHeader, int txIndex)
    {
        var gasMeter = new GasMeter(tx.GasLimit);

        try
        {
            // Base gas for deployment
            gasMeter.Consume(GasTable.TxBase);
            gasMeter.Consume(GasTable.ComputeDataGas(tx.Data));

            // Derive contract address from sender + nonce
            var contractAddress = DeriveContractAddress(tx.Sender, tx.Nonce);

            // Debit sender upfront (gas limit * gas price + value)
            var maxGasFee = tx.GasPrice * new UInt256(tx.GasLimit);
            var senderState = stateDb.GetAccount(tx.Sender) ?? AccountState.Empty;
            senderState = senderState with
            {
                Balance = senderState.Balance - maxGasFee - tx.Value,
                Nonce = senderState.Nonce + 1,
            };
            stateDb.SetAccount(tx.Sender, senderState);

            // Create contract account
            var codeHash = Blake3Hasher.Hash(tx.Data);
            var contractState = new AccountState
            {
                Nonce = 0,
                Balance = tx.Value,
                StorageRoot = Hash256.Zero,
                CodeHash = codeHash,
                AccountType = AccountType.Contract,
                ComplianceHash = Hash256.Zero,
            };
            stateDb.SetAccount(contractAddress, contractState);

            // Execute deployment
            var ctx = new VmExecutionContext
            {
                Caller = tx.Sender,
                ContractAddress = contractAddress,
                Value = tx.Value,
                BlockTimestamp = (ulong)blockHeader.Timestamp,
                BlockNumber = blockHeader.Number,
                BlockProposer = blockHeader.Proposer,
                ChainId = blockHeader.ChainId,
                GasMeter = gasMeter,
                StateDb = stateDb,
                CallDepth = 0,
            };

            var result = _contractRuntime.Deploy(tx.Data, [], ctx);

            if (!result.Success)
            {
                // Revert: remove contract, restore sender balance
                stateDb.DeleteAccount(contractAddress);
                senderState = stateDb.GetAccount(tx.Sender)!.Value;
                senderState = senderState with { Balance = senderState.Balance + tx.Value };
                stateDb.SetAccount(tx.Sender, senderState);
            }

            // Refund unused gas
            var effectiveGas = gasMeter.EffectiveGasUsed();
            var actualFee = tx.GasPrice * new UInt256(effectiveGas);
            var refund = maxGasFee - actualFee;
            if (refund > UInt256.Zero)
            {
                senderState = stateDb.GetAccount(tx.Sender)!.Value;
                senderState = senderState with { Balance = senderState.Balance + refund };
                stateDb.SetAccount(tx.Sender, senderState);
            }

            var receipt = new TransactionReceipt
            {
                TransactionHash = tx.Hash,
                BlockHash = blockHeader.Hash,
                BlockNumber = blockHeader.Number,
                TransactionIndex = txIndex,
                From = tx.Sender,
                To = contractAddress,
                GasUsed = effectiveGas,
                Success = result.Success,
                ErrorCode = result.Success ? BasaltErrorCode.Success : BasaltErrorCode.ContractDeployFailed,
                PostStateRoot = stateDb.ComputeStateRoot(),
                Logs = result.Logs,
            };

            return receipt;
        }
        catch (OutOfGasException)
        {
            return CreateReceipt(tx, blockHeader, txIndex, tx.GasLimit, false, BasaltErrorCode.OutOfGas, stateDb);
        }
    }

    private TransactionReceipt ExecuteContractCall(Transaction tx, IStateDatabase stateDb, BlockHeader blockHeader, int txIndex)
    {
        var gasMeter = new GasMeter(tx.GasLimit);

        try
        {
            // Base gas
            gasMeter.Consume(GasTable.TxBase);
            gasMeter.Consume(GasTable.ComputeDataGas(tx.Data));

            // Check contract exists
            var contractState = stateDb.GetAccount(tx.To);
            if (contractState == null || contractState.Value.AccountType is not (AccountType.Contract or AccountType.SystemContract))
            {
                return CreateReceipt(tx, blockHeader, txIndex, gasMeter.GasUsed, false,
                    BasaltErrorCode.ContractNotFound, stateDb);
            }

            // Debit sender (gas limit * gas price + value)
            var maxGasFee = tx.GasPrice * new UInt256(tx.GasLimit);
            var senderState = stateDb.GetAccount(tx.Sender) ?? AccountState.Empty;
            senderState = senderState with
            {
                Balance = senderState.Balance - maxGasFee - tx.Value,
                Nonce = senderState.Nonce + 1,
            };
            stateDb.SetAccount(tx.Sender, senderState);

            // Transfer value to contract
            if (tx.Value > UInt256.Zero)
            {
                var cs = contractState.Value;
                cs = cs with { Balance = cs.Balance + tx.Value };
                stateDb.SetAccount(tx.To, cs);
            }

            // Execute call
            var ctx = new VmExecutionContext
            {
                Caller = tx.Sender,
                ContractAddress = tx.To,
                Value = tx.Value,
                BlockTimestamp = (ulong)blockHeader.Timestamp,
                BlockNumber = blockHeader.Number,
                BlockProposer = blockHeader.Proposer,
                ChainId = blockHeader.ChainId,
                GasMeter = gasMeter,
                StateDb = stateDb,
                CallDepth = 0,
            };

            // Load contract code from storage (stored under 0xFF01... key during deploy)
            var codeStorageKey = GetCodeStorageKey();
            var code = stateDb.GetStorage(tx.To, codeStorageKey) ?? [];

            var result = _contractRuntime.Execute(code, tx.Data, ctx);

            // Refund unused gas
            var effectiveGas = gasMeter.EffectiveGasUsed();
            var actualFee = tx.GasPrice * new UInt256(effectiveGas);
            var refund = maxGasFee - actualFee;
            if (refund > UInt256.Zero)
            {
                senderState = stateDb.GetAccount(tx.Sender)!.Value;
                senderState = senderState with { Balance = senderState.Balance + refund };
                stateDb.SetAccount(tx.Sender, senderState);
            }

            return new TransactionReceipt
            {
                TransactionHash = tx.Hash,
                BlockHash = blockHeader.Hash,
                BlockNumber = blockHeader.Number,
                TransactionIndex = txIndex,
                From = tx.Sender,
                To = tx.To,
                GasUsed = effectiveGas,
                Success = result.Success,
                ErrorCode = result.Success ? BasaltErrorCode.Success : BasaltErrorCode.ContractCallFailed,
                PostStateRoot = stateDb.ComputeStateRoot(),
                Logs = result.Logs,
            };
        }
        catch (OutOfGasException)
        {
            return CreateReceipt(tx, blockHeader, txIndex, tx.GasLimit, false, BasaltErrorCode.OutOfGas, stateDb);
        }
    }

    private TransactionReceipt ExecuteValidatorRegister(Transaction tx, IStateDatabase stateDb, BlockHeader blockHeader, int txIndex)
    {
        if (_stakingState == null)
            return CreateReceipt(tx, blockHeader, txIndex, _chainParams.TransferGasCost, false, BasaltErrorCode.StakingNotAvailable, stateDb);

        var gasUsed = _chainParams.TransferGasCost;
        var gasFee = tx.GasPrice * new UInt256(gasUsed);
        var totalDebit = tx.Value + gasFee;

        var senderState = stateDb.GetAccount(tx.Sender) ?? AccountState.Empty;
        if (senderState.Balance < totalDebit)
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.InsufficientBalance, stateDb);

        if (tx.Value < _stakingState.MinValidatorStake)
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.StakeBelowMinimum, stateDb);

        // Parse optional P2P endpoint from tx.Data (UTF-8 encoded)
        string? p2pEndpoint = tx.Data.Length > 0 ? System.Text.Encoding.UTF8.GetString(tx.Data) : null;

        var result = _stakingState.RegisterValidator(tx.Sender, tx.Value, blockHeader.Number, p2pEndpoint);
        if (!result.IsSuccess)
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.ValidatorAlreadyRegistered, stateDb);

        // Debit sender
        senderState = senderState with
        {
            Balance = senderState.Balance - totalDebit,
            Nonce = senderState.Nonce + 1,
        };
        stateDb.SetAccount(tx.Sender, senderState);

        return CreateReceipt(tx, blockHeader, txIndex, gasUsed, true, BasaltErrorCode.Success, stateDb);
    }

    private TransactionReceipt ExecuteValidatorExit(Transaction tx, IStateDatabase stateDb, BlockHeader blockHeader, int txIndex)
    {
        if (_stakingState == null)
            return CreateReceipt(tx, blockHeader, txIndex, _chainParams.TransferGasCost, false, BasaltErrorCode.StakingNotAvailable, stateDb);

        var gasUsed = _chainParams.TransferGasCost;
        var gasFee = tx.GasPrice * new UInt256(gasUsed);

        var senderState = stateDb.GetAccount(tx.Sender) ?? AccountState.Empty;
        if (senderState.Balance < gasFee)
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.InsufficientBalance, stateDb);

        var selfStake = _stakingState.GetSelfStake(tx.Sender);
        if (selfStake == null)
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.ValidatorNotRegistered, stateDb);

        // Unstake the full self-stake — triggers unbonding period
        var result = _stakingState.InitiateUnstake(tx.Sender, selfStake.Value, blockHeader.Number);
        if (!result.IsSuccess)
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.ValidatorNotRegistered, stateDb);

        // Debit gas fee
        senderState = senderState with
        {
            Balance = senderState.Balance - gasFee,
            Nonce = senderState.Nonce + 1,
        };
        stateDb.SetAccount(tx.Sender, senderState);

        return CreateReceipt(tx, blockHeader, txIndex, gasUsed, true, BasaltErrorCode.Success, stateDb);
    }

    private TransactionReceipt ExecuteStakeDeposit(Transaction tx, IStateDatabase stateDb, BlockHeader blockHeader, int txIndex)
    {
        if (_stakingState == null)
            return CreateReceipt(tx, blockHeader, txIndex, _chainParams.TransferGasCost, false, BasaltErrorCode.StakingNotAvailable, stateDb);

        var gasUsed = _chainParams.TransferGasCost;
        var gasFee = tx.GasPrice * new UInt256(gasUsed);
        var totalDebit = tx.Value + gasFee;

        var senderState = stateDb.GetAccount(tx.Sender) ?? AccountState.Empty;
        if (senderState.Balance < totalDebit)
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.InsufficientBalance, stateDb);

        var result = _stakingState.AddStake(tx.Sender, tx.Value);
        if (!result.IsSuccess)
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.ValidatorNotRegistered, stateDb);

        // Debit sender
        senderState = senderState with
        {
            Balance = senderState.Balance - totalDebit,
            Nonce = senderState.Nonce + 1,
        };
        stateDb.SetAccount(tx.Sender, senderState);

        return CreateReceipt(tx, blockHeader, txIndex, gasUsed, true, BasaltErrorCode.Success, stateDb);
    }

    private TransactionReceipt ExecuteStakeWithdraw(Transaction tx, IStateDatabase stateDb, BlockHeader blockHeader, int txIndex)
    {
        if (_stakingState == null)
            return CreateReceipt(tx, blockHeader, txIndex, _chainParams.TransferGasCost, false, BasaltErrorCode.StakingNotAvailable, stateDb);

        var gasUsed = _chainParams.TransferGasCost;
        var gasFee = tx.GasPrice * new UInt256(gasUsed);

        var senderState = stateDb.GetAccount(tx.Sender) ?? AccountState.Empty;
        if (senderState.Balance < gasFee)
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.InsufficientBalance, stateDb);

        var result = _stakingState.InitiateUnstake(tx.Sender, tx.Value, blockHeader.Number);
        if (!result.IsSuccess)
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.ValidatorNotRegistered, stateDb);

        // Debit gas fee only (staked funds enter unbonding queue)
        senderState = senderState with
        {
            Balance = senderState.Balance - gasFee,
            Nonce = senderState.Nonce + 1,
        };
        stateDb.SetAccount(tx.Sender, senderState);

        return CreateReceipt(tx, blockHeader, txIndex, gasUsed, true, BasaltErrorCode.Success, stateDb);
    }

    /// <summary>
    /// Derive a contract address from the deployer's address and nonce.
    /// Uses BLAKE3(sender || nonce), taking the last 20 bytes.
    /// </summary>
    private static Address DeriveContractAddress(Address sender, ulong nonce)
    {
        Span<byte> input = stackalloc byte[Address.Size + 8];
        sender.WriteTo(input[..Address.Size]);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(input[Address.Size..], nonce);
        var hash = Blake3Hasher.Hash(input);
        Span<byte> hashBytes = stackalloc byte[Hash256.Size];
        hash.WriteTo(hashBytes);
        return new Address(hashBytes[12..]);
    }

    private static Hash256 GetCodeStorageKey()
    {
        Span<byte> key = stackalloc byte[32];
        key.Clear();
        key[0] = 0xFF;
        key[1] = 0x01;
        return new Hash256(key);
    }

    private static TransactionReceipt ExecuteStub(Transaction tx, IStateDatabase stateDb, BlockHeader blockHeader, int txIndex, BasaltErrorCode errorCode)
    {
        return CreateReceipt(tx, blockHeader, txIndex, 0, false, errorCode, stateDb);
    }

    private static TransactionReceipt CreateReceipt(Transaction tx, BlockHeader blockHeader, int txIndex,
        ulong gasUsed, bool success, BasaltErrorCode errorCode, IStateDatabase stateDb)
    {
        return new TransactionReceipt
        {
            TransactionHash = tx.Hash,
            BlockHash = blockHeader.Hash,
            BlockNumber = blockHeader.Number,
            TransactionIndex = txIndex,
            From = tx.Sender,
            To = tx.To,
            GasUsed = gasUsed,
            Success = success,
            ErrorCode = errorCode,
            PostStateRoot = stateDb.ComputeStateRoot(),
        };
    }
}
