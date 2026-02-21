# No-Loss Lottery (PoolTogether-style)

## Category

DeFi / Gamification / Savings Protocol

## Summary

A no-loss lottery where users deposit BST into a shared pool that is staked via the StakingPool contract to earn yield. At the end of each epoch, the accumulated staking yield becomes the prize, awarded to a single randomly selected winner. All other participants withdraw their full principal -- no one loses money. The winner selection uses BLAKE3 hashed with the epoch's final block hash for verifiable, tamper-resistant randomness.

## Why It's Useful

- **Risk-Free Savings Incentive**: Many users are reluctant to stake or save because the returns feel small relative to the effort. The lottery format transforms modest staking yields into a large, exciting prize, encouraging deposits from users who would otherwise hold idle BST.
- **Financial Inclusion**: No-loss lotteries make DeFi accessible to risk-averse users. The guarantee that principal is always returned removes the fear of loss that keeps many people out of crypto-native financial products.
- **Community Engagement**: The periodic draw creates recurring engagement events. Users check back each epoch to see if they won, driving regular interaction with the protocol and the broader Basalt ecosystem.
- **Yield Aggregation**: By pooling many small deposits into a single large stake, the lottery achieves better staking efficiency than individual users staking small amounts independently.
- **Protocol-Level TVL Growth**: The lottery naturally attracts and locks deposits for extended periods, contributing to the total value locked in Basalt's staking infrastructure and improving network security.
- **Gamified Onboarding**: The lottery format is intuitive and appealing to users unfamiliar with DeFi. It serves as an effective onboarding tool for the Basalt ecosystem.

## Key Features

- **Deposit and Withdraw**: Users deposit BST at any time to enter the current epoch's lottery. Withdrawals are available at any time but forfeit eligibility for the current epoch's prize.
- **Proportional Winning Odds**: A user's chance of winning is proportional to their deposit relative to the total pool. A user with 10% of the pool has a 10% chance of winning.
- **StakingPool Integration**: All deposited BST is forwarded to the StakingPool contract to earn staking yield. The contract manages stake/unstake operations automatically.
- **Epoch-Based Draws**: Each lottery epoch has a fixed duration (in blocks). At epoch end, the draw is triggered, yield is calculated, and a winner is selected.
- **Verifiable Randomness**: Winner selection uses `BLAKE3(epochNumber ++ blockHash ++ totalDepositors ++ poolSize)` where blockHash is the hash of the epoch's final block. This is deterministic, verifiable, and resistant to manipulation by any single party.
- **Early Withdrawal Penalty**: Optional configurable penalty (in basis points) for withdrawals before the epoch ends. This penalty is added to the prize pool, discouraging deposit-just-before-draw gaming.
- **Multiple Prize Tiers**: Optionally split the yield into multiple prizes (e.g., 1 grand prize of 50%, 5 second prizes of 10% each) to increase the number of winners per epoch.
- **Minimum Deposit**: Configurable minimum deposit to prevent dust deposits that complicate the draw.
- **Sponsor Deposits**: Third parties can deposit BST that earns yield for the prize pool but is not eligible to win. This allows protocols or community members to boost prize sizes.
- **Reserve Fund**: A configurable percentage of each epoch's yield is retained as a reserve fund to cover operational costs or boost future prizes during low-yield periods.

## Basalt-Specific Advantages

- **BLAKE3 Verifiable Randomness**: Basalt's native BLAKE3 hashing provides fast, cryptographically secure randomness derivation. The winner selection hash is computed in a single BLAKE3 call over deterministic inputs, making it trivially verifiable by any observer. No external VRF oracle is needed, unlike Chainlink VRF on EVM chains.
- **Native StakingPool Composability**: The lottery directly calls Basalt's first-party StakingPool contract for yield generation. There is no dependency on third-party staking protocols or yield aggregators that introduce additional smart contract risk.
- **AOT-Compiled Draw Logic**: The winner selection algorithm, which involves iterating through depositors and computing cumulative probabilities, runs as native compiled code. On EVM chains, iterating over hundreds or thousands of depositors in a single transaction is gas-prohibitive; on Basalt, AOT compilation makes this practical.
- **Pedersen Commitment Privacy**: Deposit amounts can optionally be hidden using Pedersen commitments. Users submit a commitment to their deposit amount along with a range proof. The contract can verify the commitment is valid and that the deposit meets the minimum threshold without knowing the exact amount. This hides individual wealth exposure.
- **ZK Compliance for Regulated Lotteries**: In jurisdictions where lotteries require participant verification, the contract can require ZK compliance proofs. Users prove they meet regulatory requirements (e.g., age, jurisdiction) without revealing their identity, satisfying compliance while preserving privacy.
- **Ed25519 Efficient Ticket Verification**: Each deposit transaction implicitly creates a "ticket." The Ed25519 signature on the deposit transaction serves as the ticket proof, requiring no additional cryptographic operation.
- **BST-3525 Deposit Receipts**: Deposits can be represented as BST-3525 semi-fungible tokens where the slot represents the epoch and the value represents the deposit amount. These receipt tokens can be transferred, allowing users to sell their lottery position to another party before the draw.

## Token Standards Used

- **BST-20**: The deposit token (BST) and any reward token distributions follow the BST-20 standard.
- **BST-3525 (SFT)**: Deposit receipts are BST-3525 semi-fungible tokens. Each token represents a deposit position with a specific epoch (slot) and amount (value). Transferring the SFT transfers the lottery position.
- **BST-4626 (Vault)**: The prize pool can optionally compound into a BST-4626 vault during the epoch, earning yield-on-yield until the draw occurs.

## Integration Points

- **StakingPool (0x...1005)**: The primary yield source. All deposits are staked via StakingPool, and yield is harvested at each epoch boundary to fund the prize.
- **Governance (0x...1002)**: Lottery parameters (epoch length, minimum deposit, early withdrawal penalty, reserve percentage, number of prize tiers) are governed via Governance proposals.
- **BNS (0x...1001)**: The lottery contract registers a BNS name (e.g., "lottery.bst") for user-friendly access. Winners can optionally be announced by BNS name.
- **Escrow (0x...1003)**: The prize can be held in Escrow for a challenge period after the draw, allowing anyone to verify the randomness before the prize is released to the winner.
- **SchemaRegistry (0x...1006)**: When ZK compliance is required, credential schemas for participant verification are referenced from the SchemaRegistry.
- **IssuerRegistry (0x...1007)**: Validates credential issuers for compliance-gated lottery participation.

## Technical Sketch

```csharp
// Contract type ID: 0x010A
[BasaltContract(0x010A)]
public partial class NoLossLottery : SdkContract, IDispatchable
{
    // --- Storage ---

    private StorageValue<Address> _admin;
    private StorageValue<ulong> _currentEpoch;
    private StorageValue<ulong> _epochLengthBlocks;
    private StorageValue<ulong> _epochStartBlock;
    private StorageValue<UInt256> _totalDeposits;
    private StorageValue<UInt256> _minDeposit;
    private StorageValue<ulong> _earlyWithdrawPenaltyBps;
    private StorageValue<ulong> _reserveBps;            // basis points of yield kept as reserve
    private StorageValue<UInt256> _reserveFund;
    private StorageValue<ulong> _prizeTiers;             // number of winners per epoch
    private StorageValue<bool> _complianceRequired;
    private StorageValue<bool> _paused;

    // Per-depositor tracking
    private StorageMap<Address, UInt256> _deposits;
    private StorageMap<Address, ulong> _depositEpoch;    // epoch when last deposited
    private StorageMap<Address, bool> _isSponsor;

    // Depositor list for draw iteration
    private StorageValue<ulong> _depositorCount;
    private StorageMap<ulong, Address> _depositorByIndex;
    private StorageMap<Address, ulong> _depositorIndex;
    private StorageMap<Address, bool> _isDepositor;

    // Epoch history
    private StorageMap<ulong, Address> _epochWinner;
    private StorageMap<ulong, UInt256> _epochPrize;
    private StorageMap<ulong, UInt256> _epochTotalDeposits;

    // --- Constructor ---

    public void Initialize(
        Address admin,
        ulong epochLengthBlocks,
        UInt256 minDeposit,
        ulong earlyWithdrawPenaltyBps,
        ulong reserveBps,
        ulong prizeTiers,
        bool complianceRequired)
    {
        _admin.Set(admin);
        _epochLengthBlocks.Set(epochLengthBlocks);
        _minDeposit.Set(minDeposit);
        _earlyWithdrawPenaltyBps.Set(earlyWithdrawPenaltyBps);
        _reserveBps.Set(reserveBps);
        _prizeTiers.Set(prizeTiers);
        _complianceRequired.Set(complianceRequired);
        _currentEpoch.Set(1);
        _epochStartBlock.Set(Context.BlockNumber);
    }

    // --- Deposit ---

    public void Deposit(bool asSponsor)
    {
        Require(!_paused.Get(), "Lottery paused");
        UInt256 amount = Context.TxValue;
        Require(amount >= _minDeposit.Get(), "Below minimum deposit");

        if (_complianceRequired.Get())
            RequireCompliance(Context.Caller);

        Address caller = Context.Caller;

        // Add to depositor list if new
        if (!_isDepositor.Get(caller))
        {
            ulong idx = _depositorCount.Get();
            _depositorByIndex.Set(idx, caller);
            _depositorIndex.Set(caller, idx);
            _isDepositor.Set(caller, true);
            _depositorCount.Set(idx + 1);
        }

        _deposits.Set(caller, _deposits.Get(caller) + amount);
        _depositEpoch.Set(caller, _currentEpoch.Get());
        _totalDeposits.Set(_totalDeposits.Get() + amount);

        if (asSponsor)
            _isSponsor.Set(caller, true);

        // Stake via StakingPool
        StakeDeposit(amount);
    }

    // --- Withdraw ---

    public UInt256 Withdraw(UInt256 amount)
    {
        Address caller = Context.Caller;
        UInt256 balance = _deposits.Get(caller);
        Require(balance >= amount, "Insufficient deposit");

        UInt256 penalty = UInt256.Zero;
        ulong depositEpoch = _depositEpoch.Get(caller);
        ulong currentEpoch = _currentEpoch.Get();

        // Apply early withdrawal penalty if withdrawing in same epoch as deposit
        if (depositEpoch == currentEpoch)
        {
            penalty = amount * _earlyWithdrawPenaltyBps.Get() / 10000;
        }

        UInt256 netWithdrawal = amount - penalty;

        _deposits.Set(caller, balance - amount);
        _totalDeposits.Set(_totalDeposits.Get() - amount);

        // Remove from depositor list if fully withdrawn
        if (balance - amount == UInt256.Zero)
            RemoveDepositor(caller);

        // Unstake from StakingPool
        UnstakeDeposit(amount);

        // Transfer net withdrawal to user
        Context.TransferNative(caller, netWithdrawal);

        // Penalty goes to reserve
        if (!penalty.IsZero)
            _reserveFund.Set(_reserveFund.Get() + penalty);

        return netWithdrawal;
    }

    // --- Draw ---

    public Address Draw()
    {
        ulong currentBlock = Context.BlockNumber;
        ulong epochStart = _epochStartBlock.Get();
        ulong epochLength = _epochLengthBlocks.Get();
        Require(currentBlock >= epochStart + epochLength, "Epoch not ended");

        ulong epoch = _currentEpoch.Get();
        UInt256 totalDeposits = _totalDeposits.Get();
        Require(!totalDeposits.IsZero, "No deposits");

        // Calculate yield earned this epoch
        UInt256 totalStaked = GetStakedBalance();
        UInt256 yield = totalStaked > totalDeposits
            ? totalStaked - totalDeposits
            : UInt256.Zero;

        // Reserve cut
        UInt256 reserveCut = yield * _reserveBps.Get() / 10000;
        UInt256 prizePool = yield - reserveCut;
        _reserveFund.Set(_reserveFund.Get() + reserveCut);

        // Select winner using BLAKE3 verifiable randomness
        Address winner = SelectWinner(epoch, prizePool);

        // Record epoch results
        _epochWinner.Set(epoch, winner);
        _epochPrize.Set(epoch, prizePool);
        _epochTotalDeposits.Set(epoch, totalDeposits);

        // Pay prize to winner
        if (!prizePool.IsZero)
        {
            UnstakeDeposit(prizePool);
            Context.TransferNative(winner, prizePool);
        }

        // Advance epoch
        _currentEpoch.Set(epoch + 1);
        _epochStartBlock.Set(currentBlock);

        return winner;
    }

    private Address SelectWinner(ulong epoch, UInt256 prizePool)
    {
        // Construct deterministic seed:
        // BLAKE3(epoch ++ blockHash ++ depositorCount ++ totalDeposits)
        // The block hash comes from the epoch's final block, which is
        // determined after all deposits/withdrawals are settled.

        ulong depositorCount = _depositorCount.Get();
        UInt256 totalDeposits = _totalDeposits.Get();

        // Compute random seed (simplified -- actual impl uses BLAKE3 over
        // concatenated big-endian byte representations)
        byte[] seed = ComputeBlake3Seed(epoch, Context.BlockHash, depositorCount);

        // Convert seed to a random value in [0, totalDeposits)
        UInt256 randomPoint = SeedToRange(seed, totalDeposits);

        // Iterate through depositors, accumulating deposit amounts.
        // The winner is the depositor whose cumulative range includes randomPoint.
        UInt256 cumulative = UInt256.Zero;
        for (ulong i = 0; i < depositorCount; i++)
        {
            Address depositor = _depositorByIndex.Get(i);

            // Sponsors are not eligible to win
            if (_isSponsor.Get(depositor))
                continue;

            cumulative += _deposits.Get(depositor);
            if (randomPoint < cumulative)
                return depositor;
        }

        // Fallback (should never reach here if totalDeposits > 0)
        return _depositorByIndex.Get(0);
    }

    // --- Multi-Tier Draw ---

    public void DrawMultiTier()
    {
        // Similar to Draw() but selects multiple winners based on _prizeTiers
        // Each tier gets a decreasing share of the prize:
        //   Tier 0 (grand prize): 50% of prizePool
        //   Tier 1-N: remaining 50% split equally
        // A winner at tier K is excluded from subsequent tier draws.
    }

    // --- Query ---

    public UInt256 GetDeposit(Address account) => _deposits.Get(account);
    public UInt256 TotalDeposits() => _totalDeposits.Get();
    public ulong CurrentEpoch() => _currentEpoch.Get();
    public ulong DepositorCount() => _depositorCount.Get();
    public UInt256 ReserveFund() => _reserveFund.Get();
    public Address EpochWinner(ulong epoch) => _epochWinner.Get(epoch);
    public UInt256 EpochPrize(ulong epoch) => _epochPrize.Get(epoch);

    public UInt256 GetWinProbability(Address account)
    {
        UInt256 deposit = _deposits.Get(account);
        UInt256 total = _totalDeposits.Get();
        if (total.IsZero) return UInt256.Zero;
        // Returns probability as basis points (0-10000)
        return deposit * 10000 / total;
    }

    public ulong BlocksUntilDraw()
    {
        ulong currentBlock = Context.BlockNumber;
        ulong epochStart = _epochStartBlock.Get();
        ulong epochLength = _epochLengthBlocks.Get();
        ulong epochEnd = epochStart + epochLength;
        if (currentBlock >= epochEnd) return 0;
        return epochEnd - currentBlock;
    }

    // --- Admin ---

    public void SetMinDeposit(UInt256 newMin)
    {
        Require(Context.Caller == _admin.Get(), "Not admin");
        _minDeposit.Set(newMin);
    }

    public void SetEpochLength(ulong newLength)
    {
        Require(Context.Caller == _admin.Get(), "Not admin");
        Require(newLength >= 10, "Epoch too short");
        _epochLengthBlocks.Set(newLength);
    }

    public void Pause()
    {
        Require(Context.Caller == _admin.Get(), "Not admin");
        _paused.Set(true);
    }

    public void Unpause()
    {
        Require(Context.Caller == _admin.Get(), "Not admin");
        _paused.Set(false);
    }

    public void WithdrawReserve(Address to, UInt256 amount)
    {
        Require(Context.Caller == _admin.Get(), "Not admin");
        Require(_reserveFund.Get() >= amount, "Insufficient reserve");
        _reserveFund.Set(_reserveFund.Get() - amount);
        Context.TransferNative(to, amount);
    }

    // --- Internal Helpers ---

    private void StakeDeposit(UInt256 amount)
    {
        // Cross-contract call to StakingPool.Deposit()
    }

    private void UnstakeDeposit(UInt256 amount)
    {
        // Cross-contract call to StakingPool.Withdraw()
    }

    private UInt256 GetStakedBalance()
    {
        // Cross-contract call to StakingPool.BalanceOf(this)
        return UInt256.Zero; // placeholder
    }

    private byte[] ComputeBlake3Seed(ulong epoch, byte[] blockHash, ulong depositorCount)
    {
        // BLAKE3(epoch_be_8bytes ++ blockHash_32bytes ++ depositorCount_be_8bytes)
        // Returns 32-byte hash
        return new byte[32]; // placeholder
    }

    private UInt256 SeedToRange(byte[] seed, UInt256 range)
    {
        // Interpret seed as UInt256 and compute seed % range
        return UInt256.Zero; // placeholder
    }

    private void RemoveDepositor(Address depositor)
    {
        ulong idx = _depositorIndex.Get(depositor);
        ulong lastIdx = _depositorCount.Get() - 1;

        if (idx != lastIdx)
        {
            // Swap with last element
            Address last = _depositorByIndex.Get(lastIdx);
            _depositorByIndex.Set(idx, last);
            _depositorIndex.Set(last, idx);
        }

        _depositorByIndex.Set(lastIdx, Address.Zero);
        _depositorIndex.Set(depositor, 0);
        _isDepositor.Set(depositor, false);
        _depositorCount.Set(lastIdx);
    }

    private void RequireCompliance(Address account)
    {
        // Validate ZK compliance proof via SchemaRegistry + IssuerRegistry
    }
}
```

## Complexity

**Medium** -- The deposit/withdraw mechanics are straightforward, and the StakingPool integration is a simple cross-contract call. The main complexity lies in the winner selection algorithm: maintaining an iterable depositor list with efficient insert/remove (swap-with-last pattern), computing verifiable randomness from block hashes, and converting the random seed into a weighted selection across depositors. The multi-tier draw variant adds combinatorial complexity. Edge cases around epoch transitions during active deposits/withdrawals require careful handling to ensure no funds are lost or double-counted.

## Priority

**P2** -- While no-loss lotteries are a proven and popular DeFi primitive (PoolTogether had over $300M TVL at peak), they depend on a functioning StakingPool and sufficient staking yield to generate meaningful prizes. This contract should be built after the core DeFi primitives (DEX, lending, staking) are established and generating reliable yield. It serves as an excellent user acquisition and engagement tool once the underlying infrastructure is mature.
