using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution;
using Basalt.Execution.Dex;
using Basalt.Storage;
using Microsoft.Extensions.Logging;

namespace Basalt.Node.Solver;

/// <summary>
/// Manages registered solvers, collects their solutions during the solution window,
/// and selects the best solution for block building.
///
/// Flow:
/// 1. External solvers register via P2P or REST API
/// 2. When proposer starts building a block, it opens a solution window
/// 3. Solvers submit solutions within the window (default 500ms)
/// 4. Proposer selects the best solution (highest surplus)
/// 5. If no valid external solution, falls back to built-in BatchAuctionSolver
/// </summary>
public sealed class SolverManager
{
    private readonly object _lock = new();
    private readonly Dictionary<Address, RegisteredSolver> _solvers = new();
    private readonly ILogger<SolverManager>? _logger;
    private readonly ChainParameters _chainParams;

    /// <summary>
    /// Duration in milliseconds that the proposer waits for solver solutions.
    /// </summary>
    public int SolutionWindowMs { get; init; } = 500;

    /// <summary>
    /// Maximum number of registered solvers.
    /// </summary>
    public int MaxSolvers { get; init; } = 32;

    // Per-block solution collection
    private ulong _currentBlockNumber;
    private readonly List<SolverSolution> _pendingSolutions = new();
    private bool _windowOpen;

    public SolverManager(ChainParameters chainParams, ILogger<SolverManager>? logger = null)
    {
        _chainParams = chainParams;
        _logger = logger;
    }

    /// <summary>
    /// Register a new solver. Returns true if registration succeeds.
    /// </summary>
    public bool RegisterSolver(Address solverAddress, PublicKey publicKey, string endpoint)
    {
        lock (_lock)
        {
            if (_solvers.Count >= MaxSolvers && !_solvers.ContainsKey(solverAddress))
                return false;

            _solvers[solverAddress] = new RegisteredSolver
            {
                Address = solverAddress,
                PublicKey = publicKey,
                Endpoint = endpoint,
                RegisteredAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                SolutionsAccepted = 0,
                SolutionsRejected = 0,
            };

            _logger?.LogInformation("Solver registered: {Address} at {Endpoint}",
                solverAddress, endpoint);
            return true;
        }
    }

    /// <summary>
    /// Unregister a solver.
    /// </summary>
    public bool UnregisterSolver(Address solverAddress)
    {
        lock (_lock)
        {
            var removed = _solvers.Remove(solverAddress);
            if (removed)
                _logger?.LogInformation("Solver unregistered: {Address}", solverAddress);
            return removed;
        }
    }

    /// <summary>
    /// Get information about all registered solvers.
    /// </summary>
    public List<RegisteredSolver> GetRegisteredSolvers()
    {
        lock (_lock)
        {
            return _solvers.Values.ToList();
        }
    }

    /// <summary>
    /// True if there are any registered external solvers.
    /// </summary>
    public bool HasExternalSolvers
    {
        get { lock (_lock) return _solvers.Count > 0; }
    }

    /// <summary>
    /// Open the solution window for a new block.
    /// Called by the proposer at the start of block building.
    /// </summary>
    public void OpenSolutionWindow(ulong blockNumber)
    {
        lock (_lock)
        {
            _currentBlockNumber = blockNumber;
            _pendingSolutions.Clear();
            _windowOpen = true;
            _logger?.LogDebug("Solution window opened for block #{Block}", blockNumber);
        }
    }

    /// <summary>
    /// Submit a solver solution. Returns true if the solution was accepted for consideration.
    /// </summary>
    public bool SubmitSolution(SolverSolution solution)
    {
        lock (_lock)
        {
            if (!_windowOpen)
            {
                _logger?.LogDebug("Solution rejected: window closed");
                return false;
            }

            if (solution.BlockNumber != _currentBlockNumber)
            {
                _logger?.LogDebug("Solution rejected: wrong block number (expected {Expected}, got {Got})",
                    _currentBlockNumber, solution.BlockNumber);
                return false;
            }

            if (!_solvers.ContainsKey(solution.SolverAddress))
            {
                _logger?.LogDebug("Solution rejected: solver {Address} not registered",
                    solution.SolverAddress);
                return false;
            }

            // M-14: Reject solutions with excessive fills to prevent DoS
            const int MaxFillsPerSolution = 10_000;
            if (solution.Result.Fills.Count > MaxFillsPerSolution)
            {
                _logger?.LogWarning("Solution rejected: too many fills ({Count} > {Max}) from {Address}",
                    solution.Result.Fills.Count, MaxFillsPerSolution, solution.SolverAddress);
                return false;
            }

            // H-09: Verify solution signature (includes fills hash)
            var signData = ComputeSolutionSignData(
                solution.BlockNumber, solution.PoolId, solution.ClearingPrice, solution.Result.Fills);
            var solver = _solvers[solution.SolverAddress];
            if (!Ed25519Signer.Verify(solver.PublicKey, signData, solution.SolverSignature))
            {
                _logger?.LogWarning("Solution rejected: invalid signature from {Address}",
                    solution.SolverAddress);
                if (_solvers.TryGetValue(solution.SolverAddress, out var s))
                    s.SolutionsRejected++;
                return false;
            }

            solution = new SolverSolution
            {
                BlockNumber = solution.BlockNumber,
                PoolId = solution.PoolId,
                ClearingPrice = solution.ClearingPrice,
                Result = solution.Result,
                SolverAddress = solution.SolverAddress,
                SolverSignature = solution.SolverSignature,
                ReceivedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };

            _pendingSolutions.Add(solution);
            _logger?.LogDebug("Solution accepted from {Address} for pool {Pool}",
                solution.SolverAddress, solution.PoolId);
            return true;
        }
    }

    /// <summary>
    /// Close the solution window and select the best solution for a given pool.
    /// Falls back to the built-in solver if no valid external solution exists.
    /// </summary>
    /// <param name="poolId">The pool to settle.</param>
    /// <param name="buyIntents">Buy-side intents.</param>
    /// <param name="sellIntents">Sell-side intents.</param>
    /// <param name="reserves">Current pool reserves.</param>
    /// <param name="feeBps">Pool fee in basis points.</param>
    /// <param name="intentMinAmounts">Map from tx hash → minAmountOut.</param>
    /// <param name="stateDb">State database for feasibility validation.</param>
    /// <param name="dexState">DEX state for pool lookup.</param>
    /// <param name="intentTxMap">Map from tx hash → original transaction.</param>
    /// <returns>The best settlement result, or null if no settlement possible.</returns>
    public BatchResult? GetBestSettlement(
        ulong poolId,
        List<ParsedIntent> buyIntents,
        List<ParsedIntent> sellIntents,
        PoolReserves reserves,
        uint feeBps,
        Dictionary<Hash256, UInt256> intentMinAmounts,
        IStateDatabase stateDb,
        DexState dexState,
        Dictionary<Hash256, Transaction> intentTxMap)
    {
        List<SolverSolution> candidates;
        lock (_lock)
        {
            _windowOpen = false;
            candidates = _pendingSolutions
                .Where(s => s.PoolId == poolId)
                .ToList();
        }

        // Validate and score external solutions
        var validSolutions = new List<SolverSolution>();
        foreach (var solution in candidates)
        {
            if (SolverScoring.ValidateFeasibility(solution.Result, stateDb, dexState, intentTxMap))
            {
                validSolutions.Add(solution);
                lock (_lock)
                {
                    if (_solvers.TryGetValue(solution.SolverAddress, out var solver))
                        solver.SolutionsAccepted++;
                }
            }
            else
            {
                _logger?.LogWarning("External solution from {Address} failed feasibility check",
                    solution.SolverAddress);
                lock (_lock)
                {
                    if (_solvers.TryGetValue(solution.SolverAddress, out var solver))
                        solver.SolutionsRejected++;
                }
            }
        }

        // Select best external solution
        var bestExternal = SolverScoring.SelectBest(validSolutions, intentMinAmounts);

        // Compute built-in solution for comparison
        var builtInResult = BatchAuctionSolver.ComputeSettlement(
            buyIntents, sellIntents, [], [], reserves, feeBps, poolId);

        if (bestExternal == null)
        {
            _logger?.LogDebug("No valid external solutions; using built-in solver for pool {Pool}", poolId);
            return builtInResult;
        }

        if (builtInResult == null)
        {
            _logger?.LogInformation("Using external solution from {Address} for pool {Pool} (built-in produced no result)",
                bestExternal.SolverAddress, poolId);
            bestExternal.Result.WinningSolver = bestExternal.SolverAddress;
            return bestExternal.Result;
        }

        // Compare surplus: use whichever is better
        var externalSurplus = SolverScoring.ComputeSurplus(bestExternal.Result, intentMinAmounts);
        var builtInSurplus = SolverScoring.ComputeSurplus(builtInResult, intentMinAmounts);

        if (externalSurplus > builtInSurplus)
        {
            _logger?.LogInformation(
                "External solver {Address} wins for pool {Pool}: surplus {ExtSurplus} > built-in {BuiltInSurplus}",
                bestExternal.SolverAddress, poolId, externalSurplus, builtInSurplus);
            bestExternal.Result.WinningSolver = bestExternal.SolverAddress;
            return bestExternal.Result;
        }

        _logger?.LogDebug("Built-in solver wins for pool {Pool}: surplus {BuiltInSurplus} >= external {ExtSurplus}",
            poolId, builtInSurplus, externalSurplus);
        return builtInResult;
    }

    /// <summary>
    /// H-09: Compute the data that a solver must sign to authenticate their solution.
    /// Includes a hash of all fills to prevent tampering.
    /// BLAKE3(blockNumber BE || poolId BE || clearingPrice LE 32B || fillsHash 32B)
    /// </summary>
    public static byte[] ComputeSolutionSignData(ulong blockNumber, ulong poolId, UInt256 clearingPrice, List<FillRecord>? fills = null)
    {
        // H-09: Hash fills data for signature coverage
        // CR-2: Include IsBuy, IsLimitOrder, OrderId to prevent field tampering
        byte[] fillsHash;
        if (fills != null && fills.Count > 0)
        {
            // Each fill: [20B participant][32B amountIn][32B amountOut][1B isBuy][1B isLimitOrder][8B orderId] = 94 bytes
            var fillsData = new byte[fills.Count * 94];
            for (int i = 0; i < fills.Count; i++)
            {
                var offset = i * 94;
                fills[i].Participant.WriteTo(fillsData.AsSpan(offset, 20));
                fills[i].AmountIn.WriteTo(fillsData.AsSpan(offset + 20, 32));
                fills[i].AmountOut.WriteTo(fillsData.AsSpan(offset + 52, 32));
                fillsData[offset + 84] = fills[i].IsBuy ? (byte)1 : (byte)0;
                fillsData[offset + 85] = fills[i].IsLimitOrder ? (byte)1 : (byte)0;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(
                    fillsData.AsSpan(offset + 86, 8), fills[i].OrderId);
            }
            fillsHash = Blake3Hasher.Hash(fillsData).ToArray();
        }
        else
        {
            fillsHash = new byte[32]; // zero hash for empty fills
        }

        var data = new byte[8 + 8 + 32 + 32];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(0, 8), blockNumber);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(8, 8), poolId);
        clearingPrice.WriteTo(data.AsSpan(16, 32));
        fillsHash.CopyTo(data.AsSpan(48, 32));
        return Blake3Hasher.Hash(data).ToArray();
    }

    /// <summary>
    /// H6: Increment the revert count for a solver whose settlement execution failed.
    /// Called by the block builder when a solver's settlement reverts.
    /// </summary>
    public void IncrementRevertCount(Address solverAddress)
    {
        lock (_lock)
        {
            if (_solvers.TryGetValue(solverAddress, out var solver))
                solver.RevertCount++;
        }
    }

    /// <summary>
    /// Get statistics for a registered solver.
    /// </summary>
    public RegisteredSolver? GetSolverInfo(Address solverAddress)
    {
        lock (_lock)
        {
            return _solvers.TryGetValue(solverAddress, out var solver) ? solver : null;
        }
    }
}

/// <summary>
/// Information about a registered solver.
/// </summary>
public sealed class RegisteredSolver
{
    public Address Address { get; init; }
    public PublicKey PublicKey { get; init; }
    public string Endpoint { get; init; } = "";
    public long RegisteredAtMs { get; init; }
    public int SolutionsAccepted { get; set; }
    public int SolutionsRejected { get; set; }
    /// <summary>H6: Track settlement execution reverts for reputation scoring.</summary>
    public int RevertCount { get; set; }
}
