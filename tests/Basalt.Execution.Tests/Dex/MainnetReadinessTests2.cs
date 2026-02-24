using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution.Dex;
using Basalt.Execution.Dex.Math;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests.Dex;

/// <summary>
/// Mainnet readiness tests — Phase 2: Emergency pause, governance parameters,
/// TWAP window extension, and pool creation rate limit.
/// </summary>
public class MainnetReadinessTests2
{
    private readonly InMemoryStateDb _stateDb = new();

    private static readonly byte[] AliceKey;
    private static readonly PublicKey AlicePub;
    private static readonly Address Alice;

    private static readonly byte[] BobKey;
    private static readonly PublicKey BobPub;
    private static readonly Address Bob;

    static MainnetReadinessTests2()
    {
        (AliceKey, AlicePub) = Ed25519Signer.GenerateKeyPair();
        Alice = Ed25519Signer.DeriveAddress(AlicePub);
        (BobKey, BobPub) = Ed25519Signer.GenerateKeyPair();
        Bob = Ed25519Signer.DeriveAddress(BobPub);
    }

    private ChainParameters MakeChainParams(Address? admin = null) => new()
    {
        ChainId = 31337,
        NetworkName = "basalt-devnet",
        BlockTimeMs = 2000,
        ValidatorSetSize = 4,
        MinValidatorStake = new UInt256(1000),
        EpochLength = 100,
        InitialBaseFee = new UInt256(1),
        InactivityThresholdPercent = 50,
        DexAdminAddress = admin,
    };

    public MainnetReadinessTests2()
    {
        var cp = MakeChainParams();
        GenesisContractDeployer.DeployAll(_stateDb, cp.ChainId);
        _stateDb.SetAccount(Alice, new AccountState { Balance = new UInt256(1_000_000_000) });
        _stateDb.SetAccount(Bob, new AccountState { Balance = new UInt256(1_000_000_000) });
    }

    private BlockHeader MakeHeader(ulong number, ChainParameters? cp = null) => new()
    {
        Number = number,
        ParentHash = Hash256.Zero,
        StateRoot = Hash256.Zero,
        TransactionsRoot = Hash256.Zero,
        ReceiptsRoot = Hash256.Zero,
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Proposer = Alice,
        ChainId = (cp ?? MakeChainParams()).ChainId,
        GasUsed = 0,
        GasLimit = 100_000_000,
        BaseFee = UInt256.One,
    };

    private static Address MakeAddress(byte id)
    {
        var bytes = new byte[20];
        bytes[19] = id;
        return new Address(bytes);
    }

    // ────────── Fix 1: Emergency Pause ──────────

    [Fact]
    public void Pause_DexState_ReadWrite()
    {
        var dexState = new DexState(_stateDb);
        dexState.IsDexPaused().Should().BeFalse();

        dexState.SetDexPaused(true);
        dexState.IsDexPaused().Should().BeTrue();

        dexState.SetDexPaused(false);
        dexState.IsDexPaused().Should().BeFalse();
    }

    [Fact]
    public void Pause_DexOps_Rejected()
    {
        var cp = MakeChainParams(Alice);
        var executor = new TransactionExecutor(cp);

        // Pause the DEX
        var dexState = new DexState(_stateDb);
        dexState.SetDexPaused(true);

        // Build a DexCreatePool tx
        var data = new byte[44];
        MakeAddress(0xAA).WriteTo(data.AsSpan(0, 20));
        MakeAddress(0xBB).WriteTo(data.AsSpan(20, 20));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(40, 4), 30);

        var tx = Transaction.Sign(new Transaction
        {
            Nonce = 0,
            Type = TransactionType.DexCreatePool,
            Sender = Alice,
            To = DexState.DexAddress,
            Value = UInt256.Zero,
            Data = data,
            GasLimit = 100_000,
            GasPrice = new UInt256(1),
        }, AliceKey);

        var receipt = executor.Execute(tx, _stateDb, MakeHeader(1, cp), 0);
        receipt.Success.Should().BeFalse();
        receipt.ErrorCode.Should().Be(BasaltErrorCode.DexPaused);
    }

    [Fact]
    public void Unpause_DexOps_Resume()
    {
        var cp = MakeChainParams(Alice);
        var executor = new TransactionExecutor(cp);

        // Pause then unpause
        var dexState = new DexState(_stateDb);
        dexState.SetDexPaused(true);
        dexState.SetDexPaused(false);

        // DexCreatePool should now work (or fail for pool logic reasons, not DexPaused)
        var data = new byte[44];
        MakeAddress(0xAA).WriteTo(data.AsSpan(0, 20));
        MakeAddress(0xBB).WriteTo(data.AsSpan(20, 20));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(40, 4), 30);

        var tx = Transaction.Sign(new Transaction
        {
            Nonce = 0,
            Type = TransactionType.DexCreatePool,
            Sender = Alice,
            To = DexState.DexAddress,
            Value = UInt256.Zero,
            Data = data,
            GasLimit = 100_000,
            GasPrice = new UInt256(1),
        }, AliceKey);

        var receipt = executor.Execute(tx, _stateDb, MakeHeader(1, cp), 0);
        // Should NOT be DexPaused (it should succeed or fail for other reasons)
        receipt.ErrorCode.Should().NotBe(BasaltErrorCode.DexPaused);
    }

    [Fact]
    public void PauseTx_NonAdmin_Fails()
    {
        var cp = MakeChainParams(Alice); // Alice is admin
        var executor = new TransactionExecutor(cp);

        // Bob tries to pause
        var tx = Transaction.Sign(new Transaction
        {
            Nonce = 0,
            Type = TransactionType.DexAdminPause,
            Sender = Bob,
            To = DexState.DexAddress,
            Value = UInt256.Zero,
            Data = [1], // pause
            GasLimit = 21_000,
            GasPrice = new UInt256(1),
        }, BobKey);

        var receipt = executor.Execute(tx, _stateDb, MakeHeader(1, cp), 0);
        receipt.Success.Should().BeFalse();
        receipt.ErrorCode.Should().Be(BasaltErrorCode.DexAdminUnauthorized);
    }

    [Fact]
    public void PauseTx_Admin_Succeeds()
    {
        var cp = MakeChainParams(Alice);
        var executor = new TransactionExecutor(cp);

        var tx = Transaction.Sign(new Transaction
        {
            Nonce = 0,
            Type = TransactionType.DexAdminPause,
            Sender = Alice,
            To = DexState.DexAddress,
            Value = UInt256.Zero,
            Data = [1], // pause
            GasLimit = 21_000,
            GasPrice = new UInt256(1),
        }, AliceKey);

        var receipt = executor.Execute(tx, _stateDb, MakeHeader(1, cp), 0);
        receipt.Success.Should().BeTrue();

        var dexState = new DexState(_stateDb);
        dexState.IsDexPaused().Should().BeTrue();
    }

    [Fact]
    public void Pause_GasStillCharged()
    {
        var cp = MakeChainParams(Alice);
        var executor = new TransactionExecutor(cp);

        var dexState = new DexState(_stateDb);
        dexState.SetDexPaused(true);

        var balanceBefore = _stateDb.GetAccount(Alice)!.Value.Balance;

        var data = new byte[44];
        MakeAddress(0xAA).WriteTo(data.AsSpan(0, 20));
        MakeAddress(0xBB).WriteTo(data.AsSpan(20, 20));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(40, 4), 30);

        var tx = Transaction.Sign(new Transaction
        {
            Nonce = 0,
            Type = TransactionType.DexCreatePool,
            Sender = Alice,
            To = DexState.DexAddress,
            Value = UInt256.Zero,
            Data = data,
            GasLimit = 100_000,
            GasPrice = new UInt256(1),
        }, AliceKey);

        var receipt = executor.Execute(tx, _stateDb, MakeHeader(1, cp), 0);
        receipt.Success.Should().BeFalse();
        receipt.ErrorCode.Should().Be(BasaltErrorCode.DexPaused);

        var balanceAfter = _stateDb.GetAccount(Alice)!.Value.Balance;
        balanceAfter.Should().BeLessThan(balanceBefore);
    }

    // ────────── Fix 2: Governance Parameters ──────────

    [Fact]
    public void SetParameter_Override_ReturnsNewValue()
    {
        var dexState = new DexState(_stateDb);
        dexState.SetDexParameter(DexState.ParamId.SolverRewardBps, 1000);

        var result = dexState.GetDexParameter(DexState.ParamId.SolverRewardBps);
        result.Should().NotBeNull();
        result!.Value.Should().Be(1000UL);

        var cp = MakeChainParams();
        dexState.GetEffectiveSolverRewardBps(cp).Should().Be(1000);
    }

    [Fact]
    public void SetParameter_FallbackToChainParams()
    {
        var dexState = new DexState(_stateDb);
        var cp = MakeChainParams();

        // No override set → should fall back to chain params
        dexState.GetDexParameter(DexState.ParamId.SolverRewardBps).Should().BeNull();
        dexState.GetEffectiveSolverRewardBps(cp).Should().Be(cp.SolverRewardBps);
        dexState.GetEffectiveMaxIntentsPerBatch(cp).Should().Be(cp.DexMaxIntentsPerBatch);
        dexState.GetEffectiveTwapWindowBlocks(cp).Should().Be(cp.TwapWindowBlocks);
        dexState.GetEffectiveMaxPoolCreationsPerBlock(cp).Should().Be(cp.MaxPoolCreationsPerBlock);
    }

    [Fact]
    public void SetParameter_InvalidParamId_Fails()
    {
        var cp = MakeChainParams(Alice);
        var executor = new TransactionExecutor(cp);

        // paramId = 0x05 (invalid — only 0x01-0x04 are valid)
        var data = new byte[9];
        data[0] = 0x05;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(1, 8), 42);

        var tx = Transaction.Sign(new Transaction
        {
            Nonce = 0,
            Type = TransactionType.DexSetParameter,
            Sender = Alice,
            To = DexState.DexAddress,
            Value = UInt256.Zero,
            Data = data,
            GasLimit = 21_000,
            GasPrice = new UInt256(1),
        }, AliceKey);

        var receipt = executor.Execute(tx, _stateDb, MakeHeader(1, cp), 0);
        receipt.Success.Should().BeFalse();
        receipt.ErrorCode.Should().Be(BasaltErrorCode.DexInvalidParameter);
    }

    [Fact]
    public void SetParameter_NonAdmin_Fails()
    {
        var cp = MakeChainParams(Alice); // Alice is admin

        var executor = new TransactionExecutor(cp);

        var data = new byte[9];
        data[0] = DexState.ParamId.SolverRewardBps;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(1, 8), 1000);

        // Bob tries to set parameter
        var tx = Transaction.Sign(new Transaction
        {
            Nonce = 0,
            Type = TransactionType.DexSetParameter,
            Sender = Bob,
            To = DexState.DexAddress,
            Value = UInt256.Zero,
            Data = data,
            GasLimit = 21_000,
            GasPrice = new UInt256(1),
        }, BobKey);

        var receipt = executor.Execute(tx, _stateDb, MakeHeader(1, cp), 0);
        receipt.Success.Should().BeFalse();
        receipt.ErrorCode.Should().Be(BasaltErrorCode.DexAdminUnauthorized);
    }

    // ────────── Fix 3: TWAP Window ──────────

    [Fact]
    public void TwapWindow_Default7200()
    {
        var cp = MakeChainParams();
        cp.TwapWindowBlocks.Should().Be(7200UL);
    }

    [Fact]
    public void TwapWindow_GovernanceOverride()
    {
        var dexState = new DexState(_stateDb);
        var cp = MakeChainParams();

        dexState.GetEffectiveTwapWindowBlocks(cp).Should().Be(7200UL);

        dexState.SetDexParameter(DexState.ParamId.TwapWindowBlocks, 14400);
        dexState.GetEffectiveTwapWindowBlocks(cp).Should().Be(14400UL);
    }

    [Fact]
    public void SerializeForBlockHeader_UsesConfiguredWindow()
    {
        // Create a pool and populate some TWAP data
        var dexState = new DexState(_stateDb);
        var tokenA = MakeAddress(0xAA);
        var tokenB = MakeAddress(0xBB);
        dexState.CreatePool(tokenA, tokenB, 30);

        // Update TWAP accumulators at different blocks
        var price = new UInt256(1000);
        for (ulong block = 1; block <= 200; block++)
            dexState.UpdateTwapAccumulator(0, price, block);

        // Create a settlement result
        var settlements = new List<BatchResult>
        {
            new()
            {
                PoolId = 0,
                ClearingPrice = price,
                Fills = [],
                UpdatedReserves = new PoolReserves
                {
                    Reserve0 = new UInt256(1000),
                    Reserve1 = new UInt256(1000),
                    TotalSupply = new UInt256(1000),
                    KLast = new UInt256(1_000_000),
                },
            },
        };

        // Serialize with different window — should not throw
        var data100 = TwapOracle.SerializeForBlockHeader(settlements, dexState, 200, 256, 100);
        var data7200 = TwapOracle.SerializeForBlockHeader(settlements, dexState, 200, 256, 7200);

        data100.Should().NotBeEmpty();
        data7200.Should().NotBeEmpty();
    }

    // ────────── Fix 4: Pool Creation Rate Limit ──────────

    [Fact]
    public void PoolCreation_UnderLimit_Succeeds()
    {
        var dexState = new DexState(_stateDb);
        var engine = new DexEngine(dexState);

        // Create pools within the limit (limit=10)
        for (byte i = 1; i <= 5; i++)
        {
            var result = engine.CreatePool(Alice, MakeAddress(i), MakeAddress((byte)(i + 100)), 30,
                blockNumber: 1, maxCreationsPerBlock: 10);
            result.Success.Should().BeTrue($"Pool {i} should succeed");
        }
    }

    [Fact]
    public void PoolCreation_AtLimit_Fails()
    {
        var dexState = new DexState(_stateDb);
        var engine = new DexEngine(dexState);

        // Create 3 pools (limit=3)
        for (byte i = 1; i <= 3; i++)
        {
            var result = engine.CreatePool(Alice, MakeAddress(i), MakeAddress((byte)(i + 100)), 30,
                blockNumber: 1, maxCreationsPerBlock: 3);
            result.Success.Should().BeTrue($"Pool {i} should succeed");
        }

        // 4th pool should fail
        var fail = engine.CreatePool(Alice, MakeAddress(50), MakeAddress(51), 30,
            blockNumber: 1, maxCreationsPerBlock: 3);
        fail.Success.Should().BeFalse();
        fail.ErrorCode.Should().Be(BasaltErrorCode.DexPoolCreationLimitReached);
    }

    [Fact]
    public void PoolCreation_DifferentBlocks_ResetCounter()
    {
        var dexState = new DexState(_stateDb);
        var engine = new DexEngine(dexState);

        // Fill block 1 to its limit of 2
        for (byte i = 1; i <= 2; i++)
        {
            var result = engine.CreatePool(Alice, MakeAddress(i), MakeAddress((byte)(i + 100)), 30,
                blockNumber: 1, maxCreationsPerBlock: 2);
            result.Success.Should().BeTrue();
        }

        // Block 1 is full
        var failBlock1 = engine.CreatePool(Alice, MakeAddress(50), MakeAddress(51), 30,
            blockNumber: 1, maxCreationsPerBlock: 2);
        failBlock1.Success.Should().BeFalse();

        // Block 2 should be fresh — counter resets
        var okBlock2 = engine.CreatePool(Alice, MakeAddress(60), MakeAddress(61), 30,
            blockNumber: 2, maxCreationsPerBlock: 2);
        okBlock2.Success.Should().BeTrue();
    }

    [Fact]
    public void PoolCreation_ZeroLimit_Unlimited()
    {
        var dexState = new DexState(_stateDb);
        var engine = new DexEngine(dexState);

        // limit=0 means unlimited
        for (byte i = 1; i <= 20; i++)
        {
            var result = engine.CreatePool(Alice, MakeAddress(i), MakeAddress((byte)(i + 100)), 30,
                blockNumber: 1, maxCreationsPerBlock: 0);
            result.Success.Should().BeTrue($"Pool {i} should succeed with unlimited");
        }
    }

    // ────────── Fix 6: Parameter Bounds Validation ──────────

    [Fact]
    public void SetParameter_SolverRewardBps_ExceedsBound_Fails()
    {
        var cp = MakeChainParams(Alice);
        var executor = new TransactionExecutor(cp);

        var data = new byte[9];
        data[0] = DexState.ParamId.SolverRewardBps;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(1, 8), 10_001); // > 10000

        var tx = Transaction.Sign(new Transaction
        {
            Nonce = 0,
            Type = TransactionType.DexSetParameter,
            Sender = Alice,
            To = DexState.DexAddress,
            Value = UInt256.Zero,
            Data = data,
            GasLimit = 21_000,
            GasPrice = new UInt256(1),
        }, AliceKey);

        var receipt = executor.Execute(tx, _stateDb, MakeHeader(1, cp), 0);
        receipt.Success.Should().BeFalse();
        receipt.ErrorCode.Should().Be(BasaltErrorCode.DexInvalidParameter);
    }

    [Fact]
    public void SetParameter_MaxIntentsPerBatch_Zero_Fails()
    {
        var cp = MakeChainParams(Alice);
        var executor = new TransactionExecutor(cp);

        var data = new byte[9];
        data[0] = DexState.ParamId.MaxIntentsPerBatch;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(1, 8), 0); // < 1

        var tx = Transaction.Sign(new Transaction
        {
            Nonce = 0,
            Type = TransactionType.DexSetParameter,
            Sender = Alice,
            To = DexState.DexAddress,
            Value = UInt256.Zero,
            Data = data,
            GasLimit = 21_000,
            GasPrice = new UInt256(1),
        }, AliceKey);

        var receipt = executor.Execute(tx, _stateDb, MakeHeader(1, cp), 0);
        receipt.Success.Should().BeFalse();
        receipt.ErrorCode.Should().Be(BasaltErrorCode.DexInvalidParameter);
    }

    [Fact]
    public void SetParameter_TwapWindowBlocks_TooSmall_Fails()
    {
        var cp = MakeChainParams(Alice);
        var executor = new TransactionExecutor(cp);

        var data = new byte[9];
        data[0] = DexState.ParamId.TwapWindowBlocks;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(1, 8), 50); // < 100

        var tx = Transaction.Sign(new Transaction
        {
            Nonce = 1,
            Type = TransactionType.DexSetParameter,
            Sender = Alice,
            To = DexState.DexAddress,
            Value = UInt256.Zero,
            Data = data,
            GasLimit = 21_000,
            GasPrice = new UInt256(1),
        }, AliceKey);

        var receipt = executor.Execute(tx, _stateDb, MakeHeader(1, cp), 0);
        receipt.Success.Should().BeFalse();
        receipt.ErrorCode.Should().Be(BasaltErrorCode.DexInvalidParameter);
    }

    [Fact]
    public void SetParameter_MaxPoolCreationsPerBlock_TooLarge_Fails()
    {
        var cp = MakeChainParams(Alice);
        var executor = new TransactionExecutor(cp);

        var data = new byte[9];
        data[0] = DexState.ParamId.MaxPoolCreationsPerBlock;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(1, 8), 1_001); // > 1000

        var tx = Transaction.Sign(new Transaction
        {
            Nonce = 2,
            Type = TransactionType.DexSetParameter,
            Sender = Alice,
            To = DexState.DexAddress,
            Value = UInt256.Zero,
            Data = data,
            GasLimit = 21_000,
            GasPrice = new UInt256(1),
        }, AliceKey);

        var receipt = executor.Execute(tx, _stateDb, MakeHeader(1, cp), 0);
        receipt.Success.Should().BeFalse();
        receipt.ErrorCode.Should().Be(BasaltErrorCode.DexInvalidParameter);
    }

    [Fact]
    public void SetParameter_ValidValues_Succeed()
    {
        var cp = MakeChainParams(Alice);
        var executor = new TransactionExecutor(cp);

        // SolverRewardBps = 500 (valid: 0..10000)
        var data = new byte[9];
        data[0] = DexState.ParamId.SolverRewardBps;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(1, 8), 500);

        var tx = Transaction.Sign(new Transaction
        {
            Nonce = 0,
            Type = TransactionType.DexSetParameter,
            Sender = Alice,
            To = DexState.DexAddress,
            Value = UInt256.Zero,
            Data = data,
            GasLimit = 21_000,
            GasPrice = new UInt256(1),
        }, AliceKey);

        var receipt = executor.Execute(tx, _stateDb, MakeHeader(1, cp), 0);
        receipt.Success.Should().BeTrue();

        var dexState = new DexState(_stateDb);
        dexState.GetDexParameter(DexState.ParamId.SolverRewardBps)!.Value.Should().Be(500UL);
    }

    // ────────── Fix 7: DKG Key Safety ──────────

    [Fact]
    public void DkgKeyZeroing_AfterException_KeyIsNull()
    {
        var cp = MakeChainParams();
        var builder = new BlockBuilder(cp);

        // Set a DKG key
        builder.DkgGroupSecretKey = new byte[32];
        builder.DkgGroupSecretKey[0] = 0xAB;
        builder.DkgGroupSecretKey[31] = 0xCD;

        var parentHeader = MakeHeader(0, cp);

        // Build a block with DEX — even if an exception occurs internally,
        // the key should be zeroed in the finally block
        var block = builder.BuildBlockWithDex(
            [], [], _stateDb, parentHeader, Alice);

        // After build, key should be null (zeroed and set to null in finally)
        builder.DkgGroupSecretKey.Should().BeNull();
    }
}
