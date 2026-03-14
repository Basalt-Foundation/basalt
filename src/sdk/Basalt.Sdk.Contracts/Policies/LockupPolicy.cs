using Basalt.Core;

namespace Basalt.Sdk.Contracts.Policies;

/// <summary>
/// Policy contract that enforces time-based transfer lockups per address per token.
/// Tokens cannot be transferred until the lockup period expires (checked against block timestamp).
/// Type ID: 0x0009
/// </summary>
[BasaltContract]
public partial class LockupPolicy : ITransferPolicy, INftTransferPolicy
{
    private readonly StorageMap<string, string> _admin;
    private readonly StorageMap<string, long> _lockups; // "token:address" -> unlock timestamp

    public LockupPolicy()
    {
        _admin = new StorageMap<string, string>("lkp_admin");
        _lockups = new StorageMap<string, long>("lkp_locks");
        if (Context.IsDeploying)
            _admin.Set("owner", Convert.ToHexString(Context.Caller));
    }

    /// <summary>
    /// Set a lockup expiry for an address on a token. The address cannot send
    /// tokens from this token contract until after the given timestamp (Unix seconds).
    /// </summary>
    [BasaltEntrypoint]
    public void SetLockup(byte[] token, byte[] account, long unlockTimestamp)
    {
        RequireAdmin();
        Context.Require(unlockTimestamp > 0, "Lockup: invalid timestamp");
        _lockups.Set(LockKey(token, account), unlockTimestamp);
    }

    /// <summary>
    /// Remove a lockup for an address.
    /// </summary>
    [BasaltEntrypoint]
    public void RemoveLockup(byte[] token, byte[] account)
    {
        RequireAdmin();
        _lockups.Delete(LockKey(token, account));
    }

    /// <summary>
    /// Query the unlock timestamp for an address on a token. Returns 0 if no lockup.
    /// </summary>
    [BasaltView]
    public long GetUnlockTime(byte[] token, byte[] account)
    {
        return _lockups.Get(LockKey(token, account));
    }

    /// <summary>
    /// Check if an address is currently locked for a token.
    /// </summary>
    [BasaltView]
    public bool IsLocked(byte[] token, byte[] account)
    {
        var unlock = _lockups.Get(LockKey(token, account));
        return unlock > 0 && Context.BlockTimestamp < unlock;
    }

    [BasaltView]
    public bool CheckTransfer(byte[] token, byte[] from, byte[] to, UInt256 amount)
    {
        var unlock = _lockups.Get(LockKey(token, from));
        if (unlock == 0) return true; // No lockup
        return Context.BlockTimestamp >= unlock;
    }

    [BasaltView]
    public bool CheckNftTransfer(byte[] token, byte[] from, byte[] to, ulong tokenId)
    {
        var unlock = _lockups.Get(LockKey(token, from));
        if (unlock == 0) return true;
        return Context.BlockTimestamp >= unlock;
    }

    /// <summary>
    /// Propose a new admin. The new admin must call AcceptAdmin to complete the transfer.
    /// </summary>
    [BasaltEntrypoint]
    public void TransferAdmin(byte[] newAdmin)
    {
        RequireAdmin();
        Context.Require(newAdmin.Length == 20, "Lockup: invalid address");
        _admin.Set("pending", Convert.ToHexString(newAdmin));
    }

    /// <summary>
    /// Accept admin role. Must be called by the pending admin.
    /// </summary>
    [BasaltEntrypoint]
    public void AcceptAdmin()
    {
        var pending = _admin.Get("pending");
        Context.Require(!string.IsNullOrEmpty(pending), "Lockup: no pending admin");
        Context.Require(Convert.ToHexString(Context.Caller) == pending, "Lockup: not pending admin");
        _admin.Set("owner", pending);
        _admin.Delete("pending");
    }

    private void RequireAdmin()
    {
        Context.Require(
            Convert.ToHexString(Context.Caller) == _admin.Get("owner"),
            "Lockup: not admin");
    }

    private static string LockKey(byte[] token, byte[] account) =>
        Convert.ToHexString(token) + ":" + Convert.ToHexString(account);
}
