# Time-Locked Vault

## Category

Finance / Security

## Summary

A time-locked vault contract that allows users to deposit tokens with configurable unlock schedules including linear vesting, cliff-based release, and milestone-based unlocking. It supports beneficiary designation, emergency unlock via governance vote, estate planning use cases, and both irrevocable and admin-revocable deposit modes. The vault provides a reliable, trust-minimized way to lock value for future release, serving use cases from employee token vesting to long-term savings to inheritance planning.

## Why It's Useful

- **Token vesting**: Startups and protocols distributing tokens to team members, advisors, and investors need enforceable vesting schedules that cannot be circumvented by any single party.
- **Long-term savings**: Users who want to commit to holding tokens for a specific period can use the vault as a self-imposed discipline mechanism, protecting against impulsive selling.
- **Estate planning**: Combined with beneficiary designation, the vault enables on-chain inheritance where assets automatically become accessible to heirs after a specified date or condition.
- **Regulatory compliance**: Some token distributions (e.g., Reg D securities) require mandatory lock-up periods. The vault enforces these programmatically.
- **Trust-minimized escrow**: Unlike the simple Escrow contract, the timelock vault supports complex release schedules and multiple beneficiaries with configurable splits.
- **Protocol incentive alignment**: Protocols can lock governance tokens for long periods to ensure that token holders have skin in the game, aligning incentives with the project's long-term success.
- **Grant disbursement**: Milestone-based unlocking allows grant programs to release funds as recipients complete deliverables.

## Key Features

- Linear vesting: tokens unlock gradually over a time period, claimable proportionally at any block
- Cliff vesting: no tokens are claimable until a cliff block is reached, after which a percentage unlocks immediately and the remainder vests linearly
- Milestone-based release: tokens unlock in discrete tranches when specific conditions are met (governance-verified milestones)
- Beneficiary designation: the vault owner can specify one or more beneficiaries with percentage-based splits
- Irrevocable mode: once deposited, the owner cannot withdraw or modify the vault parameters; only the beneficiary can claim after unlock
- Admin-revocable mode: the depositor retains the ability to cancel the vault and reclaim unvested tokens (useful for employee vesting with termination clause)
- Emergency unlock: a governance vote can force-unlock a vault in extraordinary circumstances (e.g., protocol migration, legal order)
- Multiple deposits per vault: additional tokens can be added to an existing vault, extending the total locked amount
- Partial claims: beneficiaries can claim any amount up to their vested balance at any time
- Vault metadata: optional description string for human-readable context (e.g., "Series A investor 12-month lockup")
- Transferable beneficiary: beneficiary can transfer their claim rights to another address

## Basalt-Specific Advantages

- **ZK compliance verification**: Before releasing funds, the vault can verify that the beneficiary holds valid ZK compliance proofs via the SchemaRegistry, ensuring that token releases only go to compliant recipients. This is critical for securities-type lockups.
- **BST-3525 SFT representation of vault positions**: Each vault position can be represented as a BST-3525 semi-fungible token where the slot represents the vesting schedule type and the value represents the locked amount. This makes vault positions tradeable on secondary markets (subject to the irrevocable/revocable flag).
- **BST-4626 vault composability**: Locked tokens can be deposited into BST-4626 yield vaults to earn returns during the lock period, so locked capital is not entirely idle.
- **Ed25519 beneficiary verification**: Beneficiary claims are verified using Basalt's native Ed25519 signatures, providing a clean and fast verification path without precompile dependencies.
- **BLAKE3 milestone hashing**: Milestone identifiers are BLAKE3 hashes of milestone descriptions, providing a collision-resistant way to reference off-chain deliverables on-chain.
- **AOT-compiled vesting math**: Linear vesting calculations (proportional unlock over time) are tight arithmetic operations that execute deterministically under AOT compilation with no JIT variance.
- **Governance emergency unlock**: Basalt's existing Governance contract provides the democratic mechanism for emergency unlocks without requiring a separate voting system.

## Token Standards Used

- **BST-20**: Primary token type for fungible token lockups
- **BST-721**: NFT lockup support (e.g., locking a valuable NFT for a period)
- **BST-3525**: Vault positions can be minted as SFTs for secondary market trading of locked positions
- **BST-4626**: Yield-bearing lockups where locked tokens are deposited into vaults during the lock period

## Integration Points

- **Governance (0x...1005 area)**: Emergency unlock proposals are validated against the Governance contract. Milestone verification for milestone-based vaults can also be governance-gated.
- **Escrow (0x...1003)**: Complements the Escrow contract for more complex release schedules. The Escrow is simple (single release block), while the Timelock Vault supports linear/cliff/milestone patterns.
- **StakingPool (0x...1005)**: Locked tokens can be staked during the lock period to earn yield, maximizing capital efficiency.
- **SchemaRegistry (0x...1006)**: ZK compliance verification before fund release to beneficiaries.
- **IssuerRegistry (0x...1007)**: Verifiable credentials for beneficiary identity verification.
- **BNS (0x...1002)**: Vault beneficiaries can be referenced by BNS name instead of raw address.

## Technical Sketch

```csharp
using Basalt.Core;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Time-Locked Vault -- configurable unlock schedules with linear, cliff,
/// and milestone-based release. Supports beneficiary designation, revocability,
/// and governance emergency unlock.
/// </summary>
[BasaltContract]
public partial class TimelockVault
{
    // --- Vault core ---
    private readonly StorageValue<ulong> _nextVaultId;
    private readonly StorageMap<string, string> _vaultOwners;           // vaultId -> owner hex
    private readonly StorageMap<string, string> _vaultBeneficiaries;    // vaultId -> beneficiary hex
    private readonly StorageMap<string, UInt256> _vaultAmounts;          // vaultId -> locked amount
    private readonly StorageMap<string, UInt256> _vaultClaimed;          // vaultId -> claimed so far
    private readonly StorageMap<string, string> _vaultStatus;            // vaultId -> "active"|"revoked"|"completed"|"emergency_unlocked"
    private readonly StorageMap<string, string> _vaultDescriptions;      // vaultId -> description

    // --- Schedule parameters ---
    private readonly StorageMap<string, string> _vaultScheduleType;     // vaultId -> "linear"|"cliff"|"milestone"
    private readonly StorageMap<string, ulong> _vaultStartBlock;         // vaultId -> start block
    private readonly StorageMap<string, ulong> _vaultEndBlock;           // vaultId -> end block (linear/cliff)
    private readonly StorageMap<string, ulong> _vaultCliffBlock;         // vaultId -> cliff block (cliff type)
    private readonly StorageMap<string, uint> _vaultCliffPercentageBps;  // vaultId -> cliff release (basis points)
    private readonly StorageMap<string, bool> _vaultRevocable;           // vaultId -> can owner revoke?

    // --- Milestone vaults ---
    private readonly StorageMap<string, uint> _vaultMilestoneCount;     // vaultId -> total milestones
    private readonly StorageMap<string, UInt256> _milestoneAmounts;     // "vaultId:milestoneIdx" -> amount
    private readonly StorageMap<string, bool> _milestoneCompleted;      // "vaultId:milestoneIdx" -> completed
    private readonly StorageMap<string, string> _milestoneDescriptions; // "vaultId:milestoneIdx" -> description hash

    // --- Beneficiary splits (multi-beneficiary) ---
    private readonly StorageMap<string, uint> _vaultBeneficiaryCount;   // vaultId -> count
    private readonly StorageMap<string, string> _beneficiaryAddresses;  // "vaultId:idx" -> address hex
    private readonly StorageMap<string, uint> _beneficiaryShareBps;     // "vaultId:idx" -> share in basis points

    // --- Governance ---
    private readonly byte[] _governanceAddress;

    public TimelockVault(byte[] governanceAddress)
    {
        _governanceAddress = governanceAddress;

        _nextVaultId = new StorageValue<ulong>("tv_next");
        _vaultOwners = new StorageMap<string, string>("tv_own");
        _vaultBeneficiaries = new StorageMap<string, string>("tv_ben");
        _vaultAmounts = new StorageMap<string, UInt256>("tv_amt");
        _vaultClaimed = new StorageMap<string, UInt256>("tv_clm");
        _vaultStatus = new StorageMap<string, string>("tv_sts");
        _vaultDescriptions = new StorageMap<string, string>("tv_desc");
        _vaultScheduleType = new StorageMap<string, string>("tv_sched");
        _vaultStartBlock = new StorageMap<string, ulong>("tv_start");
        _vaultEndBlock = new StorageMap<string, ulong>("tv_end");
        _vaultCliffBlock = new StorageMap<string, ulong>("tv_cliff");
        _vaultCliffPercentageBps = new StorageMap<string, uint>("tv_cliffpct");
        _vaultRevocable = new StorageMap<string, bool>("tv_rev");
        _vaultMilestoneCount = new StorageMap<string, uint>("tv_mcnt");
        _milestoneAmounts = new StorageMap<string, UInt256>("tv_mamt");
        _milestoneCompleted = new StorageMap<string, bool>("tv_mdone");
        _milestoneDescriptions = new StorageMap<string, string>("tv_mdesc");
        _vaultBeneficiaryCount = new StorageMap<string, uint>("tv_bcnt");
        _beneficiaryAddresses = new StorageMap<string, string>("tv_baddr");
        _beneficiaryShareBps = new StorageMap<string, uint>("tv_bshare");
    }

    // ===================== Create Vaults =====================

    /// <summary>
    /// Create a linear vesting vault. Tokens unlock proportionally
    /// between startBlock and endBlock.
    /// </summary>
    [BasaltEntrypoint]
    public ulong CreateLinearVault(byte[] beneficiary, ulong startBlock, ulong endBlock,
        bool revocable, string description)
    {
        Context.Require(!Context.TxValue.IsZero, "VAULT: must send value");
        Context.Require(endBlock > startBlock, "VAULT: invalid period");
        Context.Require(beneficiary.Length > 0, "VAULT: invalid beneficiary");

        var id = CreateVaultInternal(beneficiary, "linear", revocable, description);
        var key = id.ToString();
        _vaultStartBlock.Set(key, startBlock);
        _vaultEndBlock.Set(key, endBlock);

        Context.Emit(new VaultCreatedEvent
        {
            VaultId = id, Owner = Context.Caller, Beneficiary = beneficiary,
            Amount = Context.TxValue, ScheduleType = "linear",
            StartBlock = startBlock, EndBlock = endBlock
        });
        return id;
    }

    /// <summary>
    /// Create a cliff vesting vault. No tokens unlock until cliffBlock,
    /// then cliffPercentageBps unlocks immediately, and the rest vests linearly to endBlock.
    /// </summary>
    [BasaltEntrypoint]
    public ulong CreateCliffVault(byte[] beneficiary, ulong startBlock, ulong cliffBlock,
        ulong endBlock, uint cliffPercentageBps, bool revocable, string description)
    {
        Context.Require(!Context.TxValue.IsZero, "VAULT: must send value");
        Context.Require(cliffBlock > startBlock, "VAULT: cliff must be after start");
        Context.Require(endBlock > cliffBlock, "VAULT: end must be after cliff");
        Context.Require(cliffPercentageBps <= 10000, "VAULT: invalid cliff percentage");

        var id = CreateVaultInternal(beneficiary, "cliff", revocable, description);
        var key = id.ToString();
        _vaultStartBlock.Set(key, startBlock);
        _vaultCliffBlock.Set(key, cliffBlock);
        _vaultEndBlock.Set(key, endBlock);
        _vaultCliffPercentageBps.Set(key, cliffPercentageBps);

        Context.Emit(new VaultCreatedEvent
        {
            VaultId = id, Owner = Context.Caller, Beneficiary = beneficiary,
            Amount = Context.TxValue, ScheduleType = "cliff",
            StartBlock = startBlock, EndBlock = endBlock
        });
        return id;
    }

    /// <summary>
    /// Create a milestone-based vault. Tokens unlock in discrete tranches
    /// when milestones are marked complete (by governance or owner).
    /// milestoneAmounts must sum to Context.TxValue.
    /// </summary>
    [BasaltEntrypoint]
    public ulong CreateMilestoneVault(byte[] beneficiary, uint milestoneCount,
        bool revocable, string description)
    {
        Context.Require(!Context.TxValue.IsZero, "VAULT: must send value");
        Context.Require(milestoneCount > 0, "VAULT: need milestones");

        var id = CreateVaultInternal(beneficiary, "milestone", revocable, description);
        _vaultMilestoneCount.Set(id.ToString(), milestoneCount);

        Context.Emit(new VaultCreatedEvent
        {
            VaultId = id, Owner = Context.Caller, Beneficiary = beneficiary,
            Amount = Context.TxValue, ScheduleType = "milestone",
            StartBlock = Context.BlockHeight, EndBlock = 0
        });
        return id;
    }

    [BasaltEntrypoint]
    public void SetMilestone(ulong vaultId, uint milestoneIndex, UInt256 amount, string descriptionHash)
    {
        RequireOwner(vaultId);
        var key = vaultId.ToString() + ":" + milestoneIndex.ToString();
        _milestoneAmounts.Set(key, amount);
        _milestoneDescriptions.Set(key, descriptionHash);
    }

    // ===================== Claiming =====================

    /// <summary>
    /// Claim vested tokens. Available to the beneficiary.
    /// </summary>
    [BasaltEntrypoint]
    public void Claim(ulong vaultId)
    {
        var key = vaultId.ToString();
        Context.Require(_vaultStatus.Get(key) == "active", "VAULT: not active");
        RequireBeneficiary(vaultId);

        var total = _vaultAmounts.Get(key);
        var claimed = _vaultClaimed.Get(key);
        var vested = ComputeVestedAmount(vaultId);
        var claimable = vested > claimed ? vested - claimed : UInt256.Zero;

        Context.Require(!claimable.IsZero, "VAULT: nothing to claim");

        _vaultClaimed.Set(key, claimed + claimable);

        if (claimed + claimable >= total)
            _vaultStatus.Set(key, "completed");

        Context.TransferNative(Context.Caller, claimable);

        Context.Emit(new VaultClaimedEvent
        {
            VaultId = vaultId, Beneficiary = Context.Caller, Amount = claimable
        });
    }

    // ===================== Milestone Completion =====================

    [BasaltEntrypoint]
    public void CompleteMilestone(ulong vaultId, uint milestoneIndex)
    {
        // Can be completed by owner or governance
        var callerHex = Convert.ToHexString(Context.Caller);
        var ownerHex = _vaultOwners.Get(vaultId.ToString());
        var isGovernance = callerHex == Convert.ToHexString(_governanceAddress);
        Context.Require(callerHex == ownerHex || isGovernance, "VAULT: not authorized");

        var key = vaultId.ToString() + ":" + milestoneIndex.ToString();
        Context.Require(!_milestoneCompleted.Get(key), "VAULT: milestone already completed");

        _milestoneCompleted.Set(key, true);

        Context.Emit(new MilestoneCompletedEvent
        {
            VaultId = vaultId, MilestoneIndex = milestoneIndex
        });
    }

    // ===================== Revocation =====================

    [BasaltEntrypoint]
    public void Revoke(ulong vaultId)
    {
        RequireOwner(vaultId);
        var key = vaultId.ToString();
        Context.Require(_vaultRevocable.Get(key), "VAULT: irrevocable");
        Context.Require(_vaultStatus.Get(key) == "active", "VAULT: not active");

        var total = _vaultAmounts.Get(key);
        var claimed = _vaultClaimed.Get(key);
        var vested = ComputeVestedAmount(vaultId);

        _vaultStatus.Set(key, "revoked");

        // Release vested-but-unclaimed to beneficiary
        if (vested > claimed)
        {
            var beneficiary = Convert.FromHexString(_vaultBeneficiaries.Get(key));
            Context.TransferNative(beneficiary, vested - claimed);
        }

        // Return unvested to owner
        var unvested = total > vested ? total - vested : UInt256.Zero;
        if (!unvested.IsZero)
            Context.TransferNative(Context.Caller, unvested);

        Context.Emit(new VaultRevokedEvent { VaultId = vaultId, UnvestedReturned = unvested });
    }

    // ===================== Emergency Unlock (Governance) =====================

    [BasaltEntrypoint]
    public void EmergencyUnlock(ulong vaultId, ulong governanceProposalId)
    {
        var status = Context.CallContract<string>(_governanceAddress, "GetStatus", governanceProposalId);
        Context.Require(status == "executed", "VAULT: governance proposal not executed");

        var key = vaultId.ToString();
        Context.Require(_vaultStatus.Get(key) == "active", "VAULT: not active");

        var total = _vaultAmounts.Get(key);
        var claimed = _vaultClaimed.Get(key);
        var remaining = total > claimed ? total - claimed : UInt256.Zero;

        _vaultStatus.Set(key, "emergency_unlocked");

        if (!remaining.IsZero)
        {
            var beneficiary = Convert.FromHexString(_vaultBeneficiaries.Get(key));
            Context.TransferNative(beneficiary, remaining);
        }

        Context.Emit(new EmergencyUnlockEvent
        {
            VaultId = vaultId, ProposalId = governanceProposalId, AmountReleased = remaining
        });
    }

    // ===================== Beneficiary Transfer =====================

    [BasaltEntrypoint]
    public void TransferBeneficiary(ulong vaultId, byte[] newBeneficiary)
    {
        RequireBeneficiary(vaultId);
        Context.Require(newBeneficiary.Length > 0, "VAULT: invalid beneficiary");
        _vaultBeneficiaries.Set(vaultId.ToString(), Convert.ToHexString(newBeneficiary));

        Context.Emit(new BeneficiaryTransferredEvent
        {
            VaultId = vaultId, NewBeneficiary = newBeneficiary
        });
    }

    // ===================== Views =====================

    [BasaltView]
    public string GetVaultStatus(ulong vaultId) => _vaultStatus.Get(vaultId.ToString()) ?? "unknown";

    [BasaltView]
    public UInt256 GetVaultAmount(ulong vaultId) => _vaultAmounts.Get(vaultId.ToString());

    [BasaltView]
    public UInt256 GetVaultClaimed(ulong vaultId) => _vaultClaimed.Get(vaultId.ToString());

    [BasaltView]
    public string GetScheduleType(ulong vaultId) => _vaultScheduleType.Get(vaultId.ToString()) ?? "";

    [BasaltView]
    public ulong GetStartBlock(ulong vaultId) => _vaultStartBlock.Get(vaultId.ToString());

    [BasaltView]
    public ulong GetEndBlock(ulong vaultId) => _vaultEndBlock.Get(vaultId.ToString());

    [BasaltView]
    public bool IsRevocable(ulong vaultId) => _vaultRevocable.Get(vaultId.ToString());

    [BasaltView]
    public bool IsMilestoneCompleted(ulong vaultId, uint milestoneIndex)
        => _milestoneCompleted.Get(vaultId.ToString() + ":" + milestoneIndex.ToString());

    // ===================== Internal =====================

    private ulong CreateVaultInternal(byte[] beneficiary, string scheduleType,
        bool revocable, string description)
    {
        var id = _nextVaultId.Get();
        _nextVaultId.Set(id + 1);

        var key = id.ToString();
        _vaultOwners.Set(key, Convert.ToHexString(Context.Caller));
        _vaultBeneficiaries.Set(key, Convert.ToHexString(beneficiary));
        _vaultAmounts.Set(key, Context.TxValue);
        _vaultClaimed.Set(key, UInt256.Zero);
        _vaultStatus.Set(key, "active");
        _vaultScheduleType.Set(key, scheduleType);
        _vaultRevocable.Set(key, revocable);
        _vaultDescriptions.Set(key, description);

        return id;
    }

    private UInt256 ComputeVestedAmount(ulong vaultId)
    {
        var key = vaultId.ToString();
        var total = _vaultAmounts.Get(key);
        var scheduleType = _vaultScheduleType.Get(key);

        if (scheduleType == "linear")
        {
            var start = _vaultStartBlock.Get(key);
            var end = _vaultEndBlock.Get(key);
            if (Context.BlockHeight < start) return UInt256.Zero;
            if (Context.BlockHeight >= end) return total;
            var elapsed = Context.BlockHeight - start;
            var duration = end - start;
            return total * new UInt256(elapsed) / new UInt256(duration);
        }
        else if (scheduleType == "cliff")
        {
            var start = _vaultStartBlock.Get(key);
            var cliff = _vaultCliffBlock.Get(key);
            var end = _vaultEndBlock.Get(key);
            if (Context.BlockHeight < cliff) return UInt256.Zero;
            var cliffPct = _vaultCliffPercentageBps.Get(key);
            var cliffAmount = total * new UInt256(cliffPct) / new UInt256(10000);
            if (Context.BlockHeight >= end) return total;
            var postCliffTotal = total - cliffAmount;
            var elapsed = Context.BlockHeight - cliff;
            var duration = end - cliff;
            return cliffAmount + (postCliffTotal * new UInt256(elapsed) / new UInt256(duration));
        }
        else if (scheduleType == "milestone")
        {
            var count = _vaultMilestoneCount.Get(key);
            var vested = UInt256.Zero;
            for (uint i = 0; i < count; i++)
            {
                var mKey = key + ":" + i.ToString();
                if (_milestoneCompleted.Get(mKey))
                    vested += _milestoneAmounts.Get(mKey);
            }
            return vested;
        }

        return UInt256.Zero;
    }

    private void RequireOwner(ulong vaultId)
    {
        Context.Require(
            Convert.ToHexString(Context.Caller) == _vaultOwners.Get(vaultId.ToString()),
            "VAULT: not owner");
    }

    private void RequireBeneficiary(ulong vaultId)
    {
        Context.Require(
            Convert.ToHexString(Context.Caller) == _vaultBeneficiaries.Get(vaultId.ToString()),
            "VAULT: not beneficiary");
    }
}

// ===================== Events =====================

[BasaltEvent]
public class VaultCreatedEvent
{
    [Indexed] public ulong VaultId { get; set; }
    [Indexed] public byte[] Owner { get; set; } = null!;
    public byte[] Beneficiary { get; set; } = null!;
    public UInt256 Amount { get; set; }
    public string ScheduleType { get; set; } = "";
    public ulong StartBlock { get; set; }
    public ulong EndBlock { get; set; }
}

[BasaltEvent]
public class VaultClaimedEvent
{
    [Indexed] public ulong VaultId { get; set; }
    [Indexed] public byte[] Beneficiary { get; set; } = null!;
    public UInt256 Amount { get; set; }
}

[BasaltEvent]
public class VaultRevokedEvent
{
    [Indexed] public ulong VaultId { get; set; }
    public UInt256 UnvestedReturned { get; set; }
}

[BasaltEvent]
public class EmergencyUnlockEvent
{
    [Indexed] public ulong VaultId { get; set; }
    public ulong ProposalId { get; set; }
    public UInt256 AmountReleased { get; set; }
}

[BasaltEvent]
public class MilestoneCompletedEvent
{
    [Indexed] public ulong VaultId { get; set; }
    public uint MilestoneIndex { get; set; }
}

[BasaltEvent]
public class BeneficiaryTransferredEvent
{
    [Indexed] public ulong VaultId { get; set; }
    public byte[] NewBeneficiary { get; set; } = null!;
}
```

## Complexity

**Medium** -- The core vesting calculations (linear, cliff, milestone) are straightforward arithmetic. The main complexity lies in correctly handling the interactions between the three schedule types, the revocation logic (vested-but-unclaimed goes to beneficiary, unvested returns to owner), and the governance emergency unlock flow. Milestone-based vaults add moderate complexity due to the variable number of milestones and their completion tracking.

## Priority

**P1** -- Time-locked vaults are essential for token distribution, team vesting, and investor lockups. Any protocol launch or token sale will need vesting contracts. The existing Escrow contract covers simple time-lock scenarios, so the Timelock Vault is a high-priority enhancement rather than a blocking dependency. It should be available before any major token distribution event.
