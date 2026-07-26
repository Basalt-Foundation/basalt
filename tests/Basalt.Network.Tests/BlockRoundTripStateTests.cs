using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution;
using Basalt.Storage;
using Basalt.Storage.Trie;
using FluentAssertions;
using Xunit;

namespace Basalt.Network.Tests;

/// <summary>
/// Whether a block survives the wire well enough to reach the same state.
///
/// The proposer is the one node that never decodes its own block: it builds it in memory, computes the
/// root from the state it just mutated, and publishes. Everyone else receives bytes and rebuilds the
/// transactions from them before executing.
///
/// That makes the codec the one step present on every path except the one that produced the number
/// every other path is checked against. On the testnet, every applying node agreed with every other and
/// all of them disagreed with the header, which is the shape this would take.
/// </summary>
public class BlockRoundTripStateTests
{
    private readonly ChainParameters _chainParams = ChainParameters.Devnet;

    private static (byte[] Key, Address Address) NewAccount()
    {
        var (privateKey, publicKey) = Ed25519Signer.GenerateKeyPair();
        return (privateKey, Ed25519Signer.DeriveAddress(publicKey));
    }

    private static TrieStateDb Funded(params Address[] accounts)
    {
        var db = new TrieStateDb(new InMemoryTrieNodeStore());
        foreach (var a in accounts)
        {
            db.SetAccount(a, new AccountState
            {
                Nonce = 0,
                Balance = (UInt256)1_000_000_000_000_000UL,
                StorageRoot = Hash256.Zero,
                CodeHash = Hash256.Zero,
                AccountType = AccountType.ExternallyOwned,
                ComplianceHash = Hash256.Zero,
            });
        }

        return db;
    }

    /// <summary>
    /// Build a block, put it through the codec the way the network does, and execute what comes out.
    ///
    /// If the decoded transactions do not reach the state the header claims, then every node except the
    /// proposer computes a different root, and no block on the chain can be verified by anyone.
    /// </summary>
    [Fact]
    public void A_block_decoded_from_the_wire_reaches_the_state_its_header_claims()
    {
        var (senderKey, sender) = NewAccount();
        var (_, recipient) = NewAccount();
        var proposer = new Address(Enumerable.Repeat((byte)0x77, 20).ToArray());

        var buildDb = Funded(sender);
        var parent = new BlockHeader
        {
            Number = 0,
            ParentHash = Hash256.Zero,
            StateRoot = buildDb.ComputeStateRoot(),
            TransactionsRoot = Hash256.Zero,
            ReceiptsRoot = Hash256.Zero,
            Timestamp = 1_700_000_000_000,
            Proposer = Address.Zero,
            ChainId = _chainParams.ChainId,
            GasUsed = 0,
            GasLimit = _chainParams.BlockGasLimit,
            BaseFee = _chainParams.InitialBaseFee,
        };

        var tx = Transaction.Sign(new Transaction
        {
            Type = TransactionType.Transfer,
            Nonce = 0,
            Sender = sender,
            To = recipient,
            Value = new UInt256(1_000),
            GasLimit = 21_000,
            GasPrice = _chainParams.InitialBaseFee * new UInt256(2),
            Data = [],
            ChainId = _chainParams.ChainId,
        }, senderKey);

        Block built = new BlockBuilder(_chainParams).BuildBlock([tx], buildDb, parent, proposer);
        built.Transactions.Should().ContainSingle("the transfer has to be included for this to mean anything");

        // The step the proposer skips and nobody else does.
        Block received = BlockCodec.DeserializeBlock(BlockCodec.SerializeBlock(built));

        received.Header.StateRoot.Should().Be(built.Header.StateRoot, "the header must survive the wire");
        received.Transactions.Should().HaveCount(built.Transactions.Count);

        var applyDb = Funded(sender);
        var executor = new TransactionExecutor(_chainParams);
        for (int i = 0; i < received.Transactions.Count; i++)
            executor.Execute(received.Transactions[i], applyDb, received.Header, i);

        applyDb.ComputeStateRoot().Should().Be(built.Header.StateRoot,
            "a node executing the decoded block must reach the state the proposer published");
    }

    /// <summary>
    /// Every field that changes what a transaction does has to survive the round trip.
    ///
    /// Checking the state root alone would pass on a codec that dropped a field the executor happens
    /// not to read yet, and fail confusingly later when it does.
    /// </summary>
    [Fact]
    public void A_transaction_is_unchanged_by_the_round_trip()
    {
        var (senderKey, sender) = NewAccount();
        var (_, recipient) = NewAccount();

        var tx = Transaction.Sign(new Transaction
        {
            Type = TransactionType.ContractCall,
            Nonce = 7,
            Sender = sender,
            To = recipient,
            Value = new UInt256(4_242),
            GasLimit = 500_000,
            GasPrice = new UInt256(73_900),
            Data = [1, 2, 3, 4, 5],
            Priority = 3,
            ChainId = _chainParams.ChainId,
        }, senderKey);

        Transaction back = BlockCodec.DeserializeTransaction(BlockCodec.SerializeTransaction(tx));

        back.Type.Should().Be(tx.Type);
        back.Nonce.Should().Be(tx.Nonce);
        back.Sender.Should().Be(tx.Sender);
        back.To.Should().Be(tx.To);
        back.Value.Should().Be(tx.Value);
        back.GasLimit.Should().Be(tx.GasLimit);
        back.GasPrice.Should().Be(tx.GasPrice);
        back.Data.ToArray().Should().Equal(tx.Data.ToArray());
        back.Priority.Should().Be(tx.Priority);
        back.ChainId.Should().Be(tx.ChainId);
        back.Hash.Should().Be(tx.Hash, "the hash is what a receipt and a lookup are keyed by");
    }
}
