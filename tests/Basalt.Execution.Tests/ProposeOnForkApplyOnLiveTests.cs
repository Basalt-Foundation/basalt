using Basalt.Core;
using Basalt.Crypto;
using Basalt.Storage;
using Basalt.Storage.Trie;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests;

/// <summary>
/// The shape a node actually runs, which none of the earlier tests had.
///
/// A proposer forks its live state, builds on the fork through BuildBlockWithDex, publishes the root
/// the fork reached, and throws the fork away. The block is then applied to the live state, and that is
/// the root every node checks itself against. Earlier tests called BuildBlock, never forked, and used a
/// bare TrieStateDb, so they exercised a combination that exists nowhere.
///
/// Live state here is FlatStateDb over TrieStateDb, as a node runs it. Its fork copies the account
/// cache, leaves the storage cache behind, and wraps the node store in an overlay, so the two sides
/// differ in bookkeeping while having to agree on the answer.
/// </summary>
public class ProposeOnForkApplyOnLiveTests
{
    private readonly ChainParameters _chainParams = ChainParameters.Devnet;

    private static (byte[] Key, Address Address) NewAccount()
    {
        var (privateKey, publicKey) = Ed25519Signer.GenerateKeyPair();
        return (privateKey, Ed25519Signer.DeriveAddress(publicKey));
    }

    private static FlatStateDb LiveState(params Address[] accounts)
    {
        var db = new FlatStateDb(new TrieStateDb(new InMemoryTrieNodeStore()));
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

    private BlockHeader Genesis(IStateDatabase db) => new()
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

    private Transaction Transfer(byte[] key, Address from, Address to, ulong nonce) =>
        Transaction.Sign(new Transaction
        {
            Type = TransactionType.Transfer,
            Nonce = nonce,
            Sender = from,
            To = to,
            Value = new UInt256(1_000),
            GasLimit = 21_000,
            GasPrice = _chainParams.InitialBaseFee * new UInt256(2),
            Data = [],
            ChainId = _chainParams.ChainId,
        }, key);

    /// <summary>
    /// Several blocks, each proposed on a fork and applied to the live state it came from.
    ///
    /// One block is not enough: the first fork is taken from a state nobody has touched, so both sides
    /// start even. What the testnet shows appears once the live state carries history, which is exactly
    /// what the earlier single-block tests could not produce.
    /// </summary>
    [Fact]
    public void The_root_a_fork_publishes_is_the_root_the_live_state_reaches()
    {
        var (senderKey, sender) = NewAccount();
        var (_, recipient) = NewAccount();
        var proposer = new Address(Enumerable.Repeat((byte)0x77, 20).ToArray());

        var live = LiveState(sender);
        var builder = new BlockBuilder(_chainParams);
        var executor = new TransactionExecutor(_chainParams);
        var parent = Genesis(live);

        for (ulong nonce = 0; nonce < 6; nonce++)
        {
            var tx = Transfer(senderKey, sender, recipient, nonce);

            // Propose: fork, build, publish the fork's root, discard the fork.
            var fork = live.Fork();
            Block block = builder.BuildBlockWithDex([tx], [], fork, parent, proposer);
            block.Transactions.Should().ContainSingle($"transfer {nonce} has to be included");

            // Apply: the same block, on the live state, which is what every node checks.
            for (int i = 0; i < block.Transactions.Count; i++)
                executor.Execute(block.Transactions[i], live, block.Header, i);

            live.ComputeStateRoot().Should().Be(block.Header.StateRoot,
                $"block {block.Header.Number} must apply to the state its header claims");

            parent = block.Header;
        }
    }


    private static Address NameService()
    {
        var b = new byte[20];
        b[18] = 0x10;
        b[19] = 0x02;
        return new Address(b);
    }

    private static byte[] RegisterCall(string label)
    {
        var args = new byte[64];
        var writer = new Basalt.Codec.BasaltWriter(args);
        writer.WriteString(label);

        var selector = Basalt.Sdk.Contracts.SelectorHelper.ComputeSelectorBytes("Register");
        var callData = new byte[selector.Length + writer.Position];
        selector.CopyTo(callData, 0);
        args.AsSpan(0, writer.Position).CopyTo(callData.AsSpan(selector.Length));
        return callData;
    }

    /// <summary>
    /// The same cycle, with the genesis system contracts present and a call that writes their storage.
    ///
    /// This is what the diverging block on the testnet held. A transfer moves balances in the world
    /// trie; a contract call moves a storage trie whose root is folded back into an account, and the
    /// fork carries a copied account cache while leaving the storage cache behind.
    /// </summary>
    [Fact]
    public void The_same_holds_for_a_call_into_a_genesis_contract()
    {
        var (senderKey, sender) = NewAccount();
        var proposer = new Address(Enumerable.Repeat((byte)0x77, 20).ToArray());

        var live = LiveState(sender);
        GenesisContractDeployer.DeployAll(live, _chainParams.ChainId);

        var builder = new BlockBuilder(_chainParams);
        var executor = new TransactionExecutor(_chainParams);
        var parent = Genesis(live);

        for (ulong nonce = 0; nonce < 4; nonce++)
        {
            var tx = Transaction.Sign(new Transaction
            {
                Type = TransactionType.ContractCall,
                Nonce = nonce,
                Sender = sender,
                To = NameService(),
                Value = new UInt256(1_000_000_000),
                GasLimit = 2_000_000,
                GasPrice = _chainParams.InitialBaseFee * new UInt256(2),
                Data = RegisterCall("name" + nonce),
                ChainId = _chainParams.ChainId,
            }, senderKey);

            var fork = live.Fork();
            Block block = builder.BuildBlockWithDex([tx], [], fork, parent, proposer);
            block.Transactions.Should().ContainSingle($"registration {nonce} has to be included");

            for (int i = 0; i < block.Transactions.Count; i++)
                executor.Execute(block.Transactions[i], live, block.Header, i);

            live.ComputeStateRoot().Should().Be(block.Header.StateRoot,
                $"block {block.Header.Number} touched contract storage and must still apply to its header");

            parent = block.Header;
        }
    }


    /// <summary>
    /// A batch of blocks applied together must reach the state they reach one at a time.
    ///
    /// Replaying history uses the batch path: execute every block on one fork, compute the root once at
    /// the end, then swap. Following the chain live uses the other, computing after each block. Since
    /// computing the root is also what folds storage roots into accounts, the two differ in how often
    /// that fold happens, and a node that replays must still arrive where a node that followed did.
    /// </summary>
    [Fact]
    public void Applying_blocks_as_a_batch_reaches_the_same_state_as_one_at_a_time()
    {
        var (senderKey, sender) = NewAccount();
        var proposer = new Address(Enumerable.Repeat((byte)0x77, 20).ToArray());

        // Build the blocks once, so both sides replay exactly the same history.
        var source = LiveState(sender);
        GenesisContractDeployer.DeployAll(source, _chainParams.ChainId);
        var builder = new BlockBuilder(_chainParams);
        var executor = new TransactionExecutor(_chainParams);
        var parent = Genesis(source);

        var blocks = new List<Block>();
        for (ulong nonce = 0; nonce < 5; nonce++)
        {
            var tx = Transaction.Sign(new Transaction
            {
                Type = TransactionType.ContractCall,
                Nonce = nonce,
                Sender = sender,
                To = NameService(),
                Value = new UInt256(1_000_000_000),
                GasLimit = 2_000_000,
                GasPrice = _chainParams.InitialBaseFee * new UInt256(2),
                Data = RegisterCall("batch" + nonce),
                ChainId = _chainParams.ChainId,
            }, senderKey);

            var fork = source.Fork();
            Block block = builder.BuildBlockWithDex([tx], [], fork, parent, proposer);
            block.Transactions.Should().ContainSingle();

            for (int i = 0; i < block.Transactions.Count; i++)
                executor.Execute(block.Transactions[i], source, block.Header, i);
            source.ComputeStateRoot();

            blocks.Add(block);
            parent = block.Header;
        }

        // One at a time, folding after each block, the way a node following the chain does.
        var live = LiveState(sender);
        GenesisContractDeployer.DeployAll(live, _chainParams.ChainId);
        foreach (var block in blocks)
        {
            for (int i = 0; i < block.Transactions.Count; i++)
                executor.Execute(block.Transactions[i], live, block.Header, i);
            live.ComputeStateRoot();
        }

        // As one batch, folding once at the end, the way replaying history does.
        var replay = LiveState(sender);
        GenesisContractDeployer.DeployAll(replay, _chainParams.ChainId);
        foreach (var block in blocks)
        {
            for (int i = 0; i < block.Transactions.Count; i++)
                executor.Execute(block.Transactions[i], replay, block.Header, i);

            // Folding per block, which is what BlockApplier.ExecuteBlock now does. Deferring it to the
            // end of the batch is what made replay land somewhere else.
            replay.ComputeStateRoot();
        }

        replay.ComputeStateRoot().Should().Be(live.ComputeStateRoot(),
            "a node replaying history has to arrive where a node that followed it live did");
    }
}
