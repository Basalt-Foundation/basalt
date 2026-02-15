namespace Basalt.Sdk.Contracts;

/// <summary>
/// Provides access to blockchain context within a smart contract.
/// These properties are populated by the runtime before contract execution.
/// </summary>
public static class Context
{
    /// <summary>
    /// Address of the account that called this contract.
    /// </summary>
    public static byte[] Caller { get; set; } = new byte[20];

    /// <summary>
    /// Address of the currently executing contract.
    /// </summary>
    public static byte[] Self { get; set; } = new byte[20];

    /// <summary>
    /// Value (in base units) sent with the current call.
    /// </summary>
    public static ulong TxValue { get; set; }

    /// <summary>
    /// Current block timestamp (Unix seconds).
    /// </summary>
    public static long BlockTimestamp { get; set; }

    /// <summary>
    /// Current block number.
    /// </summary>
    public static ulong BlockHeight { get; set; }

    /// <summary>
    /// Chain ID of the network.
    /// </summary>
    public static uint ChainId { get; set; }

    /// <summary>
    /// Remaining gas for the current execution.
    /// </summary>
    public static ulong GasRemaining { get; set; }

    /// <summary>
    /// Assert a condition; revert the transaction if it fails.
    /// </summary>
    public static void Require(bool condition, string message = "Require failed")
    {
        if (!condition)
            throw new ContractRevertException(message);
    }

    /// <summary>
    /// Unconditionally revert the transaction.
    /// </summary>
    public static void Revert(string message = "Reverted")
    {
        throw new ContractRevertException(message);
    }

    /// <summary>
    /// Emit a contract event.
    /// </summary>
    public static void Emit<TEvent>(TEvent evt) where TEvent : class
    {
        EventEmitted?.Invoke(typeof(TEvent).Name, evt);
    }

    /// <summary>
    /// Event handler for emitted events (set by the runtime).
    /// </summary>
    public static Action<string, object>? EventEmitted { get; set; }

    // ---- Cross-Contract Call Support ----

    /// <summary>
    /// Maximum cross-contract call depth to prevent infinite reentrancy.
    /// </summary>
    public const int MaxCallDepth = 8;

    /// <summary>
    /// Current call depth (0 = top-level call).
    /// </summary>
    public static int CallDepth { get; set; }

    /// <summary>
    /// Set of contract addresses currently on the call stack (reentrancy guard).
    /// </summary>
    public static HashSet<string> ReentrancyGuard { get; } = new();

    /// <summary>
    /// Delegate for cross-contract calls. Set by the runtime/test host.
    /// Parameters: targetAddress, methodName, args. Returns: result object or null.
    /// </summary>
    public static Func<byte[], string, object?[], object?>? CrossContractCallHandler { get; set; }

    /// <summary>
    /// Call another contract. Enforces reentrancy protection and call depth limits.
    /// </summary>
    public static T CallContract<T>(byte[] targetAddress, string methodName, params object?[] args)
    {
        Require(CallDepth < MaxCallDepth, "Max call depth exceeded");

        var targetKey = Convert.ToHexString(targetAddress);
        Require(!ReentrancyGuard.Contains(targetKey), "Reentrancy detected");
        Require(CrossContractCallHandler != null, "Cross-contract calls not available");

        // Save caller context
        var previousCaller = Caller;
        var previousSelf = Self;
        var previousDepth = CallDepth;

        try
        {
            ReentrancyGuard.Add(targetKey);
            CallDepth++;
            Caller = Self; // The calling contract becomes the caller
            Self = targetAddress;

            var result = CrossContractCallHandler!(targetAddress, methodName, args);
            return result is T typed ? typed : default!;
        }
        finally
        {
            // Restore caller context
            ReentrancyGuard.Remove(targetKey);
            CallDepth = previousDepth;
            Caller = previousCaller;
            Self = previousSelf;
        }
    }

    /// <summary>
    /// Call another contract (void return).
    /// </summary>
    public static void CallContract(byte[] targetAddress, string methodName, params object?[] args)
    {
        CallContract<object>(targetAddress, methodName, args);
    }

    /// <summary>
    /// Reset all context state (used between test runs).
    /// </summary>
    public static void Reset()
    {
        Caller = new byte[20];
        Self = new byte[20];
        TxValue = 0;
        BlockTimestamp = 0;
        BlockHeight = 0;
        ChainId = 0;
        GasRemaining = 0;
        CallDepth = 0;
        ReentrancyGuard.Clear();
        EventEmitted = null;
        CrossContractCallHandler = null;
    }
}

/// <summary>
/// Exception thrown when a contract reverts execution.
/// </summary>
public sealed class ContractRevertException : Exception
{
    public ContractRevertException(string message) : base(message) { }
}
