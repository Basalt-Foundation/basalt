using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution.Dex;
using Basalt.Execution.Dex.Math;
using Basalt.Execution.VM;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests.Dex;

/// <summary>
/// Integration tests for mainnet readiness fixes: M-3, M-5, M-6, M-7, C-1.
/// Validates failure receipt generation, expired order cleanup, mempool rejection,
/// and BST-20 transfer failure propagation.
/// </summary>
public class MainnetReadinessTests
{
    private readonly InMemoryStateDb _stateDb = new();
    private readonly ChainParameters _chainParams = ChainParameters.Devnet;

    private static readonly byte[] AliceKey;
    private static readonly PublicKey AlicePub;
    private static readonly Address Alice;

    private static readonly byte[] BobKey;
    private static readonly PublicKey BobPub;
    private static readonly Address Bob;

    static MainnetReadinessTests()
    {
        (AliceKey, AlicePub) = Ed25519Signer.GenerateKeyPair();
        Alice = Ed25519Signer.DeriveAddress(AlicePub);
        (BobKey, BobPub) = Ed25519Signer.GenerateKeyPair();
        Bob = Ed25519Signer.DeriveAddress(BobPub);
    }

    public MainnetReadinessTests()
    {
        GenesisContractDeployer.DeployAll(_stateDb, _chainParams.ChainId);
        _stateDb.SetAccount(Alice, new AccountState { Balance = new UInt256(1_000_000_000) });
        _stateDb.SetAccount(Bob, new AccountState { Balance = new UInt256(1_000_000_000) });
    }

    private static Address MakeAddress(byte id)
    {
        var bytes = new byte[20];
        bytes[19] = id;
        return new Address(bytes);
    }

    // ────────── M-3: Failure receipt for unparseable intent in BatchSettlementExecutor ──────────

    [Fact]
    public void M3_UnparseableIntent_GeneratesFailureReceipt()
    {
        var dexState = new DexState(_stateDb);
        dexState.CreatePool(Address.Zero, MakeAddress(0xAA), 30);
        dexState.SetPoolReserves(0, new PoolReserves
        {
            Reserve0 = new UInt256(1_000_000),
            Reserve1 = new UInt256(1_000_000),
            TotalSupply = new UInt256(1_000_000),
            KLast = UInt256.Zero,
        });

        // Create a fill that references a tx with invalid (too short) intent data
        var badTx = new Transaction
        {
            Sender = Alice,
            To = DexState.DexAddress,
            Type = TransactionType.DexSwapIntent,
            Data = new byte[10], // Too short to parse as intent (needs 114 bytes)
            GasPrice = UInt256.One,
            GasLimit = 100_000,
            Nonce = 0,
            ChainId = _chainParams.ChainId,
        };
        badTx = Transaction.Sign(badTx, AliceKey);

        var batchResult = new BatchResult
        {
            PoolId = 0,
            ClearingPrice = new UInt256(1_000_000),
            TotalVolume0 = new UInt256(1_000),
            TotalVolume1 = new UInt256(1_000),
            AmmVolume = UInt256.Zero,
            Fills =
            [
                new FillRecord
                {
                    Participant = Alice,
                    AmountIn = new UInt256(1_000),
                    AmountOut = new UInt256(900),
                    IsLimitOrder = false,
                    IsBuy = true,
                    TxHash = badTx.Hash,
                }
            ],
            UpdatedReserves = new PoolReserves
            {
                Reserve0 = new UInt256(1_000_000),
                Reserve1 = new UInt256(1_000_000),
                TotalSupply = new UInt256(1_000_000),
                KLast = UInt256.Zero,
            },
        };

        var header = MakeHeader(1);
        var intentTxMap = new Dictionary<Hash256, Transaction> { { badTx.Hash, badTx } };

        var receipts = BatchSettlementExecutor.ExecuteSettlement(
            batchResult, _stateDb, dexState, header, intentTxMap, null, _chainParams);

        receipts.Should().HaveCount(1);
        receipts[0].Success.Should().BeFalse("unparseable intent should generate failure receipt");
        receipts[0].ErrorCode.Should().Be(BasaltErrorCode.DexInvalidData);
        receipts[0].TransactionHash.Should().Be(badTx.Hash);
        receipts[0].GasUsed.Should().Be(_chainParams.DexSwapGas);
    }

    // ────────── M-5: Expired order cleanup during block building ──────────

    [Fact]
    public void M5_ExpiredOrdersCleanedUp_DuringBlockBuilding()
    {
        var dexState = new DexState(_stateDb);

        // Create pool and add liquidity
        var engine = new DexEngine(dexState);
        engine.CreatePool(Alice, Address.Zero, MakeAddress(0xAA), 30);
        engine.AddLiquidity(Alice, 0,
            new UInt256(1_000_000), new UInt256(1_000_000),
            UInt256.Zero, UInt256.Zero, _stateDb);

        // Place an order that expires at block 5
        engine.PlaceOrder(Alice, 0,
            new UInt256(1_000_000), new UInt256(500), true, expiryBlock: 5, _stateDb);

        // Verify order exists
        var order = dexState.GetOrder(0);
        order.Should().NotBeNull("order should exist before cleanup");

        // Run cleanup at block 10 (past expiry)
        var cleaned = OrderBook.CleanupExpiredOrders(dexState, _stateDb, 0, currentBlock: 10);
        cleaned.Should().Be(1);

        // Order should be deleted
        var orderAfter = dexState.GetOrder(0);
        orderAfter.Should().BeNull("expired order should be deleted");
    }

    [Fact]
    public void M5_NonExpiredOrders_NotCleaned()
    {
        var dexState = new DexState(_stateDb);
        var engine = new DexEngine(dexState);
        engine.CreatePool(Alice, Address.Zero, MakeAddress(0xAA), 30);
        engine.AddLiquidity(Alice, 0,
            new UInt256(1_000_000), new UInt256(1_000_000),
            UInt256.Zero, UInt256.Zero, _stateDb);

        // Place an order that expires at block 100
        engine.PlaceOrder(Alice, 0,
            new UInt256(1_000_000), new UInt256(500), true, expiryBlock: 100, _stateDb);

        // Run cleanup at block 5 (before expiry)
        var cleaned = OrderBook.CleanupExpiredOrders(dexState, _stateDb, 0, currentBlock: 5);
        cleaned.Should().Be(0);

        var order = dexState.GetOrder(0);
        order.Should().NotBeNull("non-expired order should still exist");
    }

    [Fact]
    public void M5_NoExpiryOrder_NeverCleaned()
    {
        var dexState = new DexState(_stateDb);
        var engine = new DexEngine(dexState);
        engine.CreatePool(Alice, Address.Zero, MakeAddress(0xAA), 30);
        engine.AddLiquidity(Alice, 0,
            new UInt256(1_000_000), new UInt256(1_000_000),
            UInt256.Zero, UInt256.Zero, _stateDb);

        // Place an order with no expiry (expiryBlock = 0)
        engine.PlaceOrder(Alice, 0,
            new UInt256(1_000_000), new UInt256(500), true, expiryBlock: 0, _stateDb);

        var cleaned = OrderBook.CleanupExpiredOrders(dexState, _stateDb, 0, currentBlock: 999_999);
        cleaned.Should().Be(0, "orders with no expiry should never be cleaned");
    }

    // ────────── M-6: Failure receipts for unsettled intents in BlockBuilder ──────────

    [Fact]
    public void M6_UnsettledIntents_GenerateFailureReceipts()
    {
        var executor = new TransactionExecutor(_chainParams);
        var blockBuilder = new BlockBuilder(_chainParams, executor);

        // Create pool with liquidity
        var dexState = new DexState(_stateDb);
        var engine = new DexEngine(dexState);
        engine.CreatePool(Alice, Address.Zero, MakeAddress(0xAA), 30);

        // Don't add liquidity — pool has zero reserves.
        // Intents targeting this pool should fail to settle.

        // Create a valid intent tx
        var intentData = BuildIntentData(
            tokenIn: MakeAddress(0xAA),
            tokenOut: Address.Zero,
            amountIn: new UInt256(1_000),
            minAmountOut: new UInt256(900),
            deadline: 0);

        var intentTx = new Transaction
        {
            Sender = Alice,
            To = DexState.DexAddress,
            Type = TransactionType.DexSwapIntent,
            Data = intentData,
            GasPrice = UInt256.One,
            GasLimit = 100_000,
            Nonce = 0,
            ChainId = _chainParams.ChainId,
        };
        intentTx = Transaction.Sign(intentTx, AliceKey);

        var parentHeader = MakeHeader(0);
        var block = blockBuilder.BuildBlockWithDex(
            [], [intentTx], _stateDb, parentHeader, Alice);

        // The intent should produce a failure receipt since the pool has no liquidity
        var failureReceipts = (block.Receipts ?? []).Where(r => !r.Success).ToList();
        failureReceipts.Should().NotBeEmpty("unsettled intents should produce failure receipts");
        failureReceipts[0].ErrorCode.Should().Be(BasaltErrorCode.DexInsufficientLiquidity);
        failureReceipts[0].TransactionHash.Should().Be(intentTx.Hash);
    }

    // ────────── M-7: Mempool rejects unparseable plaintext intents ──────────

    [Fact]
    public void M7_UnparseableIntent_RejectedByMempool()
    {
        // Use a mempool without validation (to isolate the intent parsing check)
        var mempool = new Mempool(1000);

        // Create a DexSwapIntent with data too short to parse
        var badTx = new Transaction
        {
            Sender = Alice,
            To = DexState.DexAddress,
            Type = TransactionType.DexSwapIntent,
            Data = new byte[50], // Needs 114 bytes
            GasPrice = UInt256.One,
            GasLimit = 100_000,
            Nonce = 0,
            ChainId = _chainParams.ChainId,
        };
        badTx = Transaction.Sign(badTx, AliceKey);

        var added = mempool.Add(badTx, raiseEvent: false);
        added.Should().BeFalse("mempool should reject unparseable DexSwapIntent");
    }

    [Fact]
    public void M7_ValidIntent_AcceptedByMempool()
    {
        var mempool = new Mempool(1000);

        var intentData = BuildIntentData(
            tokenIn: Address.Zero,
            tokenOut: MakeAddress(0xAA),
            amountIn: new UInt256(1_000),
            minAmountOut: new UInt256(900),
            deadline: 0);

        var goodTx = new Transaction
        {
            Sender = Alice,
            To = DexState.DexAddress,
            Type = TransactionType.DexSwapIntent,
            Data = intentData,
            GasPrice = UInt256.One,
            GasLimit = 100_000,
            Nonce = 0,
            ChainId = _chainParams.ChainId,
        };
        goodTx = Transaction.Sign(goodTx, AliceKey);

        var added = mempool.Add(goodTx, raiseEvent: false);
        added.Should().BeTrue("valid DexSwapIntent should be accepted");
    }

    [Fact]
    public void M7_EncryptedIntent_NotFilteredByParsing()
    {
        // Encrypted intents cannot be parsed as plaintext — ensure they're NOT rejected
        var mempool = new Mempool(1000);

        var encTx = new Transaction
        {
            Sender = Alice,
            To = DexState.DexAddress,
            Type = TransactionType.DexEncryptedSwapIntent,
            Data = new byte[50], // Short data, but should not be rejected (encrypted intents skip parse check)
            GasPrice = UInt256.One,
            GasLimit = 100_000,
            Nonce = 0,
            ChainId = _chainParams.ChainId,
        };
        encTx = Transaction.Sign(encTx, AliceKey);

        var added = mempool.Add(encTx, raiseEvent: false);
        added.Should().BeTrue("encrypted intents should not be filtered by plaintext parsing");
    }

    // ────────── C-1: BST-20 transfer failure propagation ──────────

    [Fact]
    public void C1_TransferSingleTokenIn_FailingRuntime_Throws()
    {
        var failingRuntime = new FailingContractRuntime();
        var tokenAddr = MakeAddress(0xCC);

        // Plant fake contract code at the token address so ExecuteBst20Transfer doesn't no-op
        PlantFakeContractCode(tokenAddr);

        // ExecuteBst20Transfer throws BasaltException(DexInsufficientLiquidity) when the
        // contract call fails. This is then caught by the C-1 check, but the inner throw
        // fires first. The key invariant: the transfer DOES throw rather than silently failing.
        var act = () => DexEngine.TransferSingleTokenIn(
            _stateDb, Alice, tokenAddr, new UInt256(1_000), failingRuntime);

        act.Should().Throw<BasaltException>(
            "BST-20 transfer failure must propagate as an exception");
    }

    [Fact]
    public void C1_TransferSingleTokenOut_FailingRuntime_Throws()
    {
        var failingRuntime = new FailingContractRuntime();
        var tokenAddr = MakeAddress(0xCC);

        PlantFakeContractCode(tokenAddr);

        var act = () => DexEngine.TransferSingleTokenOut(
            _stateDb, Alice, tokenAddr, new UInt256(1_000), failingRuntime);

        act.Should().Throw<BasaltException>(
            "BST-20 transfer failure must propagate as an exception");
    }

    [Fact]
    public void C1_TransferTokensOut_FailingRuntime_Token0_Throws()
    {
        var failingRuntime = new FailingContractRuntime();
        var token0 = MakeAddress(0xCC);

        PlantFakeContractCode(token0);

        var act = () => DexEngine.TransferSingleTokenOut(
            _stateDb, Alice, token0, new UInt256(100), failingRuntime);

        act.Should().Throw<BasaltException>(
            "BST-20 transfer failure must propagate as an exception");
    }

    [Fact]
    public void C1_NativeToken_TransferStillWorks()
    {
        // Ensure native BST transfers (Address.Zero) still work normally
        _stateDb.SetAccount(DexState.DexAddress, new AccountState { Balance = new UInt256(10_000) });

        // TransferSingleTokenOut with native BST should credit Alice
        var aliceBefore = _stateDb.GetAccount(Alice)?.Balance ?? UInt256.Zero;
        DexEngine.TransferSingleTokenOut(_stateDb, Alice, Address.Zero, new UInt256(500));
        var aliceAfter = _stateDb.GetAccount(Alice)?.Balance ?? UInt256.Zero;

        (aliceAfter - aliceBefore).Should().Be(new UInt256(500));
    }

    [Fact]
    public void C1_MissingContractCode_TransferSucceeds()
    {
        // When no contract code exists at the token address, ExecuteBst20Transfer returns true (no-op).
        // This maintains backward compatibility for non-BST-20 token pairs.
        var runtime = new FailingContractRuntime();
        var tokenWithNoCode = MakeAddress(0xDD); // No contract code planted

        // Should not throw — returns silently as no-op
        DexEngine.TransferSingleTokenOut(_stateDb, Alice, tokenWithNoCode, new UInt256(100), runtime);
    }

    // ────────── H-5: Receipt gas uses ChainParameters ──────────

    [Fact]
    public void H5_SwapReceipt_UsesChainParamsGas()
    {
        var dexState = new DexState(_stateDb);
        dexState.CreatePool(Address.Zero, MakeAddress(0xAA), 30);
        dexState.SetPoolReserves(0, new PoolReserves
        {
            Reserve0 = new UInt256(1_000_000),
            Reserve1 = new UInt256(1_000_000),
            TotalSupply = new UInt256(1_000_000),
            KLast = UInt256.Zero,
        });

        // Create a valid intent and fill for it
        var intentData = BuildIntentData(Address.Zero, MakeAddress(0xAA),
            new UInt256(1000), new UInt256(900), 0);
        var tx = new Transaction
        {
            Sender = Alice,
            To = DexState.DexAddress,
            Type = TransactionType.DexSwapIntent,
            Data = intentData,
            GasPrice = UInt256.One,
            GasLimit = 100_000,
            Nonce = 0,
            ChainId = _chainParams.ChainId,
        };
        tx = Transaction.Sign(tx, AliceKey);

        var batchResult = new BatchResult
        {
            PoolId = 0,
            ClearingPrice = BatchAuctionSolver.ComputeSpotPrice(new UInt256(1_000_000), new UInt256(1_000_000)),
            Fills =
            [
                new FillRecord
                {
                    Participant = Alice,
                    AmountIn = new UInt256(1000),
                    AmountOut = new UInt256(900),
                    IsLimitOrder = false,
                    IsBuy = false,
                    TxHash = tx.Hash,
                }
            ],
            UpdatedReserves = new PoolReserves
            {
                Reserve0 = new UInt256(1_001_000),
                Reserve1 = new UInt256(999_100),
                TotalSupply = new UInt256(1_000_000),
                KLast = UInt256.Zero,
            },
        };

        var header = MakeHeader(1);
        var intentTxMap = new Dictionary<Hash256, Transaction> { { tx.Hash, tx } };

        var receipts = BatchSettlementExecutor.ExecuteSettlement(
            batchResult, _stateDb, dexState, header, intentTxMap, null, _chainParams);

        receipts.Should().NotBeEmpty();
        receipts[0].GasUsed.Should().Be(_chainParams.DexSwapGas,
            "swap receipt gas should come from ChainParameters.DexSwapGas");
    }

    // ────────── M-10: Epoch validation for encrypted intents ──────────

    [Fact]
    public void M10_DecryptWithWrongEpoch_ReturnsNull()
    {
        // Generate a test DKG key pair
        var groupSecret = new byte[32];
        groupSecret[31] = 0x01; // Non-zero scalar
        groupSecret[0] &= 0x3F;

        var groupPubKey = BlsSigner.GetPublicKeyStatic(groupSecret);
        var blsPub = new BlsPublicKey(groupPubKey);

        // Build a plaintext intent payload (114 bytes)
        var intentPayload = new byte[114];
        intentPayload[0] = 0x01; // version
        // tokenIn at bytes 1-20, tokenOut at 21-40 (leave as zeros = native BST)
        new UInt256(1000).WriteTo(intentPayload.AsSpan(41, 32)); // amountIn
        new UInt256(900).WriteTo(intentPayload.AsSpan(73, 32)); // minAmountOut

        // Encrypt for epoch 5
        var encData = EncryptedIntent.Encrypt(intentPayload, blsPub, epochNumber: 5);

        // Parse the encrypted intent
        var encTx = new Transaction
        {
            Sender = Alice,
            To = DexState.DexAddress,
            Type = TransactionType.DexEncryptedSwapIntent,
            Data = encData,
            GasPrice = UInt256.One,
            GasLimit = 100_000,
            Nonce = 0,
            ChainId = _chainParams.ChainId,
        };
        encTx = Transaction.Sign(encTx, AliceKey);

        var encrypted = EncryptedIntent.Parse(encTx);
        encrypted.Should().NotBeNull();

        // Decrypt with wrong epoch should return null
        var resultWrong = encrypted!.Value.Decrypt(groupSecret, expectedEpoch: 10);
        resultWrong.Should().BeNull("wrong epoch should reject decryption");

        // Decrypt with correct epoch should succeed
        var resultCorrect = encrypted.Value.Decrypt(groupSecret, expectedEpoch: 5);
        resultCorrect.Should().NotBeNull("correct epoch should allow decryption");

        // Decrypt with epoch 0 should succeed (no epoch check)
        var resultNoCheck = encrypted.Value.Decrypt(groupSecret, expectedEpoch: 0);
        resultNoCheck.Should().NotBeNull("epoch 0 means no epoch validation");
    }

    // ────────── Helpers ──────────

    private BlockHeader MakeHeader(ulong number) => new()
    {
        Number = number,
        ParentHash = Hash256.Zero,
        StateRoot = Hash256.Zero,
        TransactionsRoot = Hash256.Zero,
        ReceiptsRoot = Hash256.Zero,
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Proposer = Alice,
        ChainId = _chainParams.ChainId,
        GasUsed = 0,
        GasLimit = _chainParams.BlockGasLimit,
        BaseFee = UInt256.One,
    };

    /// <summary>
    /// Build raw intent data: [1B version][20B tokenIn][20B tokenOut][32B amountIn][32B minAmountOut][8B deadline][1B flags]
    /// </summary>
    private static byte[] BuildIntentData(Address tokenIn, Address tokenOut,
        UInt256 amountIn, UInt256 minAmountOut, ulong deadline, bool allowPartial = false)
    {
        var data = new byte[114];
        data[0] = 0x01; // version
        tokenIn.WriteTo(data.AsSpan(1, 20));
        tokenOut.WriteTo(data.AsSpan(21, 20));
        amountIn.WriteTo(data.AsSpan(41, 32));
        minAmountOut.WriteTo(data.AsSpan(73, 32));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(105, 8), deadline);
        data[113] = allowPartial ? (byte)0x01 : (byte)0x00;
        return data;
    }

    /// <summary>
    /// Plant fake contract code at an address so ExecuteBst20Transfer
    /// doesn't short-circuit with a no-op (it returns true when no code exists).
    /// </summary>
    private void PlantFakeContractCode(Address tokenAddr)
    {
        Span<byte> codeKeyBytes = stackalloc byte[32];
        codeKeyBytes.Clear();
        codeKeyBytes[0] = 0xFF;
        codeKeyBytes[1] = 0x01;
        var codeKey = new Hash256(codeKeyBytes);
        // Plant minimal contract code (non-empty)
        _stateDb.SetStorage(tokenAddr, codeKey, [0xBA, 0x5A, 0x00, 0x01]);
    }

    /// <summary>
    /// A contract runtime that always returns failure for Execute calls.
    /// Used to test C-1: BST-20 transfer failure propagation.
    /// </summary>
    private sealed class FailingContractRuntime : IContractRuntime
    {
        public ContractDeployResult Deploy(byte[] code, byte[] constructorArgs, VmExecutionContext ctx)
            => new() { Success = false, Code = [], ErrorMessage = "deploy not supported" };

        public ContractCallResult Execute(byte[] code, byte[] callData, VmExecutionContext ctx)
            => new() { Success = false, ErrorMessage = "simulated BST-20 transfer failure" };
    }
}
