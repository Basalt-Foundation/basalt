using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution;
using Basalt.Storage;

namespace Basalt.Benchmarks;

/// <summary>
/// Benchmarks for transaction creation, signing, verification, and block production TPS.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class TransactionBenchmarks
{
    private byte[] _privateKey = null!;
    private PublicKey _publicKey;
    private Transaction _signedTx = null!;
    private Address _sender;
    private Address _recipient;

    [GlobalSetup]
    public void Setup()
    {
        (_privateKey, _publicKey) = Ed25519Signer.GenerateKeyPair();
        _sender = Ed25519Signer.DeriveAddress(_publicKey);
        _recipient = Address.FromHexString("0x0000000000000000000000000000000000000002");

        _signedTx = Transaction.Sign(new Transaction
        {
            Type = TransactionType.Transfer,
            Nonce = 1,
            Sender = _sender,
            To = _recipient,
            Value = new UInt256(1000),
            GasLimit = 21_000,
            GasPrice = new UInt256(1),
            Data = [],
            Priority = 0,
            ChainId = 31337,
        }, _privateKey);
    }

    [Benchmark]
    public Transaction Tx_Sign()
    {
        return Transaction.Sign(new Transaction
        {
            Type = TransactionType.Transfer,
            Nonce = 1,
            Sender = _sender,
            To = _recipient,
            Value = new UInt256(1000),
            GasLimit = 21_000,
            GasPrice = new UInt256(1),
            Data = [],
            Priority = 0,
            ChainId = 31337,
        }, _privateKey);
    }

    [Benchmark]
    public bool Tx_Verify() => _signedTx.VerifySignature();

    [Benchmark]
    public Hash256 Tx_Hash() => _signedTx.Hash;
}

/// <summary>
/// Macro benchmark: measure sustained TPS for transfer execution.
/// Not a BenchmarkDotNet benchmark — run directly via --filter *TpsMacro*.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 3)]
public class TpsMacroBenchmark
{
    private Transaction[] _transactions = null!;
    private ChainParameters _chainParams = null!;

    [Params(10_000, 100_000)]
    public int TxCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _chainParams = ChainParameters.Devnet;

        // Pre-generate signed transactions from multiple accounts
        var accountCount = 100;
        var accounts = new (byte[] PrivateKey, PublicKey PublicKey, Address Address)[accountCount];
        for (int i = 0; i < accountCount; i++)
        {
            var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
            accounts[i] = (privKey, pubKey, Ed25519Signer.DeriveAddress(pubKey));
        }

        var recipient = Address.FromHexString("0x0000000000000000000000000000000000000099");

        _transactions = new Transaction[TxCount];
        for (int i = 0; i < TxCount; i++)
        {
            var acct = accounts[i % accountCount];
            var nonce = (ulong)(i / accountCount);
            _transactions[i] = Transaction.Sign(new Transaction
            {
                Type = TransactionType.Transfer,
                Nonce = nonce,
                Sender = acct.Address,
                To = recipient,
                Value = new UInt256(1),
                GasLimit = 21_000,
                GasPrice = new UInt256(1),
                Data = [],
                Priority = 0,
                ChainId = 31337,
                SenderPublicKey = acct.PublicKey,
            }, acct.PrivateKey);
        }
    }

    [Benchmark]
    public int Execute_Transfers()
    {
        // Set up a fresh state
        var stateDb = new InMemoryStateDb();
        var chainManager = new ChainManager();
        var blockBuilder = new BlockBuilder(_chainParams);

        // Fund all unique senders
        var funded = new HashSet<Address>();
        foreach (var tx in _transactions)
        {
            if (funded.Add(tx.Sender))
            {
                stateDb.SetAccount(tx.Sender, new AccountState
                {
                    Balance = UInt256.Parse("1000000000000000000000000"),
                    Nonce = 0,
                    AccountType = AccountType.ExternallyOwned,
                });
            }
        }

        // Fund recipient
        stateDb.SetAccount(
            Address.FromHexString("0x0000000000000000000000000000000000000099"),
            new AccountState
            {
                Balance = UInt256.Zero,
                Nonce = 0,
                AccountType = AccountType.ExternallyOwned,
            });

        // Create genesis
        var genesis = chainManager.CreateGenesisBlock(_chainParams);

        // Build blocks of max 10k txs each
        int processed = 0;
        int batchSize = (int)_chainParams.MaxTransactionsPerBlock;
        var parent = genesis;

        while (processed < _transactions.Length)
        {
            var batch = _transactions.Skip(processed).Take(batchSize).ToList();
            var block = blockBuilder.BuildBlock(batch, stateDb, parent.Header,
                Address.FromHexString("0x0000000000000000000000000000000000000001"));
            chainManager.AddBlock(block);
            parent = block;
            processed += batch.Count;
        }

        return processed;
    }
}
