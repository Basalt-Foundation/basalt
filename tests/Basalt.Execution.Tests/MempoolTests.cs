using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests;

public class MempoolTests
{
    private readonly ChainParameters _chainParams = ChainParameters.Devnet;

    private Transaction CreateSignedTransaction(byte[] privateKey, Address sender, ulong nonce, UInt256 gasPrice)
    {
        return Transaction.Sign(new Transaction
        {
            Type = TransactionType.Transfer,
            Nonce = nonce,
            Sender = sender,
            To = Address.Zero,
            Value = new UInt256(100),
            GasLimit = 21_000,
            GasPrice = gasPrice,
            ChainId = _chainParams.ChainId,
        }, privateKey);
    }

    private (byte[] PrivateKey, Address Address) CreateKeyPair()
    {
        var (privateKey, publicKey) = Ed25519Signer.GenerateKeyPair();
        var address = Ed25519Signer.DeriveAddress(publicKey);
        return (privateKey, address);
    }

    [Fact]
    public void Add_ValidTransaction_Succeeds()
    {
        var mempool = new Mempool();
        var (privateKey, sender) = CreateKeyPair();
        var tx = CreateSignedTransaction(privateKey, sender, 0, new UInt256(1));

        var result = mempool.Add(tx);

        result.Should().BeTrue();
        mempool.Count.Should().Be(1);
    }

    [Fact]
    public void Add_DuplicateTransaction_ReturnsFalse()
    {
        var mempool = new Mempool();
        var (privateKey, sender) = CreateKeyPair();
        var tx = CreateSignedTransaction(privateKey, sender, 0, new UInt256(1));

        mempool.Add(tx).Should().BeTrue();
        mempool.Add(tx).Should().BeFalse();
        mempool.Count.Should().Be(1);
    }

    [Fact]
    public void Count_ReflectsAddedTransactions()
    {
        var mempool = new Mempool();
        var (privateKey, sender) = CreateKeyPair();

        mempool.Count.Should().Be(0);

        for (int i = 0; i < 5; i++)
        {
            var tx = CreateSignedTransaction(privateKey, sender, (ulong)i, new UInt256(1));
            mempool.Add(tx);
        }

        mempool.Count.Should().Be(5);
    }

    [Fact]
    public void GetPending_OrdersByGasPriceDescending()
    {
        var mempool = new Mempool();
        var (pk1, addr1) = CreateKeyPair();
        var (pk2, addr2) = CreateKeyPair();
        var (pk3, addr3) = CreateKeyPair();

        var txLow = CreateSignedTransaction(pk1, addr1, 0, new UInt256(1));
        var txMid = CreateSignedTransaction(pk2, addr2, 0, new UInt256(5));
        var txHigh = CreateSignedTransaction(pk3, addr3, 0, new UInt256(10));

        // Add in ascending order
        mempool.Add(txLow);
        mempool.Add(txMid);
        mempool.Add(txHigh);

        var pending = mempool.GetPending(10);
        pending.Should().HaveCount(3);

        // Highest gas price first
        pending[0].GasPrice.Should().Be(new UInt256(10));
        pending[1].GasPrice.Should().Be(new UInt256(5));
        pending[2].GasPrice.Should().Be(new UInt256(1));
    }

    [Fact]
    public void GetPending_RespectsMaxCount()
    {
        var mempool = new Mempool();
        var (privateKey, sender) = CreateKeyPair();

        for (int i = 0; i < 10; i++)
        {
            var tx = CreateSignedTransaction(privateKey, sender, (ulong)i, new UInt256(1));
            mempool.Add(tx);
        }

        var pending = mempool.GetPending(3);
        pending.Should().HaveCount(3);
    }

    [Fact]
    public void Remove_RemovesTransaction()
    {
        var mempool = new Mempool();
        var (privateKey, sender) = CreateKeyPair();
        var tx = CreateSignedTransaction(privateKey, sender, 0, new UInt256(1));

        mempool.Add(tx);
        mempool.Count.Should().Be(1);

        var removed = mempool.Remove(tx.Hash);
        removed.Should().BeTrue();
        mempool.Count.Should().Be(0);
    }

    [Fact]
    public void Remove_NonExistentTransaction_ReturnsFalse()
    {
        var mempool = new Mempool();
        var result = mempool.Remove(Hash256.Zero);
        result.Should().BeFalse();
    }

    [Fact]
    public void IsFull_WhenAtCapacity_RejectsNewTransactions()
    {
        var mempool = new Mempool(maxSize: 3);
        var (pk1, addr1) = CreateKeyPair();
        var (pk2, addr2) = CreateKeyPair();
        var (pk3, addr3) = CreateKeyPair();
        var (pk4, addr4) = CreateKeyPair();

        mempool.Add(CreateSignedTransaction(pk1, addr1, 0, new UInt256(1))).Should().BeTrue();
        mempool.Add(CreateSignedTransaction(pk2, addr2, 0, new UInt256(1))).Should().BeTrue();
        mempool.Add(CreateSignedTransaction(pk3, addr3, 0, new UInt256(1))).Should().BeTrue();

        // Pool is full, this should be rejected
        mempool.Add(CreateSignedTransaction(pk4, addr4, 0, new UInt256(1))).Should().BeFalse();
        mempool.Count.Should().Be(3);
    }

    [Fact]
    public void Contains_ReturnsTrueForAddedTransaction()
    {
        var mempool = new Mempool();
        var (privateKey, sender) = CreateKeyPair();
        var tx = CreateSignedTransaction(privateKey, sender, 0, new UInt256(1));

        mempool.Contains(tx.Hash).Should().BeFalse();
        mempool.Add(tx);
        mempool.Contains(tx.Hash).Should().BeTrue();
    }

    [Fact]
    public void Get_ReturnsTransactionWhenPresent()
    {
        var mempool = new Mempool();
        var (privateKey, sender) = CreateKeyPair();
        var tx = CreateSignedTransaction(privateKey, sender, 0, new UInt256(1));

        mempool.Get(tx.Hash).Should().BeNull();
        mempool.Add(tx);
        mempool.Get(tx.Hash).Should().NotBeNull();
        mempool.Get(tx.Hash)!.Hash.Should().Be(tx.Hash);
    }

    [Fact]
    public void RemoveConfirmed_RemovesMultipleTransactions()
    {
        var mempool = new Mempool();
        var (privateKey, sender) = CreateKeyPair();

        var txs = new List<Transaction>();
        for (int i = 0; i < 5; i++)
        {
            var tx = CreateSignedTransaction(privateKey, sender, (ulong)i, new UInt256(1));
            txs.Add(tx);
            mempool.Add(tx);
        }

        mempool.Count.Should().Be(5);

        // Remove first 3
        mempool.RemoveConfirmed(txs.Take(3));
        mempool.Count.Should().Be(2);

        // The remaining 2 should still be there
        mempool.Contains(txs[3].Hash).Should().BeTrue();
        mempool.Contains(txs[4].Hash).Should().BeTrue();
        mempool.Contains(txs[0].Hash).Should().BeFalse();
    }

    [Fact]
    public void OnTransactionAdded_EventFires_WhenRaiseEventIsTrue()
    {
        var mempool = new Mempool();
        var (privateKey, sender) = CreateKeyPair();
        var tx = CreateSignedTransaction(privateKey, sender, 0, new UInt256(1));

        Transaction? received = null;
        mempool.OnTransactionAdded += t => received = t;

        mempool.Add(tx, raiseEvent: true);

        received.Should().NotBeNull();
        received!.Hash.Should().Be(tx.Hash);
    }

    [Fact]
    public void OnTransactionAdded_EventDoesNotFire_WhenRaiseEventIsFalse()
    {
        var mempool = new Mempool();
        var (privateKey, sender) = CreateKeyPair();
        var tx = CreateSignedTransaction(privateKey, sender, 0, new UInt256(1));

        Transaction? received = null;
        mempool.OnTransactionAdded += t => received = t;

        mempool.Add(tx, raiseEvent: false);

        received.Should().BeNull();
    }

    [Fact]
    public void GetPending_SameGasPrice_OrdersByNonceAscending()
    {
        var mempool = new Mempool();
        var (privateKey, sender) = CreateKeyPair();

        // Add transactions with the same gas price but different nonces
        var tx2 = CreateSignedTransaction(privateKey, sender, 2, new UInt256(5));
        var tx0 = CreateSignedTransaction(privateKey, sender, 0, new UInt256(5));
        var tx1 = CreateSignedTransaction(privateKey, sender, 1, new UInt256(5));

        mempool.Add(tx2);
        mempool.Add(tx0);
        mempool.Add(tx1);

        var pending = mempool.GetPending(10);
        pending.Should().HaveCount(3);

        // Same gas price => lower nonce first
        pending[0].Nonce.Should().Be(0UL);
        pending[1].Nonce.Should().Be(1UL);
        pending[2].Nonce.Should().Be(2UL);
    }
}
