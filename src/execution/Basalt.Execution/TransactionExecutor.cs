using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution.Dex;
using Basalt.Execution.VM;
using Basalt.Storage;

namespace Basalt.Execution;

/// <summary>
/// Executes transactions and applies state changes.
/// Supports Transfer (Type=0), ContractDeploy (Type=1), ContractCall (Type=2),
/// StakeDeposit (Type=3), StakeWithdraw (Type=4), ValidatorRegister (Type=5),
/// ValidatorExit (Type=6), and DEX operations (Types 7-12).
/// </summary>
public sealed class TransactionExecutor
{
    private readonly ChainParameters _chainParams;
    private readonly IContractRuntime _contractRuntime;
    private readonly IStakingState? _stakingState;
    private readonly IComplianceVerifier? _complianceVerifier;

    /// <summary>The contract runtime used for BST-20 token dispatch within DEX operations.</summary>
    public IContractRuntime ContractRuntime => _contractRuntime;

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
            var requirements = _complianceVerifier.GetRequirements(tx.To);

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
            TransactionType.DexCreatePool => ExecuteDexCreatePool(tx, stateDb, blockHeader, txIndex),
            TransactionType.DexAddLiquidity => ExecuteDexAddLiquidity(tx, stateDb, blockHeader, txIndex),
            TransactionType.DexRemoveLiquidity => ExecuteDexRemoveLiquidity(tx, stateDb, blockHeader, txIndex),
            TransactionType.DexSwapIntent => ExecuteDexSwapIntent(tx, stateDb, blockHeader, txIndex),
            TransactionType.DexLimitOrder => ExecuteDexLimitOrder(tx, stateDb, blockHeader, txIndex),
            TransactionType.DexCancelOrder => ExecuteDexCancelOrder(tx, stateDb, blockHeader, txIndex),
            TransactionType.DexTransferLp => ExecuteDexTransferLp(tx, stateDb, blockHeader, txIndex),
            TransactionType.DexApproveLp => ExecuteDexApproveLp(tx, stateDb, blockHeader, txIndex),
            _ => ExecuteStub(tx, stateDb, blockHeader, txIndex, BasaltErrorCode.InvalidTransactionType),
        };
    }

    private TransactionReceipt ExecuteTransfer(Transaction tx, IStateDatabase stateDb, BlockHeader blockHeader, int txIndex)
    {
        var gasUsed = _chainParams.TransferGasCost;
        var effectiveGasPrice = tx.EffectiveGasPrice(blockHeader.BaseFee);
        var gasFee = effectiveGasPrice * new UInt256(gasUsed);

        // H-02: Traditional compliance check (KYC, sanctions, geo, holding limits)
        if (_complianceVerifier != null)
        {
            var recipientBalance = (stateDb.GetAccount(tx.To) ?? AccountState.Empty).Balance;
            // Truncate to ulong for compliance policy checks (policies use ulong amounts)
            var amountForCompliance = recipientBalance.Hi == 0 && (ulong)(recipientBalance.Lo >> 64) == 0
                ? (ulong)recipientBalance.Lo : ulong.MaxValue;
            var txAmountForCompliance = tx.Value.Hi == 0 && (ulong)(tx.Value.Lo >> 64) == 0
                ? (ulong)tx.Value.Lo : ulong.MaxValue;
            // MED-01: Use Address.Zero as token address sentinel for native transfers.
            // Previously tx.To (recipient) was passed as tokenAddress, which would look up
            // the recipient's compliance policy instead of the native token policy.
            var outcome = _complianceVerifier.CheckTransferCompliance(
                Address.Zero.ToArray(), tx.Sender.ToArray(), tx.To.ToArray(),
                txAmountForCompliance, blockHeader.Timestamp, amountForCompliance);
            if (!outcome.Allowed)
                return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, outcome.ErrorCode, stateDb);
        }

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
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, maxGasFee, effectiveGasPrice, blockHeader, tx.GasLimit);
            // MED-02 R3: Report tx.GasLimit as GasUsed (not 0) since the sender was charged maxGasFee.
            return CreateReceipt(tx, blockHeader, txIndex, tx.GasLimit, false,
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

            // CRIT-02: Refund tx.Value when fork is not merged (deploy failed)
            if (!result.Success && tx.Value > UInt256.Zero)
                refund = UInt256.CheckedAdd(refund, tx.Value);

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
                PostStateRoot = Hash256.Zero,
                Logs = result.Logs,
                EffectiveGasPrice = effectiveGasPrice,
            };
        }
        catch (OutOfGasException)
        {
            // C-1: Fork is discarded — no storage mutations leak to canonical state.
            // C-2: Nonce already incremented, max gas fee already debited.
            var fullGasCharge = effectiveGasPrice * new UInt256(tx.GasLimit);
            var oogRefund = maxGasFee - fullGasCharge;

            // CRIT-02: Refund tx.Value since fork was discarded
            if (tx.Value > UInt256.Zero)
                oogRefund = UInt256.CheckedAdd(oogRefund, tx.Value);

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
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, sState, maxGasFee, effectiveGasPrice, blockHeader, tx.GasLimit);
            // HIGH-03: Report tx.GasLimit as GasUsed (not 0) since the sender was charged maxGasFee.
            // This ensures totalGasUsed in the block header is accurate.
            return CreateReceipt(tx, blockHeader, txIndex, tx.GasLimit, false,
                BasaltErrorCode.ContractNotFound, stateDb, effectiveGasPrice);
        }

        // C-2: Pre-check balance
        var senderState = stateDb.GetAccount(tx.Sender) ?? AccountState.Empty;
        var totalDebit = UInt256.CheckedAdd(maxGasFee, tx.Value);

        if (senderState.Balance < totalDebit)
        {
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, maxGasFee, effectiveGasPrice, blockHeader, tx.GasLimit);
            // MED-02 R3: Report tx.GasLimit as GasUsed (not 0) since the sender was charged maxGasFee.
            return CreateReceipt(tx, blockHeader, txIndex, tx.GasLimit, false,
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

            // CRIT-02: Refund tx.Value when fork is not merged (contract reverted/failed)
            if (!result.Success && tx.Value > UInt256.Zero)
                refund = UInt256.CheckedAdd(refund, tx.Value);

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
                PostStateRoot = Hash256.Zero,
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

            // CRIT-02: Refund tx.Value since fork was discarded
            if (tx.Value > UInt256.Zero)
                oogRefund = UInt256.CheckedAdd(oogRefund, tx.Value);

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

    // ────────── DEX Transaction Handlers ──────────

    private TransactionReceipt ExecuteDexCreatePool(Transaction tx, IStateDatabase stateDb, BlockHeader blockHeader, int txIndex)
    {
        var gasUsed = _chainParams.DexCreatePoolGas;
        var effectiveGasPrice = tx.EffectiveGasPrice(blockHeader.BaseFee);
        var gasFee = effectiveGasPrice * new UInt256(gasUsed);

        var senderState = stateDb.GetAccount(tx.Sender) ?? AccountState.Empty;
        if (senderState.Balance < gasFee)
        {
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, gasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.InsufficientBalance, stateDb, effectiveGasPrice);
        }

        // Parse tx.Data: [20B token0][20B token1][4B feeBps] = 44 bytes
        if (tx.Data.Length < 44)
        {
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, gasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.DexInvalidData, stateDb, effectiveGasPrice);
        }

        var tokenA = new Address(tx.Data.AsSpan(0, 20));
        var tokenB = new Address(tx.Data.AsSpan(20, 20));
        var feeBps = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(tx.Data.AsSpan(40, 4));

        // Fork state for atomicity
        var fork = stateDb.Fork();
        var dexState = new DexState(fork);
        var engine = new DexEngine(dexState);

        var result = engine.CreatePool(tx.Sender, tokenA, tokenB, feeBps);
        if (!result.Success)
        {
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, gasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, result.ErrorCode, stateDb, effectiveGasPrice);
        }

        // Commit: charge gas, increment nonce, merge fork
        senderState = senderState with
        {
            Balance = UInt256.CheckedSub(senderState.Balance, gasFee),
            Nonce = IncrementNonce(senderState.Nonce),
        };
        stateDb.SetAccount(tx.Sender, senderState);
        MergeForkState(fork, stateDb, DexState.DexAddress);
        CreditProposerTip(stateDb, blockHeader, effectiveGasPrice, gasUsed);

        return new TransactionReceipt
        {
            TransactionHash = tx.Hash,
            BlockHash = blockHeader.Hash,
            BlockNumber = blockHeader.Number,
            TransactionIndex = txIndex,
            From = tx.Sender,
            To = DexState.DexAddress,
            GasUsed = gasUsed,
            Success = true,
            ErrorCode = BasaltErrorCode.Success,
            PostStateRoot = Hash256.Zero,
            Logs = result.Logs,
            EffectiveGasPrice = effectiveGasPrice,
        };
    }

    private TransactionReceipt ExecuteDexAddLiquidity(Transaction tx, IStateDatabase stateDb, BlockHeader blockHeader, int txIndex)
    {
        var gasUsed = _chainParams.DexLiquidityGas;
        var effectiveGasPrice = tx.EffectiveGasPrice(blockHeader.BaseFee);
        var gasFee = effectiveGasPrice * new UInt256(gasUsed);

        var senderState = stateDb.GetAccount(tx.Sender) ?? AccountState.Empty;
        if (senderState.Balance < gasFee)
        {
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, gasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.InsufficientBalance, stateDb, effectiveGasPrice);
        }

        // Parse tx.Data: [8B poolId][32B amt0Desired][32B amt1Desired][32B amt0Min][32B amt1Min] = 136 bytes
        if (tx.Data.Length < 136)
        {
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, gasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.DexInvalidData, stateDb, effectiveGasPrice);
        }

        var poolId = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(tx.Data.AsSpan(0, 8));
        var amt0Desired = new UInt256(tx.Data.AsSpan(8, 32));
        var amt1Desired = new UInt256(tx.Data.AsSpan(40, 32));
        var amt0Min = new UInt256(tx.Data.AsSpan(72, 32));
        var amt1Min = new UInt256(tx.Data.AsSpan(104, 32));

        // Charge gas first, then fork for DEX operations
        senderState = senderState with
        {
            Balance = UInt256.CheckedSub(senderState.Balance, gasFee),
            Nonce = IncrementNonce(senderState.Nonce),
        };
        stateDb.SetAccount(tx.Sender, senderState);

        var fork = stateDb.Fork();
        var dexState = new DexState(fork);
        var engine = new DexEngine(dexState, _contractRuntime);

        var result = engine.AddLiquidity(tx.Sender, poolId, amt0Desired, amt1Desired, amt0Min, amt1Min, fork);
        if (!result.Success)
        {
            // Don't merge fork — gas already charged
            CreditProposerTip(stateDb, blockHeader, effectiveGasPrice, gasUsed);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, result.ErrorCode, stateDb, effectiveGasPrice);
        }

        MergeForkState(fork, stateDb, DexState.DexAddress);
        CreditProposerTip(stateDb, blockHeader, effectiveGasPrice, gasUsed);

        return new TransactionReceipt
        {
            TransactionHash = tx.Hash,
            BlockHash = blockHeader.Hash,
            BlockNumber = blockHeader.Number,
            TransactionIndex = txIndex,
            From = tx.Sender,
            To = DexState.DexAddress,
            GasUsed = gasUsed,
            Success = true,
            ErrorCode = BasaltErrorCode.Success,
            PostStateRoot = Hash256.Zero,
            Logs = result.Logs,
            EffectiveGasPrice = effectiveGasPrice,
        };
    }

    private TransactionReceipt ExecuteDexRemoveLiquidity(Transaction tx, IStateDatabase stateDb, BlockHeader blockHeader, int txIndex)
    {
        var gasUsed = _chainParams.DexLiquidityGas;
        var effectiveGasPrice = tx.EffectiveGasPrice(blockHeader.BaseFee);
        var gasFee = effectiveGasPrice * new UInt256(gasUsed);

        var senderState = stateDb.GetAccount(tx.Sender) ?? AccountState.Empty;
        if (senderState.Balance < gasFee)
        {
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, gasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.InsufficientBalance, stateDb, effectiveGasPrice);
        }

        // Parse tx.Data: [8B poolId][32B shares][32B amt0Min][32B amt1Min] = 104 bytes
        if (tx.Data.Length < 104)
        {
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, gasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.DexInvalidData, stateDb, effectiveGasPrice);
        }

        var poolId = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(tx.Data.AsSpan(0, 8));
        var shares = new UInt256(tx.Data.AsSpan(8, 32));
        var amt0Min = new UInt256(tx.Data.AsSpan(40, 32));
        var amt1Min = new UInt256(tx.Data.AsSpan(72, 32));

        senderState = senderState with
        {
            Balance = UInt256.CheckedSub(senderState.Balance, gasFee),
            Nonce = IncrementNonce(senderState.Nonce),
        };
        stateDb.SetAccount(tx.Sender, senderState);

        var fork = stateDb.Fork();
        var dexState = new DexState(fork);
        var engine = new DexEngine(dexState, _contractRuntime);

        var result = engine.RemoveLiquidity(tx.Sender, poolId, shares, amt0Min, amt1Min, fork);
        if (!result.Success)
        {
            CreditProposerTip(stateDb, blockHeader, effectiveGasPrice, gasUsed);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, result.ErrorCode, stateDb, effectiveGasPrice);
        }

        MergeForkState(fork, stateDb, DexState.DexAddress);
        CreditProposerTip(stateDb, blockHeader, effectiveGasPrice, gasUsed);

        return new TransactionReceipt
        {
            TransactionHash = tx.Hash,
            BlockHash = blockHeader.Hash,
            BlockNumber = blockHeader.Number,
            TransactionIndex = txIndex,
            From = tx.Sender,
            To = DexState.DexAddress,
            GasUsed = gasUsed,
            Success = true,
            ErrorCode = BasaltErrorCode.Success,
            PostStateRoot = Hash256.Zero,
            Logs = result.Logs,
            EffectiveGasPrice = effectiveGasPrice,
        };
    }

    private TransactionReceipt ExecuteDexSwapIntent(Transaction tx, IStateDatabase stateDb, BlockHeader blockHeader, int txIndex)
    {
        // DexSwapIntent txs are normally collected and batch-settled in BlockBuilder Phase B.
        // If executed individually (fallback path), route through the AMM directly.
        var gasUsed = _chainParams.DexSwapGas;
        var effectiveGasPrice = tx.EffectiveGasPrice(blockHeader.BaseFee);
        var gasFee = effectiveGasPrice * new UInt256(gasUsed);

        var senderState = stateDb.GetAccount(tx.Sender) ?? AccountState.Empty;
        if (senderState.Balance < gasFee)
        {
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, gasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.InsufficientBalance, stateDb, effectiveGasPrice);
        }

        // Parse tx.Data: [1B version][20B tokenIn][20B tokenOut][32B amountIn][32B minAmountOut][8B deadline][1B flags] = 114 bytes
        if (tx.Data.Length < 114)
        {
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, gasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.DexInvalidData, stateDb, effectiveGasPrice);
        }

        // var version = tx.Data[0]; // reserved for future use
        var tokenIn = new Address(tx.Data.AsSpan(1, 20));
        var tokenOut = new Address(tx.Data.AsSpan(21, 20));
        var amountIn = new UInt256(tx.Data.AsSpan(41, 32));
        var minAmountOut = new UInt256(tx.Data.AsSpan(73, 32));
        var deadline = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(tx.Data.AsSpan(105, 8));
        // var flags = tx.Data[113]; // bit0 = allowPartialFill

        // Check deadline
        if (deadline > 0 && blockHeader.Number > deadline)
        {
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, gasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.DexDeadlineExpired, stateDb, effectiveGasPrice);
        }

        senderState = senderState with
        {
            Balance = UInt256.CheckedSub(senderState.Balance, gasFee),
            Nonce = IncrementNonce(senderState.Nonce),
        };
        stateDb.SetAccount(tx.Sender, senderState);

        // Find pool for this pair (try all fee tiers)
        var fork = stateDb.Fork();
        var dexState = new DexState(fork);
        var (t0, t1) = DexEngine.SortTokens(tokenIn, tokenOut);

        ulong? poolId = null;
        foreach (var tier in Dex.Math.DexLibrary.AllowedFeeTiers)
        {
            poolId = dexState.LookupPool(t0, t1, tier);
            if (poolId != null) break;
        }

        if (poolId == null)
        {
            CreditProposerTip(stateDb, blockHeader, effectiveGasPrice, gasUsed);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.DexPoolNotFound, stateDb, effectiveGasPrice);
        }

        var engine = new DexEngine(dexState, _contractRuntime);
        var result = engine.ExecuteSwap(tx.Sender, poolId.Value, tokenIn, amountIn, minAmountOut, fork);

        if (!result.Success)
        {
            CreditProposerTip(stateDb, blockHeader, effectiveGasPrice, gasUsed);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, result.ErrorCode, stateDb, effectiveGasPrice);
        }

        MergeForkState(fork, stateDb, DexState.DexAddress);
        CreditProposerTip(stateDb, blockHeader, effectiveGasPrice, gasUsed);

        return new TransactionReceipt
        {
            TransactionHash = tx.Hash,
            BlockHash = blockHeader.Hash,
            BlockNumber = blockHeader.Number,
            TransactionIndex = txIndex,
            From = tx.Sender,
            To = DexState.DexAddress,
            GasUsed = gasUsed,
            Success = true,
            ErrorCode = BasaltErrorCode.Success,
            PostStateRoot = Hash256.Zero,
            Logs = result.Logs,
            EffectiveGasPrice = effectiveGasPrice,
        };
    }

    private TransactionReceipt ExecuteDexLimitOrder(Transaction tx, IStateDatabase stateDb, BlockHeader blockHeader, int txIndex)
    {
        var gasUsed = _chainParams.DexLimitOrderGas;
        var effectiveGasPrice = tx.EffectiveGasPrice(blockHeader.BaseFee);
        var gasFee = effectiveGasPrice * new UInt256(gasUsed);

        var senderState = stateDb.GetAccount(tx.Sender) ?? AccountState.Empty;
        if (senderState.Balance < gasFee)
        {
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, gasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.InsufficientBalance, stateDb, effectiveGasPrice);
        }

        // Parse tx.Data: [8B poolId][32B price][32B amount][1B isBuy][8B expiryBlock] = 81 bytes
        if (tx.Data.Length < 81)
        {
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, gasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.DexInvalidData, stateDb, effectiveGasPrice);
        }

        var poolId = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(tx.Data.AsSpan(0, 8));
        var price = new UInt256(tx.Data.AsSpan(8, 32));
        var amount = new UInt256(tx.Data.AsSpan(40, 32));
        var isBuy = tx.Data[72] == 1;
        var expiryBlock = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(tx.Data.AsSpan(73, 8));

        senderState = senderState with
        {
            Balance = UInt256.CheckedSub(senderState.Balance, gasFee),
            Nonce = IncrementNonce(senderState.Nonce),
        };
        stateDb.SetAccount(tx.Sender, senderState);

        var fork = stateDb.Fork();
        var dexState = new DexState(fork);
        var engine = new DexEngine(dexState, _contractRuntime);

        var result = engine.PlaceOrder(tx.Sender, poolId, price, amount, isBuy, expiryBlock, fork);
        if (!result.Success)
        {
            CreditProposerTip(stateDb, blockHeader, effectiveGasPrice, gasUsed);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, result.ErrorCode, stateDb, effectiveGasPrice);
        }

        MergeForkState(fork, stateDb, DexState.DexAddress);
        CreditProposerTip(stateDb, blockHeader, effectiveGasPrice, gasUsed);

        return new TransactionReceipt
        {
            TransactionHash = tx.Hash,
            BlockHash = blockHeader.Hash,
            BlockNumber = blockHeader.Number,
            TransactionIndex = txIndex,
            From = tx.Sender,
            To = DexState.DexAddress,
            GasUsed = gasUsed,
            Success = true,
            ErrorCode = BasaltErrorCode.Success,
            PostStateRoot = Hash256.Zero,
            Logs = result.Logs,
            EffectiveGasPrice = effectiveGasPrice,
        };
    }

    private TransactionReceipt ExecuteDexCancelOrder(Transaction tx, IStateDatabase stateDb, BlockHeader blockHeader, int txIndex)
    {
        var gasUsed = _chainParams.DexCancelOrderGas;
        var effectiveGasPrice = tx.EffectiveGasPrice(blockHeader.BaseFee);
        var gasFee = effectiveGasPrice * new UInt256(gasUsed);

        var senderState = stateDb.GetAccount(tx.Sender) ?? AccountState.Empty;
        if (senderState.Balance < gasFee)
        {
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, gasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.InsufficientBalance, stateDb, effectiveGasPrice);
        }

        // Parse tx.Data: [8B orderId] = 8 bytes
        if (tx.Data.Length < 8)
        {
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, gasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.DexInvalidData, stateDb, effectiveGasPrice);
        }

        var orderId = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(tx.Data.AsSpan(0, 8));

        senderState = senderState with
        {
            Balance = UInt256.CheckedSub(senderState.Balance, gasFee),
            Nonce = IncrementNonce(senderState.Nonce),
        };
        stateDb.SetAccount(tx.Sender, senderState);

        var fork = stateDb.Fork();
        var dexState = new DexState(fork);
        var engine = new DexEngine(dexState, _contractRuntime);

        var result = engine.CancelOrder(tx.Sender, orderId, fork);
        if (!result.Success)
        {
            CreditProposerTip(stateDb, blockHeader, effectiveGasPrice, gasUsed);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, result.ErrorCode, stateDb, effectiveGasPrice);
        }

        MergeForkState(fork, stateDb, DexState.DexAddress);
        CreditProposerTip(stateDb, blockHeader, effectiveGasPrice, gasUsed);

        return new TransactionReceipt
        {
            TransactionHash = tx.Hash,
            BlockHash = blockHeader.Hash,
            BlockNumber = blockHeader.Number,
            TransactionIndex = txIndex,
            From = tx.Sender,
            To = DexState.DexAddress,
            GasUsed = gasUsed,
            Success = true,
            ErrorCode = BasaltErrorCode.Success,
            PostStateRoot = Hash256.Zero,
            Logs = result.Logs,
            EffectiveGasPrice = effectiveGasPrice,
        };
    }

    private TransactionReceipt ExecuteDexTransferLp(Transaction tx, IStateDatabase stateDb, BlockHeader blockHeader, int txIndex)
    {
        var gasUsed = _chainParams.DexTransferLpGas;
        var effectiveGasPrice = tx.EffectiveGasPrice(blockHeader.BaseFee);
        var gasFee = effectiveGasPrice * new UInt256(gasUsed);

        var senderState = stateDb.GetAccount(tx.Sender) ?? AccountState.Empty;
        if (senderState.Balance < gasFee)
        {
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, gasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.InsufficientBalance, stateDb, effectiveGasPrice);
        }

        // Parse tx.Data: [8B poolId][20B recipient][32B amount] = 60 bytes
        if (tx.Data.Length < 60)
        {
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, gasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.DexInvalidData, stateDb, effectiveGasPrice);
        }

        var poolId = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(tx.Data.AsSpan(0, 8));
        var recipient = new Address(tx.Data.AsSpan(8, 20));
        var amount = new UInt256(tx.Data.AsSpan(28, 32));

        senderState = senderState with
        {
            Balance = UInt256.CheckedSub(senderState.Balance, gasFee),
            Nonce = IncrementNonce(senderState.Nonce),
        };
        stateDb.SetAccount(tx.Sender, senderState);

        var fork = stateDb.Fork();
        var dexState = new DexState(fork);
        var engine = new DexEngine(dexState);

        var result = engine.TransferLp(tx.Sender, poolId, recipient, amount);
        if (!result.Success)
        {
            CreditProposerTip(stateDb, blockHeader, effectiveGasPrice, gasUsed);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, result.ErrorCode, stateDb, effectiveGasPrice);
        }

        MergeForkState(fork, stateDb, DexState.DexAddress);
        CreditProposerTip(stateDb, blockHeader, effectiveGasPrice, gasUsed);

        return new TransactionReceipt
        {
            TransactionHash = tx.Hash,
            BlockHash = blockHeader.Hash,
            BlockNumber = blockHeader.Number,
            TransactionIndex = txIndex,
            From = tx.Sender,
            To = DexState.DexAddress,
            GasUsed = gasUsed,
            Success = true,
            ErrorCode = BasaltErrorCode.Success,
            PostStateRoot = Hash256.Zero,
            Logs = result.Logs,
            EffectiveGasPrice = effectiveGasPrice,
        };
    }

    private TransactionReceipt ExecuteDexApproveLp(Transaction tx, IStateDatabase stateDb, BlockHeader blockHeader, int txIndex)
    {
        var gasUsed = _chainParams.DexApproveLpGas;
        var effectiveGasPrice = tx.EffectiveGasPrice(blockHeader.BaseFee);
        var gasFee = effectiveGasPrice * new UInt256(gasUsed);

        var senderState = stateDb.GetAccount(tx.Sender) ?? AccountState.Empty;
        if (senderState.Balance < gasFee)
        {
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, gasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.InsufficientBalance, stateDb, effectiveGasPrice);
        }

        // Parse tx.Data: [8B poolId][20B spender][32B amount] = 60 bytes
        if (tx.Data.Length < 60)
        {
            ChargeGasAndIncrementNonce(stateDb, tx.Sender, senderState, gasFee, effectiveGasPrice, blockHeader);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, BasaltErrorCode.DexInvalidData, stateDb, effectiveGasPrice);
        }

        var poolId = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(tx.Data.AsSpan(0, 8));
        var spender = new Address(tx.Data.AsSpan(8, 20));
        var amount = new UInt256(tx.Data.AsSpan(28, 32));

        senderState = senderState with
        {
            Balance = UInt256.CheckedSub(senderState.Balance, gasFee),
            Nonce = IncrementNonce(senderState.Nonce),
        };
        stateDb.SetAccount(tx.Sender, senderState);

        var fork = stateDb.Fork();
        var dexState = new DexState(fork);
        var engine = new DexEngine(dexState);

        var result = engine.ApproveLp(tx.Sender, poolId, spender, amount);
        if (!result.Success)
        {
            CreditProposerTip(stateDb, blockHeader, effectiveGasPrice, gasUsed);
            return CreateReceipt(tx, blockHeader, txIndex, gasUsed, false, result.ErrorCode, stateDb, effectiveGasPrice);
        }

        MergeForkState(fork, stateDb, DexState.DexAddress);
        CreditProposerTip(stateDb, blockHeader, effectiveGasPrice, gasUsed);

        return new TransactionReceipt
        {
            TransactionHash = tx.Hash,
            BlockHash = blockHeader.Hash,
            BlockNumber = blockHeader.Number,
            TransactionIndex = txIndex,
            From = tx.Sender,
            To = DexState.DexAddress,
            GasUsed = gasUsed,
            Success = true,
            ErrorCode = BasaltErrorCode.Success,
            PostStateRoot = Hash256.Zero,
            Logs = result.Logs,
            EffectiveGasPrice = effectiveGasPrice,
        };
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
        UInt256 gasFee, UInt256 effectiveGasPrice, BlockHeader blockHeader,
        ulong gasLimit = GasTable.TxBase)
    {
        // Charge what we can (capped at balance)
        var actualCharge = senderState.Balance < gasFee ? senderState.Balance : gasFee;
        senderState = senderState with
        {
            Balance = UInt256.CheckedSub(senderState.Balance, actualCharge),
            Nonce = IncrementNonce(senderState.Nonce),
        };
        stateDb.SetAccount(sender, senderState);

        // MED-01 R3: Credit proposer tip using the actual gas equivalent charged to the sender,
        // not the fixed TxBase (21,000). A failed tx with GasLimit=1,000,000 charges the sender
        // the full gas limit but was only crediting the proposer for 21,000 gas worth of tip.
        if (!actualCharge.IsZero && !effectiveGasPrice.IsZero)
        {
            var gasEquivalent = actualCharge / effectiveGasPrice;
            var gasUnits = (gasEquivalent.IsZero || gasEquivalent > new UInt256(gasLimit))
                ? gasLimit
                : (ulong)gasEquivalent.Lo;
            CreditProposerTip(stateDb, blockHeader, effectiveGasPrice, gasUnits);
        }
    }

    /// <summary>
    /// C-1: Merge contract state from a fork back into canonical state.
    /// Copies the contract account and all storage mutations made on the fork.
    /// </summary>
    private static void MergeForkState(IStateDatabase fork, IStateDatabase canonical, Address contractAddress)
    {
        // CRIT-01 R3: Merge ALL modified accounts from the fork, not just the contract address.
        // When a contract calls Context.TransferNative(recipient, amount), the recipient's balance
        // credit lives on the fork. Without merging all dirty accounts, native transfer recipients
        // permanently lose funds (debited from contract but never credited to recipient).
        foreach (var addr in fork.GetModifiedAccounts())
        {
            var account = fork.GetAccount(addr);
            if (account.HasValue)
                canonical.SetAccount(addr, account.Value);
            else
                canonical.DeleteAccount(addr);
        }

        // Merge storage mutations from the fork back to canonical.
        foreach (var (contract, key) in fork.GetModifiedStorageKeys())
        {
            var value = fork.GetStorage(contract, key);
            if (value != null)
                canonical.SetStorage(contract, key, value);
            else
                canonical.DeleteStorage(contract, key);
        }
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
        // MED-03: PostStateRoot set to Hash256.Zero per-receipt to avoid O(n^2) ComputeStateRoot().
        // The final state root is computed once in BlockBuilder after all txs execute.
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
            PostStateRoot = Hash256.Zero,
            EffectiveGasPrice = effectiveGasPrice,
        };
    }
}
