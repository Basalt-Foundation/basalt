using Basalt.Core;
using Basalt.Execution.Dex;

namespace Basalt.Node.Solver;

/// <summary>
/// Represents a solver's proposed settlement for a batch of swap intents.
/// External solvers compute these off-chain and submit them to the proposer.
/// </summary>
public sealed class SolverSolution
{
    /// <summary>The block number this solution targets.</summary>
    public ulong BlockNumber { get; init; }

    /// <summary>The pool ID this settlement applies to.</summary>
    public ulong PoolId { get; init; }

    /// <summary>The proposed clearing price (token1-per-token0 scaled by 2^64).</summary>
    public UInt256 ClearingPrice { get; init; }

    /// <summary>The batch result containing fills and updated reserves.</summary>
    public BatchResult Result { get; init; } = null!;

    /// <summary>Address of the solver that submitted this solution.</summary>
    public Address SolverAddress { get; init; }

    /// <summary>Ed25519 signature of BLAKE3(blockNumber || poolId || clearingPrice).</summary>
    public Signature SolverSignature { get; init; }

    /// <summary>Timestamp when the solution was received.</summary>
    public long ReceivedAtMs { get; init; }
}
