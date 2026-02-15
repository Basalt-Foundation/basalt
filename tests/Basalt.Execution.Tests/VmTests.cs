using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution.VM;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests;

public class VmTests
{
    private readonly ChainParameters _chainParams = ChainParameters.Devnet;
    private readonly InMemoryStateDb _stateDb = new();

    private (byte[] PrivateKey, PublicKey PublicKey, Address Address) CreateFundedAccount(UInt256 balance)
    {
        var (privateKey, publicKey) = Ed25519Signer.GenerateKeyPair();
        var address = Ed25519Signer.DeriveAddress(publicKey);
        _stateDb.SetAccount(address, new AccountState
        {
            Nonce = 0,
            Balance = balance,
            StorageRoot = Hash256.Zero,
            CodeHash = Hash256.Zero,
            AccountType = AccountType.ExternallyOwned,
            ComplianceHash = Hash256.Zero,
        });
        return (privateKey, publicKey, address);
    }

    private BlockHeader CreateBlockHeader(ulong number = 1) => new()
    {
        Number = number,
        ParentHash = Hash256.Zero,
        StateRoot = Hash256.Zero,
        TransactionsRoot = Hash256.Zero,
        ReceiptsRoot = Hash256.Zero,
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        Proposer = Address.Zero,
        ChainId = _chainParams.ChainId,
        GasUsed = 0,
        GasLimit = _chainParams.BlockGasLimit,
    };

    [Fact]
    public void GasMeter_ConsumesGas()
    {
        var meter = new GasMeter(100_000);
        meter.Consume(50_000);
        meter.GasUsed.Should().Be(50_000);
        meter.GasRemaining.Should().Be(50_000);
    }

    [Fact]
    public void GasMeter_OutOfGas_Throws()
    {
        var meter = new GasMeter(100);
        Assert.Throws<OutOfGasException>(() => meter.Consume(101));
    }

    [Fact]
    public void GasMeter_Refund_CappedAt50Percent()
    {
        var meter = new GasMeter(100_000);
        meter.Consume(80_000);
        meter.AddRefund(60_000); // More than 50% of 80_000

        // Refund capped at 50% = 40_000
        meter.EffectiveGasUsed().Should().Be(40_000);
    }

    [Fact]
    public void ContractDeploy_CreatesContractAccount()
    {
        var (privateKey, publicKey, sender) = CreateFundedAccount((UInt256)10_000_000_000);
        var executor = new TransactionExecutor(_chainParams);
        var header = CreateBlockHeader();

        var contractCode = new byte[] { 0x01, 0x02, 0x03, 0x04 };

        var tx = Transaction.Sign(new Transaction
        {
            Type = TransactionType.ContractDeploy,
            Nonce = 0,
            Sender = sender,
            To = Address.Zero,
            Value = UInt256.Zero,
            GasLimit = 1_000_000,
            GasPrice = (UInt256)1,
            Data = contractCode,
            ChainId = _chainParams.ChainId,
        }, privateKey);

        var receipt = executor.Execute(tx, _stateDb, header, 0);

        receipt.Success.Should().BeTrue();
        receipt.GasUsed.Should().BeGreaterThan(0);

        // Contract address should be derived from sender + nonce
        var contractAccount = _stateDb.GetAccount(receipt.To);
        contractAccount.Should().NotBeNull();
        contractAccount!.Value.AccountType.Should().Be(AccountType.Contract);
    }

    [Fact]
    public void ContractCall_ToNonContract_Fails()
    {
        var (privateKey, _, sender) = CreateFundedAccount((UInt256)10_000_000_000);
        var executor = new TransactionExecutor(_chainParams);
        var header = CreateBlockHeader();

        // Create a regular (non-contract) account
        var regularAddr = new Address(new byte[20]);
        _stateDb.SetAccount(regularAddr, AccountState.Empty);

        var callData = new byte[] { 0x53, 0x74, 0x6f, 0x72, /* selector */ 0x00, 0x00 };

        var tx = Transaction.Sign(new Transaction
        {
            Type = TransactionType.ContractCall,
            Nonce = 0,
            Sender = sender,
            To = regularAddr,
            Value = UInt256.Zero,
            GasLimit = 100_000,
            GasPrice = (UInt256)1,
            Data = callData,
            ChainId = _chainParams.ChainId,
        }, privateKey);

        var receipt = executor.Execute(tx, _stateDb, header, 0);
        receipt.Success.Should().BeFalse();
        receipt.ErrorCode.Should().Be(BasaltErrorCode.ContractNotFound);
    }

    [Fact]
    public void ContractDeploy_InsufficientGas_Fails()
    {
        var (privateKey, _, sender) = CreateFundedAccount((UInt256)10_000_000_000);
        var executor = new TransactionExecutor(_chainParams);
        var header = CreateBlockHeader();

        // Very large code that will exhaust gas quickly
        var largeCode = new byte[100_000];
        Array.Fill(largeCode, (byte)0xFF);

        var tx = Transaction.Sign(new Transaction
        {
            Type = TransactionType.ContractDeploy,
            Nonce = 0,
            Sender = sender,
            To = Address.Zero,
            Value = UInt256.Zero,
            GasLimit = 1_000, // Very low gas limit
            GasPrice = (UInt256)1,
            Data = largeCode,
            ChainId = _chainParams.ChainId,
        }, privateKey);

        var receipt = executor.Execute(tx, _stateDb, header, 0);
        receipt.Success.Should().BeFalse();
        receipt.ErrorCode.Should().Be(BasaltErrorCode.OutOfGas);
    }

    [Fact]
    public void HostInterface_StorageReadWrite()
    {
        var contractAddr = new Address(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10, 0x11, 0x12, 0x13, 0x14 });

        _stateDb.SetAccount(contractAddr, new AccountState
        {
            AccountType = AccountType.Contract,
            Balance = UInt256.Zero,
            Nonce = 0,
            StorageRoot = Hash256.Zero,
            CodeHash = Hash256.Zero,
            ComplianceHash = Hash256.Zero,
        });

        var ctx = new VmExecutionContext
        {
            Caller = Address.Zero,
            ContractAddress = contractAddr,
            Value = UInt256.Zero,
            BlockTimestamp = 1000,
            BlockNumber = 1,
            BlockProposer = Address.Zero,
            ChainId = 1,
            GasMeter = new GasMeter(1_000_000),
            StateDb = _stateDb,
            CallDepth = 0,
        };

        var host = new HostInterface(ctx);

        var key = new Hash256(new byte[]
        {
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
            0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10,
            0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18,
            0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20,
        });

        // Read non-existent key
        host.StorageRead(key).Should().BeNull();

        // Write and read back
        host.StorageWrite(key, [0xAA, 0xBB]);
        host.StorageRead(key).Should().Equal([0xAA, 0xBB]);

        // Delete
        host.StorageDelete(key);
        host.StorageRead(key).Should().BeNull();

        // Gas should have been consumed
        ctx.GasMeter.GasUsed.Should().BeGreaterThan(0);
    }

    [Fact]
    public void HostInterface_EmitEvent()
    {
        var ctx = new VmExecutionContext
        {
            Caller = Address.Zero,
            ContractAddress = Address.Zero,
            Value = UInt256.Zero,
            BlockTimestamp = 1000,
            BlockNumber = 1,
            BlockProposer = Address.Zero,
            ChainId = 1,
            GasMeter = new GasMeter(1_000_000),
            StateDb = _stateDb,
            CallDepth = 0,
        };

        var host = new HostInterface(ctx);
        var eventSig = new Hash256(new byte[32]);
        host.EmitEvent(eventSig, [], [0x01, 0x02]);

        ctx.EmittedLogs.Should().HaveCount(1);
        ctx.EmittedLogs[0].Data.Should().Equal([0x01, 0x02]);
    }

    [Fact]
    public void HostInterface_Require_FalseReverts()
    {
        var ctx = new VmExecutionContext
        {
            Caller = Address.Zero,
            ContractAddress = Address.Zero,
            Value = UInt256.Zero,
            BlockTimestamp = 1000,
            BlockNumber = 1,
            BlockProposer = Address.Zero,
            ChainId = 1,
            GasMeter = new GasMeter(1_000_000),
            StateDb = _stateDb,
            CallDepth = 0,
        };

        var host = new HostInterface(ctx);
        Assert.Throws<ContractRevertException>(() => host.Require(false, "condition failed"));
    }
}
