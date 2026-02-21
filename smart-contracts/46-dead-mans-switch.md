# Dead Man's Switch / Inheritance

## Category

Security / Estate Planning

## Summary

A dead man's switch contract that requires the owner to periodically check in (heartbeat) to prove they are still active. If the heartbeat is missed for a configurable duration, designated beneficiaries can claim the owner's funds according to pre-defined percentage splits. This contract enables on-chain estate planning, key recovery, and inheritance without requiring trust in any third party, ensuring that digital assets are not permanently lost when their owner becomes incapacitated or passes away.

## Why It's Useful

- **Digital asset inheritance**: Cryptocurrency holdings are frequently lost forever when the owner dies or becomes incapacitated without sharing private keys. This contract provides a trustless solution.
- **Key recovery backstop**: If a user loses access to their primary wallet, the dead man's switch provides a time-delayed recovery mechanism through designated beneficiaries.
- **Business continuity**: For businesses or protocols with single-key administrators, the switch ensures that operational control transfers to designated successors if the admin goes offline.
- **No trusted third party**: Unlike legal wills or custodial services, the contract executes autonomously on-chain. No lawyer, executor, or custodian needs to be trusted.
- **Configurable safety periods**: The check-in period can be set from days to years, balancing security (shorter = faster recovery) with convenience (longer = fewer required check-ins).
- **Privacy-preserving beneficiaries**: Beneficiary addresses are stored on-chain, but using ZK identity, beneficiaries can prove their identity without revealing their address publicly until they claim.
- **Multi-asset coverage**: A single dead man's switch can cover native BST, BST-20 tokens, NFTs, and other digital assets held by the contract.

## Key Features

- Owner heartbeat: owner calls a check-in function periodically to reset the dead man's switch timer
- Configurable check-in period: measured in blocks (e.g., 216,000 blocks at 12s = ~30 days), adjustable by the owner
- Multiple beneficiaries: up to N beneficiaries with configurable percentage splits (basis points, summing to 10,000)
- Delayed activation: after the heartbeat expires, a waiting period begins during which the owner can still check in (grace period)
- Beneficiary claim: after the grace period, any beneficiary can trigger the distribution
- Owner recovery: even after the grace period starts, if the owner checks in, the switch resets (until distribution is finalized)
- Beneficiary management: owner can add, remove, or modify beneficiary splits while the switch is active (not during grace period)
- Deposit and withdrawal: owner can deposit and withdraw funds freely while the switch has not expired
- Multi-token support: holds native BST and BST-20 tokens; distribution splits apply to all held assets
- Emergency contacts: owner can designate emergency contacts who can extend the deadline by a limited amount (but cannot claim funds)
- Notification events: events are emitted when the switch enters grace period, enabling off-chain notification services to alert the owner

## Basalt-Specific Advantages

- **ZK identity for beneficiary verification**: Beneficiaries can be required to present a valid ZK compliance proof (via SchemaRegistry/IssuerRegistry) before claiming, adding Sybil resistance and identity verification without centralized KYC. This is critical for preventing unauthorized claims.
- **Ed25519 signature for heartbeat**: The heartbeat check-in is a regular Basalt transaction signed with the owner's Ed25519 key, requiring no additional key management.
- **BNS name-based beneficiaries**: Beneficiaries can be referenced by their BNS name (e.g., "alice.bst"), making the setup more human-readable and reducing address-entry errors that could result in permanently lost funds.
- **Governance emergency override**: In extraordinary circumstances (e.g., the owner is in a jurisdiction where they temporarily cannot access the blockchain), governance can vote to extend the deadline, using the existing Governance contract.
- **BST-3525 SFT inheritance positions**: Inheritance positions can be represented as BST-3525 SFTs, where the slot represents the switch contract and the value represents the beneficiary's share. This makes inheritance positions transferable if needed.
- **Confidential beneficiary amounts via Pedersen commitments**: The actual distribution amounts can be hidden on-chain using Pedersen commitments, revealing only during the claim phase. This prevents front-running or social engineering based on known inheritance amounts.
- **AOT-compiled distribution**: The distribution calculation (percentage splits across multiple beneficiaries) executes in AOT-compiled code with predictable gas costs, ensuring that the distribution transaction does not fail due to gas estimation errors.

## Token Standards Used

- **BST-20**: Holds and distributes BST-20 fungible tokens across beneficiaries
- **BST-721**: NFT inheritance -- specific NFTs can be designated to specific beneficiaries
- **BST-3525**: Inheritance positions represented as SFTs for transferability
- **BST-1155**: Multi-token inheritance support

## Integration Points

- **BNS (0x...1002)**: Beneficiary designation by BNS name for human-readable setup.
- **Governance (0x...1005 area)**: Emergency deadline extension via governance proposal.
- **SchemaRegistry (0x...1006)**: ZK identity verification for beneficiary claims.
- **IssuerRegistry (0x...1007)**: Verifiable credentials for beneficiary identity.
- **Escrow (0x...1003)**: Could be combined with Escrow for conditional inheritance (e.g., "beneficiary receives funds only after completing some condition").
- **StakingPool (0x...1005)**: Funds held in the switch can be staked for yield during the owner's lifetime.

## Technical Sketch

```csharp
using Basalt.Core;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Dead Man's Switch / Inheritance -- owner must periodically check in.
/// If the heartbeat is missed, designated beneficiaries can claim funds
/// after a grace period. Enables trustless on-chain estate planning.
/// </summary>
[BasaltContract]
public partial class DeadMansSwitch
{
    // --- Owner and config ---
    private readonly StorageMap<string, string> _owner;
    private readonly StorageValue<ulong> _checkInPeriodBlocks;        // blocks between required check-ins
    private readonly StorageValue<ulong> _gracePeriodBlocks;          // additional blocks after expiry before claim
    private readonly StorageValue<ulong> _lastCheckInBlock;           // last check-in block number
    private readonly StorageValue<string> _status;                    // "active"|"grace"|"claimable"|"distributed"

    // --- Beneficiaries ---
    private readonly StorageValue<uint> _beneficiaryCount;
    private readonly StorageMap<string, string> _beneficiaryAddresses;   // idx -> address hex
    private readonly StorageMap<string, uint> _beneficiaryShareBps;      // idx -> basis points (0-10000)
    private readonly StorageMap<string, bool> _beneficiaryClaimed;       // idx -> has claimed

    // --- Emergency contacts ---
    private readonly StorageValue<uint> _emergencyContactCount;
    private readonly StorageMap<string, string> _emergencyContacts;      // idx -> address hex
    private readonly StorageMap<string, ulong> _emergencyExtensions;     // idx -> blocks extended so far
    private readonly StorageValue<ulong> _maxEmergencyExtension;         // max total extension blocks

    // --- Governance ---
    private readonly byte[] _governanceAddress;

    public DeadMansSwitch(ulong checkInPeriodBlocks = 216000,
        ulong gracePeriodBlocks = 50400, ulong maxEmergencyExtension = 432000)
    {
        _owner = new StorageMap<string, string>("dms_owner");
        _checkInPeriodBlocks = new StorageValue<ulong>("dms_period");
        _gracePeriodBlocks = new StorageValue<ulong>("dms_grace");
        _lastCheckInBlock = new StorageValue<ulong>("dms_lastci");
        _status = new StorageValue<string>("dms_status");
        _beneficiaryCount = new StorageValue<uint>("dms_bcnt");
        _beneficiaryAddresses = new StorageMap<string, string>("dms_baddr");
        _beneficiaryShareBps = new StorageMap<string, uint>("dms_bshare");
        _beneficiaryClaimed = new StorageMap<string, bool>("dms_bclaim");
        _emergencyContactCount = new StorageValue<uint>("dms_ecnt");
        _emergencyContacts = new StorageMap<string, string>("dms_eaddr");
        _emergencyExtensions = new StorageMap<string, ulong>("dms_eext");
        _maxEmergencyExtension = new StorageValue<ulong>("dms_maxext");

        _governanceAddress = new byte[20];
        // Governance address -- would be set to actual governance contract

        _owner.Set("owner", Convert.ToHexString(Context.Caller));
        _checkInPeriodBlocks.Set(checkInPeriodBlocks);
        _gracePeriodBlocks.Set(gracePeriodBlocks);
        _lastCheckInBlock.Set(Context.BlockHeight);
        _maxEmergencyExtension.Set(maxEmergencyExtension);
        _status.Set("active");
    }

    // ===================== Owner Operations =====================

    /// <summary>
    /// Owner checks in to reset the dead man's switch timer.
    /// Can also be called during grace period to cancel the switch.
    /// </summary>
    [BasaltEntrypoint]
    public void CheckIn()
    {
        RequireOwner();
        var status = _status.Get();
        Context.Require(status == "active" || status == "grace", "DMS: cannot check in");

        _lastCheckInBlock.Set(Context.BlockHeight);
        _status.Set("active");

        Context.Emit(new CheckInEvent
        {
            Owner = Context.Caller, Block = Context.BlockHeight
        });
    }

    /// <summary>
    /// Owner deposits native BST into the switch.
    /// </summary>
    [BasaltEntrypoint]
    public void Deposit()
    {
        RequireOwner();
        Context.Require(!Context.TxValue.IsZero, "DMS: must send value");

        Context.Emit(new SwitchDepositEvent
        {
            Owner = Context.Caller, Amount = Context.TxValue
        });
    }

    /// <summary>
    /// Owner withdraws native BST while the switch is active (not in grace period).
    /// </summary>
    [BasaltEntrypoint]
    public void Withdraw(UInt256 amount)
    {
        RequireOwner();
        Context.Require(_status.Get() == "active", "DMS: not active");
        Context.Require(!amount.IsZero, "DMS: zero amount");

        Context.TransferNative(Context.Caller, amount);

        Context.Emit(new SwitchWithdrawEvent
        {
            Owner = Context.Caller, Amount = amount
        });
    }

    /// <summary>
    /// Owner updates the check-in period.
    /// </summary>
    [BasaltEntrypoint]
    public void SetCheckInPeriod(ulong newPeriodBlocks)
    {
        RequireOwner();
        Context.Require(newPeriodBlocks > 0, "DMS: invalid period");
        Context.Require(_status.Get() == "active", "DMS: not active");
        _checkInPeriodBlocks.Set(newPeriodBlocks);
    }

    // ===================== Beneficiary Management =====================

    /// <summary>
    /// Owner adds a beneficiary with a share in basis points.
    /// </summary>
    [BasaltEntrypoint]
    public void AddBeneficiary(byte[] beneficiary, uint shareBps)
    {
        RequireOwner();
        Context.Require(_status.Get() == "active", "DMS: not active");
        Context.Require(beneficiary.Length > 0, "DMS: invalid beneficiary");
        Context.Require(shareBps > 0 && shareBps <= 10000, "DMS: invalid share");

        var idx = _beneficiaryCount.Get();
        _beneficiaryAddresses.Set(idx.ToString(), Convert.ToHexString(beneficiary));
        _beneficiaryShareBps.Set(idx.ToString(), shareBps);
        _beneficiaryCount.Set(idx + 1);

        Context.Emit(new BeneficiaryAddedEvent
        {
            Index = idx, Beneficiary = beneficiary, ShareBps = shareBps
        });
    }

    /// <summary>
    /// Owner updates a beneficiary's share.
    /// </summary>
    [BasaltEntrypoint]
    public void UpdateBeneficiary(uint index, uint newShareBps)
    {
        RequireOwner();
        Context.Require(_status.Get() == "active", "DMS: not active");
        Context.Require(index < _beneficiaryCount.Get(), "DMS: invalid index");
        Context.Require(newShareBps <= 10000, "DMS: invalid share");

        _beneficiaryShareBps.Set(index.ToString(), newShareBps);
    }

    /// <summary>
    /// Owner removes a beneficiary (sets share to 0).
    /// </summary>
    [BasaltEntrypoint]
    public void RemoveBeneficiary(uint index)
    {
        RequireOwner();
        Context.Require(_status.Get() == "active", "DMS: not active");
        Context.Require(index < _beneficiaryCount.Get(), "DMS: invalid index");
        _beneficiaryShareBps.Set(index.ToString(), 0);
    }

    // ===================== Emergency Contacts =====================

    [BasaltEntrypoint]
    public void AddEmergencyContact(byte[] contact)
    {
        RequireOwner();
        var idx = _emergencyContactCount.Get();
        _emergencyContacts.Set(idx.ToString(), Convert.ToHexString(contact));
        _emergencyContactCount.Set(idx + 1);
    }

    /// <summary>
    /// Emergency contact extends the deadline by a limited amount.
    /// </summary>
    [BasaltEntrypoint]
    public void EmergencyExtend(ulong extensionBlocks)
    {
        var callerHex = Convert.ToHexString(Context.Caller);
        var isEmergencyContact = false;
        uint contactIdx = 0;
        var count = _emergencyContactCount.Get();
        for (uint i = 0; i < count; i++)
        {
            if (_emergencyContacts.Get(i.ToString()) == callerHex)
            {
                isEmergencyContact = true;
                contactIdx = i;
                break;
            }
        }
        Context.Require(isEmergencyContact, "DMS: not emergency contact");

        var used = _emergencyExtensions.Get(contactIdx.ToString());
        var maxExt = _maxEmergencyExtension.Get();
        Context.Require(used + extensionBlocks <= maxExt, "DMS: exceeds max extension");

        _emergencyExtensions.Set(contactIdx.ToString(), used + extensionBlocks);
        _lastCheckInBlock.Set(_lastCheckInBlock.Get() + extensionBlocks);

        Context.Emit(new EmergencyExtendedEvent
        {
            Contact = Context.Caller, ExtensionBlocks = extensionBlocks
        });
    }

    // ===================== Switch Trigger & Claim =====================

    /// <summary>
    /// Anyone can call this to transition the switch to grace period or claimable.
    /// </summary>
    [BasaltEntrypoint]
    public void UpdateStatus()
    {
        var status = _status.Get();
        if (status == "distributed") return;

        var lastCheckIn = _lastCheckInBlock.Get();
        var period = _checkInPeriodBlocks.Get();
        var grace = _gracePeriodBlocks.Get();

        if (status == "active" && Context.BlockHeight > lastCheckIn + period)
        {
            _status.Set("grace");
            Context.Emit(new GracePeriodStartedEvent
            {
                ExpiredBlock = lastCheckIn + period,
                ClaimableBlock = lastCheckIn + period + grace
            });
        }
        else if (status == "grace" && Context.BlockHeight > lastCheckIn + period + grace)
        {
            _status.Set("claimable");
            Context.Emit(new SwitchClaimableEvent { Block = Context.BlockHeight });
        }
    }

    /// <summary>
    /// Beneficiary claims their share after the switch becomes claimable.
    /// </summary>
    [BasaltEntrypoint]
    public void Claim(uint beneficiaryIndex, UInt256 totalBalance)
    {
        Context.Require(_status.Get() == "claimable", "DMS: not claimable");
        var key = beneficiaryIndex.ToString();

        var beneficiaryHex = _beneficiaryAddresses.Get(key);
        Context.Require(Convert.ToHexString(Context.Caller) == beneficiaryHex, "DMS: not beneficiary");
        Context.Require(!_beneficiaryClaimed.Get(key), "DMS: already claimed");

        var shareBps = _beneficiaryShareBps.Get(key);
        Context.Require(shareBps > 0, "DMS: zero share");

        var amount = totalBalance * new UInt256(shareBps) / new UInt256(10000);
        Context.Require(!amount.IsZero, "DMS: zero claim amount");

        _beneficiaryClaimed.Set(key, true);
        Context.TransferNative(Context.Caller, amount);

        Context.Emit(new BeneficiaryClaimedEvent
        {
            Index = beneficiaryIndex, Beneficiary = Context.Caller,
            Amount = amount, ShareBps = shareBps
        });
    }

    /// <summary>
    /// Distribute all funds to all beneficiaries at once.
    /// Can be called by any beneficiary once the switch is claimable.
    /// </summary>
    [BasaltEntrypoint]
    public void DistributeAll(UInt256 totalBalance)
    {
        Context.Require(_status.Get() == "claimable", "DMS: not claimable");
        // Verify caller is a beneficiary
        var callerHex = Convert.ToHexString(Context.Caller);
        var count = _beneficiaryCount.Get();
        var isBeneficiary = false;
        for (uint i = 0; i < count; i++)
        {
            if (_beneficiaryAddresses.Get(i.ToString()) == callerHex)
            {
                isBeneficiary = true;
                break;
            }
        }
        Context.Require(isBeneficiary, "DMS: not beneficiary");

        for (uint i = 0; i < count; i++)
        {
            var key = i.ToString();
            if (_beneficiaryClaimed.Get(key)) continue;

            var shareBps = _beneficiaryShareBps.Get(key);
            if (shareBps == 0) continue;

            var amount = totalBalance * new UInt256(shareBps) / new UInt256(10000);
            if (amount.IsZero) continue;

            var recipient = Convert.FromHexString(_beneficiaryAddresses.Get(key));
            _beneficiaryClaimed.Set(key, true);
            Context.TransferNative(recipient, amount);
        }

        _status.Set("distributed");
        Context.Emit(new AllDistributedEvent { Block = Context.BlockHeight });
    }

    // ===================== Views =====================

    [BasaltView]
    public string GetStatus() => _status.Get();

    [BasaltView]
    public ulong GetLastCheckInBlock() => _lastCheckInBlock.Get();

    [BasaltView]
    public ulong GetCheckInPeriod() => _checkInPeriodBlocks.Get();

    [BasaltView]
    public ulong GetGracePeriod() => _gracePeriodBlocks.Get();

    [BasaltView]
    public uint GetBeneficiaryCount() => _beneficiaryCount.Get();

    [BasaltView]
    public uint GetBeneficiaryShare(uint index) => _beneficiaryShareBps.Get(index.ToString());

    [BasaltView]
    public bool HasBeneficiaryClaimed(uint index) => _beneficiaryClaimed.Get(index.ToString());

    [BasaltView]
    public ulong GetDeadlineBlock()
    {
        return _lastCheckInBlock.Get() + _checkInPeriodBlocks.Get();
    }

    [BasaltView]
    public ulong GetClaimableBlock()
    {
        return _lastCheckInBlock.Get() + _checkInPeriodBlocks.Get() + _gracePeriodBlocks.Get();
    }

    // ===================== Internal =====================

    private void RequireOwner()
    {
        Context.Require(
            Convert.ToHexString(Context.Caller) == _owner.Get("owner"),
            "DMS: not owner");
    }
}

// ===================== Events =====================

[BasaltEvent]
public class CheckInEvent
{
    [Indexed] public byte[] Owner { get; set; } = null!;
    public ulong Block { get; set; }
}

[BasaltEvent]
public class SwitchDepositEvent
{
    [Indexed] public byte[] Owner { get; set; } = null!;
    public UInt256 Amount { get; set; }
}

[BasaltEvent]
public class SwitchWithdrawEvent
{
    [Indexed] public byte[] Owner { get; set; } = null!;
    public UInt256 Amount { get; set; }
}

[BasaltEvent]
public class BeneficiaryAddedEvent
{
    [Indexed] public uint Index { get; set; }
    public byte[] Beneficiary { get; set; } = null!;
    public uint ShareBps { get; set; }
}

[BasaltEvent]
public class GracePeriodStartedEvent
{
    public ulong ExpiredBlock { get; set; }
    public ulong ClaimableBlock { get; set; }
}

[BasaltEvent]
public class SwitchClaimableEvent
{
    public ulong Block { get; set; }
}

[BasaltEvent]
public class BeneficiaryClaimedEvent
{
    [Indexed] public uint Index { get; set; }
    [Indexed] public byte[] Beneficiary { get; set; } = null!;
    public UInt256 Amount { get; set; }
    public uint ShareBps { get; set; }
}

[BasaltEvent]
public class AllDistributedEvent
{
    public ulong Block { get; set; }
}

[BasaltEvent]
public class EmergencyExtendedEvent
{
    [Indexed] public byte[] Contact { get; set; } = null!;
    public ulong ExtensionBlocks { get; set; }
}
```

## Complexity

**Low** -- The core logic is straightforward: track a timestamp, compare it against a deadline, and distribute funds if the deadline is missed. The beneficiary management (add/remove/update) is basic CRUD on storage maps. The emergency contact extension mechanism and governance override add minor complexity. The most nuanced aspect is the state machine (active -> grace -> claimable -> distributed) and ensuring that the owner can always recover during the grace period.

## Priority

**P2** -- While the dead man's switch addresses a real and important problem (lost crypto due to owner death/incapacitation), it is not a blocking dependency for the core DeFi ecosystem. It becomes more important as the chain accumulates significant value and users begin thinking about long-term asset custody. Recommended for deployment within the first year of mainnet.
