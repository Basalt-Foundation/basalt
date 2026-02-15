using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Integration.Tests;

/// <summary>
/// Integration tests for mempool operations with validated transactions.
/// </summary>
public class MempoolIntegrationTests
{
    private readonly ChainParameters _params = ChainParameters.Devnet;
    private readonly InMemoryStateDb _stateDb = new();
    private readonly Mempool _mempool = new();

    [Fact]
    public void Mempool_Orders_By_GasPrice()
    {
        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var senderAddr = Ed25519Signer.DeriveAddress(pubKey);

        _stateDb.SetAccount(senderAddr, new AccountState
        {
            Balance = new UInt256(10_000_000),
            Nonce = 0,
        });

        // Add transactions with different gas prices
        var txLow = Transaction.Sign(new Transaction
        {
            Type = TransactionType.Transfer,
            Nonce = 0,
            Sender = senderAddr,
            To = TestHelper.MakeAddress(2),
            Value = new UInt256(100),
            GasLimit = 21_000,
            GasPrice = new UInt256(1),
            ChainId = _params.ChainId,
        }, privKey);

        var txHigh = Transaction.Sign(new Transaction
        {
            Type = TransactionType.Transfer,
            Nonce = 1,
            Sender = senderAddr,
            To = TestHelper.MakeAddress(2),
            Value = new UInt256(100),
            GasLimit = 21_000,
            GasPrice = new UInt256(10),
            ChainId = _params.ChainId,
        }, privKey);

        _mempool.Add(txLow).Should().BeTrue();
        _mempool.Add(txHigh).Should().BeTrue();

        var pending = _mempool.GetPending(10);
        pending.Should().HaveCount(2);
        pending[0].GasPrice.Should().BeGreaterOrEqualTo(pending[1].GasPrice);
    }

    [Fact]
    public void Mempool_Rejects_Duplicates()
    {
        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var senderAddr = Ed25519Signer.DeriveAddress(pubKey);

        var tx = Transaction.Sign(new Transaction
        {
            Type = TransactionType.Transfer,
            Nonce = 0,
            Sender = senderAddr,
            To = TestHelper.MakeAddress(2),
            Value = new UInt256(100),
            GasLimit = 21_000,
            GasPrice = new UInt256(1),
            ChainId = _params.ChainId,
        }, privKey);

        _mempool.Add(tx).Should().BeTrue();
        _mempool.Add(tx).Should().BeFalse(); // Duplicate
        _mempool.Count.Should().Be(1);
    }

    [Fact]
    public void Mempool_RemoveConfirmed_Clears_Included_Transactions()
    {
        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var senderAddr = Ed25519Signer.DeriveAddress(pubKey);

        var txs = new List<Transaction>();
        for (ulong i = 0; i < 5; i++)
        {
            var tx = Transaction.Sign(new Transaction
            {
                Type = TransactionType.Transfer,
                Nonce = i,
                Sender = senderAddr,
                To = TestHelper.MakeAddress(2),
                Value = new UInt256(10),
                GasLimit = 21_000,
                GasPrice = new UInt256(1),
                ChainId = _params.ChainId,
            }, privKey);
            _mempool.Add(tx);
            txs.Add(tx);
        }

        _mempool.Count.Should().Be(5);

        // Remove first 3 as "confirmed"
        _mempool.RemoveConfirmed(txs.Take(3));
        _mempool.Count.Should().Be(2);
    }
}
