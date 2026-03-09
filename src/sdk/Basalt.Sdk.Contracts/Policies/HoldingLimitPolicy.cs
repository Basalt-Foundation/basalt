using Basalt.Core;

namespace Basalt.Sdk.Contracts.Policies;

/// <summary>
/// Policy contract that enforces maximum holding limits per address per token.
/// Deploy this contract, configure limits, then register it with BST tokens.
/// Type ID: 0x0008
/// </summary>
[BasaltContract]
public partial class HoldingLimitPolicy : ITransferPolicy
{
    private readonly StorageMap<string, string> _admin;
    private readonly StorageMap<string, UInt256> _limits; // "token:address" -> max balance
    private readonly StorageMap<string, UInt256> _defaultLimits; // token -> default max

    public HoldingLimitPolicy()
    {
        _admin = new StorageMap<string, string>("hlp_admin");
        _limits = new StorageMap<string, UInt256>("hlp_limits");
        _defaultLimits = new StorageMap<string, UInt256>("hlp_deflim");
        if (Context.IsDeploying)
            _admin.Set("owner", Convert.ToHexString(Context.Caller));
    }

    /// <summary>
    /// Set the default holding limit for a token. Zero means no limit.
    /// </summary>
    [BasaltEntrypoint]
    public void SetDefaultLimit(byte[] token, UInt256 maxBalance)
    {
        RequireAdmin();
        _defaultLimits.Set(Convert.ToHexString(token), maxBalance);
    }

    /// <summary>
    /// Set a per-address holding limit for a specific token. Zero means use default.
    /// </summary>
    [BasaltEntrypoint]
    public void SetAddressLimit(byte[] token, byte[] account, UInt256 maxBalance)
    {
        RequireAdmin();
        _limits.Set(LimitKey(token, account), maxBalance);
    }

    /// <summary>
    /// Query the effective limit for an address on a token.
    /// </summary>
    [BasaltView]
    public UInt256 GetEffectiveLimit(byte[] token, byte[] account)
    {
        var perAddr = _limits.Get(LimitKey(token, account));
        if (perAddr > 0) return perAddr;
        return _defaultLimits.Get(Convert.ToHexString(token));
    }

    /// <summary>
    /// ITransferPolicy implementation. Called by token contracts via cross-contract call.
    /// Queries the recipient's balance on the token and checks against the limit.
    /// </summary>
    [BasaltView]
    public bool CheckTransfer(byte[] token, byte[] from, byte[] to, UInt256 amount)
    {
        var limit = GetEffectiveLimit(token, to);
        if (limit.IsZero) return true; // No limit configured

        // Query recipient's current balance on the token
        var currentBalance = Context.CallContract<UInt256>(token, "BalanceOf", to);
        var newBalance = UInt256.CheckedAdd(currentBalance, amount);

        return newBalance <= limit;
    }

    private void RequireAdmin()
    {
        Context.Require(
            Convert.ToHexString(Context.Caller) == _admin.Get("owner"),
            "HoldingLimit: not admin");
    }

    private static string LimitKey(byte[] token, byte[] account) =>
        Convert.ToHexString(token) + ":" + Convert.ToHexString(account);
}
