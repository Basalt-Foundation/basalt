using Basalt.Storage;

namespace Basalt.Node;

/// <summary>
/// Which state a compliance lookup should read.
///
/// Compliance resolves verifying keys out of contract storage, and the closure that does it had no way
/// to know which state was executing, so it read the live canonical reference. That is right at
/// finalization and wrong on replay: a sync batch runs on a fork that is not swapped to canonical until
/// the batch succeeds, so a key registered by an earlier block in the same batch is invisible to a later
/// one. The lookup returns null, compliance fails, and a transaction that finalized as success replays
/// as failure.
///
/// Holding it here rather than threading a state parameter through the verifier keeps the change to the
/// one thing that was wrong. The default is canonical, which is what every non-replay caller wants.
/// </summary>
public sealed class ExecutionStateRef(IStateDatabase canonical)
{
    private IStateDatabase _current = canonical;

    /// <summary>The state a lookup should read right now.</summary>
    public IStateDatabase Current => _current;

    /// <summary>
    /// Points lookups at <paramref name="state"/> until the returned scope is disposed.
    ///
    /// Restores the previous value rather than resetting to canonical, so nesting behaves.
    /// </summary>
    public IDisposable Use(IStateDatabase state)
    {
        var previous = _current;
        _current = state;
        return new Scope(this, previous);
    }

    private sealed class Scope(ExecutionStateRef owner, IStateDatabase previous) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            owner._current = previous;
        }
    }
}
