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

    /// <summary>
    /// Generate a DKG keypair (secret scalar + group public key in G1).
    /// </summary>
    private static (byte[] SecretKey, BlsPublicKey PublicKey) GenerateDkgKeyPair()
    {
        var sk = new byte[32];
        RandomNumberGenerator.Fill(sk);
        sk[0] &= 0x3F;
        if (sk[0] == 0 && sk[1] == 0) sk[1] = 1;
        var pkBytes = BlsSigner.GetPublicKeyStatic(sk);
        return (sk, new BlsPublicKey(pkBytes));
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
        var (_, gpk) = GenerateDkgKeyPair();
        var payload = CreateSwapIntentPayload(
            MakeAddress(0xAA), MakeAddress(0xBB),
            new UInt256(1000), new UInt256(900));

        var txData = EncryptedIntent.Encrypt(payload, gpk, 1);

        // Should be 8 (epoch) + 48 (C1) + 12 (GCM nonce) + 114 (payload) + 16 (tag) = 198 bytes
        txData.Length.Should().Be(198);

        // Epoch should be 1
        var epoch = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(txData.AsSpan(0, 8));
        epoch.Should().Be(1UL);
    }

    [Fact]
    public void EncryptDecrypt_RoundTrip_RecoversOriginalIntent()
    {
        var (sk, gpk) = GenerateDkgKeyPair();
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
        encrypted.Value.EphemeralKey.Length.Should().Be(48);
        encrypted.Value.GcmNonce.Length.Should().Be(12);
        encrypted.Value.GcmTag.Length.Should().Be(16);
        encrypted.Value.Ciphertext.Length.Should().Be(114);

        var decrypted = encrypted.Value.Decrypt(sk);
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
    public void Decrypt_WrongKey_ReturnsNull_DueToAuthFailure()
    {
        var (sk1, gpk1) = GenerateDkgKeyPair();
        var (sk2, _) = GenerateDkgKeyPair();

        var payload = CreateSwapIntentPayload(
            MakeAddress(0xAA), MakeAddress(0xBB),
            new UInt256(1000), new UInt256(900));

        var txData = EncryptedIntent.Encrypt(payload, gpk1, 1);

        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var tx = MakeEncryptedTx(privKey, Ed25519Signer.DeriveAddress(pubKey), txData);

        var encrypted = EncryptedIntent.Parse(tx)!.Value;

        // Decrypt with correct key succeeds
        var correctDecrypted = encrypted.Decrypt(sk1);
        correctDecrypted.Should().NotBeNull();
        correctDecrypted!.Value.TokenIn.Should().Be(MakeAddress(0xAA));

        // Decrypt with wrong key fails (AES-GCM authentication rejects)
        var wrongDecrypted = encrypted.Decrypt(sk2);
        wrongDecrypted.Should().BeNull();
    }

    [Fact]
    public void Parse_TooShortData_ReturnsNull()
    {
        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var tx = MakeEncryptedTx(privKey, Ed25519Signer.DeriveAddress(pubKey), new byte[50]);
        EncryptedIntent.Parse(tx).Should().BeNull();
    }

    [Fact]
    public void EncryptWithScalar_Deterministic()
    {
        var (_, gpk) = GenerateDkgKeyPair();
        var payload = CreateSwapIntentPayload(
            MakeAddress(0xAA), MakeAddress(0xBB),
            new UInt256(1000), new UInt256(900));

        var rScalar = new byte[32];
        RandomNumberGenerator.Fill(rScalar);
        rScalar[0] &= 0x3F;
        if (rScalar[0] == 0) rScalar[0] = 1;

        var rScalar2 = (byte[])rScalar.Clone(); // L-12: EncryptWithScalar zeros the scalar
        var data1 = EncryptedIntent.EncryptWithScalar(payload, gpk, 1, rScalar);
        var data2 = EncryptedIntent.EncryptWithScalar(payload, gpk, 1, rScalar2);

        // Ephemeral key (C1) should be the same
        data1.AsSpan(8, 48).SequenceEqual(data2.AsSpan(8, 48)).Should().BeTrue();
        // Note: GCM nonce is randomly generated each time, so full data differs
    }

    [Fact]
    public void DifferentScalars_ProduceDifferentEphemeralKeys()
    {
        var (_, gpk) = GenerateDkgKeyPair();
        var payload = CreateSwapIntentPayload(
            MakeAddress(0xAA), MakeAddress(0xBB),
            new UInt256(1000), new UInt256(900));

        var data1 = EncryptedIntent.Encrypt(payload, gpk, 1);
        var data2 = EncryptedIntent.Encrypt(payload, gpk, 1);

        // Different random scalars → different ephemeral keys (C1)
        data1.AsSpan(8, 48).SequenceEqual(data2.AsSpan(8, 48)).Should().BeFalse();
    }

    [Fact]
    public void AesGcm_TamperedCiphertext_DecryptionFails()
    {
        var (sk, gpk) = GenerateDkgKeyPair();
        var payload = CreateSwapIntentPayload(
            MakeAddress(0xAA), MakeAddress(0xBB),
            new UInt256(1000), new UInt256(900));

        var txData = EncryptedIntent.Encrypt(payload, gpk, 1);

        // Tamper with ciphertext (flip a byte in the encrypted payload area)
        txData[EncryptedIntent.MinDataLength - 20] ^= 0xFF;

        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var tx = MakeEncryptedTx(privKey, Ed25519Signer.DeriveAddress(pubKey), txData);

        var encrypted = EncryptedIntent.Parse(tx);
        encrypted.Should().NotBeNull();

        // Decryption should fail due to GCM authentication
        var decrypted = encrypted!.Value.Decrypt(sk);
        decrypted.Should().BeNull();
    }

    [Fact]
    public void ExecuteDexEncryptedSwapIntent_ValidData_Succeeds()
    {
        var (_, gpk) = GenerateDkgKeyPair();
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
        var (_, gpk) = GenerateDkgKeyPair();
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
        var (sk, gpk) = GenerateDkgKeyPair();
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

        // Build block with encrypted intents — using DkgGroupSecretKey
        var builder = new BlockBuilder(DefaultParams);
        builder.DkgGroupPublicKey = gpk;
        builder.DkgGroupSecretKey = sk;

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
        var (_, gpk) = GenerateDkgKeyPair();
        var payload = CreateSwapIntentPayload(
            MakeAddress(0xAA), MakeAddress(0xBB),
            new UInt256(1000), new UInt256(900));
        var txData = EncryptedIntent.Encrypt(payload, gpk, 1);

        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var sender = Ed25519Signer.DeriveAddress(pubKey);

        var stateDb = new InMemoryStateDb();
        stateDb.SetAccount(sender, new AccountState { Balance = new UInt256(10_000_000) });

        var encTx = MakeEncryptedTx(privKey, sender, txData);

        // Build block WITHOUT DKG secret key set
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
