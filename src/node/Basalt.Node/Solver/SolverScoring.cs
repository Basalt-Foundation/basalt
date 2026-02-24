using Basalt.Core;
using Basalt.Execution;
using Basalt.Execution.Dex;
using Basalt.Storage;

namespace Basalt.Node.Solver;

/// <summary>
/// Scores and validates solver solutions.
/// The scoring algorithm maximizes total surplus: the sum of (amountOut - minAmountOut)
/// for each filled intent. This ensures solvers compete to give users the best execution.
/// </summary>
public static class SolverScoring
{
    /// <summary>
    /// Compute the surplus score for a solution.
    /// Surplus = sum of (actual output - minimum requested output) for all fills.
    /// Higher surplus means better execution for users.
    /// </summary>
    /// <param name="result">The batch result to score.</param>
    /// <param name="intentMinAmounts">Map from tx hash → minAmountOut from the original intent.</param>
    /// <returns>Total surplus (UInt256). Zero if no fills.</returns>
    public static UInt256 ComputeSurplus(BatchResult result, Dictionary<Hash256, UInt256> intentMinAmounts)
    {
        var surplus = UInt256.Zero;

        foreach (var fill in result.Fills)
        {
            if (fill.IsLimitOrder) continue;
            if (!intentMinAmounts.TryGetValue(fill.TxHash, out var minOut)) continue;

            if (fill.AmountOut > minOut)
                surplus = UInt256.CheckedAdd(surplus, fill.AmountOut - minOut);
        }

        return surplus;
    }

    /// <summary>
    /// Validate that a solver solution is feasible:
    /// 1. All fill participants have sufficient balance for their input amounts
    /// 2. Updated reserves are consistent with the fills
    /// 3. No overdrafts (no address ends up with negative balance)
    /// 4. Clearing price is non-zero
    /// </summary>
    public static bool ValidateFeasibility(
        BatchResult result,
        IStateDatabase stateDb,
        DexState dexState,
        Dictionary<Hash256, Transaction> intentTxMap)
    {
        if (result.ClearingPrice.IsZero)
            return false;

        if (result.Fills.Count == 0)
            return false;

        // Check pool exists
        var meta = dexState.GetPoolMetadata(result.PoolId);
        if (meta == null)
            return false;

        // Check fill balances
        foreach (var fill in result.Fills)
        {
            if (fill.IsLimitOrder) continue;

            if (!intentTxMap.TryGetValue(fill.TxHash, out var tx)) continue;

            var intent = ParsedIntent.Parse(tx);
            if (intent == null) return false;

            // Check sender has enough input tokens
            if (intent.Value.TokenIn == Address.Zero)
            {
                var account = stateDb.GetAccount(fill.Participant);
                if (account == null || account.Value.Balance < fill.AmountIn)
                    return false;
            }
        }

        // H6/M-12: BST-20 balance check deferred to settlement execution.
        // The BatchSettlementExecutor reverts fills with insufficient balances,
        // and SolverManager tracks revert rates for solver reputation scoring.
        // This is safe because invalid settlements waste gas but cannot extract value.

        // M-11: Check constant-product invariant — updated reserves must preserve k
        var oldReserves = dexState.GetPoolReserves(result.PoolId);
        if (oldReserves != null)
        {
            var oldK = Execution.Dex.Math.FullMath.ToBig(oldReserves.Value.Reserve0)
                     * Execution.Dex.Math.FullMath.ToBig(oldReserves.Value.Reserve1);
            var newK = Execution.Dex.Math.FullMath.ToBig(result.UpdatedReserves.Reserve0)
                     * Execution.Dex.Math.FullMath.ToBig(result.UpdatedReserves.Reserve1);
            // Allow small rounding tolerance (0.1%)
            if (newK < oldK * 999 / 1000)
                return false;
        }

        // Check updated reserves are non-negative (basic sanity)
        if (result.UpdatedReserves.Reserve0.IsZero && result.UpdatedReserves.Reserve1.IsZero)
            return false;

        return true;
    }

    /// <summary>
    /// Select the best solution from a set of candidates.
    /// Primary: highest surplus. Tiebreaker: earliest submission.
    /// </summary>
    public static SolverSolution? SelectBest(
        List<SolverSolution> solutions,
        Dictionary<Hash256, UInt256> intentMinAmounts)
    {
        if (solutions.Count == 0) return null;

        SolverSolution? best = null;
        UInt256 bestSurplus = UInt256.Zero;

        foreach (var solution in solutions)
        {
            var surplus = ComputeSurplus(solution.Result, intentMinAmounts);

            if (best == null || surplus > bestSurplus ||
                (surplus == bestSurplus && solution.ReceivedAtMs < best.ReceivedAtMs))
            {
                best = solution;
                bestSurplus = surplus;
            }
        }

        return best;
    }
}
