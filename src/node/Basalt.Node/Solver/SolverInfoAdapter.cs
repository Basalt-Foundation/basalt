using Basalt.Api.Rest;
using Basalt.Core;

namespace Basalt.Node.Solver;

/// <summary>
/// Adapts the SolverManager and Mempool into the REST API's ISolverInfoProvider interface.
/// The SolverManager reference is set lazily because NodeCoordinator is constructed
/// after the REST API endpoints are mapped.
/// </summary>
public sealed class SolverInfoAdapter : ISolverInfoProvider
{
    private SolverManager? _solverManager;
    private Execution.Mempool? _mempool;

    public void SetSolverManager(SolverManager manager) => _solverManager = manager;
    public void SetMempool(Execution.Mempool mempool) => _mempool = mempool;

    public SolverInfoResponse[] GetRegisteredSolvers()
    {
        if (_solverManager == null) return [];

        return _solverManager.GetRegisteredSolvers()
            .Select(s => new SolverInfoResponse
            {
                Address = s.Address.ToHexString(),
                Endpoint = s.Endpoint,
                RegisteredAt = s.RegisteredAtMs,
                SolutionsAccepted = s.SolutionsAccepted,
                SolutionsRejected = s.SolutionsRejected,
            })
            .ToArray();
    }

    public bool RegisterSolver(Address address, PublicKey publicKey, string endpoint)
    {
        return _solverManager?.RegisterSolver(address, publicKey, endpoint) ?? false;
    }

    public Hash256[] GetPendingIntentHashes()
    {
        if (_mempool == null) return [];

        var intents = _mempool.GetPendingDexIntents(100);
        return intents.Select(tx => tx.Hash).ToArray();
    }
}
