using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution.Dex;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests.Dex;

/// <summary>
/// Tests for LP token transfer and approval operations (Phase E1).
/// Covers direct transfer, approve/transferFrom, edge cases, and error paths.
/// </summary>
public class LpTokenTests
{
    private readonly InMemoryStateDb _stateDb = new();
    private readonly DexState _dexState;
    private readonly DexEngine _engine;

    private static readonly Address Alice;
    private static readonly Address Bob;
    private static readonly Address Charlie;

    static LpTokenTests()
    {
        var (_, alicePub) = Ed25519Signer.GenerateKeyPair();
        Alice = Ed25519Signer.DeriveAddress(alicePub);
        var (_, bobPub) = Ed25519Signer.GenerateKeyPair();
        Bob = Ed25519Signer.DeriveAddress(bobPub);
        var (_, charliePub) = Ed25519Signer.GenerateKeyPair();
        Charlie = Ed25519Signer.DeriveAddress(charliePub);
    }

    public LpTokenTests()
    {
        GenesisContractDeployer.DeployAll(_stateDb, ChainParameters.Devnet.ChainId);
        _dexState = new DexState(_stateDb);
        _engine = new DexEngine(_dexState);

        // Create a pool and give Alice LP tokens
        _dexState.CreatePool(Address.Zero, MakeAddress(0xAA), 30);
        _dexState.SetPoolReserves(0, new PoolReserves
        {
            Reserve0 = new UInt256(1_000_000),
            Reserve1 = new UInt256(1_000_000),
            TotalSupply = new UInt256(100_000),
        });
        _dexState.SetLpBalance(0, Alice, new UInt256(50_000));
    }

    // ─── TransferLp ───

    [Fact]
    public void TransferLp_Success()
    {
        var result = _engine.TransferLp(Alice, 0, Bob, new UInt256(10_000));

        result.Success.Should().BeTrue();
        _dexState.GetLpBalance(0, Alice).Should().Be(new UInt256(40_000));
        _dexState.GetLpBalance(0, Bob).Should().Be(new UInt256(10_000));
    }

    [Fact]
    public void TransferLp_FullBalance()
    {
        var result = _engine.TransferLp(Alice, 0, Bob, new UInt256(50_000));

        result.Success.Should().BeTrue();
        _dexState.GetLpBalance(0, Alice).Should().Be(UInt256.Zero);
        _dexState.GetLpBalance(0, Bob).Should().Be(new UInt256(50_000));
    }

    [Fact]
    public void TransferLp_InsufficientBalance_Fails()
    {
        var result = _engine.TransferLp(Alice, 0, Bob, new UInt256(60_000));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.DexInsufficientLpBalance);
    }

    [Fact]
    public void TransferLp_ZeroAmount_Fails()
    {
        var result = _engine.TransferLp(Alice, 0, Bob, UInt256.Zero);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.DexInvalidAmount);
    }

    [Fact]
    public void TransferLp_SelfTransfer_Fails()
    {
        var result = _engine.TransferLp(Alice, 0, Alice, new UInt256(1000));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.DexInvalidPair);
    }

    [Fact]
    public void TransferLp_NonexistentPool_Fails()
    {
        var result = _engine.TransferLp(Alice, 999, Bob, new UInt256(1000));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.DexPoolNotFound);
    }

    [Fact]
    public void TransferLp_EmitsLog()
    {
        var result = _engine.TransferLp(Alice, 0, Bob, new UInt256(5_000));

        result.Logs.Should().HaveCount(1);
        result.Logs[0].Contract.Should().Be(DexState.DexAddress);
    }

    // ─── ApproveLp ───

    [Fact]
    public void ApproveLp_Success()
    {
        var result = _engine.ApproveLp(Alice, 0, Bob, new UInt256(20_000));

        result.Success.Should().BeTrue();
        _dexState.GetLpAllowance(0, Alice, Bob).Should().Be(new UInt256(20_000));
    }

    [Fact]
    public void ApproveLp_OverwriteExisting()
    {
        _engine.ApproveLp(Alice, 0, Bob, new UInt256(20_000));
        var result = _engine.ApproveLp(Alice, 0, Bob, new UInt256(5_000));

        result.Success.Should().BeTrue();
        _dexState.GetLpAllowance(0, Alice, Bob).Should().Be(new UInt256(5_000));
    }

    [Fact]
    public void ApproveLp_ZeroAmount_Revokes()
    {
        _engine.ApproveLp(Alice, 0, Bob, new UInt256(20_000));
        var result = _engine.ApproveLp(Alice, 0, Bob, UInt256.Zero);

        result.Success.Should().BeTrue();
        _dexState.GetLpAllowance(0, Alice, Bob).Should().Be(UInt256.Zero);
    }

    [Fact]
    public void ApproveLp_NonexistentPool_Fails()
    {
        var result = _engine.ApproveLp(Alice, 999, Bob, new UInt256(1000));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.DexPoolNotFound);
    }

    // ─── TransferLpFrom ───

    [Fact]
    public void TransferLpFrom_Success()
    {
        _engine.ApproveLp(Alice, 0, Bob, new UInt256(15_000));

        var result = _engine.TransferLpFrom(Bob, Alice, 0, Charlie, new UInt256(10_000));

        result.Success.Should().BeTrue();
        _dexState.GetLpBalance(0, Alice).Should().Be(new UInt256(40_000));
        _dexState.GetLpBalance(0, Charlie).Should().Be(new UInt256(10_000));
        _dexState.GetLpAllowance(0, Alice, Bob).Should().Be(new UInt256(5_000));
    }

    [Fact]
    public void TransferLpFrom_InsufficientAllowance_Fails()
    {
        _engine.ApproveLp(Alice, 0, Bob, new UInt256(5_000));

        var result = _engine.TransferLpFrom(Bob, Alice, 0, Charlie, new UInt256(10_000));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.DexInsufficientLpAllowance);
    }

    [Fact]
    public void TransferLpFrom_InsufficientBalance_Fails()
    {
        _engine.ApproveLp(Alice, 0, Bob, new UInt256(100_000));

        var result = _engine.TransferLpFrom(Bob, Alice, 0, Charlie, new UInt256(60_000));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.DexInsufficientLpBalance);
    }

    [Fact]
    public void TransferLpFrom_ExhaustsAllowance()
    {
        _engine.ApproveLp(Alice, 0, Bob, new UInt256(10_000));

        var result = _engine.TransferLpFrom(Bob, Alice, 0, Charlie, new UInt256(10_000));

        result.Success.Should().BeTrue();
        _dexState.GetLpAllowance(0, Alice, Bob).Should().Be(UInt256.Zero);
    }

    [Fact]
    public void TransferLpFrom_ZeroAmount_Fails()
    {
        _engine.ApproveLp(Alice, 0, Bob, new UInt256(10_000));

        var result = _engine.TransferLpFrom(Bob, Alice, 0, Charlie, UInt256.Zero);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.DexInvalidAmount);
    }

    // ─── DexState LP Allowance Key ───

    [Fact]
    public void LpAllowance_DifferentSpenders_Independent()
    {
        _dexState.SetLpAllowance(0, Alice, Bob, new UInt256(1000));
        _dexState.SetLpAllowance(0, Alice, Charlie, new UInt256(2000));

        _dexState.GetLpAllowance(0, Alice, Bob).Should().Be(new UInt256(1000));
        _dexState.GetLpAllowance(0, Alice, Charlie).Should().Be(new UInt256(2000));
    }

    [Fact]
    public void LpAllowance_DefaultIsZero()
    {
        _dexState.GetLpAllowance(0, Alice, Bob).Should().Be(UInt256.Zero);
    }

    // ─── Transaction Integration ───

    [Fact]
    public void TransferLp_ViaTransactionExecutor()
    {
        // Set up with funded accounts
        var (aliceKey, alicePub) = Ed25519Signer.GenerateKeyPair();
        var alice = Ed25519Signer.DeriveAddress(alicePub);
        var (bobKey, bobPub) = Ed25519Signer.GenerateKeyPair();
        var bob = Ed25519Signer.DeriveAddress(bobPub);

        var stateDb = new InMemoryStateDb();
        GenesisContractDeployer.DeployAll(stateDb, ChainParameters.Devnet.ChainId);
        stateDb.SetAccount(alice, new AccountState { Balance = new UInt256(10_000_000) });

        // Create pool and give Alice LP tokens directly
        var dexState = new DexState(stateDb);
        dexState.CreatePool(Address.Zero, MakeAddress(0xBB), 30);
        dexState.SetLpBalance(0, alice, new UInt256(10_000));

        var chainParams = ChainParameters.Devnet;
        var executor = new TransactionExecutor(chainParams);
        var blockBuilder = new BlockBuilder(chainParams, executor);

        // Build TransferLp transaction: [8B poolId][20B recipient][32B amount]
        var data = new byte[60];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(0, 8), 0);
        bob.WriteTo(data.AsSpan(8, 20));
        new UInt256(5_000).WriteTo(data.AsSpan(28, 32));

        var tx = Transaction.Sign(new Transaction
        {
            Type = TransactionType.DexTransferLp,
            Nonce = 0,
            Sender = alice,
            To = DexState.DexAddress,
            Data = data,
            GasLimit = 200_000,
            GasPrice = new UInt256(1),
            MaxFeePerGas = new UInt256(10),
            MaxPriorityFeePerGas = new UInt256(1),
            ChainId = chainParams.ChainId,
        }, aliceKey);

        var parent = new BlockHeader
        {
            Number = 0,
            ParentHash = Hash256.Zero,
            StateRoot = Hash256.Zero,
            TransactionsRoot = Hash256.Zero,
            ReceiptsRoot = Hash256.Zero,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Proposer = alice,
            ChainId = chainParams.ChainId,
            GasUsed = 0,
            GasLimit = chainParams.BlockGasLimit,
            BaseFee = new UInt256(1),
        };

        var block = blockBuilder.BuildBlock([tx], stateDb, parent, alice);
        block.Receipts![0].Success.Should().BeTrue();

        // Verify LP balances
        dexState = new DexState(stateDb);
        dexState.GetLpBalance(0, alice).Should().Be(new UInt256(5_000));
        dexState.GetLpBalance(0, bob).Should().Be(new UInt256(5_000));
    }

    private static Address MakeAddress(byte id)
    {
        var bytes = new byte[20];
        bytes[19] = id;
        return new Address(bytes);
    }
}
