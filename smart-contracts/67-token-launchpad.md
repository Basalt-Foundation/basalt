# Token Launchpad / IDO Platform

## Category

Decentralized Finance (DeFi) -- Token Distribution and Fundraising

## Summary

A permissionless token launch platform supporting multiple sale formats (fixed price, Dutch auction, overflow allocation) with built-in ZK compliance gating, token vesting schedules, liquidity locking, and anti-whale protections. Projects can conduct fair, transparent token sales where participants prove KYC compliance via zero-knowledge proofs without revealing personal information on-chain.

## Why It's Useful

- **Fair Token Distribution**: Configurable sale mechanisms prevent insider advantages and ensure equitable access to new token offerings, addressing the widespread problem of unfair launches on other chains.
- **Regulatory Compliance Without Privacy Sacrifice**: KYC-gated participation via ZK proofs satisfies securities regulations in many jurisdictions without forcing participants to publicly link their identity to their wallet address.
- **Liquidity Bootstrapping**: Mandatory liquidity lock ensures that launched tokens have immediate trading depth, reducing the prevalence of rug-pulls and abandoned projects.
- **Anti-Whale Protection**: Per-address caps and allocation limits prevent large holders from dominating token sales, enabling broader community distribution.
- **Vesting Enforcement**: On-chain vesting schedules for purchased tokens prevent immediate dump pressure and align holder incentives with project success.
- **Project Discovery**: A curated launchpad serves as a quality signal, attracting users and capital to vetted projects within the Basalt ecosystem.
- **Revenue for the Ecosystem**: Platform fees from successful launches fund ongoing Basalt development and staking rewards.

## Key Features

- **Multiple Sale Types**:
  - **Fixed Price Sale**: Tokens sold at a predetermined price. First-come-first-served or allocation-based.
  - **Dutch Auction**: Price starts high and decreases over time until all tokens are sold or a reserve price is reached. All buyers pay the final clearing price.
  - **Overflow Sale**: All participants deposit funds; if oversubscribed, each participant receives a pro-rata allocation and excess funds are refunded.
- **ZK Compliance Whitelist**: Participants must present a ZK proof of KYC compliance (proving they hold a valid BST-VC credential from a registered issuer) to join a sale. The contract verifies the proof without learning the participant's identity.
- **Tiered Participation**: Optional tier system based on BST staking amount. Higher tiers get earlier access windows, larger allocations, or guaranteed spots.
- **Token Vesting**: Purchased tokens vest according to a configurable schedule (cliff + linear vesting). Vested tokens are claimable incrementally.
- **Liquidity Lock**: A configurable percentage of raised funds is automatically paired with project tokens and locked in an AMM pool for a minimum period.
- **Anti-Whale Caps**: Per-address maximum contribution limits, enforceable even with ZK-hidden identities via nullifier-based tracking.
- **Project Staking Requirement**: Project teams must stake BST as a quality bond, forfeited if governance determines the project violated terms.
- **Emergency Refund**: If a sale fails to meet its soft cap, all participants can claim full refunds.
- **Multi-Currency Raises**: Accept BST, WBSLT, or any approved BST-20 token as the payment currency.
- **Launch Scheduling**: Projects schedule launches with configurable start time, duration, and cooldown periods.
- **Post-Launch Analytics**: On-chain metrics for total raised, participant count, average allocation, and vesting progress.

## Basalt-Specific Advantages

- **ZK Compliance Gating (Native)**: Basalt's SchemaRegistry and IssuerRegistry enable KYC verification via Groth16 proofs natively. Participants prove compliance without revealing identity -- impossible on chains without a built-in ZK credential layer.
- **BST-VC Credential Verification**: The launchpad directly verifies BST-VC credentials for KYC, accredited investor status, or jurisdiction restrictions using Basalt's W3C-compatible credential standard.
- **Nullifier-Based Anti-Whale**: Basalt's nullifier system prevents the same KYC credential from being used across multiple addresses to bypass whale caps, a critical anti-sybil measure that is uniquely feasible with Basalt's ZK infrastructure.
- **AOT-Compiled Auction Logic**: Dutch auction price decay calculations and overflow allocation math execute in AOT-compiled native code, reducing gas costs for complex allocation computations during high-demand launches.
- **BST-4626 Vault Integration**: Vesting positions can be wrapped in BST-4626 vaults, enabling vesting token holders to earn yield on their locked tokens while waiting for the vesting cliff.
- **BST-3525 SFT Vesting Positions**: Individual vesting schedules are represented as BST-3525 semi-fungible tokens, enabling secondary market trading of vesting positions with full metadata (remaining cliff, vesting rate, total allocation).
- **Ed25519 Signature Speed**: High-throughput sale events with thousands of concurrent participants benefit from Ed25519's faster signature verification compared to ECDSA.
- **Confidential Contribution Amounts**: Pedersen commitment support allows contribution amounts to be hidden, preventing front-running and preventing competitors from monitoring fundraise progress in real-time.

## Token Standards Used

- **BST-20**: The launched token itself is BST-20. Payment currencies are BST-20. Platform fee token is BST-20.
- **BST-3525 (SFT)**: Vesting positions represented as semi-fungible tokens with slot metadata encoding vesting schedule, cliff date, claimed amount, and remaining balance.
- **BST-4626 (Vault)**: Optional yield-bearing wrapper for vesting tokens, enabling holders to earn interest during the vesting period.
- **BST-VC (Verifiable Credentials)**: KYC credentials verified in zero-knowledge for participant whitelisting.

## Integration Points

- **SchemaRegistry (0x...1006)**: Defines KYC credential schemas (e.g., "KYCCredentialV1" with fields: jurisdiction, accreditation-level, expiry).
- **IssuerRegistry (0x...1007)**: Validates that KYC credential issuers are trusted and registered. Only credentials from approved issuers grant participation access.
- **Governance (0x0102)**: Governs platform parameters (fee rates, minimum liquidity lock duration, tier thresholds). Also handles disputes and project bond forfeiture.
- **Escrow (0x0103)**: Raised funds held in escrow until sale completion conditions are met (soft cap reached, vesting configured, liquidity locked).
- **StakingPool (0x0105)**: Tier-based participation determined by BST staking amount. Platform fee revenue can be directed to staking rewards.
- **BNS (0x0101)**: Launched projects register under BNS names (e.g., `projectname.launch.basalt`).
- **BridgeETH (0x...1008)**: Cross-chain participants can bridge ETH/ERC-20 tokens to participate in Basalt launches.
- **WBSLT (0x0100)**: Wrapped BST used as the default payment currency for launches.

## Technical Sketch

```csharp
// ============================================================
// TokenLaunchpad -- Fair token launch platform
// ============================================================

[BasaltContract(TypeId = 0x0301)]
public partial class TokenLaunchpad : SdkContract
{
    // --- Storage ---

    private StorageValue<ulong> _nextSaleId;
    private StorageMap<ulong, SaleConfig> _sales;
    private StorageMap<ulong, SaleState> _saleStates;

    // saleId => participant address => contribution
    private StorageMap<ulong, StorageMap<Address, UInt256>> _contributions;

    // saleId => participant address => claimed vesting amount
    private StorageMap<ulong, StorageMap<Address, UInt256>> _claimed;

    // saleId => participant nullifier => bool (anti-sybil)
    private StorageMap<ulong, StorageMap<Hash256, bool>> _participantNullifiers;

    // saleId => total raised
    private StorageMap<ulong, UInt256> _totalRaised;

    // saleId => participant count
    private StorageMap<ulong, ulong> _participantCount;

    // Tier thresholds (tier => minimum BST staked)
    private StorageMap<uint, UInt256> _tierThresholds;

    // Platform fee basis points
    private StorageValue<uint> _platformFeeBps;
    private StorageValue<Address> _feeRecipient;

    // Minimum liquidity lock duration in blocks
    private StorageValue<ulong> _minLiquidityLockDuration;

    // --- Data Structures ---

    public struct SaleConfig
    {
        public Address ProjectOwner;
        public Address TokenAddress;         // BST-20 token being sold
        public Address PaymentToken;         // BST-20 payment currency
        public byte SaleType;               // 0=fixed, 1=dutch, 2=overflow
        public UInt256 TotalTokensForSale;
        public UInt256 StartPrice;          // Fixed price or Dutch auction start price
        public UInt256 ReservePrice;        // Dutch auction minimum price (0 for fixed)
        public UInt256 SoftCap;             // Minimum raise amount
        public UInt256 HardCap;             // Maximum raise amount
        public UInt256 MaxPerAddress;       // Anti-whale cap
        public ulong StartBlock;
        public ulong EndBlock;
        public ulong VestingCliffBlocks;    // Blocks before first claim
        public ulong VestingDurationBlocks; // Total vesting duration
        public uint LiquidityPercentBps;    // % of raised funds locked as liquidity
        public ulong LiquidityLockDuration; // Lock duration in blocks
        public byte[] RequiredSchemaHash;   // KYC schema required for participation
        public UInt256 ProjectBond;         // BST staked by project team
    }

    public struct SaleState
    {
        public byte Status;  // 0=pending, 1=active, 2=completed, 3=failed, 4=cancelled
        public UInt256 FinalPrice;        // Clearing price (for Dutch auction)
        public UInt256 TotalAllocated;    // Total tokens allocated
        public ulong LiquidityLockedUntil;
    }

    // --- Sale Creation ---

    /// <summary>
    /// Create a new token sale. Project owner deposits tokens and project bond.
    /// </summary>
    public ulong CreateSale(SaleConfig config)
    {
        Require(config.ProjectOwner == Context.Sender, "NOT_OWNER");
        Require(config.StartBlock > Context.BlockNumber, "INVALID_START");
        Require(config.EndBlock > config.StartBlock, "INVALID_DURATION");
        Require(config.TotalTokensForSale > UInt256.Zero, "ZERO_TOKENS");
        Require(config.HardCap >= config.SoftCap, "INVALID_CAPS");
        Require(config.LiquidityLockDuration >= _minLiquidityLockDuration.Get(),
                "LOCK_TOO_SHORT");

        // Transfer sale tokens from project owner to contract
        TransferTokenIn(config.TokenAddress, Context.Sender, config.TotalTokensForSale);

        // Transfer project bond
        Require(Context.TxValue >= config.ProjectBond, "INSUFFICIENT_BOND");

        ulong saleId = _nextSaleId.Get();
        _nextSaleId.Set(saleId + 1);

        _sales.Set(saleId, config);
        _saleStates.Set(saleId, new SaleState { Status = 0 });

        EmitEvent("SaleCreated", saleId, config.TokenAddress, config.SaleType);
        return saleId;
    }

    // --- Participation ---

    /// <summary>
    /// Participate in a token sale with ZK compliance proof.
    /// </summary>
    public void Participate(
        ulong saleId,
        UInt256 amount,
        byte[] zkProof,
        Hash256 participantNullifier,
        byte[] issuerPublicInputs)
    {
        var config = _sales.Get(saleId);
        var state = _saleStates.Get(saleId);

        // Timing checks
        Require(Context.BlockNumber >= config.StartBlock, "NOT_STARTED");
        Require(Context.BlockNumber <= config.EndBlock, "SALE_ENDED");
        Require(state.Status <= 1, "SALE_CLOSED");

        // Activate sale on first participation
        if (state.Status == 0)
        {
            state.Status = 1;
            _saleStates.Set(saleId, state);
        }

        // Verify ZK compliance proof (KYC without identity revelation)
        bool proofValid = VerifyComplianceProof(
            zkProof, participantNullifier, config.RequiredSchemaHash, issuerPublicInputs);
        Require(proofValid, "INVALID_COMPLIANCE_PROOF");

        // Anti-sybil: ensure this credential has not already participated
        Require(!_participantNullifiers.Get(saleId).Get(participantNullifier),
                "ALREADY_PARTICIPATED");
        _participantNullifiers.Get(saleId).Set(participantNullifier, true);

        // Anti-whale cap
        UInt256 existingContribution = _contributions.Get(saleId).Get(Context.Sender);
        Require(existingContribution + amount <= config.MaxPerAddress, "EXCEEDS_CAP");

        // Hard cap check (for fixed price and Dutch auction)
        if (config.SaleType != 2) // not overflow
        {
            Require(_totalRaised.Get(saleId) + amount <= config.HardCap, "HARD_CAP_REACHED");
        }

        // Transfer payment tokens
        TransferTokenIn(config.PaymentToken, Context.Sender, amount);

        // Record contribution
        _contributions.Get(saleId).Set(Context.Sender, existingContribution + amount);
        _totalRaised.Set(saleId, _totalRaised.Get(saleId) + amount);

        if (existingContribution.IsZero)
            _participantCount.Set(saleId, _participantCount.Get(saleId) + 1);

        EmitEvent("Participated", saleId, Context.Sender, amount);
    }

    // --- Sale Finalization ---

    /// <summary>
    /// Finalize a completed sale. Calculates allocations, locks liquidity,
    /// and enables vesting claims.
    /// </summary>
    public void FinalizeSale(ulong saleId)
    {
        var config = _sales.Get(saleId);
        var state = _saleStates.Get(saleId);
        Require(Context.BlockNumber > config.EndBlock, "SALE_NOT_ENDED");
        Require(state.Status == 1, "INVALID_STATUS");

        UInt256 totalRaised = _totalRaised.Get(saleId);

        // Check soft cap
        if (totalRaised < config.SoftCap)
        {
            state.Status = 3; // failed
            _saleStates.Set(saleId, state);
            EmitEvent("SaleFailed", saleId, totalRaised);
            return;
        }

        // Calculate final price for Dutch auction
        if (config.SaleType == 1)
        {
            state.FinalPrice = CalculateDutchAuctionClearingPrice(saleId, config);
        }
        else
        {
            state.FinalPrice = config.StartPrice;
        }

        // Lock liquidity
        UInt256 liquidityAmount = (totalRaised * config.LiquidityPercentBps) / 10000;
        if (!liquidityAmount.IsZero)
        {
            LockLiquidity(config.PaymentToken, config.TokenAddress,
                         liquidityAmount, config.LiquidityLockDuration);
            state.LiquidityLockedUntil = Context.BlockNumber + config.LiquidityLockDuration;
        }

        // Deduct platform fee
        uint feeBps = _platformFeeBps.Get();
        UInt256 fee = (totalRaised * feeBps) / 10000;
        if (!fee.IsZero)
        {
            TransferTokenOut(config.PaymentToken, _feeRecipient.Get(), fee);
        }

        // Transfer remaining raised funds to project owner
        UInt256 projectProceeds = totalRaised - liquidityAmount - fee;
        TransferTokenOut(config.PaymentToken, config.ProjectOwner, projectProceeds);

        state.Status = 2; // completed
        _saleStates.Set(saleId, state);
        EmitEvent("SaleFinalized", saleId, totalRaised, state.FinalPrice);
    }

    // --- Token Claiming (Vesting) ---

    /// <summary>
    /// Claim vested tokens from a completed sale.
    /// </summary>
    public UInt256 ClaimVestedTokens(ulong saleId)
    {
        var config = _sales.Get(saleId);
        var state = _saleStates.Get(saleId);
        Require(state.Status == 2, "SALE_NOT_COMPLETED");

        UInt256 contribution = _contributions.Get(saleId).Get(Context.Sender);
        Require(!contribution.IsZero, "NO_CONTRIBUTION");

        // Calculate total allocation
        UInt256 totalAllocation = CalculateAllocation(saleId, config, contribution);

        // Calculate vested amount
        UInt256 vestedAmount = CalculateVestedAmount(
            totalAllocation, config.StartBlock + config.EndBlock,
            config.VestingCliffBlocks, config.VestingDurationBlocks,
            Context.BlockNumber);

        UInt256 alreadyClaimed = _claimed.Get(saleId).Get(Context.Sender);
        UInt256 claimable = vestedAmount - alreadyClaimed;
        Require(!claimable.IsZero, "NOTHING_TO_CLAIM");

        _claimed.Get(saleId).Set(Context.Sender, alreadyClaimed + claimable);
        TransferTokenOut(config.TokenAddress, Context.Sender, claimable);

        EmitEvent("TokensClaimed", saleId, Context.Sender, claimable);
        return claimable;
    }

    /// <summary>
    /// Claim refund from a failed sale.
    /// </summary>
    public UInt256 ClaimRefund(ulong saleId)
    {
        var config = _sales.Get(saleId);
        var state = _saleStates.Get(saleId);
        Require(state.Status == 3, "SALE_NOT_FAILED");

        UInt256 contribution = _contributions.Get(saleId).Get(Context.Sender);
        Require(!contribution.IsZero, "NO_CONTRIBUTION");

        _contributions.Get(saleId).Set(Context.Sender, UInt256.Zero);
        TransferTokenOut(config.PaymentToken, Context.Sender, contribution);

        EmitEvent("RefundClaimed", saleId, Context.Sender, contribution);
        return contribution;
    }

    // --- Dutch Auction ---

    /// <summary>
    /// Get the current price in a Dutch auction.
    /// </summary>
    public UInt256 GetCurrentDutchPrice(ulong saleId)
    {
        var config = _sales.Get(saleId);
        Require(config.SaleType == 1, "NOT_DUTCH_AUCTION");

        if (Context.BlockNumber < config.StartBlock)
            return config.StartPrice;
        if (Context.BlockNumber >= config.EndBlock)
            return config.ReservePrice;

        ulong elapsed = Context.BlockNumber - config.StartBlock;
        ulong duration = config.EndBlock - config.StartBlock;
        UInt256 priceDrop = config.StartPrice - config.ReservePrice;
        UInt256 currentDrop = (priceDrop * elapsed) / duration;

        return config.StartPrice - currentDrop;
    }

    // --- Overflow Allocation ---

    /// <summary>
    /// Calculate pro-rata allocation for overflow sales.
    /// </summary>
    public UInt256 GetOverflowAllocation(ulong saleId, Address participant)
    {
        var config = _sales.Get(saleId);
        Require(config.SaleType == 2, "NOT_OVERFLOW");

        UInt256 contribution = _contributions.Get(saleId).Get(participant);
        UInt256 totalRaised = _totalRaised.Get(saleId);

        if (totalRaised <= config.HardCap)
            return contribution; // Not oversubscribed

        // Pro-rata: (contribution / totalRaised) * hardCap
        return (contribution * config.HardCap) / totalRaised;
    }

    // --- Query Methods ---

    public SaleConfig GetSaleConfig(ulong saleId) => _sales.Get(saleId);
    public SaleState GetSaleState(ulong saleId) => _saleStates.Get(saleId);
    public UInt256 GetTotalRaised(ulong saleId) => _totalRaised.Get(saleId);
    public ulong GetParticipantCount(ulong saleId) => _participantCount.Get(saleId);
    public UInt256 GetContribution(ulong saleId, Address participant)
        => _contributions.Get(saleId).Get(participant);

    // --- Internal Helpers ---

    private UInt256 CalculateAllocation(ulong saleId, SaleConfig config,
                                        UInt256 contribution)
    {
        if (config.SaleType == 2)
            return GetOverflowAllocation(saleId, Context.Sender);
        return (contribution * config.TotalTokensForSale) /
               _totalRaised.Get(saleId);
    }

    private UInt256 CalculateVestedAmount(UInt256 total, ulong vestingStart,
        ulong cliff, ulong duration, ulong currentBlock)
    {
        if (currentBlock < vestingStart + cliff)
            return UInt256.Zero;
        if (currentBlock >= vestingStart + duration)
            return total;
        ulong elapsed = currentBlock - vestingStart;
        return (total * elapsed) / duration;
    }

    private UInt256 CalculateDutchAuctionClearingPrice(ulong saleId,
                                                       SaleConfig config)
    { /* ... */ return UInt256.Zero; }

    private bool VerifyComplianceProof(byte[] zkProof, Hash256 nullifier,
        byte[] schemaHash, byte[] issuerInputs)
    { /* ZK proof verification via ZkComplianceVerifier */ return true; }

    private void LockLiquidity(Address paymentToken, Address saleToken,
        UInt256 amount, ulong duration)
    { /* Adds liquidity to AMM and locks LP tokens */ }

    private void TransferTokenIn(Address token, Address from, UInt256 amount) { /* ... */ }
    private void TransferTokenOut(Address token, Address to, UInt256 amount) { /* ... */ }
}
```

## Complexity

**High** -- The contract combines three distinct sale mechanisms (fixed, Dutch auction, overflow), each with different pricing and allocation logic. ZK compliance verification adds cryptographic complexity. Token vesting with cliff and linear schedules requires precise block-based timing. Liquidity locking involves cross-contract interaction with AMM pools. The anti-sybil nullifier system must correctly prevent multi-address participation while preserving privacy. Overflow refund calculations must be exact to prevent rounding errors that could lock funds.

## Priority

**P1** -- A token launchpad is critical ecosystem infrastructure that drives new project adoption on Basalt. It generates network activity, attracts developers building new tokens, and creates demand for BST (used for participation staking and project bonds). While not as fundamental as the AMM DEX (P0), a launchpad is the primary mechanism for ecosystem growth and should be available early in the network's lifecycle.
