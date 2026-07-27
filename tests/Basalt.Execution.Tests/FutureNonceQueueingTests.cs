using Basalt.Core;
using Basalt.Crypto;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests;

/// <summary>
/// Whether a sender can offer several transactions without waiting between them.
///
/// The mempool has always served only the contiguous run from a sender's on-chain nonce, stopping at
/// the first gap, so holding a future nonce was safe. Admission refused them anyway, which meant one
/// block of latency per action for anything doing two things in a row.
/// </summary>
public class FutureNonceQueueingTests
{
    private readonly ChainParameters _chainParams = ChainParameters.Devnet;

    private static (byte[] Key, Address Address) NewAccount()
    {
        var (privateKey, publicKey) = Ed25519Signer.GenerateKeyPair();
        return (privateKey, Ed25519Signer.DeriveAddress(publicKey));
    }

    private InMemoryStateDb Funded(Address a)
    {
        var db = new InMemoryStateDb();
        db.SetAccount(a, new AccountState
        {
            Nonce = 0,
            Balance = (UInt256)1_000_000_000_000_000UL,
            StorageRoot = Hash256.Zero,
            CodeHash = Hash256.Zero,
            AccountType = AccountType.ExternallyOwned,
            ComplianceHash = Hash256.Zero,
        });
        return db;
    }

    private Transaction Tx(byte[] key, Address from, Address to, ulong nonce) =>
        Transaction.Sign(new Transaction
        {
            Type = TransactionType.Transfer,
            Nonce = nonce,
            Sender = from,
            To = to,
            Value = new UInt256(1),
            GasLimit = 21_000,
            GasPrice = _chainParams.InitialBaseFee * new UInt256(2),
            Data = [],
            ChainId = _chainParams.ChainId,
        }, key);

    [Fact]
    public void A_nonce_ahead_of_the_account_is_accepted_for_later()
    {
        var (key, sender) = NewAccount();
        var (_, to) = NewAccount();
        var db = Funded(sender);
        var validator = new TransactionValidator(_chainParams);

        validator.Validate(Tx(key, sender, to, 3), db, UInt256.Zero, false, allowFutureNonce: true)
            .IsSuccess.Should().BeTrue();
    }

    // Building a block is the opposite job: only the next nonce can go in, or the block is invalid.
    [Fact]
    public void Block_building_still_wants_exactly_the_next_nonce()
    {
        var (key, sender) = NewAccount();
        var (_, to) = NewAccount();
        var db = Funded(sender);
        var validator = new TransactionValidator(_chainParams);

        validator.Validate(Tx(key, sender, to, 3), db).IsSuccess.Should().BeFalse();
        validator.Validate(Tx(key, sender, to, 0), db).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void An_already_used_nonce_is_still_refused()
    {
        var (key, sender) = NewAccount();
        var (_, to) = NewAccount();
        var db = Funded(sender);
        db.SetAccount(sender, db.GetAccount(sender)!.Value with { Nonce = 5 });
        var validator = new TransactionValidator(_chainParams);

        validator.Validate(Tx(key, sender, to, 4), db, UInt256.Zero, false, allowFutureNonce: true)
            .IsSuccess.Should().BeFalse("nonce 4 is spent and can never become valid again");
    }

    // A queue nobody can fill is a queue anyone can flood.
    [Fact]
    public void A_nonce_far_ahead_is_refused_rather_than_held_forever()
    {
        var (key, sender) = NewAccount();
        var (_, to) = NewAccount();
        var db = Funded(sender);
        var validator = new TransactionValidator(_chainParams);

        var tooFar = TransactionValidator.MaxNonceLookahead + 1;
        validator.Validate(Tx(key, sender, to, tooFar), db, UInt256.Zero, false, allowFutureNonce: true)
            .IsSuccess.Should().BeFalse();
    }

    /// <summary>
    /// The mempool serves the run, not the gap. Offering 0, 1 and 3 must yield 0 and 1 only, since a
    /// block containing 3 without 2 would be invalid.
    /// </summary>
    [Fact]
    public void Only_the_contiguous_run_is_offered_for_a_block()
    {
        var (key, sender) = NewAccount();
        var (_, to) = NewAccount();
        var db = Funded(sender);
        var mempool = new Mempool(100, new TransactionValidator(_chainParams), db);

        mempool.Add(Tx(key, sender, to, 0)).Should().BeTrue();
        mempool.Add(Tx(key, sender, to, 1)).Should().BeTrue();
        mempool.Add(Tx(key, sender, to, 3)).Should().BeTrue("a gap is held, not refused");

        var pending = mempool.GetPending(10, db);
        pending.Select(t => t.Nonce).Should().Equal(0UL, 1UL);
    }
}
