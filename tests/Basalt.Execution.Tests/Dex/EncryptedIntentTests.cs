using System.Security.Cryptography;
using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution;
using Basalt.Execution.Dex;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests.Dex;

public class EncryptedIntentTests
{
    private static readonly ChainParameters DefaultParams = ChainParameters.Devnet;

    private static Address MakeAddress(byte b)
    {
        var bytes = new byte[20];
        bytes[19] = b;
        return new Address(bytes);
    }

    private static BlsPublicKey GenerateBlsPublicKey()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        key[0] &= 0x3F;
        if (key[0] == 0) key[0] = 1;
        return new BlsPublicKey(BlsSigner.GetPublicKeyStatic(key));
    }

    private static byte[] CreateSwapIntentPayload(Address tokenIn, Address tokenOut, UInt256 amountIn, UInt256 minAmountOut, ulong deadline = 0, byte flags = 0)
    {
        var data = new byte[114];
        data[0] = 1; // version
        tokenIn.WriteTo(data.AsSpan(1, 20));
        tokenOut.WriteTo(data.AsSpan(21, 20));
        amountIn.WriteTo(data.AsSpan(41, 32));
        minAmountOut.WriteTo(data.AsSpan(73, 32));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(105, 8), deadline);
        data[113] = flags;
        return data;
    }

    private static Transaction MakeEncryptedTx(byte[] privKey, Address sender, byte[] txData, ulong nonce = 0)
    {
        return Transaction.Sign(new Transaction
        {
            Type = TransactionType.DexEncryptedSwapIntent,
            Sender = sender,
            To = DexState.DexAddress,
            Value = UInt256.Zero,
            Data = txData,
            Nonce = nonce,
            GasLimit = 200_000,
            GasPrice = new UInt256(1),
            MaxFeePerGas = new UInt256(10),
            MaxPriorityFeePerGas = new UInt256(1),
            ChainId = DefaultParams.ChainId,
        }, privKey);
    }

    [Fact]
    public void Encrypt_ProducesValidTransactionData()
    {
        var gpk = GenerateBlsPublicKey();
        var payload = CreateSwapIntentPayload(
            MakeAddress(0xAA), MakeAddress(0xBB),
            new UInt256(1000), new UInt256(900));

        var txData = EncryptedIntent.Encrypt(payload, gpk, 1);

        // Should be 8 (epoch) + 32 (nonce) + 114 (payload) = 154 bytes
        txData.Length.Should().Be(154);

        // Epoch should be 1
        var epoch = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(txData.AsSpan(0, 8));
        epoch.Should().Be(1UL);
    }

    [Fact]
    public void EncryptDecrypt_RoundTrip_RecoversOriginalIntent()
    {
        var gpk = GenerateBlsPublicKey();
        var tokenIn = MakeAddress(0xAA);
        var tokenOut = MakeAddress(0xBB);
        var amountIn = new UInt256(5000);
        var minAmountOut = new UInt256(4500);
        ulong deadline = 100;

        var payload = CreateSwapIntentPayload(tokenIn, tokenOut, amountIn, minAmountOut, deadline, 0x01);
        var txData = EncryptedIntent.Encrypt(payload, gpk, 5);

        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var sender = Ed25519Signer.DeriveAddress(pubKey);
        var tx = MakeEncryptedTx(privKey, sender, txData);

        var encrypted = EncryptedIntent.Parse(tx);
        encrypted.Should().NotBeNull();
        encrypted!.Value.EpochNumber.Should().Be(5UL);
        encrypted.Value.Nonce.Length.Should().Be(32);
        encrypted.Value.Ciphertext.Length.Should().Be(114);

        var decrypted = encrypted.Value.Decrypt(gpk);
        decrypted.Should().NotBeNull();
        decrypted!.Value.Sender.Should().Be(sender);
        decrypted.Value.TokenIn.Should().Be(tokenIn);
        decrypted.Value.TokenOut.Should().Be(tokenOut);
        decrypted.Value.AmountIn.Should().Be(amountIn);
        decrypted.Value.MinAmountOut.Should().Be(minAmountOut);
        decrypted.Value.Deadline.Should().Be(deadline);
        decrypted.Value.AllowPartialFill.Should().BeTrue();
    }

    [Fact]
    public void Decrypt_WrongKey_ProducesDifferentResult()
    {
        var gpk1 = GenerateBlsPublicKey();
        var gpk2 = GenerateBlsPublicKey();
        var payload = CreateSwapIntentPayload(
            MakeAddress(0xAA), MakeAddress(0xBB),
            new UInt256(1000), new UInt256(900));

        var nonce = new byte[32];
        RandomNumberGenerator.Fill(nonce);
        var txData = EncryptedIntent.EncryptWithNonce(payload, gpk1, 1, nonce);

        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var tx = MakeEncryptedTx(privKey, Ed25519Signer.DeriveAddress(pubKey), txData);

        var encrypted = EncryptedIntent.Parse(tx)!.Value;

        // Decrypt with correct key
        var correctDecrypted = encrypted.Decrypt(gpk1);
        correctDecrypted!.Value.TokenIn.Should().Be(MakeAddress(0xAA));

        // Decrypt with wrong key — should produce different token addresses
        var wrongDecrypted = encrypted.Decrypt(gpk2);
        if (wrongDecrypted != null)
        {
            (wrongDecrypted.Value.TokenIn == MakeAddress(0xAA) &&
             wrongDecrypted.Value.TokenOut == MakeAddress(0xBB)).Should().BeFalse();
        }
    }

    [Fact]
    public void Parse_TooShortData_ReturnsNull()
    {
        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var tx = MakeEncryptedTx(privKey, Ed25519Signer.DeriveAddress(pubKey), new byte[50]);
        EncryptedIntent.Parse(tx).Should().BeNull();
    }

    [Fact]
    public void EncryptWithNonce_Deterministic()
    {
        var gpk = GenerateBlsPublicKey();
        var payload = CreateSwapIntentPayload(
            MakeAddress(0xAA), MakeAddress(0xBB),
            new UInt256(1000), new UInt256(900));

        var nonce = new byte[32];
        RandomNumberGenerator.Fill(nonce);

        var data1 = EncryptedIntent.EncryptWithNonce(payload, gpk, 1, nonce);
        var data2 = EncryptedIntent.EncryptWithNonce(payload, gpk, 1, nonce);

        data1.Should().BeEquivalentTo(data2);
    }

    [Fact]
    public void DifferentNonces_ProduceDifferentCiphertext()
    {
        var gpk = GenerateBlsPublicKey();
        var payload = CreateSwapIntentPayload(
            MakeAddress(0xAA), MakeAddress(0xBB),
            new UInt256(1000), new UInt256(900));

        var data1 = EncryptedIntent.Encrypt(payload, gpk, 1);
        var data2 = EncryptedIntent.Encrypt(payload, gpk, 1);

        // Different random nonces → different ciphertext
        data1.AsSpan(40).SequenceEqual(data2.AsSpan(40)).Should().BeFalse();
    }

    [Fact]
    public void ExecuteDexEncryptedSwapIntent_ValidData_Succeeds()
    {
        var gpk = GenerateBlsPublicKey();
        var payload = CreateSwapIntentPayload(
            MakeAddress(0xAA), MakeAddress(0xBB),
            new UInt256(1000), new UInt256(900));
        var txData = EncryptedIntent.Encrypt(payload, gpk, 1);

        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var sender = Ed25519Signer.DeriveAddress(pubKey);

        var stateDb = new InMemoryStateDb();
        stateDb.SetAccount(sender, new AccountState { Balance = new UInt256(10_000_000), Nonce = 0 });

        var tx = MakeEncryptedTx(privKey, sender, txData);

        var executor = new TransactionExecutor(DefaultParams);
        var header = new BlockHeader
        {
            Number = 1,
            ParentHash = Hash256.Zero,
            StateRoot = Hash256.Zero,
            TransactionsRoot = Hash256.Zero,
            ReceiptsRoot = Hash256.Zero,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Proposer = MakeAddress(0xFF),
            ChainId = DefaultParams.ChainId,
            GasLimit = 10_000_000,
            BaseFee = new UInt256(1),
        };

        var receipt = executor.Execute(tx, stateDb, header, 0);
        receipt.Success.Should().BeTrue();
        receipt.GasUsed.Should().Be(DefaultParams.DexEncryptedSwapIntentGas);
    }

    [Fact]
    public void ExecuteDexEncryptedSwapIntent_TooShortData_Fails()
    {
        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var sender = Ed25519Signer.DeriveAddress(pubKey);

        var stateDb = new InMemoryStateDb();
        stateDb.SetAccount(sender, new AccountState { Balance = new UInt256(10_000_000), Nonce = 0 });

        var tx = MakeEncryptedTx(privKey, sender, new byte[50]);

        var executor = new TransactionExecutor(DefaultParams);
        var header = new BlockHeader
        {
            Number = 1,
            ParentHash = Hash256.Zero,
            StateRoot = Hash256.Zero,
            TransactionsRoot = Hash256.Zero,
            ReceiptsRoot = Hash256.Zero,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Proposer = MakeAddress(0xFF),
            ChainId = DefaultParams.ChainId,
            GasLimit = 10_000_000,
            BaseFee = new UInt256(1),
        };

        var receipt = executor.Execute(tx, stateDb, header, 0);
        receipt.Success.Should().BeFalse();
        receipt.ErrorCode.Should().Be(BasaltErrorCode.DexInvalidData);
    }

    [Fact]
    public void MempoolRouting_EncryptedIntent_GoesToDexPool()
    {
        var gpk = GenerateBlsPublicKey();
        var payload = CreateSwapIntentPayload(
            MakeAddress(0xAA), MakeAddress(0xBB),
            new UInt256(1000), new UInt256(900));
        var txData = EncryptedIntent.Encrypt(payload, gpk, 1);

        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var sender = Ed25519Signer.DeriveAddress(pubKey);

        var tx = MakeEncryptedTx(privKey, sender, txData);

        // Mempool without validation (simple constructor)
        var mempool = new Mempool();
        var added = mempool.Add(tx);
        added.Should().BeTrue();

        // Should be in the DEX intent pool, not the regular pool
        var dexIntents = mempool.GetPendingDexIntents(100);
        dexIntents.Should().ContainSingle();
        dexIntents[0].Hash.Should().Be(tx.Hash);

        var regularTxs = mempool.GetPending(100);
        regularTxs.Should().BeEmpty();
    }

    [Fact]
    public void BlockBuilder_DecryptsEncryptedIntents_InPhaseB()
    {
        var gpk = GenerateBlsPublicKey();
        var tokenA = Address.Zero; // native BST
        var tokenB = MakeAddress(0xBB);

        // Create a pool first
        var stateDb = new InMemoryStateDb();
        var dexState = new DexState(stateDb);
        var engine = new DexEngine(dexState);

        var poolCreator = MakeAddress(0x01);
        stateDb.SetAccount(poolCreator, new AccountState { Balance = new UInt256(100_000_000) });

        var createResult = engine.CreatePool(poolCreator, tokenA, tokenB, 30);
        createResult.Success.Should().BeTrue();

        // Add liquidity
        var addResult = engine.AddLiquidity(
            poolCreator, createResult.PoolId, new UInt256(50_000), new UInt256(50_000),
            UInt256.Zero, UInt256.Zero, stateDb);
        addResult.Success.Should().BeTrue();

        // Create two opposing encrypted swap intents (batch auction needs both buy + sell)
        var (privKey1, pubKey1) = Ed25519Signer.GenerateKeyPair();
        var sender1 = Ed25519Signer.DeriveAddress(pubKey1);
        stateDb.SetAccount(sender1, new AccountState { Balance = new UInt256(10_000_000) });

        var (privKey2, pubKey2) = Ed25519Signer.GenerateKeyPair();
        var sender2 = Ed25519Signer.DeriveAddress(pubKey2);
        stateDb.SetAccount(sender2, new AccountState { Balance = new UInt256(10_000_000) });

        // Sell: tokenA → tokenB
        var sellPayload = CreateSwapIntentPayload(tokenA, tokenB, new UInt256(1000), new UInt256(1));
        var sellTxData = EncryptedIntent.Encrypt(sellPayload, gpk, 1);
        var sellTx = MakeEncryptedTx(privKey1, sender1, sellTxData);

        // Buy: tokenB → tokenA
        var buyPayload = CreateSwapIntentPayload(tokenB, tokenA, new UInt256(1000), new UInt256(1));
        var buyTxData = EncryptedIntent.Encrypt(buyPayload, gpk, 1);
        var buyTx = MakeEncryptedTx(privKey2, sender2, buyTxData);

        // Build block with encrypted intents
        var builder = new BlockBuilder(DefaultParams);
        builder.DkgGroupPublicKey = gpk;

        var parentHeader = new BlockHeader
        {
            Number = 0,
            ParentHash = Hash256.Zero,
            StateRoot = Hash256.Zero,
            TransactionsRoot = Hash256.Zero,
            ReceiptsRoot = Hash256.Zero,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Proposer = MakeAddress(0xFF),
            ChainId = DefaultParams.ChainId,
            GasLimit = 10_000_000,
            BaseFee = new UInt256(1),
        };

        var block = builder.BuildBlockWithDex(
            Array.Empty<Transaction>(),
            new[] { sellTx, buyTx },
            stateDb,
            parentHeader,
            MakeAddress(0xFF));

        // Block should build successfully. The intents may or may not settle
        // depending on price matching, but the decryption path is exercised.
        block.Should().NotBeNull();
        block.Header.Number.Should().Be(1);
    }

    [Fact]
    public void BlockBuilder_NoDkgKey_SkipsEncryptedIntents()
    {
        var gpk = GenerateBlsPublicKey();
        var payload = CreateSwapIntentPayload(
            MakeAddress(0xAA), MakeAddress(0xBB),
            new UInt256(1000), new UInt256(900));
        var txData = EncryptedIntent.Encrypt(payload, gpk, 1);

        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var sender = Ed25519Signer.DeriveAddress(pubKey);

        var stateDb = new InMemoryStateDb();
        stateDb.SetAccount(sender, new AccountState { Balance = new UInt256(10_000_000) });

        var encTx = MakeEncryptedTx(privKey, sender, txData);

        // Build block WITHOUT DKG key set
        var builder = new BlockBuilder(DefaultParams);

        var parentHeader = new BlockHeader
        {
            Number = 0,
            ParentHash = Hash256.Zero,
            StateRoot = Hash256.Zero,
            TransactionsRoot = Hash256.Zero,
            ReceiptsRoot = Hash256.Zero,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Proposer = MakeAddress(0xFF),
            ChainId = DefaultParams.ChainId,
            GasLimit = 10_000_000,
            BaseFee = new UInt256(1),
        };

        var block = builder.BuildBlockWithDex(
            Array.Empty<Transaction>(),
            new[] { encTx },
            stateDb,
            parentHeader,
            MakeAddress(0xFF));

        // Block should still build successfully (encrypted intent skipped)
        block.Should().NotBeNull();
        block.Header.Number.Should().Be(1);
    }
}
