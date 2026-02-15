using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Integration.Tests;

/// <summary>
/// Tests for state root determinism and consistency across execution.
/// </summary>
public class StateConsistencyTests
{
    private readonly ChainParameters _params = ChainParameters.Devnet;

    [Fact]
    public void Same_Transactions_Produce_Same_StateRoot()
    {
        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var senderAddr = Ed25519Signer.DeriveAddress(pubKey);
        var recipientAddr = TestHelper.MakeAddress(2);
        var initialBalance = new UInt256(1_000_000);

        // Execute same transactions on two independent state DBs
        var root1 = ExecuteTransfers(senderAddr, recipientAddr, initialBalance, privKey);
        var root2 = ExecuteTransfers(senderAddr, recipientAddr, initialBalance, privKey);

        root1.Should().Be(root2, "identical transactions should produce identical state roots");
    }

    [Fact]
    public void Different_Transactions_Produce_Different_StateRoots()
    {
        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var senderAddr = Ed25519Signer.DeriveAddress(pubKey);
        var initialBalance = new UInt256(1_000_000);

        var root1 = ExecuteTransfers(senderAddr, TestHelper.MakeAddress(2), initialBalance, privKey);
        var root2 = ExecuteTransfers(senderAddr, TestHelper.MakeAddress(3), initialBalance, privKey);

        root1.Should().NotBe(root2, "different recipients should produce different state roots");
    }

    [Fact]
    public void State_Root_Changes_After_Each_Transaction()
    {
        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var senderAddr = Ed25519Signer.DeriveAddress(pubKey);
        var recipientAddr = TestHelper.MakeAddress(2);

        var stateDb = new InMemoryStateDb();
        stateDb.SetAccount(senderAddr, new AccountState
        {
            Balance = new UInt256(1_000_000),
            Nonce = 0,
        });

        var executor = new TransactionExecutor(_params);
        var blockHeader = new BlockHeader
        {
            Number = 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ChainId = _params.ChainId,
            GasLimit = _params.BlockGasLimit,
        };

        var roots = new HashSet<Hash256>();
        roots.Add(stateDb.ComputeStateRoot()); // Initial root

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

            executor.Execute(tx, stateDb, blockHeader, (int)i);
            roots.Add(stateDb.ComputeStateRoot());
        }

        // All 4 roots should be unique (initial + 3 transactions)
        roots.Should().HaveCount(4);
    }

    private Hash256 ExecuteTransfers(Address sender, Address recipient, UInt256 initialBalance, byte[] privKey)
    {
        var stateDb = new InMemoryStateDb();
        stateDb.SetAccount(sender, new AccountState
        {
            Balance = initialBalance,
            Nonce = 0,
        });

        var executor = new TransactionExecutor(_params);
        var blockHeader = new BlockHeader
        {
            Number = 1,
            Timestamp = 1000,
            ChainId = _params.ChainId,
            GasLimit = _params.BlockGasLimit,
        };

        for (ulong i = 0; i < 3; i++)
        {
            var tx = Transaction.Sign(new Transaction
            {
                Type = TransactionType.Transfer,
                Nonce = i,
                Sender = sender,
                To = recipient,
                Value = new UInt256(100),
                GasLimit = 21_000,
                GasPrice = new UInt256(1),
                ChainId = _params.ChainId,
            }, privKey);

            executor.Execute(tx, stateDb, blockHeader, (int)i);
        }

        return stateDb.ComputeStateRoot();
    }
}
