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
    private readonly IComplianceVerifier? _complianceVerifier;

    public TransactionExecutor(ChainParameters chainParams)
        : this(chainParams, new ManagedContractRuntime(), null, null) { }

    public TransactionExecutor(ChainParameters chainParams, IContractRuntime contractRuntime)
        : this(chainParams, contractRuntime, null, null) { }

    public TransactionExecutor(ChainParameters chainParams, IContractRuntime contractRuntime, IStakingState? stakingState)
        : this(chainParams, contractRuntime, stakingState, null) { }

    public TransactionExecutor(ChainParameters chainParams, IContractRuntime contractRuntime, IStakingState? stakingState, IComplianceVerifier? complianceVerifier)
    {
        _chainParams = chainParams;
        _contractRuntime = contractRuntime;
        _stakingState = stakingState;
        _complianceVerifier = complianceVerifier;
    }

    /// <summary>
    /// Execute a single transaction against the state database.
    /// </summary>
    public TransactionReceipt Execute(Transaction tx, IStateDatabase stateDb, BlockHeader blockHeader, int txIndex)
    {
        // COMPL-06: Compliance check before tx type dispatch.
        // M-12: Skip compliance for staking transaction types (To is not a meaningful recipient)
        if (_complianceVerifier != null && tx.Type is TransactionType.Transfer or TransactionType.ContractDeploy or TransactionType.ContractCall)
        {
            // COMPL-01: Look up actual policy requirements for the target address
            var requirements = _complianceVerifier.GetRequirements(tx.To.ToArray());

            // COMPL-14: Verify ZK proofs if requirements exist or proofs are attached
            if (requirements.Length > 0 || tx.ComplianceProofs.Length > 0)
            {
                var outcome = _complianceVerifier.VerifyProofs(tx.ComplianceProofs, requirements, blockHeader.Timestamp);
                if (!outcome.Allowed)
                    return CreateReceipt(tx, blockHeader, txIndex, 0, false, outcome.ErrorCode, stateDb);
            }
        }

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
        var effectiveGasPrice = tx.EffectiveGasPrice(blockHeader.BaseFee);
        var gasFee = effectiveGasPrice * new UInt256(gasUsed);

        // C-2: Always charge gas + increment nonce, even on failure
        var senderState = stateDb.GetAccount(tx.Sender) ?? AccountState.Empty;
        var totalDebit = UInt256.CheckedAdd(tx.Value, gasFee);

        if (senderState.Balance < totalDebit)
        {
            // C-2: Still charge gas fee and increment nonce on failure
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, gasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.InsufficientBalance, stateDb, effectiveGasPrice);
        }

        senderState = senderState with
        {
            Balance = UInt256.CheckedSub(senderState.Balance, totalDebit),
            Nonce = IncrementNonce(senderState.Nonce),
        };
        stateDb.SetAccount(tx.Sender, senderState);

        // Credit recipient (H-4: use checked arithmetic)
        var recipientState = stateDb.GetAccount(tx.To) ?? AccountState.Empty;
        recipientState = recipientState with
        {
            Balance = UInt256.CheckedAdd(recipientState.Balance, tx.Value),
        };
        stateDb.SetAccount(tx.To, recipientState);

        // Credit proposer tip (effectiveGasPrice - baseFee) * gasUsed; base fee is burned
        CreditProposerTip(stateDb, blockHeader, effectiveGasPrice, gasUsed);

        return CreateReceipt(tx, blockHeader, txIndex, gasUsed, true, BasaltErrorCode.Success, stateDb, effectiveGasPrice);
    }

    private TransactionReceipt ExecuteContractDeploy(Transaction tx, IStateDatabase stateDb, BlockHeader blockHeader, int txIndex)
    {
        var gasMeter = new GasMeter(tx.GasLimit);
        var effectiveGasPrice = tx.EffectiveGasPrice(blockHeader.BaseFee);
        var maxGasFee = tx.EffectiveMaxFee * new UInt256(tx.GasLimit);

        // C-2: Pre-check balance — if can't even cover gas, charge what we can + increment nonce
        var senderState = stateDb.GetAccount(tx.Sender) ?? AccountState.Empty;
        var totalDebit = UInt256.CheckedAdd(maxGasFee, tx.Value);

        if (senderState.Balance < totalDebit)
        {
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, maxGasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasMeter.GasUsed, false,
                BasaltErrorCode.InsufficientBalance, stateDb, effectiveGasPrice);
        }

        // C-1: Fork state before contract execution for atomicity.
        // Nonce + gas debit on the canonical state, contract execution on the fork.
        senderState = senderState with
        {
            Balance = UInt256.CheckedSub(senderState.Balance, totalDebit),
            Nonce = IncrementNonce(senderState.Nonce),
        };
        stateDb.SetAccount(tx.Sender, senderState);

        // C-1: Fork — all contract mutations go to the fork
        var fork = stateDb.Fork();
        var contractAddress = DeriveContractAddress(tx.Sender, tx.Nonce);

        // L-9: Reject deployments that would collide with system contract addresses
        if (IsSystemAddress(contractAddress))
        {
            return CreateReceipt(tx, blockHeader, txIndex, GasTable.TxBase, false,
                BasaltErrorCode.ContractDeployFailed, stateDb, effectiveGasPrice);
        }

        try
        {
            // Base gas for deployment
            gasMeter.Consume(GasTable.TxBase);
            gasMeter.Consume(GasTable.ComputeDataGas(tx.Data));

            // Create contract account on the fork
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
            fork.SetAccount(contractAddress, contractState);

            // Execute deployment on the fork
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
                StateDb = fork,
                CallDepth = 0,
            };

            var result = _contractRuntime.Deploy(tx.Data, [], ctx);
            var effectiveGas = gasMeter.EffectiveGasUsed();

            if (result.Success)
            {
                // C-1: Merge fork into canonical state (contract account + storage mutations)
                MergeForkState(fork, stateDb, contractAddress);
            }

            // Refund unused gas to sender on canonical state
            var actualFee = effectiveGasPrice * new UInt256(effectiveGas);
            var refund = maxGasFee - actualFee;
            if (refund > UInt256.Zero)
            {
                senderState = stateDb.GetAccount(tx.Sender)!.Value;
                senderState = senderState with { Balance = UInt256.CheckedAdd(senderState.Balance, refund) };
                stateDb.SetAccount(tx.Sender, senderState);
            }

            // Credit proposer with priority tip
            CreditProposerTip(stateDb, blockHeader, effectiveGasPrice, effectiveGas);

            return new TransactionReceipt
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
                EffectiveGasPrice = effectiveGasPrice,
            };
        }
        catch (OutOfGasException)
        {
            // C-1: Fork is discarded — no storage mutations leak to canonical state.
            // C-2: Nonce already incremented, max gas fee already debited.
            // Refund (maxGasFee - fullGasCharge) where fullGasCharge = effectiveGasPrice * gasLimit
            var fullGasCharge = effectiveGasPrice * new UInt256(tx.GasLimit);
            var oogRefund = maxGasFee - fullGasCharge;
            if (oogRefund > UInt256.Zero)
            {
                senderState = stateDb.GetAccount(tx.Sender)!.Value;
                senderState = senderState with { Balance = UInt256.CheckedAdd(senderState.Balance, oogRefund) };
                stateDb.SetAccount(tx.Sender, senderState);
            }
            CreditProposerTip(stateDb, blockHeader, effectiveGasPrice, tx.GasLimit);
            return CreateReceipt(tx, blockHeader, txIndex, tx.GasLimit, false, BasaltErrorCode.OutOfGas, stateDb, effectiveGasPrice);
        }
    }

    private TransactionReceipt ExecuteContractCall(Transaction tx, IStateDatabase stateDb, BlockHeader blockHeader, int txIndex)
    {
        var gasMeter = new GasMeter(tx.GasLimit);
        var effectiveGasPrice = tx.EffectiveGasPrice(blockHeader.BaseFee);
        var maxGasFee = tx.EffectiveMaxFee * new UInt256(tx.GasLimit);

        // Check contract exists before charging
        var contractState = stateDb.GetAccount(tx.To);
        if (contractState == null || contractState.Value.AccountType is not (AccountType.Contract or AccountType.SystemContract))
        {
            // C-2: Still charge gas + increment nonce
            var sState = stateDb.GetAccount(tx.Sender) ?? AccountState.Empty;
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, sState, maxGasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasMeter.GasUsed, false,
                BasaltErrorCode.ContractNotFound, stateDb, effectiveGasPrice);
        }

        // C-2: Pre-check balance
        var senderState = stateDb.GetAccount(tx.Sender) ?? AccountState.Empty;
        var totalDebit = UInt256.CheckedAdd(maxGasFee, tx.Value);

        if (senderState.Balance < totalDebit)
        {
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, maxGasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasMeter.GasUsed, false,
                BasaltErrorCode.InsufficientBalance, stateDb, effectiveGasPrice);
        }

        // C-1 + C-2: Debit sender on canonical state (nonce + maxGasFee + value)
        senderState = senderState with
        {
            Balance = UInt256.CheckedSub(senderState.Balance, totalDebit),
            Nonce = IncrementNonce(senderState.Nonce),
        };
        stateDb.SetAccount(tx.Sender, senderState);

        // C-1: Fork state — contract execution happens on the fork
        var fork = stateDb.Fork();

        // Transfer value to contract on the fork
        if (tx.Value > UInt256.Zero)
        {
            var cs = fork.GetAccount(tx.To) ?? contractState.Value;
            cs = cs with { Balance = UInt256.CheckedAdd(cs.Balance, tx.Value) };
            fork.SetAccount(tx.To, cs);
        }

        try
        {
            // Base gas
            gasMeter.Consume(GasTable.TxBase);
            gasMeter.Consume(GasTable.ComputeDataGas(tx.Data));

            // Execute call on the fork
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
                StateDb = fork,
                CallDepth = 0,
            };

            // Load contract code from storage
            var codeStorageKey = GetCodeStorageKey();
            var code = stateDb.GetStorage(tx.To, codeStorageKey) ?? [];

            var result = _contractRuntime.Execute(code, tx.Data, ctx);
            var effectiveGas = gasMeter.EffectiveGasUsed();

            if (result.Success)
            {
                // C-1: Merge fork into canonical state (contract account + storage mutations)
                MergeForkState(fork, stateDb, tx.To);
            }

            // Refund unused gas to sender on canonical state
            var actualFee = effectiveGasPrice * new UInt256(effectiveGas);
            var refund = maxGasFee - actualFee;
            if (refund > UInt256.Zero)
            {
                senderState = stateDb.GetAccount(tx.Sender)!.Value;
                senderState = senderState with { Balance = UInt256.CheckedAdd(senderState.Balance, refund) };
                stateDb.SetAccount(tx.Sender, senderState);
            }

            // Credit proposer with priority tip
            CreditProposerTip(stateDb, blockHeader, effectiveGasPrice, effectiveGas);

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
                EffectiveGasPrice = effectiveGasPrice,
            };
        }
        catch (OutOfGasException)
        {
            // C-1: Fork is discarded — no storage mutations leak.
            // C-2: Nonce incremented, max gas fee debited already.
            var fullGasCharge = effectiveGasPrice * new UInt256(tx.GasLimit);
            var oogRefund = maxGasFee - fullGasCharge;
            if (oogRefund > UInt256.Zero)
            {
                senderState = stateDb.GetAccount(tx.Sender)!.Value;
                senderState = senderState with { Balance = UInt256.CheckedAdd(senderState.Balance, oogRefund) };
                stateDb.SetAccount(tx.Sender, senderState);
            }
            CreditProposerTip(stateDb, blockHeader, effectiveGasPrice, tx.GasLimit);
            return CreateReceipt(tx, blockHeader, txIndex, tx.GasLimit, false, BasaltErrorCode.OutOfGas, stateDb, effectiveGasPrice);
        }
    }

    private TransactionReceipt ExecuteValidatorRegister(Transaction tx, IStateDatabase stateDb, BlockHeader blockHeader, int txIndex)
    {
        var gasUsed = _chainParams.TransferGasCost;
        var effectiveGasPrice = tx.EffectiveGasPrice(blockHeader.BaseFee);
        var gasFee = effectiveGasPrice * new UInt256(gasUsed);

        // C-2: Always charge gas + increment nonce on failure
        var senderState = stateDb.GetAccount(tx.Sender) ?? AccountState.Empty;

        if (_stakingState == null)
        {
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, gasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.StakingNotAvailable, stateDb, effectiveGasPrice);
        }

        var totalDebit = UInt256.CheckedAdd(tx.Value, gasFee);

        if (senderState.Balance < totalDebit)
        {
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, gasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.InsufficientBalance, stateDb, effectiveGasPrice);
        }

        if (tx.Value < _stakingState.MinValidatorStake)
        {
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, gasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.StakeBelowMinimum, stateDb, effectiveGasPrice);
        }

        // Parse optional P2P endpoint from tx.Data (UTF-8 encoded)
        string? p2pEndpoint = tx.Data.Length > 0 ? System.Text.Encoding.UTF8.GetString(tx.Data) : null;

        var result = _stakingState.RegisterValidator(tx.Sender, tx.Value, blockHeader.Number, p2pEndpoint);
        if (!result.IsSuccess)
        {
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, gasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.ValidatorAlreadyRegistered, stateDb, effectiveGasPrice);
        }

        // Debit sender: value + gas
        senderState = senderState with
        {
            Balance = UInt256.CheckedSub(senderState.Balance, totalDebit),
            Nonce = IncrementNonce(senderState.Nonce),
        };
        stateDb.SetAccount(tx.Sender, senderState);

        CreditProposerTip(stateDb, blockHeader, effectiveGasPrice, gasUsed);

        return CreateReceipt(tx, blockHeader, txIndex, gasUsed, true, BasaltErrorCode.Success, stateDb, effectiveGasPrice);
    }

    private TransactionReceipt ExecuteValidatorExit(Transaction tx, IStateDatabase stateDb, BlockHeader blockHeader, int txIndex)
    {
        var gasUsed = _chainParams.TransferGasCost;
        var effectiveGasPrice = tx.EffectiveGasPrice(blockHeader.BaseFee);
        var gasFee = effectiveGasPrice * new UInt256(gasUsed);

        // C-2: Always charge gas + increment nonce on failure
        var senderState = stateDb.GetAccount(tx.Sender) ?? AccountState.Empty;

        if (_stakingState == null)
        {
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, gasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.StakingNotAvailable, stateDb, effectiveGasPrice);
        }

        if (senderState.Balance < gasFee)
        {
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, gasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.InsufficientBalance, stateDb, effectiveGasPrice);
        }

        var selfStake = _stakingState.GetSelfStake(tx.Sender);
        if (selfStake == null)
        {
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, gasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.ValidatorNotRegistered, stateDb, effectiveGasPrice);
        }

        // Unstake the full self-stake — triggers unbonding period
        var result = _stakingState.InitiateUnstake(tx.Sender, selfStake.Value, blockHeader.Number);
        if (!result.IsSuccess)
        {
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, gasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.ValidatorNotRegistered, stateDb, effectiveGasPrice);
        }

        // Debit gas fee
        senderState = senderState with
        {
            Balance = UInt256.CheckedSub(senderState.Balance, gasFee),
            Nonce = IncrementNonce(senderState.Nonce),
        };
        stateDb.SetAccount(tx.Sender, senderState);

        CreditProposerTip(stateDb, blockHeader, effectiveGasPrice, gasUsed);

        return CreateReceipt(tx, blockHeader, txIndex, gasUsed, true, BasaltErrorCode.Success, stateDb, effectiveGasPrice);
    }

    private TransactionReceipt ExecuteStakeDeposit(Transaction tx, IStateDatabase stateDb, BlockHeader blockHeader, int txIndex)
    {
        var gasUsed = _chainParams.TransferGasCost;
        var effectiveGasPrice = tx.EffectiveGasPrice(blockHeader.BaseFee);
        var gasFee = effectiveGasPrice * new UInt256(gasUsed);

        // C-2: Always charge gas + increment nonce on failure
        var senderState = stateDb.GetAccount(tx.Sender) ?? AccountState.Empty;

        if (_stakingState == null)
        {
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, gasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.StakingNotAvailable, stateDb, effectiveGasPrice);
        }

        var totalDebit = UInt256.CheckedAdd(tx.Value, gasFee);

        if (senderState.Balance < totalDebit)
        {
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, gasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.InsufficientBalance, stateDb, effectiveGasPrice);
        }

        var result = _stakingState.AddStake(tx.Sender, tx.Value);
        if (!result.IsSuccess)
        {
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, gasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.ValidatorNotRegistered, stateDb, effectiveGasPrice);
        }

        // Debit sender: value + gas
        senderState = senderState with
        {
            Balance = UInt256.CheckedSub(senderState.Balance, totalDebit),
            Nonce = IncrementNonce(senderState.Nonce),
        };
        stateDb.SetAccount(tx.Sender, senderState);

        CreditProposerTip(stateDb, blockHeader, effectiveGasPrice, gasUsed);

        return CreateReceipt(tx, blockHeader, txIndex, gasUsed, true, BasaltErrorCode.Success, stateDb, effectiveGasPrice);
    }

    private TransactionReceipt ExecuteStakeWithdraw(Transaction tx, IStateDatabase stateDb, BlockHeader blockHeader, int txIndex)
    {
        var gasUsed = _chainParams.TransferGasCost;
        var effectiveGasPrice = tx.EffectiveGasPrice(blockHeader.BaseFee);
        var gasFee = effectiveGasPrice * new UInt256(gasUsed);

        // C-2: Always charge gas + increment nonce on failure
        var senderState = stateDb.GetAccount(tx.Sender) ?? AccountState.Empty;

        if (_stakingState == null)
        {
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, gasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.StakingNotAvailable, stateDb, effectiveGasPrice);
        }

        if (senderState.Balance < gasFee)
        {
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, gasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.InsufficientBalance, stateDb, effectiveGasPrice);
        }

        var result = _stakingState.InitiateUnstake(tx.Sender, tx.Value, blockHeader.Number);
        if (!result.IsSuccess)
        {
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, gasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.ValidatorNotRegistered, stateDb, effectiveGasPrice);
        }

        // Debit gas fee only (staked funds enter unbonding queue)
        senderState = senderState with
        {
            Balance = UInt256.CheckedSub(senderState.Balance, gasFee),
            Nonce = IncrementNonce(senderState.Nonce),
        };
        stateDb.SetAccount(tx.Sender, senderState);

        CreditProposerTip(stateDb, blockHeader, effectiveGasPrice, gasUsed);

        return CreateReceipt(tx, blockHeader, txIndex, gasUsed, true, BasaltErrorCode.Success, stateDb, effectiveGasPrice);
    }

    /// <summary>
    /// L-9: Check whether a derived contract address collides with a system contract address.
    /// System contracts use the address range 0x000...0000 to 0x000...FFFF.
    /// </summary>
    private static bool IsSystemAddress(Address address)
    {
        // System addresses have all zeros in bytes 0-17, with only bytes 18-19 non-zero
        Span<byte> bytes = stackalloc byte[Address.Size];
        address.WriteTo(bytes);
        for (int i = 0; i < 18; i++)
        {
            if (bytes[i] != 0)
                return false;
        }
        return true;
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

    /// <summary>
    /// L-4: Check for nonce overflow before incrementing.
    /// Practically unreachable (2^64 txs from one address) but prevents replay attacks.
    /// </summary>
    private static ulong IncrementNonce(ulong currentNonce)
    {
        if (currentNonce == ulong.MaxValue)
            throw new BasaltException(BasaltErrorCode.InvalidNonce, "Nonce overflow: account has exhausted all nonces.");
        return currentNonce + 1;
    }

    /// <summary>
    /// C-2: Charge gas fee and increment nonce on failed transactions.
    /// Charges up to the gas fee (capped at sender balance) and always increments nonce.
    /// </summary>
    private static void ChargeGasAndIncrementNonce(
        IStateDatabase stateDb, Address sender, AccountState senderState,
        UInt256 gasFee, UInt256 effectiveGasPrice, BlockHeader blockHeader)
    {
        // Charge what we can (capped at balance)
        var actualCharge = senderState.Balance < gasFee ? senderState.Balance : gasFee;
        senderState = senderState with
        {
            Balance = UInt256.CheckedSub(senderState.Balance, actualCharge),
            Nonce = IncrementNonce(senderState.Nonce),
        };
        stateDb.SetAccount(sender, senderState);

        // Credit proposer tip from actual charge
        if (!actualCharge.IsZero)
            CreditProposerTip(stateDb, blockHeader, effectiveGasPrice, 0); // Tip from base gas only
    }

    /// <summary>
    /// C-1: Merge contract state from a fork back into canonical state.
    /// Copies the contract account and any storage that was modified on the fork.
    /// </summary>
    private static void MergeForkState(IStateDatabase fork, IStateDatabase canonical, Address contractAddress)
    {
        // Copy the contract account state from fork to canonical
        var contractAccount = fork.GetAccount(contractAddress);
        if (contractAccount.HasValue)
            canonical.SetAccount(contractAddress, contractAccount.Value);
    }

    /// <summary>
    /// Credit the block proposer with the priority tip portion of the gas fee.
    /// The base fee portion is burned (not credited to anyone).
    /// </summary>
    private static void CreditProposerTip(IStateDatabase stateDb, BlockHeader blockHeader, UInt256 effectiveGasPrice, ulong gasUsed)
    {
        if (blockHeader.BaseFee >= effectiveGasPrice)
            return; // No tip — entire fee is base fee (burned)

        var tipPerGas = effectiveGasPrice - blockHeader.BaseFee;
        var totalTip = tipPerGas * new UInt256(gasUsed);
        if (totalTip.IsZero)
            return;

        var proposerState = stateDb.GetAccount(blockHeader.Proposer) ?? AccountState.Empty;
        proposerState = proposerState with { Balance = UInt256.CheckedAdd(proposerState.Balance, totalTip) };
        stateDb.SetAccount(blockHeader.Proposer, proposerState);
    }

    private static TransactionReceipt CreateReceipt(Transaction tx, BlockHeader blockHeader, int txIndex,
        ulong gasUsed, bool success, BasaltErrorCode errorCode, IStateDatabase stateDb,
        UInt256 effectiveGasPrice = default)
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
            EffectiveGasPrice = effectiveGasPrice,
        };
    }
}
