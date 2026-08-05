using Basalt.Core;
using Basalt.Crypto;
using Basalt.Storage;
using Basalt.Storage.Trie;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests;

/// <summary>
/// Whether proposing a block and applying it produce the same state.
///
/// They must, and on the testnet they did not: every node that applied a block computed one root while
/// the proposer's header claimed another. The nodes agreed with each other and disagreed with the
/// header, which is the signature of the two paths differing rather than of one node being broken.
/// </summary>
public class BuildVersusApplyTests
{
    private readonly ChainParameters _chainParams = ChainParameters.Devnet;

    private static (byte[] Key, Address Address) NewAccount()
    {
        var (privateKey, publicKey) = Ed25519Signer.GenerateKeyPair();
        return (privateKey, Ed25519Signer.DeriveAddress(publicKey));
    }

    /// <summary>
    /// A trie-backed state, not InMemoryStateDb.
    ///
    /// InMemoryStateDb computes a root over account records only, and says so: SetStorage does not touch
    /// StorageRoot, so contract storage is invisible to it. A test written against it cannot see a
    /// divergence that lives in contract storage, which is most of them.
    /// </summary>
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

    private BlockHeader Parent(TrieStateDb db) => new()
    {
        Number = 0,
        ParentHash = Hash256.Zero,
        StateRoot = db.ComputeStateRoot(),
        TransactionsRoot = Hash256.Zero,
        ReceiptsRoot = Hash256.Zero,
        Timestamp = 1_700_000_000_000,
        Proposer = Address.Zero,
        ChainId = _chainParams.ChainId,
        GasUsed = 0,
        GasLimit = _chainParams.BlockGasLimit,
        BaseFee = _chainParams.InitialBaseFee,
    };

    /// <summary>
    /// One plain transfer, proposed and then applied on an identical starting state.
    ///
    /// The header a proposer publishes is what every other node checks itself against, so if the two
    /// differ here then the root in every block on the chain is wrong, and a node that enforces it
    /// cannot follow.
    /// </summary>
    [Fact]
    public void A_proposed_block_applies_to_the_state_its_header_claims()
    {
        var (senderKey, sender) = NewAccount();
        var (_, recipient) = NewAccount();

        var proposer = new Address(Enumerable.Repeat((byte)0x77, 20).ToArray());
        var buildDb = Funded(sender);
        var parent = Parent(buildDb);

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

        var builder = new BlockBuilder(_chainParams);
        Block block = builder.BuildBlock([tx], buildDb, parent, proposer);
        block.Transactions.Should().ContainSingle("the transfer has to be included for this to mean anything");

        // A second node receives that block and applies it to the same starting state, the way every
        // node other than the proposer arrives at its own view of the chain.
        var applyDb = Funded(sender);
        var executor = new TransactionExecutor(_chainParams);
        for (int i = 0; i < block.Transactions.Count; i++)
            executor.Execute(block.Transactions[i], applyDb, block.Header, i);

        Hash256 applied = applyDb.ComputeStateRoot();

        applied.Should().Be(block.Header.StateRoot,
            "a node that applies the block must reach the state the proposer published");
    }


    /// <summary>
    /// The same block, applied the way a node actually applies one.
    ///
    /// BlockApplier does not only run the transactions: it also calls ApplyDexSettlement on every block,
    /// and BuildBlock never does. That asymmetry is invisible while it changes nothing, and a consensus
    /// divergence the moment it changes anything at all.
    /// </summary>
    [Fact]
    public void Settlement_on_the_apply_path_does_not_move_the_state_the_proposer_published()
    {
        var (senderKey, sender) = NewAccount();
        var (_, recipient) = NewAccount();

        var proposer = new Address(Enumerable.Repeat((byte)0x77, 20).ToArray());
        var buildDb = Funded(sender);
        var parent = Parent(buildDb);

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

        var builder = new BlockBuilder(_chainParams);
        Block block = builder.BuildBlock([tx], buildDb, parent, proposer);

        var applyDb = Funded(sender);
        var executor = new TransactionExecutor(_chainParams);
        for (int i = 0; i < block.Transactions.Count; i++)
            executor.Execute(block.Transactions[i], applyDb, block.Header, i);

        // The step BlockApplier adds and BuildBlock omits.
        builder.ApplyDexSettlement(applyDb, block.Header);

        applyDb.ComputeStateRoot().Should().Be(block.Header.StateRoot,
            "settlement runs on every applied block, so whatever it touches has to be in the proposed root too");
    }


    private static byte[] TokenManifest()
    {
        var buffer = new byte[256];
        var writer = new Basalt.Codec.BasaltWriter(buffer);
        writer.WriteString("Token");
        writer.WriteString("TKN");
        writer.WriteByte(18);
        writer.WriteUInt256(new UInt256(1_000_000));
        return VM.ContractRegistry.BuildManifest(0x0001, buffer[..writer.Position]);
    }

    /// <summary>
    /// The case a transfer cannot reach: a transaction that writes contract storage.
    ///
    /// Both blocks that diverged on the testnet carried one. Account balances live in the world trie and
    /// contract storage lives in a trie of its own whose root is folded into the account, so a path that
    /// folds it at a different moment produces a different world root while holding the same data.
    /// </summary>
    [Fact]
    public void A_block_that_writes_contract_storage_applies_to_the_state_its_header_claims()
    {
        var (senderKey, sender) = NewAccount();
        var proposer = new Address(Enumerable.Repeat((byte)0x77, 20).ToArray());

        var buildDb = Funded(sender);
        var parent = Parent(buildDb);

        var tx = Transaction.Sign(new Transaction
        {
            Type = TransactionType.ContractDeploy,
            Nonce = 0,
            Sender = sender,
            To = Address.Zero,
            Value = UInt256.Zero,
            GasLimit = 5_000_000,
            GasPrice = _chainParams.InitialBaseFee * new UInt256(2),
            Data = TokenManifest(),
            ChainId = _chainParams.ChainId,
        }, senderKey);

        var builder = new BlockBuilder(_chainParams);
        Block block = builder.BuildBlock([tx], buildDb, parent, proposer);
        block.Transactions.Should().ContainSingle("the deploy has to be included for this to mean anything");

        var applyDb = Funded(sender);
        var executor = new TransactionExecutor(_chainParams);
        for (int i = 0; i < block.Transactions.Count; i++)
            executor.Execute(block.Transactions[i], applyDb, block.Header, i);

        applyDb.ComputeStateRoot().Should().Be(block.Header.StateRoot,
            "a node applying a block that touched contract storage must reach the published state");
    }
}
