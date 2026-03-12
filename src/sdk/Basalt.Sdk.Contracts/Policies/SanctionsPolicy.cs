using Basalt.Core;

namespace Basalt.Sdk.Contracts.Policies;

/// <summary>
/// Policy contract that maintains an on-chain sanctions list.
/// Denies transfers where either sender or receiver is sanctioned.
/// Type ID: 0x000B
/// </summary>
[BasaltContract]
public partial class SanctionsPolicy : ITransferPolicy, INftTransferPolicy
{
    private readonly StorageMap<string, string> _admin;
    private readonly StorageMap<string, bool> _sanctioned;

    public SanctionsPolicy()
    {
        _admin = new StorageMap<string, string>("san_admin");
        _sanctioned = new StorageMap<string, bool>("san_list");
        if (Context.IsDeploying)
            _admin.Set("owner", Convert.ToHexString(Context.Caller));
    }

    [BasaltEntrypoint]
    public void AddSanction(byte[] account)
    {
        RequireAdmin();
        _sanctioned.Set(Convert.ToHexString(account), true);
    }

    [BasaltEntrypoint]
    public void RemoveSanction(byte[] account)
    {
        RequireAdmin();
        _sanctioned.Delete(Convert.ToHexString(account));
    }

    [BasaltView]
    public bool IsSanctioned(byte[] account)
    {
        return _sanctioned.Get(Convert.ToHexString(account));
    }

    [BasaltView]
    public bool CheckTransfer(byte[] token, byte[] from, byte[] to, UInt256 amount)
    {
        if (_sanctioned.Get(Convert.ToHexString(from))) return false;
        if (_sanctioned.Get(Convert.ToHexString(to))) return false;
        return true;
    }

    [BasaltView]
    public bool CheckNftTransfer(byte[] token, byte[] from, byte[] to, ulong tokenId)
    {
        if (_sanctioned.Get(Convert.ToHexString(from))) return false;
        if (_sanctioned.Get(Convert.ToHexString(to))) return false;
        return true;
    }

    /// <summary>
    /// Propose a new admin. The new admin must call AcceptAdmin to complete the transfer.
    /// </summary>
    [BasaltEntrypoint]
    public void TransferAdmin(byte[] newAdmin)
    {
        RequireAdmin();
        Context.Require(newAdmin.Length == 20, "Sanctions: invalid address");
        _admin.Set("pending", Convert.ToHexString(newAdmin));
    }

    /// <summary>
    /// Accept admin role. Must be called by the pending admin.
    /// </summary>
    [BasaltEntrypoint]
    public void AcceptAdmin()
    {
        var pending = _admin.Get("pending");
        Context.Require(!string.IsNullOrEmpty(pending), "Sanctions: no pending admin");
        Context.Require(Convert.ToHexString(Context.Caller) == pending, "Sanctions: not pending admin");
        _admin.Set("owner", pending);
        _admin.Delete("pending");
    }

    private void RequireAdmin()
    {
        Context.Require(
            Convert.ToHexString(Context.Caller) == _admin.Get("owner"),
            "Sanctions: not admin");
    }
}
