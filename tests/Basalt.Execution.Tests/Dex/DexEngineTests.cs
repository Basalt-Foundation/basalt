using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution.Dex;
using Basalt.Execution.Dex.Math;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests.Dex;

/// <summary>
/// Tests for DexEngine — the core DEX logic layer.
/// Validates pool creation, liquidity operations, swaps, and limit orders.
/// All tests use InMemoryStateDb for isolation and Address.Zero for native BST token pairs.
/// </summary>
public class DexEngineTests
{
    private readonly InMemoryStateDb _stateDb = new();
    private readonly DexState _dexState;
    private readonly DexEngine _engine;

    // Use Address.Zero as native BST token, and synthetic addresses for BST-20 tokens
    private static readonly Address NativeBst = Address.Zero;
    private static readonly Address TokenA = MakeAddress(0xAA);
    private static readonly Address TokenB = MakeAddress(0xBB);
    private static readonly Address User1 = MakeAddress(0x01);
    private static readonly Address User2 = MakeAddress(0x02);

    public DexEngineTests()
    {
        _dexState = new DexState(_stateDb);
        _engine = new DexEngine(_dexState);

        // Ensure DexAddress has a system account
        _stateDb.SetAccount(DexState.DexAddress, new AccountState
        {
            AccountType = AccountType.SystemContract,
            Balance = UInt256.Zero,
        });
    }

    private static Address MakeAddress(byte id)
    {
        var bytes = new byte[20];
        bytes[19] = id;
        return new Address(bytes);
    }

    private void FundUser(Address user, UInt256 amount)
    {
        var state = _stateDb.GetAccount(user) ?? AccountState.Empty;
        _stateDb.SetAccount(user, state with { Balance = UInt256.CheckedAdd(state.Balance, amount) });
    }

    // ────────── Pool Creation ──────────

    [Fact]
    public void CreatePool_Success()
    {
        var result = _engine.CreatePool(User1, NativeBst, TokenA, 30);
        result.Success.Should().BeTrue();
        result.PoolId.Should().Be(0UL);
        result.Logs.Should().NotBeEmpty();
    }

    [Fact]
    public void CreatePool_CanonicalOrdering()
    {
        // Pass tokens in reverse order — should still create pool with correct order
        var result = _engine.CreatePool(User1, TokenB, TokenA, 30);
        result.Success.Should().BeTrue();

        var meta = _dexState.GetPoolMetadata(result.PoolId);
        meta.Should().NotBeNull();
        // TokenA < TokenB, so token0 should be TokenA
        meta!.Value.Token0.CompareTo(meta.Value.Token1).Should().BeLessThan(0);
    }

    [Fact]
    public void CreatePool_IdenticalTokens_Fails()
    {
        var result = _engine.CreatePool(User1, TokenA, TokenA, 30);
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.DexInvalidPair);
    }

    [Fact]
    public void CreatePool_InvalidFeeTier_Fails()
    {
        var result = _engine.CreatePool(User1, NativeBst, TokenA, 50);
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.DexInvalidFeeTier);
    }

    [Fact]
    public void CreatePool_DuplicatePair_Fails()
    {
        _engine.CreatePool(User1, NativeBst, TokenA, 30);
        var result = _engine.CreatePool(User1, NativeBst, TokenA, 30);
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.DexPoolAlreadyExists);
    }

    [Fact]
    public void CreatePool_SamePairDifferentFee_Succeeds()
    {
        var r1 = _engine.CreatePool(User1, NativeBst, TokenA, 30);
        var r2 = _engine.CreatePool(User1, NativeBst, TokenA, 100);
        r1.Success.Should().BeTrue();
        r2.Success.Should().BeTrue();
        r1.PoolId.Should().NotBe(r2.PoolId);
    }

    // ────────── Add Liquidity ──────────

    [Fact]
    public void AddLiquidity_FirstDeposit()
    {
        FundUser(User1, new UInt256(100_000));
        _engine.CreatePool(User1, NativeBst, TokenA, 30);

        // For native BST pool, only token0=NativeBst (Address.Zero) affects balance
        var result = _engine.AddLiquidity(
            User1, 0,
            new UInt256(10_000), new UInt256(10_000),
            UInt256.Zero, UInt256.Zero,
            _stateDb);

        result.Success.Should().BeTrue();
        result.Shares.Should().Be(new UInt256(9000)); // sqrt(10000*10000) - 1000

        // Check reserves updated
        var reserves = _dexState.GetPoolReserves(0);
        reserves!.Value.Reserve0.Should().Be(new UInt256(10_000));
        reserves.Value.Reserve1.Should().Be(new UInt256(10_000));
        // TotalSupply = shares + MinimumLiquidity = 9000 + 1000 = 10000
        reserves.Value.TotalSupply.Should().Be(new UInt256(10_000));

        // Check LP balance
        _dexState.GetLpBalance(0, User1).Should().Be(new UInt256(9000));
    }

    [Fact]
    public void AddLiquidity_SubsequentDeposit_ProportionalShares()
    {
        FundUser(User1, new UInt256(200_000));
        FundUser(User2, new UInt256(200_000));
        _engine.CreatePool(User1, NativeBst, TokenA, 30);

        // First deposit by User1
        _engine.AddLiquidity(User1, 0,
            new UInt256(10_000), new UInt256(10_000),
            UInt256.Zero, UInt256.Zero, _stateDb);

        // Second deposit by User2 — same ratio, same size → same shares
        var result = _engine.AddLiquidity(User2, 0,
            new UInt256(10_000), new UInt256(10_000),
            UInt256.Zero, UInt256.Zero, _stateDb);

        result.Success.Should().BeTrue();
        // shares = min(10000 * 10000 / 10000, 10000 * 10000 / 10000) = 10000
        result.Shares.Should().Be(new UInt256(10_000));
    }

    [Fact]
    public void AddLiquidity_NonexistentPool_Fails()
    {
        var result = _engine.AddLiquidity(User1, 999,
            new UInt256(1000), new UInt256(1000),
            UInt256.Zero, UInt256.Zero, _stateDb);
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.DexPoolNotFound);
    }

    // ────────── Remove Liquidity ──────────

    [Fact]
    public void RemoveLiquidity_ReturnsProportionalAmounts()
    {
        FundUser(User1, new UInt256(200_000));
        _engine.CreatePool(User1, NativeBst, TokenA, 30);
        _engine.AddLiquidity(User1, 0,
            new UInt256(10_000), new UInt256(10_000),
            UInt256.Zero, UInt256.Zero, _stateDb);

        // Remove half of User1's shares (4500 of 9000)
        var result = _engine.RemoveLiquidity(User1, 0,
            new UInt256(4500), UInt256.Zero, UInt256.Zero, _stateDb);

        result.Success.Should().BeTrue();
        // amount0 = 4500 * 10000 / 10000 = 4500
        result.Amount0.Should().Be(new UInt256(4500));
        result.Amount1.Should().Be(new UInt256(4500));

        // Check remaining LP balance
        _dexState.GetLpBalance(0, User1).Should().Be(new UInt256(4500));
    }

    [Fact]
    public void RemoveLiquidity_InsufficientShares_Fails()
    {
        FundUser(User1, new UInt256(200_000));
        _engine.CreatePool(User1, NativeBst, TokenA, 30);
        _engine.AddLiquidity(User1, 0,
            new UInt256(10_000), new UInt256(10_000),
            UInt256.Zero, UInt256.Zero, _stateDb);

        var result = _engine.RemoveLiquidity(User1, 0,
            new UInt256(99_999), UInt256.Zero, UInt256.Zero, _stateDb);
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.DexInsufficientLiquidity);
    }

    [Fact]
    public void RemoveLiquidity_SlippageProtection_Fails()
    {
        FundUser(User1, new UInt256(200_000));
        _engine.CreatePool(User1, NativeBst, TokenA, 30);
        _engine.AddLiquidity(User1, 0,
            new UInt256(10_000), new UInt256(10_000),
            UInt256.Zero, UInt256.Zero, _stateDb);

        // Require more token0 than proportional share returns
        var result = _engine.RemoveLiquidity(User1, 0,
            new UInt256(1000), new UInt256(2000), UInt256.Zero, _stateDb);
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.DexSlippageExceeded);
    }

    // ────────── Swaps ──────────

    [Fact]
    public void ExecuteSwap_Success()
    {
        FundUser(User1, new UInt256(200_000));
        FundUser(User2, new UInt256(200_000));
        _engine.CreatePool(User1, NativeBst, TokenA, 30);
        _engine.AddLiquidity(User1, 0,
            new UInt256(50_000), new UInt256(50_000),
            UInt256.Zero, UInt256.Zero, _stateDb);

        // Determine which token is token0 and which is token1
        var meta = _dexState.GetPoolMetadata(0)!.Value;
        var tokenIn = meta.Token0; // NativeBst or TokenA depending on sort order

        var result = _engine.ExecuteSwap(User2, 0, tokenIn, new UInt256(1000), UInt256.Zero, _stateDb);
        result.Success.Should().BeTrue();
        result.Amount0.Should().BeGreaterThan(UInt256.Zero); // Got some output
    }

    [Fact]
    public void ExecuteSwap_SlippageProtection_Fails()
    {
        FundUser(User1, new UInt256(200_000));
        FundUser(User2, new UInt256(200_000));
        _engine.CreatePool(User1, NativeBst, TokenA, 30);
        _engine.AddLiquidity(User1, 0,
            new UInt256(10_000), new UInt256(10_000),
            UInt256.Zero, UInt256.Zero, _stateDb);

        var meta = _dexState.GetPoolMetadata(0)!.Value;
        // Swap 1000 in, but require unreasonably high output
        var result = _engine.ExecuteSwap(User2, 0, meta.Token0, new UInt256(1000), new UInt256(9999), _stateDb);
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.DexSlippageExceeded);
    }

    [Fact]
    public void ExecuteSwap_InvalidToken_Fails()
    {
        FundUser(User1, new UInt256(200_000));
        _engine.CreatePool(User1, NativeBst, TokenA, 30);
        _engine.AddLiquidity(User1, 0,
            new UInt256(10_000), new UInt256(10_000),
            UInt256.Zero, UInt256.Zero, _stateDb);

        var result = _engine.ExecuteSwap(User2, 0, TokenB, new UInt256(1000), UInt256.Zero, _stateDb);
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.DexInvalidPair);
    }

    [Fact]
    public void ExecuteSwap_EmptyPool_Fails()
    {
        _engine.CreatePool(User1, NativeBst, TokenA, 30);
        var meta = _dexState.GetPoolMetadata(0)!.Value;

        var result = _engine.ExecuteSwap(User2, 0, meta.Token0, new UInt256(1000), UInt256.Zero, _stateDb);
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.DexInsufficientLiquidity);
    }

    [Fact]
    public void ExecuteSwap_PreservesConstantProduct()
    {
        FundUser(User1, new UInt256(500_000));
        FundUser(User2, new UInt256(500_000));
        _engine.CreatePool(User1, NativeBst, TokenA, 30);
        _engine.AddLiquidity(User1, 0,
            new UInt256(100_000), new UInt256(100_000),
            UInt256.Zero, UInt256.Zero, _stateDb);

        var reservesBefore = _dexState.GetPoolReserves(0)!.Value;
        var kBefore = FullMath.MulDiv(reservesBefore.Reserve0, reservesBefore.Reserve1, UInt256.One);

        var meta = _dexState.GetPoolMetadata(0)!.Value;
        _engine.ExecuteSwap(User2, 0, meta.Token0, new UInt256(5000), UInt256.Zero, _stateDb);

        var reservesAfter = _dexState.GetPoolReserves(0)!.Value;
        var kAfter = FullMath.MulDiv(reservesAfter.Reserve0, reservesAfter.Reserve1, UInt256.One);

        // k should increase or stay the same (fee accumulation)
        kAfter.Should().BeGreaterThanOrEqualTo(kBefore);
    }

    // ────────── Limit Orders ──────────

    [Fact]
    public void PlaceOrder_Success()
    {
        FundUser(User1, new UInt256(100_000));
        _engine.CreatePool(User1, NativeBst, TokenA, 30);

        // Place buy order (escrowing token1)
        var result = _engine.PlaceOrder(
            User1, 0, new UInt256(1000), new UInt256(500), true, 100, _stateDb);
        result.Success.Should().BeTrue();
        result.OrderId.Should().Be(0UL);

        var order = _dexState.GetOrder(0);
        order.Should().NotBeNull();
        order!.Value.Owner.Should().Be(User1);
        order.Value.IsBuy.Should().BeTrue();
    }

    [Fact]
    public void PlaceOrder_ZeroAmount_Fails()
    {
        _engine.CreatePool(User1, NativeBst, TokenA, 30);

        var result = _engine.PlaceOrder(
            User1, 0, new UInt256(1000), UInt256.Zero, true, 100, _stateDb);
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.DexInvalidAmount);
    }

    [Fact]
    public void CancelOrder_Success()
    {
        FundUser(User1, new UInt256(100_000));
        _engine.CreatePool(User1, NativeBst, TokenA, 30);

        _engine.PlaceOrder(User1, 0, new UInt256(1000), new UInt256(500), true, 100, _stateDb);

        var result = _engine.CancelOrder(User1, 0, _stateDb);
        result.Success.Should().BeTrue();

        _dexState.GetOrder(0).Should().BeNull();
    }

    [Fact]
    public void CancelOrder_NonOwner_Fails()
    {
        FundUser(User1, new UInt256(100_000));
        _engine.CreatePool(User1, NativeBst, TokenA, 30);
        _engine.PlaceOrder(User1, 0, new UInt256(1000), new UInt256(500), true, 100, _stateDb);

        var result = _engine.CancelOrder(User2, 0, _stateDb);
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.DexUnauthorized);
    }

    [Fact]
    public void CancelOrder_NonexistentOrder_Fails()
    {
        var result = _engine.CancelOrder(User1, 999, _stateDb);
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.DexOrderNotFound);
    }

    // ────────── TransactionExecutor Integration ──────────

    [Fact]
    public void Executor_CreatePool_EndToEnd()
    {
        var chainParams = ChainParameters.Devnet;
        var (privateKey, publicKey) = Ed25519Signer.GenerateKeyPair();
        var sender = Ed25519Signer.DeriveAddress(publicKey);
        var stateDb = new InMemoryStateDb();

        // Fund sender
        stateDb.SetAccount(sender, new AccountState { Balance = new UInt256(10_000_000), Nonce = 0 });

        // Ensure DEX system account exists
        stateDb.SetAccount(DexState.DexAddress, new AccountState
        {
            AccountType = AccountType.SystemContract,
            Balance = UInt256.Zero,
        });

        // Build tx.Data: [20B token0][20B token1][4B feeBps]
        var data = new byte[44];
        NativeBst.WriteTo(data.AsSpan(0, 20));
        TokenA.WriteTo(data.AsSpan(20, 20));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(40, 4), 30);

        var tx = Transaction.Sign(new Transaction
        {
            Type = TransactionType.DexCreatePool,
            Nonce = 0,
            Sender = sender,
            To = DexState.DexAddress,
            Value = UInt256.Zero,
            GasLimit = 200_000,
            GasPrice = new UInt256(1),
            ChainId = chainParams.ChainId,
            Data = data,
        }, privateKey);

        var executor = new TransactionExecutor(chainParams);
        var header = new BlockHeader
        {
            Number = 1,
            ParentHash = Hash256.Zero,
            StateRoot = Hash256.Zero,
            TransactionsRoot = Hash256.Zero,
            ReceiptsRoot = Hash256.Zero,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Proposer = sender,
            ChainId = chainParams.ChainId,
            GasUsed = 0,
            GasLimit = chainParams.BlockGasLimit,
            BaseFee = UInt256.Zero,
        };

        var receipt = executor.Execute(tx, stateDb, header, 0);
        receipt.Success.Should().BeTrue();
        receipt.ErrorCode.Should().Be(BasaltErrorCode.Success);
        receipt.GasUsed.Should().Be(chainParams.DexCreatePoolGas);
        receipt.To.Should().Be(DexState.DexAddress);

        // Verify pool was created
        var dexState = new DexState(stateDb);
        dexState.GetPoolCount().Should().Be(1UL);
    }

    [Fact]
    public void Executor_DexCreatePool_InvalidData_Fails()
    {
        var chainParams = ChainParameters.Devnet;
        var (privateKey, publicKey) = Ed25519Signer.GenerateKeyPair();
        var sender = Ed25519Signer.DeriveAddress(publicKey);
        var stateDb = new InMemoryStateDb();
        stateDb.SetAccount(sender, new AccountState { Balance = new UInt256(10_000_000), Nonce = 0 });

        var tx = Transaction.Sign(new Transaction
        {
            Type = TransactionType.DexCreatePool,
            Nonce = 0,
            Sender = sender,
            To = DexState.DexAddress,
            Value = UInt256.Zero,
            GasLimit = 200_000,
            GasPrice = new UInt256(1),
            ChainId = chainParams.ChainId,
            Data = new byte[10], // Too short
        }, privateKey);

        var executor = new TransactionExecutor(chainParams);
        var header = MakeHeader(1, sender, chainParams);

        var receipt = executor.Execute(tx, stateDb, header, 0);
        receipt.Success.Should().BeFalse();
        receipt.ErrorCode.Should().Be(BasaltErrorCode.DexInvalidData);
    }

    private static BlockHeader MakeHeader(ulong number, Address proposer, ChainParameters chainParams) => new()
    {
        Number = number,
        ParentHash = Hash256.Zero,
        StateRoot = Hash256.Zero,
        TransactionsRoot = Hash256.Zero,
        ReceiptsRoot = Hash256.Zero,
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        Proposer = proposer,
        ChainId = chainParams.ChainId,
        GasUsed = 0,
        GasLimit = chainParams.BlockGasLimit,
        BaseFee = UInt256.Zero,
    };
}
