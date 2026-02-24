using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution;
using Basalt.Execution.Dex;
using Basalt.Node.Solver;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Node.Tests.Solver;

public class SolverScoringTests
{
    private static Address MakeAddress(byte b)
    {
        var bytes = new byte[20];
        bytes[19] = b;
        return new Address(bytes);
    }

    private static Hash256 MakeHash(byte b)
    {
        var bytes = new byte[32];
        bytes[31] = b;
        return new Hash256(bytes);
    }

    [Fact]
    public void ComputeSurplus_NoFills_ReturnsZero()
    {
        var result = new BatchResult
        {
            PoolId = 0,
            ClearingPrice = new UInt256(1000),
            Fills = [],
        };

        var surplus = SolverScoring.ComputeSurplus(result, new Dictionary<Hash256, UInt256>());
        surplus.Should().Be(UInt256.Zero);
    }

    [Fact]
    public void ComputeSurplus_FillExceedsMinimum_ReturnsSurplus()
    {
        var txHash = MakeHash(0x01);
        var result = new BatchResult
        {
            PoolId = 0,
            ClearingPrice = new UInt256(1000),
            Fills =
            [
                new FillRecord
                {
                    Participant = MakeAddress(0xAA),
                    AmountIn = new UInt256(1000),
                    AmountOut = new UInt256(950),
                    IsLimitOrder = false,
                    TxHash = txHash,
                },
            ],
        };

        var intentMinAmounts = new Dictionary<Hash256, UInt256>
        {
            [txHash] = new UInt256(900),
        };

        var surplus = SolverScoring.ComputeSurplus(result, intentMinAmounts);
        surplus.Should().Be(new UInt256(50)); // 950 - 900 = 50
    }

    [Fact]
    public void ComputeSurplus_FillBelowMinimum_ReturnsZero()
    {
        var txHash = MakeHash(0x01);
        var result = new BatchResult
        {
            PoolId = 0,
            ClearingPrice = new UInt256(1000),
            Fills =
            [
                new FillRecord
                {
                    Participant = MakeAddress(0xAA),
                    AmountIn = new UInt256(1000),
                    AmountOut = new UInt256(800),
                    IsLimitOrder = false,
                    TxHash = txHash,
                },
            ],
        };

        var intentMinAmounts = new Dictionary<Hash256, UInt256>
        {
            [txHash] = new UInt256(900), // min was 900, got 800
        };

        var surplus = SolverScoring.ComputeSurplus(result, intentMinAmounts);
        surplus.Should().Be(UInt256.Zero);
    }

    [Fact]
    public void ComputeSurplus_MultipleFills_SumsSurplus()
    {
        var hash1 = MakeHash(0x01);
        var hash2 = MakeHash(0x02);
        var result = new BatchResult
        {
            PoolId = 0,
            ClearingPrice = new UInt256(1000),
            Fills =
            [
                new FillRecord
                {
                    Participant = MakeAddress(0xAA),
                    AmountIn = new UInt256(1000),
                    AmountOut = new UInt256(950),
                    IsLimitOrder = false,
                    TxHash = hash1,
                },
                new FillRecord
                {
                    Participant = MakeAddress(0xBB),
                    AmountIn = new UInt256(2000),
                    AmountOut = new UInt256(1800),
                    IsLimitOrder = false,
                    TxHash = hash2,
                },
            ],
        };

        var intentMinAmounts = new Dictionary<Hash256, UInt256>
        {
            [hash1] = new UInt256(900),  // surplus: 50
            [hash2] = new UInt256(1700), // surplus: 100
        };

        var surplus = SolverScoring.ComputeSurplus(result, intentMinAmounts);
        surplus.Should().Be(new UInt256(150)); // 50 + 100
    }

    [Fact]
    public void ComputeSurplus_LimitOrderFills_IgnoredInSurplus()
    {
        var result = new BatchResult
        {
            PoolId = 0,
            ClearingPrice = new UInt256(1000),
            Fills =
            [
                new FillRecord
                {
                    Participant = MakeAddress(0xAA),
                    AmountIn = new UInt256(1000),
                    AmountOut = new UInt256(5000),
                    IsLimitOrder = true,
                    OrderId = 1,
                },
            ],
        };

        var surplus = SolverScoring.ComputeSurplus(result, new Dictionary<Hash256, UInt256>());
        surplus.Should().Be(UInt256.Zero);
    }

    [Fact]
    public void SelectBest_EmptySolutions_ReturnsNull()
    {
        var best = SolverScoring.SelectBest([], new Dictionary<Hash256, UInt256>());
        best.Should().BeNull();
    }

    [Fact]
    public void SelectBest_SingleSolution_ReturnsThat()
    {
        var solution = MakeSolution(0x01, new UInt256(1000), new UInt256(950), new UInt256(900));
        var best = SolverScoring.SelectBest(
            [solution],
            new Dictionary<Hash256, UInt256> { [MakeHash(0x01)] = new UInt256(900) });
        best.Should().BeSameAs(solution);
    }

    [Fact]
    public void SelectBest_HigherSurplusWins()
    {
        var sol1 = MakeSolution(0x01, new UInt256(1000), new UInt256(950), new UInt256(900), receivedAt: 100);
        var sol2 = MakeSolution(0x02, new UInt256(1000), new UInt256(980), new UInt256(900), receivedAt: 200);

        var minAmounts = new Dictionary<Hash256, UInt256>
        {
            [MakeHash(0x01)] = new UInt256(900),
            [MakeHash(0x02)] = new UInt256(900),
        };

        var best = SolverScoring.SelectBest([sol1, sol2], minAmounts);
        best.Should().BeSameAs(sol2); // sol2 has 80 surplus vs sol1's 50
    }

    [Fact]
    public void SelectBest_EqualSurplus_EarliestWins()
    {
        var sol1 = MakeSolution(0x01, new UInt256(1000), new UInt256(950), new UInt256(900), receivedAt: 200);
        var sol2 = MakeSolution(0x02, new UInt256(1000), new UInt256(950), new UInt256(900), receivedAt: 100);

        var minAmounts = new Dictionary<Hash256, UInt256>
        {
            [MakeHash(0x01)] = new UInt256(900),
            [MakeHash(0x02)] = new UInt256(900),
        };

        var best = SolverScoring.SelectBest([sol1, sol2], minAmounts);
        best.Should().BeSameAs(sol2); // Earlier timestamp
    }

    [Fact]
    public void ValidateFeasibility_ZeroClearingPrice_ReturnsFalse()
    {
        var result = new BatchResult
        {
            PoolId = 0,
            ClearingPrice = UInt256.Zero,
            Fills = [new FillRecord { Participant = MakeAddress(0xAA), AmountOut = new UInt256(100) }],
        };

        SolverScoring.ValidateFeasibility(result, new InMemoryStateDb(), new DexState(new InMemoryStateDb()),
            new Dictionary<Hash256, Transaction>()).Should().BeFalse();
    }

    [Fact]
    public void ValidateFeasibility_NoFills_ReturnsFalse()
    {
        var result = new BatchResult
        {
            PoolId = 0,
            ClearingPrice = new UInt256(1000),
            Fills = [],
        };

        SolverScoring.ValidateFeasibility(result, new InMemoryStateDb(), new DexState(new InMemoryStateDb()),
            new Dictionary<Hash256, Transaction>()).Should().BeFalse();
    }

    [Fact]
    public void ValidateFeasibility_PoolNotFound_ReturnsFalse()
    {
        var result = new BatchResult
        {
            PoolId = 999, // Non-existent pool
            ClearingPrice = new UInt256(1000),
            Fills = [new FillRecord { Participant = MakeAddress(0xAA), AmountOut = new UInt256(100), IsLimitOrder = true }],
            UpdatedReserves = new PoolReserves { Reserve0 = new UInt256(1000), Reserve1 = new UInt256(1000) },
        };

        SolverScoring.ValidateFeasibility(result, new InMemoryStateDb(), new DexState(new InMemoryStateDb()),
            new Dictionary<Hash256, Transaction>()).Should().BeFalse();
    }

    [Fact]
    public void ValidateFeasibility_ValidSolution_ReturnsTrue()
    {
        // Test 11: ValidateFeasibility positive path returns true.
        var stateDb = new InMemoryStateDb();
        var dexState = new DexState(stateDb);

        // Create a pool so it exists
        dexState.CreatePool(MakeAddress(0x01), MakeAddress(0x02), 30);
        dexState.SetPoolReserves(0, new PoolReserves
        {
            Reserve0 = new UInt256(100_000),
            Reserve1 = new UInt256(100_000),
            TotalSupply = new UInt256(10_000),
        });

        // Fund the participant
        var participant = MakeAddress(0xAA);
        stateDb.SetAccount(participant, new AccountState { Balance = new UInt256(10_000) });

        var txHash = MakeHash(0x01);
        var result = new BatchResult
        {
            PoolId = 0,
            ClearingPrice = new UInt256(1000),
            Fills =
            [
                new FillRecord
                {
                    Participant = participant,
                    AmountIn = new UInt256(500),
                    AmountOut = new UInt256(480),
                    IsLimitOrder = false,
                    TxHash = txHash,
                },
            ],
            UpdatedReserves = new PoolReserves
            {
                Reserve0 = new UInt256(100_500),
                Reserve1 = new UInt256(99_520),
                TotalSupply = new UInt256(10_000),
            },
        };

        // Build intent tx map with a real transaction
        var (pk, pub) = Basalt.Crypto.Ed25519Signer.GenerateKeyPair();
        var sender = Basalt.Crypto.Ed25519Signer.DeriveAddress(pub);
        var intentData = new byte[114];
        intentData[0] = 1;
        MakeAddress(0x01).WriteTo(intentData.AsSpan(1, 20));
        MakeAddress(0x02).WriteTo(intentData.AsSpan(21, 20));
        new UInt256(500).WriteTo(intentData.AsSpan(41, 32));
        new UInt256(480).WriteTo(intentData.AsSpan(73, 32));
        var tx = Transaction.Sign(new Transaction
        {
            Type = TransactionType.DexSwapIntent,
            Nonce = 0,
            Sender = sender,
            To = DexState.DexAddress,
            GasLimit = 200_000,
            GasPrice = new UInt256(1),
            ChainId = ChainParameters.Devnet.ChainId,
            Data = intentData,
        }, pk);

        var intentTxMap = new Dictionary<Hash256, Transaction> { [txHash] = tx };

        SolverScoring.ValidateFeasibility(result, stateDb, dexState, intentTxMap)
            .Should().BeTrue("a valid solution with funded participants and existing pool should pass");
    }

    // Helper to create a solution with a single fill
    private static SolverSolution MakeSolution(byte txByte, UInt256 amountIn, UInt256 amountOut, UInt256 clearingPrice, long receivedAt = 0)
    {
        var txHash = MakeHash(txByte);
        return new SolverSolution
        {
            BlockNumber = 1,
            PoolId = 0,
            ClearingPrice = clearingPrice,
            Result = new BatchResult
            {
                PoolId = 0,
                ClearingPrice = clearingPrice,
                Fills =
                [
                    new FillRecord
                    {
                        Participant = MakeAddress(txByte),
                        AmountIn = amountIn,
                        AmountOut = amountOut,
                        IsLimitOrder = false,
                        TxHash = txHash,
                    },
                ],
                UpdatedReserves = new PoolReserves
                {
                    Reserve0 = new UInt256(50_000),
                    Reserve1 = new UInt256(50_000),
                },
            },
            SolverAddress = MakeAddress(txByte),
            ReceivedAtMs = receivedAt,
        };
    }
}
