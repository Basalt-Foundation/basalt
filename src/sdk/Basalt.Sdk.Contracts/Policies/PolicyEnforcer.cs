using Basalt.Core;

namespace Basalt.Sdk.Contracts.Policies;

/// <summary>
/// Helper that manages a storage-backed list of policy contract addresses
/// and enforces them via cross-contract calls before transfers.
/// Embed this in any BST token contract to add policy support.
/// </summary>
/// <remarks>
/// Policies are called in registration order. A single denial reverts the transfer.
/// The admin address is stored externally by the owning token contract.
/// </remarks>
public sealed class PolicyEnforcer
{
    private readonly StorageList<ulong> _policySlots; // placeholder for count tracking
    private readonly StorageMap<string, string> _policies; // index -> policy address hex
    private readonly StorageValue<ulong> _policyCount;
    private readonly string _prefix;

    public PolicyEnforcer(string storagePrefix = "pol")
    {
        _prefix = storagePrefix;
        _policies = new StorageMap<string, string>($"{_prefix}_addr");
        _policyCount = new StorageValue<ulong>($"{_prefix}_count");
        _policySlots = new StorageList<ulong>($"{_prefix}_slots");
    }

    /// <summary>
    /// Number of registered policies.
    /// </summary>
    public ulong Count => _policyCount.Get();

    /// <summary>
    /// Get the policy address at a given index.
    /// </summary>
    public byte[] GetPolicy(ulong index)
    {
        Context.Require(index < Count, "Policy: index out of bounds");
        var hex = _policies.Get(index.ToString());
        return string.IsNullOrEmpty(hex) ? [] : Convert.FromHexString(hex);
    }

    /// <summary>
    /// Add a policy contract address. Caller must verify admin access.
    /// </summary>
    public void AddPolicy(byte[] policyAddress)
    {
        Context.Require(policyAddress.Length == 20, "Policy: invalid address");
        var hex = Convert.ToHexString(policyAddress);

        // Check for duplicates
        var count = Count;
        for (ulong i = 0; i < count; i++)
        {
            Context.Require(_policies.Get(i.ToString()) != hex, "Policy: already registered");
        }

        _policies.Set(count.ToString(), hex);
        _policyCount.Set(count + 1);

        Context.Emit(new PolicyAddedEvent
        {
            Token = Context.Self,
            Policy = policyAddress,
        });
    }

    /// <summary>
    /// Remove a policy contract address by shifting remaining entries. Caller must verify admin access.
    /// </summary>
    public void RemovePolicy(byte[] policyAddress)
    {
        var hex = Convert.ToHexString(policyAddress);
        var count = Count;
        bool found = false;

        for (ulong i = 0; i < count; i++)
        {
            if (!found && _policies.Get(i.ToString()) == hex)
            {
                found = true;
            }

            // Shift entries left after the removed one
            if (found && i + 1 < count)
            {
                _policies.Set(i.ToString(), _policies.Get((i + 1).ToString()));
            }
        }

        Context.Require(found, "Policy: not registered");
        _policies.Delete((count - 1).ToString());
        _policyCount.Set(count - 1);

        Context.Emit(new PolicyRemovedEvent
        {
            Token = Context.Self,
            Policy = policyAddress,
        });
    }

    /// <summary>
    /// Enforce all registered policies for a fungible transfer.
    /// Reverts if any policy denies the transfer.
    /// </summary>
    public void EnforceTransfer(byte[] from, byte[] to, UInt256 amount)
    {
        var count = Count;
        if (count == 0) return;

        var token = Context.Self;
        for (ulong i = 0; i < count; i++)
        {
            var policyHex = _policies.Get(i.ToString());
            if (string.IsNullOrEmpty(policyHex)) continue;

            var policyAddr = Convert.FromHexString(policyHex);
            var allowed = Context.CallContract<bool>(
                policyAddr, "CheckTransfer", token, from, to, amount);

            if (!allowed)
            {
                Context.Emit(new TransferDeniedEvent
                {
                    Token = token,
                    Policy = policyAddr,
                    From = from,
                    To = to,
                });
                Context.Revert("Policy: transfer denied by " + policyHex);
            }
        }
    }

    /// <summary>
    /// Enforce all registered policies for an NFT transfer.
    /// Reverts if any policy denies the transfer.
    /// </summary>
    public void EnforceNftTransfer(byte[] from, byte[] to, ulong tokenId)
    {
        var count = Count;
        if (count == 0) return;

        var token = Context.Self;
        for (ulong i = 0; i < count; i++)
        {
            var policyHex = _policies.Get(i.ToString());
            if (string.IsNullOrEmpty(policyHex)) continue;

            var policyAddr = Convert.FromHexString(policyHex);
            var allowed = Context.CallContract<bool>(
                policyAddr, "CheckNftTransfer", token, from, to, tokenId);

            if (!allowed)
            {
                Context.Emit(new TransferDeniedEvent
                {
                    Token = token,
                    Policy = policyAddr,
                    From = from,
                    To = to,
                });
                Context.Revert("Policy: NFT transfer denied by " + policyHex);
            }
        }
    }
}
