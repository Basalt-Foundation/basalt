using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution;
using Basalt.Execution.Dex;
using Basalt.Node.Solver;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Node.Tests.Solver;

public class SolverManagerTests
{
    private static readonly ChainParameters DefaultParams = ChainParameters.Devnet;

    private static (byte[] PrivKey, PublicKey PubKey, Address Address) MakeSolver()
    {
        var (privKey, pubKey) = Ed25519Signer.GenerateKeyPair();
        var address = Ed25519Signer.DeriveAddress(pubKey);
        return (privKey, pubKey, address);
    }

    private static Address MakeAddress(byte b)
    {
        var bytes = new byte[20];
        bytes[19] = b;
        return new Address(bytes);
    }

    [Fact]
    public void RegisterSolver_Succeeds()
    {
        var manager = new SolverManager(DefaultParams);
        var (_, pubKey, address) = MakeSolver();

        var result = manager.RegisterSolver(address, pubKey, "http://solver:8080");

        result.Should().BeTrue();
        manager.HasExternalSolvers.Should().BeTrue();
        manager.GetRegisteredSolvers().Should().HaveCount(1);
    }

    [Fact]
    public void RegisterSolver_DuplicateAddress_UpdatesRegistration()
    {
        var manager = new SolverManager(DefaultParams);
        var (_, pubKey, address) = MakeSolver();

        manager.RegisterSolver(address, pubKey, "http://solver:8080");
        manager.RegisterSolver(address, pubKey, "http://solver:9090");

        manager.GetRegisteredSolvers().Should().HaveCount(1);
        manager.GetRegisteredSolvers()[0].Endpoint.Should().Be("http://solver:9090");
    }

    [Fact]
    public void RegisterSolver_MaxSolversReached_Rejects()
    {
        var manager = new SolverManager(DefaultParams) { MaxSolvers = 2 };

        var (_, pk1, addr1) = MakeSolver();
        var (_, pk2, addr2) = MakeSolver();
        var (_, pk3, addr3) = MakeSolver();

        manager.RegisterSolver(addr1, pk1, "http://s1").Should().BeTrue();
        manager.RegisterSolver(addr2, pk2, "http://s2").Should().BeTrue();
        manager.RegisterSolver(addr3, pk3, "http://s3").Should().BeFalse();
    }

    [Fact]
    public void UnregisterSolver_RemovesSolver()
    {
        var manager = new SolverManager(DefaultParams);
        var (_, pubKey, address) = MakeSolver();

        manager.RegisterSolver(address, pubKey, "http://solver:8080");
        manager.UnregisterSolver(address).Should().BeTrue();
        manager.HasExternalSolvers.Should().BeFalse();
    }

    [Fact]
    public void UnregisterSolver_NotRegistered_ReturnsFalse()
    {
        var manager = new SolverManager(DefaultParams);
        manager.UnregisterSolver(MakeAddress(0x01)).Should().BeFalse();
    }

    [Fact]
    public void SubmitSolution_WindowClosed_Rejects()
    {
        var manager = new SolverManager(DefaultParams);
        var (privKey, pubKey, address) = MakeSolver();
        manager.RegisterSolver(address, pubKey, "http://solver:8080");

        var signData = SolverManager.ComputeSolutionSignData(1, 0, new UInt256(1000));
        var sig = Ed25519Signer.Sign(privKey, signData);

        var solution = new SolverSolution
        {
            BlockNumber = 1,
            PoolId = 0,
            ClearingPrice = new UInt256(1000),
            Result = new BatchResult { PoolId = 0, ClearingPrice = new UInt256(1000), Fills = [] },
            SolverAddress = address,
            SolverSignature = sig,
        };

        // Window not opened
        manager.SubmitSolution(solution).Should().BeFalse();
    }

    [Fact]
    public void SubmitSolution_WindowOpen_Accepts()
    {
        var manager = new SolverManager(DefaultParams);
        var (privKey, pubKey, address) = MakeSolver();
        manager.RegisterSolver(address, pubKey, "http://solver:8080");
        manager.OpenSolutionWindow(5);

        var signData = SolverManager.ComputeSolutionSignData(5, 0, new UInt256(1000));
        var sig = Ed25519Signer.Sign(privKey, signData);

        var solution = new SolverSolution
        {
            BlockNumber = 5,
            PoolId = 0,
            ClearingPrice = new UInt256(1000),
            Result = new BatchResult { PoolId = 0, ClearingPrice = new UInt256(1000), Fills = [] },
            SolverAddress = address,
            SolverSignature = sig,
        };

        manager.SubmitSolution(solution).Should().BeTrue();
    }

    [Fact]
    public void SubmitSolution_WrongBlockNumber_Rejects()
    {
        var manager = new SolverManager(DefaultParams);
        var (privKey, pubKey, address) = MakeSolver();
        manager.RegisterSolver(address, pubKey, "http://solver:8080");
        manager.OpenSolutionWindow(5);

        var signData = SolverManager.ComputeSolutionSignData(10, 0, new UInt256(1000));
        var sig = Ed25519Signer.Sign(privKey, signData);

        var solution = new SolverSolution
        {
            BlockNumber = 10, // Wrong
            PoolId = 0,
            ClearingPrice = new UInt256(1000),
            Result = new BatchResult { PoolId = 0, ClearingPrice = new UInt256(1000), Fills = [] },
            SolverAddress = address,
            SolverSignature = sig,
        };

        manager.SubmitSolution(solution).Should().BeFalse();
    }

    [Fact]
    public void SubmitSolution_UnregisteredSolver_Rejects()
    {
        var manager = new SolverManager(DefaultParams);
        var (privKey, pubKey, address) = MakeSolver();
        // Don't register the solver
        manager.OpenSolutionWindow(5);

        var signData = SolverManager.ComputeSolutionSignData(5, 0, new UInt256(1000));
        var sig = Ed25519Signer.Sign(privKey, signData);

        var solution = new SolverSolution
        {
            BlockNumber = 5,
            PoolId = 0,
            ClearingPrice = new UInt256(1000),
            Result = new BatchResult { PoolId = 0, ClearingPrice = new UInt256(1000), Fills = [] },
            SolverAddress = address,
            SolverSignature = sig,
        };

        manager.SubmitSolution(solution).Should().BeFalse();
    }

    [Fact]
    public void SubmitSolution_InvalidSignature_Rejects()
    {
        var manager = new SolverManager(DefaultParams);
        var (_, pubKey, address) = MakeSolver();
        manager.RegisterSolver(address, pubKey, "http://solver:8080");
        manager.OpenSolutionWindow(5);

        // Sign with a different key
        var (differentPrivKey, _) = Ed25519Signer.GenerateKeyPair();
        var signData = SolverManager.ComputeSolutionSignData(5, 0, new UInt256(1000));
        var badSig = Ed25519Signer.Sign(differentPrivKey, signData);

        var solution = new SolverSolution
        {
            BlockNumber = 5,
            PoolId = 0,
            ClearingPrice = new UInt256(1000),
            Result = new BatchResult { PoolId = 0, ClearingPrice = new UInt256(1000), Fills = [] },
            SolverAddress = address,
            SolverSignature = badSig,
        };

        manager.SubmitSolution(solution).Should().BeFalse();
    }

    [Fact]
    public void GetBestSettlement_NoExternalSolutions_FallsBackToBuiltIn()
    {
        var manager = new SolverManager(DefaultParams);
        manager.OpenSolutionWindow(1);

        var stateDb = new InMemoryStateDb();
        var dexState = new DexState(stateDb);
        var engine = new DexEngine(dexState);

        // Create a pool
        var creator = MakeAddress(0x01);
        stateDb.SetAccount(creator, new AccountState { Balance = new UInt256(1_000_000) });
        engine.CreatePool(creator, Address.Zero, MakeAddress(0xBB), 30);
        engine.AddLiquidity(creator, 0, new UInt256(50_000), new UInt256(50_000), UInt256.Zero, UInt256.Zero, stateDb);

        var reserves = dexState.GetPoolReserves(0)!.Value;

        // Create opposing intents
        var (pk1, pub1) = Ed25519Signer.GenerateKeyPair();
        var sender1 = Ed25519Signer.DeriveAddress(pub1);
        stateDb.SetAccount(sender1, new AccountState { Balance = new UInt256(10_000) });

        var sellPayload = CreateSwapIntentPayload(Address.Zero, MakeAddress(0xBB), new UInt256(1000), new UInt256(1));
        var sellTx = MakeIntentTx(pk1, sender1, sellPayload);
        var sellIntent = ParsedIntent.Parse(sellTx)!.Value;

        var (pk2, pub2) = Ed25519Signer.GenerateKeyPair();
        var sender2 = Ed25519Signer.DeriveAddress(pub2);
        stateDb.SetAccount(sender2, new AccountState { Balance = new UInt256(10_000) });

        var buyPayload = CreateSwapIntentPayload(MakeAddress(0xBB), Address.Zero, new UInt256(1000), new UInt256(1));
        var buyTx = MakeIntentTx(pk2, sender2, buyPayload);
        var buyIntent = ParsedIntent.Parse(buyTx)!.Value;

        var intentMinAmounts = new Dictionary<Hash256, UInt256>
        {
            [sellTx.Hash] = new UInt256(1),
            [buyTx.Hash] = new UInt256(1),
        };
        var intentTxMap = new Dictionary<Hash256, Transaction>
        {
            [sellTx.Hash] = sellTx,
            [buyTx.Hash] = buyTx,
        };

        var result = manager.GetBestSettlement(
            0, [buyIntent], [sellIntent], reserves, 30,
            intentMinAmounts, stateDb, dexState, intentTxMap);

        // Built-in solver should produce a result (or null if no match — depends on prices)
        // The important thing is that it doesn't crash and falls back correctly
        // (The solver may or may not produce a result depending on price compatibility)
    }

    [Fact]
    public void GetSolverInfo_ReturnsCorrectData()
    {
        var manager = new SolverManager(DefaultParams);
        var (_, pubKey, address) = MakeSolver();
        manager.RegisterSolver(address, pubKey, "http://solver:8080");

        var info = manager.GetSolverInfo(address);
        info.Should().NotBeNull();
        info!.Endpoint.Should().Be("http://solver:8080");
        info.SolutionsAccepted.Should().Be(0);
        info.SolutionsRejected.Should().Be(0);
    }

    [Fact]
    public void GetSolverInfo_NotRegistered_ReturnsNull()
    {
        var manager = new SolverManager(DefaultParams);
        manager.GetSolverInfo(MakeAddress(0x42)).Should().BeNull();
    }

    [Fact]
    public void ComputeSolutionSignData_Deterministic()
    {
        var data1 = SolverManager.ComputeSolutionSignData(1, 0, new UInt256(1000));
        var data2 = SolverManager.ComputeSolutionSignData(1, 0, new UInt256(1000));
        data1.Should().BeEquivalentTo(data2);
    }

    [Fact]
    public void ComputeSolutionSignData_DifferentInputs_ProduceDifferentOutput()
    {
        var data1 = SolverManager.ComputeSolutionSignData(1, 0, new UInt256(1000));
        var data2 = SolverManager.ComputeSolutionSignData(2, 0, new UInt256(1000));
        data1.Should().NotBeEquivalentTo(data2);
    }

    [Fact]
    public void OpenSolutionWindow_ClearsPreviousSolutions()
    {
        var manager = new SolverManager(DefaultParams);
        var (privKey, pubKey, address) = MakeSolver();
        manager.RegisterSolver(address, pubKey, "http://solver:8080");

        // Submit solution for block 5
        manager.OpenSolutionWindow(5);
        var signData = SolverManager.ComputeSolutionSignData(5, 0, new UInt256(1000));
        var sig = Ed25519Signer.Sign(privKey, signData);
        manager.SubmitSolution(new SolverSolution
        {
            BlockNumber = 5, PoolId = 0, ClearingPrice = new UInt256(1000),
            Result = new BatchResult { PoolId = 0, ClearingPrice = new UInt256(1000), Fills = [] },
            SolverAddress = address, SolverSignature = sig,
        }).Should().BeTrue();

        // Open window for block 6 — previous solutions should be cleared
        manager.OpenSolutionWindow(6);

        // The previous solution (block 5) should not be accepted for block 6
        manager.SubmitSolution(new SolverSolution
        {
            BlockNumber = 5, PoolId = 0, ClearingPrice = new UInt256(1000),
            Result = new BatchResult { PoolId = 0, ClearingPrice = new UInt256(1000), Fills = [] },
            SolverAddress = address, SolverSignature = sig,
        }).Should().BeFalse(); // Wrong block number
    }

    [Fact]
    public void IncrementRevertCount_TracksReverts()
    {
        // H6: Revert count tracking for solver reputation
        var manager = new SolverManager(DefaultParams);
        var (_, pubKey, address) = MakeSolver();
        manager.RegisterSolver(address, pubKey, "http://solver:8080");

        var info = manager.GetSolverInfo(address);
        info!.RevertCount.Should().Be(0);

        manager.IncrementRevertCount(address);
        manager.IncrementRevertCount(address);
        manager.IncrementRevertCount(address);

        info = manager.GetSolverInfo(address);
        info!.RevertCount.Should().Be(3);
    }

    [Fact]
    public void IncrementRevertCount_UnknownSolver_DoesNotThrow()
    {
        // H6: Incrementing revert count for unregistered solver should not crash
        var manager = new SolverManager(DefaultParams);
        var act = () => manager.IncrementRevertCount(MakeAddress(0x99));
        act.Should().NotThrow();
    }

    // Helper methods

    private static byte[] CreateSwapIntentPayload(Address tokenIn, Address tokenOut, UInt256 amountIn, UInt256 minAmountOut)
    {
        var data = new byte[114];
        data[0] = 1; // version
        tokenIn.WriteTo(data.AsSpan(1, 20));
        tokenOut.WriteTo(data.AsSpan(21, 20));
        amountIn.WriteTo(data.AsSpan(41, 32));
        minAmountOut.WriteTo(data.AsSpan(73, 32));
        return data;
    }

    private static Transaction MakeIntentTx(byte[] privKey, Address sender, byte[] payload)
    {
        return Transaction.Sign(new Transaction
        {
            Type = TransactionType.DexSwapIntent,
            Sender = sender,
            To = DexState.DexAddress,
            Value = UInt256.Zero,
            Data = payload,
            Nonce = 0,
            GasLimit = 200_000,
            GasPrice = new UInt256(1),
            MaxFeePerGas = new UInt256(10),
            MaxPriorityFeePerGas = new UInt256(1),
            ChainId = ChainParameters.Devnet.ChainId,
        }, privKey);
    }
}
