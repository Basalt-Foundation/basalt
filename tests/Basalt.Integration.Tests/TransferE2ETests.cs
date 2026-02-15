using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Integration.Tests;

/// <summary>
/// End-to-end transfer tests: create accounts, sign transactions,
/// validate, execute, and verify state changes.
/// </summary>
public class TransferE2ETests
{
    private readonly ChainParameters _params = ChainParameters.Devnet;
    private readonly InMemoryStateDb _stateDb = new();
    private readonly ChainManager _chainManager = new();
    private readonly TransactionValidator _validator;
    private readonly TransactionExecutor _executor;
    private readonly Mempool _mempool = new();

    public TransferE2ETests()
    {
        _validator = new TransactionValidator(_params);
        _executor = new TransactionExecutor(_params);
    }

    [Fact]
    public void Transfer_Full_Lifecycle()
    {
        // Setup: create genesis with funded account
        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var senderAddr = Ed25519Signer.DeriveAddress(pubKey);
        var recipientAddr = TestHelper.MakeAddress(2);

        var genesis = _chainManager.CreateGenesisBlock(_params, new Dictionary<Address, UInt256>
        {
            [senderAddr] = new UInt256(1_000_000),
        }, _stateDb);

        // Create and sign transaction
        var tx = Transaction.Sign(new Transaction
        {
            Type = TransactionType.Transfer,
            Nonce = 0,
            Sender = senderAddr,
            To = recipientAddr,
            Value = new UInt256(500),
            GasLimit = 21_000,
            GasPrice = new UInt256(1),
            ChainId = _params.ChainId,
        }, privKey);

        // Validate
        var validationResult = _validator.Validate(tx, _stateDb);
        validationResult.IsSuccess.Should().BeTrue();

        // Add to mempool
        _mempool.Add(tx).Should().BeTrue();
        _mempool.Count.Should().Be(1);

        // Execute
        var blockHeader = new BlockHeader
        {
            Number = 1,
            ParentHash = genesis.Hash,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ChainId = _params.ChainId,
            GasLimit = _params.BlockGasLimit,
        };

        var receipt = _executor.Execute(tx, _stateDb, blockHeader, 0);
        receipt.Success.Should().BeTrue();
        receipt.GasUsed.Should().BeGreaterThan(0);

        // Verify final state
        var senderAccount = _stateDb.GetAccount(senderAddr)!.Value;
        var recipientAccount = _stateDb.GetAccount(recipientAddr)!.Value;

        recipientAccount.Balance.Should().Be(new UInt256(500));
        senderAccount.Nonce.Should().Be(1);
        senderAccount.Balance.Should().BeLessThan(new UInt256(1_000_000)); // Deducted value + gas
    }

    [Fact]
    public void Multiple_Transfers_Update_State_Correctly()
    {
        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var senderAddr = Ed25519Signer.DeriveAddress(pubKey);
        var recipientAddr = TestHelper.MakeAddress(10);

        _chainManager.CreateGenesisBlock(_params, new Dictionary<Address, UInt256>
        {
            [senderAddr] = new UInt256(1_000_000),
        }, _stateDb);

        var blockHeader = new BlockHeader
        {
            Number = 1,
            ParentHash = _chainManager.LatestBlock!.Hash,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ChainId = _params.ChainId,
            GasLimit = _params.BlockGasLimit,
        };

        // Execute 3 transfers sequentially
        for (ulong i = 0; i < 3; i++)
        {
            var tx = Transaction.Sign(new Transaction
            {
                Type = TransactionType.Transfer,
                Nonce = i,
                Sender = senderAddr,
                To = recipientAddr,
                Value = new UInt256(100),
                GasLimit = 21_000,
                GasPrice = new UInt256(1),
                ChainId = _params.ChainId,
            }, privKey);

            var receipt = _executor.Execute(tx, _stateDb, blockHeader, (int)i);
            receipt.Success.Should().BeTrue($"transfer {i} should succeed");
        }

        var recipientAccount = _stateDb.GetAccount(recipientAddr)!.Value;
        recipientAccount.Balance.Should().Be(new UInt256(300)); // 3 * 100

        var senderAccount = _stateDb.GetAccount(senderAddr)!.Value;
        senderAccount.Nonce.Should().Be(3);
    }

    [Fact]
    public void Transfer_Insufficient_Balance_Fails()
    {
        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var senderAddr = Ed25519Signer.DeriveAddress(pubKey);

        _chainManager.CreateGenesisBlock(_params, new Dictionary<Address, UInt256>
        {
            [senderAddr] = new UInt256(100), // Very low balance
        }, _stateDb);

        var tx = Transaction.Sign(new Transaction
        {
            Type = TransactionType.Transfer,
            Nonce = 0,
            Sender = senderAddr,
            To = TestHelper.MakeAddress(2),
            Value = new UInt256(1_000_000), // More than balance
            GasLimit = 21_000,
            GasPrice = new UInt256(1),
            ChainId = _params.ChainId,
        }, privKey);

        // Validation should fail
        var result = _validator.Validate(tx, _stateDb);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Transfer_Wrong_Nonce_Fails_Validation()
    {
        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var senderAddr = Ed25519Signer.DeriveAddress(pubKey);

        _chainManager.CreateGenesisBlock(_params, new Dictionary<Address, UInt256>
        {
            [senderAddr] = new UInt256(1_000_000),
        }, _stateDb);

        var tx = Transaction.Sign(new Transaction
        {
            Type = TransactionType.Transfer,
            Nonce = 5, // Wrong nonce, should be 0
            Sender = senderAddr,
            To = TestHelper.MakeAddress(2),
            Value = new UInt256(100),
            GasLimit = 21_000,
            GasPrice = new UInt256(1),
            ChainId = _params.ChainId,
        }, privKey);

        var result = _validator.Validate(tx, _stateDb);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public void Transfer_Wrong_ChainId_Fails_Validation()
    {
        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var senderAddr = Ed25519Signer.DeriveAddress(pubKey);

        _chainManager.CreateGenesisBlock(_params, new Dictionary<Address, UInt256>
        {
            [senderAddr] = new UInt256(1_000_000),
        }, _stateDb);

        var tx = Transaction.Sign(new Transaction
        {
            Type = TransactionType.Transfer,
            Nonce = 0,
            Sender = senderAddr,
            To = TestHelper.MakeAddress(2),
            Value = new UInt256(100),
            GasLimit = 21_000,
            GasPrice = new UInt256(1),
            ChainId = 99999, // Wrong chain ID
        }, privKey);

        var result = _validator.Validate(tx, _stateDb);
        result.IsSuccess.Should().BeFalse();
    }
}
