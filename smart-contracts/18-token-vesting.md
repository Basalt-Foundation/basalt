# Token Vesting Contract

## Category

Token Distribution / Team Management / Investor Relations

## Summary

A configurable vesting contract that manages the gradual release of tokens to beneficiaries over time according to predefined schedules. It supports linear vesting, cliff-then-linear vesting, and milestone-based vesting, with both revocable and irrevocable grant types. The contract provides per-beneficiary tracking, admin controls for grant management, and transparent on-chain visibility into vesting progress.

## Why It's Useful

- **Team Token Lockups**: Every blockchain project with team token allocations needs a vesting mechanism to ensure long-term alignment. Vesting prevents team members from dumping tokens immediately after a launch event, protecting both the project and its community.
- **Investor Allocation Management**: SAFTs, seed rounds, and strategic investor allocations typically include vesting terms. This contract enforces those terms programmatically, eliminating the need for legal escrow or manual distribution.
- **Grant Programs**: Ecosystem grants, contributor rewards, and bounty programs often include vesting to ensure continued participation. A standard vesting contract simplifies grant administration.
- **Transparency and Trust**: On-chain vesting provides full visibility into token release schedules. Community members can verify that team and investor tokens are locked according to the published terms, building trust.
- **Revocation Capability**: For employee and contractor grants, the admin can revoke unvested tokens if the beneficiary leaves the project. This is a standard feature in equity compensation and translates naturally to token vesting.
- **Milestone-Based Releases**: Some grants are tied to deliverables rather than time. Milestone-based vesting allows tokens to be released when specific objectives are met and verified, aligning incentives with outcomes.
- **Regulatory Compliance**: In jurisdictions where token sales are regulated, vesting schedules may be legally required. The contract provides auditable proof that distribution terms are being followed.
- **Reduced Selling Pressure**: By controlling the release of tokens over time, vesting reduces sudden selling pressure on the market, contributing to price stability during the critical early growth phase of the project.

## Key Features

- **Linear Vesting**: Tokens vest continuously over a specified duration. At any point, the beneficiary can claim the proportional share of tokens that have vested since the start.
- **Cliff + Linear Vesting**: No tokens vest during the cliff period. After the cliff, all tokens from the cliff period vest immediately (the "cliff unlock"), and remaining tokens vest linearly over the remaining duration.
- **Milestone-Based Vesting**: Tokens are released in discrete tranches when predefined milestones are met. Milestones are confirmed by the admin or by governance vote.
- **Revocable Grants**: The admin can revoke a beneficiary's unvested tokens at any time. Already-vested tokens remain claimable by the beneficiary. Revoked tokens are returned to the admin or a specified return address.
- **Irrevocable Grants**: Once created, the grant cannot be revoked by anyone. The beneficiary is guaranteed to receive all tokens according to the schedule, regardless of admin actions.
- **Multiple Grants Per Beneficiary**: A single beneficiary can have multiple active grants with different schedules, amounts, and revocability settings. Each grant is tracked independently.
- **Batch Grant Creation**: The admin can create grants for multiple beneficiaries in a single transaction, reducing gas costs for large team or investor distributions.
- **Claim Function**: Beneficiaries call `Claim(grantId)` to withdraw any tokens that have vested but not yet been claimed. The contract calculates the claimable amount based on the current block number and the vesting schedule.
- **Transfer of Beneficiary**: A beneficiary can transfer their grant to a new address (subject to admin approval if configured). This handles cases where a team member changes wallets.
- **Admin Delegation**: The primary admin can delegate grant management to sub-admins for specific grant categories, enabling distributed team management.
- **Vesting Acceleration**: The admin can optionally accelerate vesting for a specific beneficiary, releasing unvested tokens ahead of schedule (e.g., for exceptional performance or early departure on good terms).
- **Total Allocation Tracking**: The contract tracks the total tokens allocated across all grants, ensuring the admin cannot over-allocate beyond the deposited token balance.

## Basalt-Specific Advantages

- **ZK Compliance for Investor Verification**: When creating grants for investors, the contract can require ZK compliance proofs to verify accredited investor status, jurisdiction, or other regulatory requirements. This is enforced at grant creation time, ensuring that only eligible parties receive token allocations without revealing their identity on-chain.
- **BST-3525 Grant Tokens**: Each vesting grant is represented as a BST-3525 semi-fungible token. The slot represents the grant category (team, investor, advisor, community), and the value represents the remaining unvested amount. Grant tokens can be transferred (if allowed), providing a standardized interface for vesting position management.
- **AOT-Compiled Vesting Calculations**: The vesting calculation logic (linear interpolation, cliff detection, milestone checking) runs as native AOT-compiled code. Batch operations that process many grants simultaneously benefit significantly from this compilation model.
- **Pedersen Commitment Privacy for Grant Amounts**: Grant amounts can optionally be hidden using Pedersen commitments. The contract verifies that the total committed amounts do not exceed the deposited balance using range proofs, but individual grant sizes remain private. This is valuable for projects that do not want to publicly disclose individual team or investor allocations.
- **Ed25519 Efficient Claim Signatures**: Claim transactions use Ed25519 signatures, which are faster to verify than ECDSA. For projects with hundreds of beneficiaries claiming simultaneously, this reduces block space consumption.
- **BLAKE3 Grant Identifier Hashing**: Each grant is identified by a BLAKE3 hash of its parameters (beneficiary, amount, schedule, creation block), providing a compact and collision-resistant identifier.
- **Governance Integration for Milestones**: Milestone completion can be verified by Governance vote rather than admin declaration alone. This decentralizes the milestone verification process and prevents admin abuse.

## Token Standards Used

- **BST-20**: The vested token is a BST-20 fungible token (typically the project's governance or utility token). The contract holds BST-20 tokens and releases them to beneficiaries according to the schedule.
- **BST-3525 (SFT)**: Vesting grants are represented as BST-3525 semi-fungible tokens with rich metadata (slot = grant category, value = unvested amount). These tokens provide a standardized interface for position management and enable transfer of vesting positions.

## Integration Points

- **Governance (0x...1002)**: Milestone completion for milestone-based grants can be confirmed by Governance vote. Governance can also propose changes to vesting parameters (e.g., extending a cliff period for the entire team category).
- **Escrow (0x...1003)**: Tokens deposited by the admin for vesting are held in the vesting contract itself, but an Escrow layer can add a timelock on admin revocations, giving beneficiaries a grace period to dispute.
- **BNS (0x...1001)**: The vesting contract registers a BNS name (e.g., "vesting.project.bst"). Beneficiaries can be identified by BNS names for human-readable grant management.
- **SchemaRegistry (0x...1006)**: Credential schemas for investor verification are referenced from the SchemaRegistry when creating compliance-gated grants.
- **IssuerRegistry (0x...1007)**: Validates credential issuers for ZK compliance proofs required during grant creation.
- **StakingPool (0x...1005)**: Vested but unclaimed tokens can optionally be staked in StakingPool to earn yield while waiting to be claimed. The yield can accrue to the beneficiary or to the project treasury.

## Technical Sketch

```csharp
// Contract type ID: 0x010D
[BasaltContract(0x010D)]
public partial class TokenVesting : SdkContract, IDispatchable
{
    // --- Enums ---

    public enum VestingType : byte
    {
        Linear = 0,
        CliffLinear = 1,
        Milestone = 2
    }

    public enum GrantCategory : byte
    {
        Team = 0,
        Investor = 1,
        Advisor = 2,
        Community = 3,
        Ecosystem = 4
    }

    // --- Storage ---

    private StorageValue<Address> _admin;
    private StorageValue<Address> _token;                  // BST-20 token being vested
    private StorageValue<ulong> _nextGrantId;
    private StorageValue<UInt256> _totalDeposited;         // total tokens deposited by admin
    private StorageValue<UInt256> _totalAllocated;         // total tokens allocated across grants
    private StorageValue<UInt256> _totalClaimed;           // total tokens claimed by beneficiaries
    private StorageValue<UInt256> _totalRevoked;           // total unvested tokens revoked
    private StorageValue<bool> _complianceRequired;

    // Grant fields (keyed by grantId)
    private StorageMap<ulong, Address> _grantBeneficiary;
    private StorageMap<ulong, UInt256> _grantTotalAmount;
    private StorageMap<ulong, UInt256> _grantClaimed;
    private StorageMap<ulong, UInt256> _grantRevoked;
    private StorageMap<ulong, byte> _grantVestingType;
    private StorageMap<ulong, byte> _grantCategory;
    private StorageMap<ulong, bool> _grantRevocable;
    private StorageMap<ulong, bool> _grantActive;

    // Linear / CliffLinear schedule parameters
    private StorageMap<ulong, ulong> _grantStartBlock;
    private StorageMap<ulong, ulong> _grantCliffBlocks;       // 0 for pure linear
    private StorageMap<ulong, ulong> _grantDurationBlocks;
    private StorageMap<ulong, UInt256> _grantCliffAmount;      // amount released at cliff

    // Milestone parameters
    private StorageMap<ulong, ulong> _grantMilestoneCount;
    // Milestone data: milestoneIndex -> (amount, confirmed)
    // Stored with composite key: grantId * 1000 + milestoneIndex
    private StorageMap<ulong, UInt256> _milestoneAmount;
    private StorageMap<ulong, bool> _milestoneConfirmed;

    // Sub-admin delegation
    private StorageMap<Address, bool> _subAdmins;

    // Beneficiary transfer approval
    private StorageMap<ulong, Address> _pendingTransfer;

    // --- Constructor ---

    public void Initialize(Address admin, Address token, bool complianceRequired)
    {
        _admin.Set(admin);
        _token.Set(token);
        _complianceRequired.Set(complianceRequired);
        _nextGrantId.Set(1);
    }

    // --- Admin: Deposit Tokens ---

    public void DepositTokens(UInt256 amount)
    {
        Require(IsAdmin(Context.Caller), "Not admin");
        Require(!amount.IsZero, "Must deposit non-zero amount");
        // Transfer BST-20 tokens from admin to this contract
        TransferTokensFrom(Context.Caller, Context.ContractAddress, amount);
        _totalDeposited.Set(_totalDeposited.Get() + amount);
    }

    // --- Admin: Create Grants ---

    public ulong CreateLinearGrant(
        Address beneficiary,
        UInt256 totalAmount,
        ulong startBlock,
        ulong durationBlocks,
        byte category,
        bool revocable)
    {
        Require(IsAdmin(Context.Caller), "Not admin");
        Require(!totalAmount.IsZero, "Amount must be > 0");
        Require(durationBlocks > 0, "Duration must be > 0");
        VerifyAllocationCapacity(totalAmount);

        if (_complianceRequired.Get())
            RequireCompliance(beneficiary);

        ulong grantId = AllocateGrantId();
        SetCommonGrantFields(grantId, beneficiary, totalAmount, VestingType.Linear, category, revocable);
        _grantStartBlock.Set(grantId, startBlock);
        _grantDurationBlocks.Set(grantId, durationBlocks);
        _grantCliffBlocks.Set(grantId, 0);

        _totalAllocated.Set(_totalAllocated.Get() + totalAmount);
        return grantId;
    }

    public ulong CreateCliffLinearGrant(
        Address beneficiary,
        UInt256 totalAmount,
        ulong startBlock,
        ulong cliffBlocks,
        ulong durationBlocks,
        UInt256 cliffAmount,
        byte category,
        bool revocable)
    {
        Require(IsAdmin(Context.Caller), "Not admin");
        Require(!totalAmount.IsZero, "Amount must be > 0");
        Require(durationBlocks > cliffBlocks, "Duration must exceed cliff");
        Require(cliffAmount <= totalAmount, "Cliff amount exceeds total");
        VerifyAllocationCapacity(totalAmount);

        if (_complianceRequired.Get())
            RequireCompliance(beneficiary);

        ulong grantId = AllocateGrantId();
        SetCommonGrantFields(grantId, beneficiary, totalAmount, VestingType.CliffLinear, category, revocable);
        _grantStartBlock.Set(grantId, startBlock);
        _grantCliffBlocks.Set(grantId, cliffBlocks);
        _grantDurationBlocks.Set(grantId, durationBlocks);
        _grantCliffAmount.Set(grantId, cliffAmount);

        _totalAllocated.Set(_totalAllocated.Get() + totalAmount);
        return grantId;
    }

    public ulong CreateMilestoneGrant(
        Address beneficiary,
        UInt256 totalAmount,
        ulong milestoneCount,
        byte category,
        bool revocable)
    {
        Require(IsAdmin(Context.Caller), "Not admin");
        Require(!totalAmount.IsZero, "Amount must be > 0");
        Require(milestoneCount > 0 && milestoneCount <= 50, "Invalid milestone count");
        VerifyAllocationCapacity(totalAmount);

        if (_complianceRequired.Get())
            RequireCompliance(beneficiary);

        ulong grantId = AllocateGrantId();
        SetCommonGrantFields(grantId, beneficiary, totalAmount, VestingType.Milestone, category, revocable);
        _grantMilestoneCount.Set(grantId, milestoneCount);

        // Milestone amounts must be set separately via SetMilestoneAmount()
        _totalAllocated.Set(_totalAllocated.Get() + totalAmount);
        return grantId;
    }

    public void SetMilestoneAmount(ulong grantId, ulong milestoneIndex, UInt256 amount)
    {
        Require(IsAdmin(Context.Caller), "Not admin");
        Require(_grantActive.Get(grantId), "Grant not active");
        Require((VestingType)_grantVestingType.Get(grantId) == VestingType.Milestone, "Not milestone grant");
        Require(milestoneIndex < _grantMilestoneCount.Get(grantId), "Invalid milestone index");

        ulong key = grantId * 1000 + milestoneIndex;
        _milestoneAmount.Set(key, amount);
    }

    // --- Beneficiary: Claim ---

    public UInt256 Claim(ulong grantId)
    {
        Address caller = Context.Caller;
        Require(_grantBeneficiary.Get(grantId) == caller, "Not beneficiary");
        Require(_grantActive.Get(grantId), "Grant not active");

        UInt256 vested = CalculateVestedAmount(grantId);
        UInt256 claimed = _grantClaimed.Get(grantId);
        UInt256 claimable = vested - claimed;
        Require(!claimable.IsZero, "Nothing to claim");

        _grantClaimed.Set(grantId, claimed + claimable);
        _totalClaimed.Set(_totalClaimed.Get() + claimable);

        // Transfer tokens to beneficiary
        TransferTokensTo(caller, claimable);

        return claimable;
    }

    // --- Vesting Calculation ---

    public UInt256 CalculateVestedAmount(ulong grantId)
    {
        VestingType vType = (VestingType)_grantVestingType.Get(grantId);
        UInt256 totalAmount = _grantTotalAmount.Get(grantId);
        UInt256 revoked = _grantRevoked.Get(grantId);
        UInt256 effectiveTotal = totalAmount - revoked;

        if (vType == VestingType.Linear)
            return CalculateLinearVested(grantId, effectiveTotal);

        if (vType == VestingType.CliffLinear)
            return CalculateCliffLinearVested(grantId, effectiveTotal);

        if (vType == VestingType.Milestone)
            return CalculateMilestoneVested(grantId);

        return UInt256.Zero;
    }

    private UInt256 CalculateLinearVested(ulong grantId, UInt256 effectiveTotal)
    {
        ulong startBlock = _grantStartBlock.Get(grantId);
        ulong duration = _grantDurationBlocks.Get(grantId);
        ulong currentBlock = Context.BlockNumber;

        if (currentBlock < startBlock) return UInt256.Zero;
        if (currentBlock >= startBlock + duration) return effectiveTotal;

        ulong elapsed = currentBlock - startBlock;
        return effectiveTotal * elapsed / duration;
    }

    private UInt256 CalculateCliffLinearVested(ulong grantId, UInt256 effectiveTotal)
    {
        ulong startBlock = _grantStartBlock.Get(grantId);
        ulong cliffBlocks = _grantCliffBlocks.Get(grantId);
        ulong duration = _grantDurationBlocks.Get(grantId);
        ulong currentBlock = Context.BlockNumber;

        if (currentBlock < startBlock + cliffBlocks) return UInt256.Zero;

        UInt256 cliffAmount = _grantCliffAmount.Get(grantId);
        if (cliffAmount > effectiveTotal) cliffAmount = effectiveTotal;

        if (currentBlock >= startBlock + duration) return effectiveTotal;

        UInt256 postCliffTotal = effectiveTotal - cliffAmount;
        ulong postCliffDuration = duration - cliffBlocks;
        ulong postCliffElapsed = currentBlock - startBlock - cliffBlocks;

        return cliffAmount + (postCliffTotal * postCliffElapsed / postCliffDuration);
    }

    private UInt256 CalculateMilestoneVested(ulong grantId)
    {
        ulong milestoneCount = _grantMilestoneCount.Get(grantId);
        UInt256 totalVested = UInt256.Zero;

        for (ulong i = 0; i < milestoneCount; i++)
        {
            ulong key = grantId * 1000 + i;
            if (_milestoneConfirmed.Get(key))
                totalVested += _milestoneAmount.Get(key);
        }

        return totalVested;
    }

    // --- Admin: Milestone Confirmation ---

    public void ConfirmMilestone(ulong grantId, ulong milestoneIndex)
    {
        Require(IsAdmin(Context.Caller), "Not admin");
        Require(_grantActive.Get(grantId), "Grant not active");
        Require((VestingType)_grantVestingType.Get(grantId) == VestingType.Milestone, "Not milestone grant");
        Require(milestoneIndex < _grantMilestoneCount.Get(grantId), "Invalid milestone");

        ulong key = grantId * 1000 + milestoneIndex;
        Require(!_milestoneConfirmed.Get(key), "Already confirmed");
        _milestoneConfirmed.Set(key, true);
    }

    // --- Admin: Revocation ---

    public UInt256 RevokeGrant(ulong grantId)
    {
        Require(IsAdmin(Context.Caller), "Not admin");
        Require(_grantActive.Get(grantId), "Grant not active");
        Require(_grantRevocable.Get(grantId), "Grant is irrevocable");

        UInt256 vested = CalculateVestedAmount(grantId);
        UInt256 claimed = _grantClaimed.Get(grantId);
        UInt256 totalAmount = _grantTotalAmount.Get(grantId);
        UInt256 unvested = totalAmount - vested;

        _grantRevoked.Set(grantId, unvested);
        _grantActive.Set(grantId, false);
        _totalRevoked.Set(_totalRevoked.Get() + unvested);
        _totalAllocated.Set(_totalAllocated.Get() - unvested);

        // Return unvested tokens to admin
        TransferTokensTo(_admin.Get(), unvested);

        // Allow beneficiary to still claim vested-but-unclaimed tokens
        if (vested > claimed)
        {
            // Grant remains partially active for claiming
            // Beneficiary can call Claim() to get vested - claimed
        }

        return unvested;
    }

    // --- Admin: Acceleration ---

    public void AccelerateGrant(ulong grantId)
    {
        Require(IsAdmin(Context.Caller), "Not admin");
        Require(_grantActive.Get(grantId), "Grant not active");

        // Set the duration to end at current block, effectively vesting everything
        VestingType vType = (VestingType)_grantVestingType.Get(grantId);
        if (vType == VestingType.Linear || vType == VestingType.CliffLinear)
        {
            ulong startBlock = _grantStartBlock.Get(grantId);
            ulong currentBlock = Context.BlockNumber;
            if (currentBlock > startBlock)
                _grantDurationBlocks.Set(grantId, currentBlock - startBlock);
            _grantCliffBlocks.Set(grantId, 0);
        }
        else if (vType == VestingType.Milestone)
        {
            // Confirm all milestones
            ulong milestoneCount = _grantMilestoneCount.Get(grantId);
            for (ulong i = 0; i < milestoneCount; i++)
            {
                ulong key = grantId * 1000 + i;
                _milestoneConfirmed.Set(key, true);
            }
        }
    }

    // --- Beneficiary Transfer ---

    public void InitiateTransfer(ulong grantId, Address newBeneficiary)
    {
        Require(_grantBeneficiary.Get(grantId) == Context.Caller, "Not beneficiary");
        Require(_grantActive.Get(grantId), "Grant not active");
        _pendingTransfer.Set(grantId, newBeneficiary);
    }

    public void ApproveTransfer(ulong grantId)
    {
        Require(IsAdmin(Context.Caller), "Not admin");
        Address newBeneficiary = _pendingTransfer.Get(grantId);
        Require(newBeneficiary != Address.Zero, "No pending transfer");

        if (_complianceRequired.Get())
            RequireCompliance(newBeneficiary);

        _grantBeneficiary.Set(grantId, newBeneficiary);
        _pendingTransfer.Set(grantId, Address.Zero);
    }

    // --- Sub-Admin Management ---

    public void AddSubAdmin(Address subAdmin)
    {
        Require(Context.Caller == _admin.Get(), "Not primary admin");
        _subAdmins.Set(subAdmin, true);
    }

    public void RemoveSubAdmin(Address subAdmin)
    {
        Require(Context.Caller == _admin.Get(), "Not primary admin");
        _subAdmins.Set(subAdmin, false);
    }

    // --- Query ---

    public UInt256 GetClaimable(ulong grantId)
    {
        UInt256 vested = CalculateVestedAmount(grantId);
        UInt256 claimed = _grantClaimed.Get(grantId);
        return vested > claimed ? vested - claimed : UInt256.Zero;
    }

    public UInt256 GetGrantTotal(ulong grantId) => _grantTotalAmount.Get(grantId);
    public UInt256 GetGrantClaimed(ulong grantId) => _grantClaimed.Get(grantId);
    public Address GetGrantBeneficiary(ulong grantId) => _grantBeneficiary.Get(grantId);
    public bool IsGrantActive(ulong grantId) => _grantActive.Get(grantId);
    public bool IsGrantRevocable(ulong grantId) => _grantRevocable.Get(grantId);
    public UInt256 TotalDeposited() => _totalDeposited.Get();
    public UInt256 TotalAllocated() => _totalAllocated.Get();
    public UInt256 TotalClaimed() => _totalClaimed.Get();
    public UInt256 AvailableForAllocation() =>
        _totalDeposited.Get() - _totalAllocated.Get() + _totalRevoked.Get() - _totalClaimed.Get();

    // --- Internal Helpers ---

    private bool IsAdmin(Address account)
    {
        return account == _admin.Get() || _subAdmins.Get(account);
    }

    private void VerifyAllocationCapacity(UInt256 amount)
    {
        UInt256 available = _totalDeposited.Get() - _totalAllocated.Get()
            + _totalRevoked.Get() - _totalClaimed.Get();
        Require(available >= amount, "Insufficient deposited tokens for allocation");
    }

    private ulong AllocateGrantId()
    {
        ulong id = _nextGrantId.Get();
        _nextGrantId.Set(id + 1);
        return id;
    }

    private void SetCommonGrantFields(
        ulong grantId, Address beneficiary, UInt256 totalAmount,
        VestingType vestingType, byte category, bool revocable)
    {
        _grantBeneficiary.Set(grantId, beneficiary);
        _grantTotalAmount.Set(grantId, totalAmount);
        _grantVestingType.Set(grantId, (byte)vestingType);
        _grantCategory.Set(grantId, category);
        _grantRevocable.Set(grantId, revocable);
        _grantActive.Set(grantId, true);
    }

    private void TransferTokensFrom(Address from, Address to, UInt256 amount)
    {
        // Cross-contract call to BST-20 token.TransferFrom(from, to, amount)
    }

    private void TransferTokensTo(Address to, UInt256 amount)
    {
        // Cross-contract call to BST-20 token.Transfer(to, amount)
    }

    private void RequireCompliance(Address account)
    {
        // Validate ZK compliance proof via SchemaRegistry + IssuerRegistry
    }
}
```

## Complexity

**Low** -- Token vesting is one of the simpler DeFi contract patterns. The core logic is a time-based linear interpolation (or milestone check) to determine claimable amounts. The main sources of complexity are handling edge cases in revocation (ensuring already-vested tokens remain claimable), managing the allocation accounting (preventing over-allocation), and the milestone composite key storage pattern. The cliff-linear variant adds a minor branching condition. Overall, this is a well-understood pattern with limited mathematical complexity and no oracle dependencies.

## Priority

**P0** -- Token vesting is among the very first contracts any project needs. Before a mainnet launch, team tokens, investor allocations, and advisor grants must be locked in vesting contracts. This contract should be available early in the ecosystem lifecycle, as it is a prerequisite for responsible token distribution and is often a requirement for exchange listings that need to verify token lockup schedules. It has no external dependencies beyond the BST-20 token standard.
