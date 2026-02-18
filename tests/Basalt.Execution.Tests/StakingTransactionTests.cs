using Basalt.Consensus.Staking;
using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution;
using Basalt.Execution.VM;
using Basalt.Storage;
using Xunit;

namespace Basalt.Execution.Tests;

public class StakingTransactionTests
{
    private static readonly ChainParameters Params = new()
    {
        ChainId = 31337,
        NetworkName = "test",
        MinValidatorStake = new UInt256(1000),
    };

    private static (byte[] PrivateKey, Address Address) MakeAccount()
    {
        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var addr = Ed25519Signer.DeriveAddress(pubKey);
        return (privKey, addr);
    }

    private static BlockHeader MakeHeader(ulong number = 1) => new()
    {
        Number = number,
        ParentHash = Hash256.Zero,
        StateRoot = Hash256.Zero,
        TransactionsRoot = Hash256.Zero,
        ReceiptsRoot = Hash256.Zero,
        Timestamp = 1000,
        Proposer = Address.Zero,
        ChainId = Params.ChainId,
        GasUsed = 0,
        GasLimit = 100_000_000,
        ProtocolVersion = 1,
        ExtraData = [],
    };

    private static Transaction SignTx(byte[] privKey, Address sender, TransactionType type,
        UInt256 value, byte[]? data = null, ulong nonce = 0)
    {
        return Transaction.Sign(new Transaction
        {
            Type = type,
            Nonce = nonce,
            Sender = sender,
            To = Address.Zero,
            Value = value,
            GasLimit = 21_000,
            GasPrice = new UInt256(1),
            ChainId = Params.ChainId,
            Data = data ?? [],
        }, privKey);
    }

    [Fact]
    public void ValidatorRegister_Success()
    {
        var ss = new StakingState { MinValidatorStake = new UInt256(1000) };
        var executor = new TransactionExecutor(Params, new ManagedContractRuntime(), ss);
        var stateDb = new InMemoryStateDb();
        var (privKey, addr) = MakeAccount();

        // Fund the account
        stateDb.SetAccount(addr, new AccountState { Balance = new UInt256(100000) });

        var tx = SignTx(privKey, addr, TransactionType.ValidatorRegister, new UInt256(5000));
        var receipt = executor.Execute(tx, stateDb, MakeHeader(), 0);

        Assert.True(receipt.Success);
        Assert.Equal(BasaltErrorCode.Success, receipt.ErrorCode);

        var info = ss.GetStakeInfo(addr);
        Assert.NotNull(info);
        Assert.Equal(new UInt256(5000), info.TotalStake);
        Assert.True(info.IsActive);
    }

    [Fact]
    public void ValidatorRegister_Fails_BelowMinStake()
    {
        var ss = new StakingState { MinValidatorStake = new UInt256(1000) };
        var executor = new TransactionExecutor(Params, new ManagedContractRuntime(), ss);
        var stateDb = new InMemoryStateDb();
        var (privKey, addr) = MakeAccount();

        stateDb.SetAccount(addr, new AccountState { Balance = new UInt256(100000) });

        var tx = SignTx(privKey, addr, TransactionType.ValidatorRegister, new UInt256(500));
        var receipt = executor.Execute(tx, stateDb, MakeHeader(), 0);

        Assert.False(receipt.Success);
        Assert.Equal(BasaltErrorCode.StakeBelowMinimum, receipt.ErrorCode);
    }

    [Fact]
    public void ValidatorRegister_Fails_InsufficientBalance()
    {
        var ss = new StakingState { MinValidatorStake = new UInt256(1000) };
        var executor = new TransactionExecutor(Params, new ManagedContractRuntime(), ss);
        var stateDb = new InMemoryStateDb();
        var (privKey, addr) = MakeAccount();

        stateDb.SetAccount(addr, new AccountState { Balance = new UInt256(500) });

        var tx = SignTx(privKey, addr, TransactionType.ValidatorRegister, new UInt256(5000));
        var receipt = executor.Execute(tx, stateDb, MakeHeader(), 0);

        Assert.False(receipt.Success);
        Assert.Equal(BasaltErrorCode.InsufficientBalance, receipt.ErrorCode);
    }

    [Fact]
    public void ValidatorRegister_With_P2PEndpoint()
    {
        var ss = new StakingState { MinValidatorStake = new UInt256(1000) };
        var executor = new TransactionExecutor(Params, new ManagedContractRuntime(), ss);
        var stateDb = new InMemoryStateDb();
        var (privKey, addr) = MakeAccount();

        stateDb.SetAccount(addr, new AccountState { Balance = new UInt256(100000) });

        var endpoint = "validator-5:30305";
        var tx = SignTx(privKey, addr, TransactionType.ValidatorRegister, new UInt256(5000),
            System.Text.Encoding.UTF8.GetBytes(endpoint));
        var receipt = executor.Execute(tx, stateDb, MakeHeader(), 0);

        Assert.True(receipt.Success);
        Assert.Equal(endpoint, ss.GetStakeInfo(addr)!.P2PEndpoint);
    }

    [Fact]
    public void ValidatorExit_Success()
    {
        var ss = new StakingState { MinValidatorStake = new UInt256(1000), UnbondingPeriod = 100 };
        var executor = new TransactionExecutor(Params, new ManagedContractRuntime(), ss);
        var stateDb = new InMemoryStateDb();
        var (privKey, addr) = MakeAccount();

        stateDb.SetAccount(addr, new AccountState { Balance = new UInt256(100000) });

        // Register first
        var regTx = SignTx(privKey, addr, TransactionType.ValidatorRegister, new UInt256(5000));
        executor.Execute(regTx, stateDb, MakeHeader(), 0);

        // Exit
        var exitTx = SignTx(privKey, addr, TransactionType.ValidatorExit, UInt256.Zero, nonce: 1);
        var receipt = executor.Execute(exitTx, stateDb, MakeHeader(50), 0);

        Assert.True(receipt.Success);

        var info = ss.GetStakeInfo(addr);
        Assert.NotNull(info);
        Assert.False(info.IsActive);
    }

    [Fact]
    public void ValidatorExit_Fails_NotRegistered()
    {
        var ss = new StakingState { MinValidatorStake = new UInt256(1000) };
        var executor = new TransactionExecutor(Params, new ManagedContractRuntime(), ss);
        var stateDb = new InMemoryStateDb();
        var (privKey, addr) = MakeAccount();

        stateDb.SetAccount(addr, new AccountState { Balance = new UInt256(100000) });

        var tx = SignTx(privKey, addr, TransactionType.ValidatorExit, UInt256.Zero);
        var receipt = executor.Execute(tx, stateDb, MakeHeader(), 0);

        Assert.False(receipt.Success);
        Assert.Equal(BasaltErrorCode.ValidatorNotRegistered, receipt.ErrorCode);
    }

    [Fact]
    public void StakeDeposit_Success()
    {
        var ss = new StakingState { MinValidatorStake = new UInt256(1000) };
        var executor = new TransactionExecutor(Params, new ManagedContractRuntime(), ss);
        var stateDb = new InMemoryStateDb();
        var (privKey, addr) = MakeAccount();

        stateDb.SetAccount(addr, new AccountState { Balance = new UInt256(100000) });

        // Register
        var regTx = SignTx(privKey, addr, TransactionType.ValidatorRegister, new UInt256(5000));
        executor.Execute(regTx, stateDb, MakeHeader(), 0);

        // Deposit additional stake
        var depTx = SignTx(privKey, addr, TransactionType.StakeDeposit, new UInt256(3000), nonce: 1);
        var receipt = executor.Execute(depTx, stateDb, MakeHeader(), 1);

        Assert.True(receipt.Success);
        Assert.Equal(new UInt256(8000), ss.GetStakeInfo(addr)!.TotalStake);
    }

    [Fact]
    public void StakeWithdraw_Success()
    {
        var ss = new StakingState { MinValidatorStake = new UInt256(1000), UnbondingPeriod = 100 };
        var executor = new TransactionExecutor(Params, new ManagedContractRuntime(), ss);
        var stateDb = new InMemoryStateDb();
        var (privKey, addr) = MakeAccount();

        stateDb.SetAccount(addr, new AccountState { Balance = new UInt256(100000) });

        // Register
        var regTx = SignTx(privKey, addr, TransactionType.ValidatorRegister, new UInt256(5000));
        executor.Execute(regTx, stateDb, MakeHeader(), 0);

        // Withdraw all
        var withdrawTx = SignTx(privKey, addr, TransactionType.StakeWithdraw, new UInt256(5000), nonce: 1);
        var receipt = executor.Execute(withdrawTx, stateDb, MakeHeader(50), 1);

        Assert.True(receipt.Success);

        var info = ss.GetStakeInfo(addr);
        Assert.NotNull(info);
        Assert.False(info.IsActive);
    }

    [Fact]
    public void Staking_Fails_Without_StakingState()
    {
        var executor = new TransactionExecutor(Params); // No staking state
        var stateDb = new InMemoryStateDb();
        var (privKey, addr) = MakeAccount();

        stateDb.SetAccount(addr, new AccountState { Balance = new UInt256(100000) });

        var tx = SignTx(privKey, addr, TransactionType.ValidatorRegister, new UInt256(5000));
        var receipt = executor.Execute(tx, stateDb, MakeHeader(), 0);

        Assert.False(receipt.Success);
        Assert.Equal(BasaltErrorCode.StakingNotAvailable, receipt.ErrorCode);
    }

    [Fact]
    public void ValidatorRegister_Debits_Sender()
    {
        var ss = new StakingState { MinValidatorStake = new UInt256(1000) };
        var executor = new TransactionExecutor(Params, new ManagedContractRuntime(), ss);
        var stateDb = new InMemoryStateDb();
        var (privKey, addr) = MakeAccount();

        var initialBalance = new UInt256(100000);
        stateDb.SetAccount(addr, new AccountState { Balance = initialBalance });

        var stakeAmount = new UInt256(5000);
        var tx = SignTx(privKey, addr, TransactionType.ValidatorRegister, stakeAmount);
        var receipt = executor.Execute(tx, stateDb, MakeHeader(), 0);

        Assert.True(receipt.Success);

        var senderState = stateDb.GetAccount(addr)!.Value;
        var gasFee = tx.GasPrice * new UInt256(Params.TransferGasCost);
        var expectedBalance = initialBalance - stakeAmount - gasFee;
        Assert.Equal(expectedBalance, senderState.Balance);
        Assert.Equal(1UL, senderState.Nonce);
    }
}
