# NFT Collateralized Lending

## Category

Decentralized Finance (DeFi) -- NFT Financialization

## Summary

A lending protocol that enables BST-721 NFT holders to borrow BST or BST-20 tokens against their NFTs as collateral. The protocol supports both peer-to-peer lending (direct negotiation between lender and borrower) and pool-based lending (automated loans from shared liquidity pools with collection-level risk parameters). Floor price oracles determine collateral values, and defaulted loans trigger liquidation auctions.

## Why It's Useful

- **Unlock NFT Liquidity**: NFT holders often have significant value locked in illiquid assets. This protocol allows them to access capital without selling their NFTs, enabling productive use of dormant assets.
- **Capital Efficiency**: Instead of leaving NFTs idle in wallets, holders can borrow against them to participate in DeFi opportunities, fund operations, or cover expenses.
- **Lender Yield**: Lenders earn interest on their capital by funding NFT-backed loans, creating a new yield source in the ecosystem.
- **Price Discovery**: Liquidation auctions and loan activity contribute to NFT price discovery, making the market more efficient.
- **Retain Utility Access**: Borrowers retain metadata access and certain utility rights of their NFTs during the loan period, unlike selling.
- **Collection Floor Support**: By providing an alternative to forced selling, the protocol supports collection floor prices during market downturns.
- **Composability**: Loan positions can be tokenized, enabling secondary markets for debt instruments and complex structured products.

## Key Features

- **Peer-to-Peer Lending**: Borrowers list NFTs as collateral with desired terms (amount, rate, duration). Lenders browse and fund loans directly. Full term customization.
- **Pool-Based Lending**: Shared liquidity pools with automated loan origination. Pool parameters set per NFT collection (LTV ratio, interest rate curve, maximum duration).
- **Floor Price Oracle**: On-chain oracle aggregating floor price data for NFT collections. Supports multiple oracle sources with median/TWAP calculation.
- **Loan-to-Value Ratios**: Configurable LTV per collection based on floor price volatility, trading volume, and collection age. Typical range: 30-70%.
- **Interest Rate Model**: Utilization-based interest rates for pool lending. Fixed rates for peer-to-peer. Compound interest calculated per block.
- **Liquidation Auctions**: When a loan's LTV exceeds the liquidation threshold (due to floor price decline), anyone can trigger a Dutch auction for the collateral. Proceeds repay the lender, surplus returned to the borrower.
- **Partial Repayment**: Borrowers can make partial repayments to reduce their loan balance and improve their collateral ratio.
- **Loan Extension**: Before expiry, borrowers can extend loans by paying accrued interest.
- **Collection Risk Parameters**: Each NFT collection has configurable risk parameters: LTV, liquidation threshold, interest rate multiplier, minimum/maximum loan duration.
- **Metadata Access Preservation**: Collateralized NFTs remain accessible for metadata queries (viewable in galleries, usable for access verification) even while locked in the contract.
- **Loan Position Tokens**: Active loans are represented as BST-3525 SFT positions, enabling secondary market trading of debt.
- **Grace Period**: Configurable grace period after loan expiry before liquidation is triggered.
- **Whitelist Collections**: Governance curates the list of accepted NFT collections to prevent toxic collateral.

## Basalt-Specific Advantages

- **AOT-Compiled Auction Logic**: Liquidation auction price calculations (Dutch auction decay curves) execute in AOT-compiled native code, ensuring that time-critical liquidation transactions complete efficiently with predictable gas costs.
- **BST-3525 SFT Loan Positions**: Loan positions are represented as BST-3525 semi-fungible tokens with rich slot metadata (collateral address, token ID, principal, accrued interest, LTV, expiry block). This enables structured finance products and secondary debt markets natively -- something that requires complex wrapper contracts on EVM chains.
- **BST-4626 Vault for Pool Lending**: Lender deposits into pool-based lending are managed via BST-4626 vaults, providing a standardized yield-bearing position that integrates seamlessly with other DeFi protocols (index funds, yield aggregators).
- **ZK Compliance for Regulated NFTs**: Loans against regulated NFTs (security token NFTs, real-world asset NFTs) can require ZK compliance proofs from borrowers, satisfying KYC requirements without identity revelation.
- **Confidential Loan Terms**: Pedersen commitments allow loan amounts and interest rates to be hidden in peer-to-peer lending, preventing competitors from front-running or extracting MEV from loan liquidation signals.
- **Ed25519 Signature Speed**: High-frequency oracle price updates benefit from Ed25519's fast verification, enabling more frequent floor price updates with lower overhead.
- **BLS Aggregate Oracle Signatures**: Floor price oracle reports from multiple sources can be aggregated into a single BLS signature, reducing verification costs for multi-source price feeds.

## Token Standards Used

- **BST-721**: NFT collateral. The lending contract holds BST-721 tokens in escrow during active loans.
- **BST-20**: Loan denomination (BST, WBSLT, stablecoins). Interest payments and liquidation proceeds denominated in BST-20.
- **BST-3525 (SFT)**: Loan position tokens representing active debt, with slot metadata for principal, interest, collateral details, and expiry.
- **BST-4626 (Vault)**: Pool-based lending vaults where lenders deposit BST-20 and receive yield-bearing vault shares.
- **BST-VC (Verifiable Credentials)**: Optional KYC credentials for regulated NFT collateral lending.

## Integration Points

- **Governance (0x0102)**: Governs collection whitelist additions/removals, risk parameter updates, protocol fee changes, and oracle source management.
- **Escrow (0x0103)**: NFT collateral held in escrow during loan duration. Multi-step liquidation auction settlement uses escrow for atomicity.
- **StakingPool (0x0105)**: Protocol fees from interest can be directed to staking rewards. Liquidation bot operators may need to stake BST as a performance bond.
- **BNS (0x0101)**: Protocol and collection pool addresses registered under BNS names (e.g., `nft-lending.basalt`, `bayc.nft-lending.basalt`).
- **SchemaRegistry (0x...1006)**: KYC credential schemas for regulated NFT lending.
- **IssuerRegistry (0x...1007)**: Trusted KYC issuers for compliance verification.
- **BridgeETH (0x...1008)**: Bridged NFTs from Ethereum can be used as collateral after governance approval.

## Technical Sketch

```csharp
// ============================================================
// NftLending -- NFT collateralized lending protocol
// ============================================================

[BasaltContract(TypeId = 0x0303)]
public partial class NftLending : SdkContract
{
    // --- Storage ---

    private StorageValue<ulong> _nextLoanId;
    private StorageMap<ulong, Loan> _loans;
    private StorageMap<ulong, byte> _loanStatus; // 0=pending, 1=active, 2=repaid,
                                                  // 3=liquidating, 4=liquidated, 5=defaulted

    // Collection risk parameters: collectionAddress => CollectionParams
    private StorageMap<Address, CollectionParams> _collectionParams;
    private StorageMap<Address, bool> _whitelistedCollections;

    // Floor price oracle: collectionAddress => floor price
    private StorageMap<Address, UInt256> _floorPrices;
    private StorageMap<Address, ulong> _floorPriceUpdatedAt;

    // Pool lending: collectionAddress => PoolState
    private StorageMap<Address, PoolState> _pools;

    // Pool lender deposits: collectionAddress => lender => deposit
    private StorageMap<Address, StorageMap<Address, UInt256>> _poolDeposits;

    // Liquidation auction: loanId => AuctionState
    private StorageMap<ulong, AuctionState> _auctions;

    // Protocol fee (basis points)
    private StorageValue<uint> _protocolFeeBps;
    private StorageValue<Address> _feeRecipient;

    // Oracle sources
    private StorageMap<uint, Address> _oracleSources;
    private StorageValue<uint> _oracleSourceCount;

    // Grace period in blocks
    private StorageValue<ulong> _gracePeriodBlocks;

    // --- Data Structures ---

    public struct Loan
    {
        public ulong LoanId;
        public Address Borrower;
        public Address Lender;             // Address.Zero for pool loans
        public Address NftContract;
        public ulong NftTokenId;
        public Address LoanToken;          // BST-20 denomination
        public UInt256 Principal;
        public UInt256 AccruedInterest;
        public uint InterestRateBps;       // Annual rate in basis points
        public ulong StartBlock;
        public ulong ExpiryBlock;
        public ulong LastInterestBlock;    // Last block interest was calculated
        public bool IsPoolLoan;
    }

    public struct CollectionParams
    {
        public uint MaxLtvBps;             // Maximum loan-to-value (e.g., 5000 = 50%)
        public uint LiquidationThresholdBps; // LTV at which liquidation triggers
        public uint InterestRateMultiplierBps; // Collection-specific rate adjustment
        public ulong MinDurationBlocks;
        public ulong MaxDurationBlocks;
        public UInt256 MinLoanAmount;
        public UInt256 MaxLoanAmount;
    }

    public struct PoolState
    {
        public UInt256 TotalDeposited;
        public UInt256 TotalBorrowed;
        public UInt256 TotalShares;
        public uint BaseRateBps;           // Base interest rate
        public uint RateMultiplierBps;     // Utilization multiplier
    }

    public struct AuctionState
    {
        public UInt256 StartPrice;
        public UInt256 ReservePrice;
        public ulong StartBlock;
        public ulong DurationBlocks;
        public Address HighestBidder;
        public UInt256 HighestBid;
    }

    // --- Peer-to-Peer Lending ---

    /// <summary>
    /// Create a loan listing by depositing NFT collateral.
    /// The borrower specifies desired loan terms.
    /// </summary>
    public ulong CreateLoanListing(
        Address nftContract,
        ulong nftTokenId,
        Address loanToken,
        UInt256 desiredAmount,
        uint interestRateBps,
        ulong durationBlocks)
    {
        Require(_whitelistedCollections.Get(nftContract), "COLLECTION_NOT_WHITELISTED");

        var colParams = _collectionParams.Get(nftContract);
        Require(durationBlocks >= colParams.MinDurationBlocks, "DURATION_TOO_SHORT");
        Require(durationBlocks <= colParams.MaxDurationBlocks, "DURATION_TOO_LONG");

        // Verify LTV against floor price
        UInt256 floorPrice = _floorPrices.Get(nftContract);
        Require(!floorPrice.IsZero, "NO_FLOOR_PRICE");
        UInt256 maxLoan = (floorPrice * colParams.MaxLtvBps) / 10000;
        Require(desiredAmount <= maxLoan, "EXCEEDS_MAX_LTV");

        // Transfer NFT to this contract
        TransferNftIn(nftContract, Context.Sender, nftTokenId);

        ulong loanId = _nextLoanId.Get();
        _nextLoanId.Set(loanId + 1);

        _loans.Set(loanId, new Loan
        {
            LoanId = loanId,
            Borrower = Context.Sender,
            Lender = Address.Zero,
            NftContract = nftContract,
            NftTokenId = nftTokenId,
            LoanToken = loanToken,
            Principal = desiredAmount,
            AccruedInterest = UInt256.Zero,
            InterestRateBps = interestRateBps,
            StartBlock = 0,
            ExpiryBlock = 0,
            LastInterestBlock = 0,
            IsPoolLoan = false
        });

        _loanStatus.Set(loanId, 0); // pending
        EmitEvent("LoanListed", loanId, nftContract, nftTokenId, desiredAmount);
        return loanId;
    }

    /// <summary>
    /// Fund a loan listing. The lender provides the loan amount and the loan activates.
    /// </summary>
    public void FundLoan(ulong loanId)
    {
        Require(_loanStatus.Get(loanId) == 0, "NOT_PENDING");
        var loan = _loans.Get(loanId);

        // Transfer loan tokens from lender to borrower
        TransferTokenIn(loan.LoanToken, Context.Sender, loan.Principal);
        TransferTokenOut(loan.LoanToken, loan.Borrower, loan.Principal);

        // Activate loan
        loan.Lender = Context.Sender;
        loan.StartBlock = Context.BlockNumber;
        loan.ExpiryBlock = Context.BlockNumber + GetLoanDuration(loan);
        loan.LastInterestBlock = Context.BlockNumber;
        _loans.Set(loanId, loan);
        _loanStatus.Set(loanId, 1); // active

        EmitEvent("LoanFunded", loanId, Context.Sender, loan.Principal);
    }

    // --- Pool-Based Lending ---

    /// <summary>
    /// Deposit into a collection lending pool. Returns vault shares.
    /// </summary>
    public UInt256 DepositToPool(Address nftCollection, UInt256 amount)
    {
        Require(_whitelistedCollections.Get(nftCollection), "COLLECTION_NOT_WHITELISTED");
        Require(amount > UInt256.Zero, "ZERO_AMOUNT");

        var pool = _pools.Get(nftCollection);

        // Calculate shares
        UInt256 shares;
        if (pool.TotalShares.IsZero)
        {
            shares = amount;
        }
        else
        {
            shares = (amount * pool.TotalShares) / pool.TotalDeposited;
        }

        pool.TotalDeposited = pool.TotalDeposited + amount;
        pool.TotalShares = pool.TotalShares + shares;
        _pools.Set(nftCollection, pool);

        _poolDeposits.Get(nftCollection).Set(Context.Sender,
            _poolDeposits.Get(nftCollection).Get(Context.Sender) + shares);

        EmitEvent("PoolDeposit", nftCollection, Context.Sender, amount, shares);
        return shares;
    }

    /// <summary>
    /// Withdraw from a collection lending pool by burning vault shares.
    /// </summary>
    public UInt256 WithdrawFromPool(Address nftCollection, UInt256 shares)
    {
        var pool = _pools.Get(nftCollection);
        UInt256 depositorShares = _poolDeposits.Get(nftCollection).Get(Context.Sender);
        Require(depositorShares >= shares, "INSUFFICIENT_SHARES");

        UInt256 amount = (shares * pool.TotalDeposited) / pool.TotalShares;

        // Check available liquidity
        UInt256 available = pool.TotalDeposited - pool.TotalBorrowed;
        Require(amount <= available, "INSUFFICIENT_LIQUIDITY");

        pool.TotalDeposited = pool.TotalDeposited - amount;
        pool.TotalShares = pool.TotalShares - shares;
        _pools.Set(nftCollection, pool);

        _poolDeposits.Get(nftCollection).Set(Context.Sender, depositorShares - shares);
        Context.TransferNative(Context.Sender, amount);

        EmitEvent("PoolWithdrawal", nftCollection, Context.Sender, amount, shares);
        return amount;
    }

    /// <summary>
    /// Borrow from a collection pool using an NFT as collateral.
    /// </summary>
    public ulong BorrowFromPool(
        Address nftContract,
        ulong nftTokenId,
        UInt256 borrowAmount,
        ulong durationBlocks)
    {
        Require(_whitelistedCollections.Get(nftContract), "COLLECTION_NOT_WHITELISTED");
        var pool = _pools.Get(nftContract);
        var colParams = _collectionParams.Get(nftContract);

        // Check available liquidity
        UInt256 available = pool.TotalDeposited - pool.TotalBorrowed;
        Require(borrowAmount <= available, "INSUFFICIENT_POOL_LIQUIDITY");

        // Verify LTV
        UInt256 floorPrice = _floorPrices.Get(nftContract);
        UInt256 maxLoan = (floorPrice * colParams.MaxLtvBps) / 10000;
        Require(borrowAmount <= maxLoan, "EXCEEDS_MAX_LTV");

        // Calculate interest rate (utilization-based)
        uint interestRate = CalculatePoolInterestRate(pool, borrowAmount);

        // Transfer NFT to contract
        TransferNftIn(nftContract, Context.Sender, nftTokenId);

        // Create loan
        ulong loanId = _nextLoanId.Get();
        _nextLoanId.Set(loanId + 1);

        _loans.Set(loanId, new Loan
        {
            LoanId = loanId,
            Borrower = Context.Sender,
            Lender = Address.Zero, // pool loan
            NftContract = nftContract,
            NftTokenId = nftTokenId,
            LoanToken = Address.Zero, // native BST
            Principal = borrowAmount,
            AccruedInterest = UInt256.Zero,
            InterestRateBps = interestRate,
            StartBlock = Context.BlockNumber,
            ExpiryBlock = Context.BlockNumber + durationBlocks,
            LastInterestBlock = Context.BlockNumber,
            IsPoolLoan = true
        });

        _loanStatus.Set(loanId, 1); // active

        // Update pool state
        pool.TotalBorrowed = pool.TotalBorrowed + borrowAmount;
        _pools.Set(nftContract, pool);

        // Transfer borrowed amount to borrower
        Context.TransferNative(Context.Sender, borrowAmount);

        EmitEvent("PoolLoanCreated", loanId, nftContract, nftTokenId, borrowAmount);
        return loanId;
    }

    // --- Repayment ---

    /// <summary>
    /// Repay a loan (partial or full). Full repayment returns the NFT.
    /// </summary>
    public void Repay(ulong loanId, UInt256 amount)
    {
        Require(_loanStatus.Get(loanId) == 1, "NOT_ACTIVE");
        var loan = _loans.Get(loanId);
        Require(Context.Sender == loan.Borrower, "NOT_BORROWER");

        // Update accrued interest
        loan = AccrueInterest(loan);

        UInt256 totalOwed = loan.Principal + loan.AccruedInterest;
        Require(amount <= totalOwed, "OVERPAYMENT");

        // Apply payment: interest first, then principal
        if (amount <= loan.AccruedInterest)
        {
            loan.AccruedInterest = loan.AccruedInterest - amount;
        }
        else
        {
            UInt256 principalPayment = amount - loan.AccruedInterest;
            loan.AccruedInterest = UInt256.Zero;
            loan.Principal = loan.Principal - principalPayment;
        }

        _loans.Set(loanId, loan);

        // Full repayment: return NFT and close loan
        if (loan.Principal.IsZero && loan.AccruedInterest.IsZero)
        {
            _loanStatus.Set(loanId, 2); // repaid
            TransferNftOut(loan.NftContract, loan.Borrower, loan.NftTokenId);

            if (loan.IsPoolLoan)
            {
                var pool = _pools.Get(loan.NftContract);
                pool.TotalBorrowed = pool.TotalBorrowed - loan.Principal;
                _pools.Set(loan.NftContract, pool);
            }

            EmitEvent("LoanRepaid", loanId);
        }
        else
        {
            EmitEvent("PartialRepayment", loanId, amount, loan.Principal);
        }
    }

    // --- Liquidation ---

    /// <summary>
    /// Trigger liquidation auction for an undercollateralized or expired loan.
    /// </summary>
    public void TriggerLiquidation(ulong loanId)
    {
        Require(_loanStatus.Get(loanId) == 1, "NOT_ACTIVE");
        var loan = _loans.Get(loanId);
        loan = AccrueInterest(loan);

        bool expired = Context.BlockNumber > loan.ExpiryBlock + _gracePeriodBlocks.Get();
        bool undercollateralized = false;

        if (!expired)
        {
            UInt256 floorPrice = _floorPrices.Get(loan.NftContract);
            var colParams = _collectionParams.Get(loan.NftContract);
            UInt256 totalOwed = loan.Principal + loan.AccruedInterest;
            // LTV = totalOwed / floorPrice
            UInt256 currentLtvBps = (totalOwed * 10000) / floorPrice;
            undercollateralized = currentLtvBps > colParams.LiquidationThresholdBps;
        }

        Require(expired || undercollateralized, "NOT_LIQUIDATABLE");

        _loanStatus.Set(loanId, 3); // liquidating

        // Start Dutch auction
        UInt256 floorPriceValue = _floorPrices.Get(loan.NftContract);
        _auctions.Set(loanId, new AuctionState
        {
            StartPrice = floorPriceValue * 2,      // Start at 2x floor
            ReservePrice = loan.Principal,           // Minimum: principal owed
            StartBlock = Context.BlockNumber,
            DurationBlocks = 7200,                   // ~24 hours
            HighestBidder = Address.Zero,
            HighestBid = UInt256.Zero
        });

        EmitEvent("LiquidationTriggered", loanId, expired, undercollateralized);
    }

    /// <summary>
    /// Place a bid in a liquidation auction.
    /// </summary>
    public void BidOnLiquidation(ulong loanId)
    {
        Require(_loanStatus.Get(loanId) == 3, "NOT_LIQUIDATING");
        var auction = _auctions.Get(loanId);

        Require(Context.BlockNumber <= auction.StartBlock + auction.DurationBlocks,
                "AUCTION_ENDED");

        // Calculate current Dutch auction price
        UInt256 currentPrice = GetCurrentAuctionPrice(auction);
        Require(Context.TxValue >= currentPrice, "BID_TOO_LOW");
        Require(Context.TxValue > auction.HighestBid, "BID_NOT_HIGHEST");

        // Refund previous bidder
        if (auction.HighestBidder != Address.Zero)
        {
            Context.TransferNative(auction.HighestBidder, auction.HighestBid);
        }

        auction.HighestBidder = Context.Sender;
        auction.HighestBid = Context.TxValue;
        _auctions.Set(loanId, auction);

        EmitEvent("LiquidationBid", loanId, Context.Sender, Context.TxValue);
    }

    /// <summary>
    /// Settle a completed liquidation auction.
    /// </summary>
    public void SettleLiquidation(ulong loanId)
    {
        Require(_loanStatus.Get(loanId) == 3, "NOT_LIQUIDATING");
        var auction = _auctions.Get(loanId);
        Require(Context.BlockNumber > auction.StartBlock + auction.DurationBlocks,
                "AUCTION_NOT_ENDED");
        Require(auction.HighestBidder != Address.Zero, "NO_BIDS");

        var loan = _loans.Get(loanId);
        loan = AccrueInterest(loan);

        // Transfer NFT to winning bidder
        TransferNftOut(loan.NftContract, auction.HighestBidder, loan.NftTokenId);

        // Distribute proceeds
        UInt256 totalOwed = loan.Principal + loan.AccruedInterest;
        UInt256 protocolFee = (auction.HighestBid * _protocolFeeBps.Get()) / 10000;

        if (auction.HighestBid >= totalOwed)
        {
            // Repay lender / pool
            RepayLender(loan, totalOwed);

            // Protocol fee
            if (!protocolFee.IsZero)
                Context.TransferNative(_feeRecipient.Get(), protocolFee);

            // Surplus to borrower
            UInt256 surplus = auction.HighestBid - totalOwed - protocolFee;
            if (surplus > UInt256.Zero)
                Context.TransferNative(loan.Borrower, surplus);
        }
        else
        {
            // Partial recovery
            RepayLender(loan, auction.HighestBid - protocolFee);
            if (!protocolFee.IsZero)
                Context.TransferNative(_feeRecipient.Get(), protocolFee);
        }

        _loanStatus.Set(loanId, 4); // liquidated
        EmitEvent("LiquidationSettled", loanId, auction.HighestBidder, auction.HighestBid);
    }

    // --- Oracle ---

    /// <summary>
    /// Update floor price for a collection. Oracle source only.
    /// </summary>
    public void UpdateFloorPrice(Address nftCollection, UInt256 floorPrice,
                                  byte[] oracleSignature)
    {
        // Verify oracle signature
        bool validOracle = VerifyOracleAttestation(nftCollection, floorPrice,
                                                    oracleSignature);
        Require(validOracle, "INVALID_ORACLE");

        _floorPrices.Set(nftCollection, floorPrice);
        _floorPriceUpdatedAt.Set(nftCollection, Context.BlockNumber);

        EmitEvent("FloorPriceUpdated", nftCollection, floorPrice);
    }

    // --- Query Methods ---

    public Loan GetLoan(ulong loanId) => _loans.Get(loanId);
    public byte GetLoanStatus(ulong loanId) => _loanStatus.Get(loanId);
    public UInt256 GetFloorPrice(Address collection) => _floorPrices.Get(collection);
    public CollectionParams GetCollectionParams(Address collection)
        => _collectionParams.Get(collection);
    public PoolState GetPoolState(Address collection) => _pools.Get(collection);
    public AuctionState GetAuctionState(ulong loanId) => _auctions.Get(loanId);

    public UInt256 GetCurrentAuctionPrice(AuctionState auction)
    {
        ulong elapsed = Context.BlockNumber - auction.StartBlock;
        if (elapsed >= auction.DurationBlocks)
            return auction.ReservePrice;

        UInt256 priceDrop = auction.StartPrice - auction.ReservePrice;
        UInt256 currentDrop = (priceDrop * elapsed) / auction.DurationBlocks;
        return auction.StartPrice - currentDrop;
    }

    public UInt256 GetTotalOwed(ulong loanId)
    {
        var loan = _loans.Get(loanId);
        loan = AccrueInterest(loan);
        return loan.Principal + loan.AccruedInterest;
    }

    // --- Internal Helpers ---

    private Loan AccrueInterest(Loan loan)
    {
        ulong blocksSince = Context.BlockNumber - loan.LastInterestBlock;
        if (blocksSince == 0) return loan;

        // Simple interest per block: principal * rateBps / 10000 / blocksPerYear
        UInt256 interest = (loan.Principal * loan.InterestRateBps * blocksSince)
                           / (10000 * 2628000); // ~2.6M blocks/year at 12s/block
        loan.AccruedInterest = loan.AccruedInterest + interest;
        loan.LastInterestBlock = Context.BlockNumber;
        return loan;
    }

    private uint CalculatePoolInterestRate(PoolState pool, UInt256 additionalBorrow)
    {
        UInt256 totalBorrowed = pool.TotalBorrowed + additionalBorrow;
        UInt256 utilization = (totalBorrowed * 10000) / pool.TotalDeposited;
        return pool.BaseRateBps + (uint)((utilization * pool.RateMultiplierBps) / 10000);
    }

    private ulong GetLoanDuration(Loan loan) { /* ... */ return 0; }
    private void RepayLender(Loan loan, UInt256 amount) { /* ... */ }
    private bool VerifyOracleAttestation(Address collection, UInt256 price,
                                         byte[] sig) { /* ... */ return true; }
    private void TransferNftIn(Address contract, Address from, ulong tokenId) { /* ... */ }
    private void TransferNftOut(Address contract, Address to, ulong tokenId) { /* ... */ }
    private void TransferTokenIn(Address token, Address from, UInt256 amount) { /* ... */ }
    private void TransferTokenOut(Address token, Address to, UInt256 amount) { /* ... */ }
}
```

## Complexity

**High** -- NFT lending combines multiple complex subsystems: peer-to-peer order matching, pool-based automated lending with utilization curves, floor price oracle aggregation, per-block interest accrual, LTV monitoring, Dutch auction liquidation mechanics, and collection-level risk parameterization. The interaction between volatile NFT floor prices and loan health monitoring creates edge cases around rapid price drops, stale oracle data, and cascading liquidations. Correct handling of partial repayments, loan extensions, and grace periods adds state machine complexity. Oracle security (preventing price manipulation to trigger or avoid liquidations) is a critical concern.

## Priority

**P1** -- NFT lending is a high-demand DeFi primitive that drives NFT ecosystem growth by unlocking liquidity. As Basalt's NFT ecosystem matures (BST-721 collections, marketplaces), lending becomes essential for capital efficiency. It should be deployed alongside or shortly after the core NFT marketplace and AMM DEX, as it depends on reliable floor price data and liquid markets for collateral liquidation.
