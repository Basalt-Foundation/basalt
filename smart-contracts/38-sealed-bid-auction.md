# Sealed-Bid Auction

## Category

DeFi / Privacy / Marketplace

## Summary

A sealed-bid auction contract where bidders submit encrypted bids as Pedersen commitments during the bidding phase, then reveal their bids during the reveal phase. The highest valid bid wins the auction. ZK proofs verify that revealed bids match their original commitments, preventing bid manipulation. Escrow holds bid deposits, and compliance gating ensures that regulated asset auctions (real estate, securities, spectrum) satisfy KYC requirements.

This contract eliminates front-running, shill bidding based on visible bids, and last-second sniping -- creating fair, transparent auctions where the outcome is determined by genuine valuation rather than information asymmetry.

## Why It's Useful

- **Eliminates front-running**: In open auctions, later bidders can see earlier bids and bid just above them. Sealed bids force bidders to bid their true valuation.
- **Prevents shill bidding**: When bids are visible, auctioneers can submit fake bids to drive up prices. Sealed bids make shill bidding unprofitable since the auctioneer does not know which bids to beat.
- **Fair price discovery**: Sealed-bid mechanisms (particularly Vickrey/second-price sealed-bid) incentivize truthful bidding, leading to efficient price discovery.
- **Regulated asset sales**: Real estate, spectrum licenses, government contracts, and securities sales often require sealed-bid processes by law. On-chain sealed bids provide auditable compliance.
- **Anti-sniping**: Unlike timed open auctions, sealed bids are submitted during a fixed window. There is no advantage to bidding at the last second.
- **Privacy during bidding**: Bidder valuations are commercially sensitive information. Sealed bids protect this information during the auction while ensuring transparency at the conclusion.

## Key Features

- Two auction types:
  - **First-price sealed-bid**: Highest bidder wins, pays their bid amount
  - **Vickrey (second-price sealed-bid)**: Highest bidder wins, pays the second-highest bid amount (incentivizes truthful bidding)
- Pedersen commitment bids: bid = `C = amount * G + blinding * H`
- Three-phase auction: Bidding (submit commitments + deposits) -> Reveal (open commitments) -> Settlement (determine winner, transfer asset/funds)
- Bid deposit via Escrow: bidders lock a minimum deposit to prevent frivolous bids
- ZK proof on reveal: Groth16 proof that the revealed amount matches the Pedersen commitment (prevents bid manipulation)
- Range proof: bid amount is within valid bounds (above reserve price, within max bid)
- Compliance gating: configurable KYC requirement for regulated asset auctions
- Reserve price: minimum acceptable bid (hidden or public, configurable)
- Multi-asset support: auction native BST, BST-20 tokens, BST-721 NFTs, or BST-3525 SFTs
- Automatic settlement: winner's escrow is released to the auctioneer, losers' deposits are refunded
- Auction cancellation: auctioneer can cancel before reveal phase begins
- Extension prevention: fixed phase durations prevent strategic timing manipulation
- Bid withdrawal: bidders can withdraw before bidding phase ends (forfeiting a penalty)
- Multiple lot auctions: batch auction support for selling multiple items in one session

## Basalt-Specific Advantages

- **Native Pedersen commitments**: Basalt's cryptographic layer supports Pedersen commitments as a first-class primitive, making bid commitment and verification gas-efficient and secure. The binding property ensures bids cannot be changed after commitment; the hiding property ensures bid amounts are invisible until reveal.
- **ZkComplianceVerifier for bid proofs**: The Groth16 proof that the revealed bid matches the Pedersen commitment is verified on-chain using Basalt's native ZK verification infrastructure. No external verifier contracts or precompiles needed.
- **BST-VC compliance for regulated auctions**: For regulated asset sales (real estate, securities), both auctioneers and bidders must hold valid BST-VC KYC credentials. The contract checks credential validity via cross-contract calls to BSTVCRegistry, integrating with the KYC marketplace ecosystem.
- **Escrow system contract**: Basalt's built-in Escrow contract (0x...1003) provides time-locked bid deposit custody with automatic refund for losing bidders, eliminating the need for custom escrow logic.
- **BST-3525 SFT auctions**: Basalt's BST-3525 semi-fungible token standard enables auctioning of fractional asset positions (e.g., partial real estate ownership, structured finance tranches) -- a capability unique to chains with SFT support.
- **SchemaRegistry auction circuits**: The bid commitment ZK circuit's verification key is stored in SchemaRegistry (0x...1006), ensuring consistent trusted parameters. Circuit updates (e.g., adding support for multi-item bids) go through governance.
- **AOT-compiled settlement**: Winner determination, escrow release, and refund processing run in AOT-compiled code with deterministic gas costs, ensuring predictable settlement costs regardless of bidder count.
- **Ed25519 bid signatures**: Bid commitments are signed with Ed25519 for non-repudiation. The auctioneer can prove that a specific bidder submitted a specific commitment at a specific time.
- **Governance-controlled parameters**: Reserve price policies, compliance requirements, and auction type configurations can be modified through Basalt's Governance contract.

## Token Standards Used

- **BST-721** (BST721Token, type 0x0003): For NFT auctions -- the auctioned item is a BST-721 token transferred to the winner upon settlement.
- **BST-3525** (BST3525Token, type 0x0005): For semi-fungible token auctions -- fractional asset positions, structured products, or tokenized real-world assets.
- **BST-20** (BST20Token, type 0x0001): For auctions denominated in BST-20 stablecoins rather than native BST.
- **BST-VC** (BSTVCRegistry, type 0x0007): For compliance-gated auctions requiring KYC credentials from bidders.

## Integration Points

- **Escrow** (0x...1003): Bid deposits are held in Escrow during the auction. Winner's deposit is released to the auctioneer; losers' deposits are refunded. Escrow timeout provides safety if settlement is not triggered.
- **SchemaRegistry** (0x...1006): The bid commitment ZK circuit verification key is stored in SchemaRegistry for on-chain proof verification.
- **IssuerRegistry** (0x...1007): For compliance-gated auctions, validates that bidder KYC credential issuers are active and at sufficient tier.
- **BSTVCRegistry** (deployed instance): Validates KYC credential status for compliance-gated auctions.
- **Governance** (0x...1002): Auction policy parameters (maximum auction duration, minimum deposit percentage, compliance requirements) can be governed through proposals.
- **BNS** (Basalt Name Service): Auctioneers can be identified by BNS name for trust signaling.

## Technical Sketch

```csharp
using Basalt.Core;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Sealed-bid auction with Pedersen commitment bids, ZK reveal proofs,
/// Escrow deposits, and compliance gating for regulated assets.
/// Type ID: 0x0111.
/// </summary>
[BasaltContract]
public partial class SealedBidAuction
{
    // --- Storage ---

    // Auction configuration
    private readonly StorageValue<ulong> _nextAuctionId;
    private readonly StorageMap<string, string> _auctioneer;         // auctionId -> auctioneerHex
    private readonly StorageMap<string, string> _auctionTitle;       // auctionId -> title
    private readonly StorageMap<string, string> _auctionDescription; // auctionId -> description
    private readonly StorageMap<string, byte> _auctionType;          // auctionId -> 1=first-price, 2=vickrey
    private readonly StorageMap<string, string> _auctionStatus;      // auctionId -> created/bidding/reveal/settled/cancelled
    private readonly StorageMap<string, UInt256> _auctionReserve;    // auctionId -> reserve price (0 = no reserve)
    private readonly StorageMap<string, bool> _auctionComplianceReq; // auctionId -> KYC required
    private readonly StorageMap<string, UInt256> _auctionMinDeposit; // auctionId -> minimum bid deposit

    // Phase timing
    private readonly StorageMap<string, long> _biddingStart;         // auctionId -> bidding start timestamp
    private readonly StorageMap<string, long> _biddingEnd;           // auctionId -> bidding end timestamp
    private readonly StorageMap<string, long> _revealEnd;            // auctionId -> reveal end timestamp

    // Auctioned asset
    private readonly StorageMap<string, string> _assetType;          // auctionId -> "native"/"bst20"/"bst721"/"bst3525"
    private readonly StorageMap<string, string> _assetContract;      // auctionId -> asset contract address hex
    private readonly StorageMap<string, ulong> _assetTokenId;        // auctionId -> token ID (for NFTs)
    private readonly StorageMap<string, UInt256> _assetAmount;       // auctionId -> amount (for fungible)

    // Bids
    private readonly StorageMap<string, ulong> _bidCount;            // auctionId -> number of bids
    private readonly StorageMap<string, string> _bidCommitment;      // auctionId:bidderHex -> commitment hex
    private readonly StorageMap<string, ulong> _bidEscrowId;         // auctionId:bidderHex -> Escrow escrow ID
    private readonly StorageMap<string, UInt256> _bidDeposit;        // auctionId:bidderHex -> deposit amount
    private readonly StorageMap<string, bool> _bidRevealed;          // auctionId:bidderHex -> revealed
    private readonly StorageMap<string, UInt256> _bidRevealedAmount; // auctionId:bidderHex -> revealed amount

    // Winner tracking
    private readonly StorageMap<string, string> _winner;             // auctionId -> winnerHex
    private readonly StorageMap<string, UInt256> _winningBid;        // auctionId -> winning bid amount
    private readonly StorageMap<string, UInt256> _settlementPrice;   // auctionId -> settlement price (for Vickrey: 2nd price)
    private readonly StorageMap<string, string> _secondHighest;      // auctionId -> second highest bidder hex
    private readonly StorageMap<string, UInt256> _secondHighestBid;  // auctionId -> second highest bid amount

    // Schema reference
    private readonly StorageMap<string, string> _bidSchemaId;        // "schema" -> schema ID hex

    // Admin
    private readonly StorageMap<string, string> _admin;

    // Protocol configuration
    private readonly StorageValue<ulong> _protocolFeeBps;
    private readonly StorageValue<UInt256> _protocolFeeBalance;

    // System contract addresses
    private readonly byte[] _escrowAddress;
    private readonly byte[] _schemaRegistryAddress;
    private readonly byte[] _vcRegistryAddress;

    public SealedBidAuction(ulong protocolFeeBps = 100)
    {
        _nextAuctionId = new StorageValue<ulong>("sba_next");
        _auctioneer = new StorageMap<string, string>("sba_auctioneer");
        _auctionTitle = new StorageMap<string, string>("sba_title");
        _auctionDescription = new StorageMap<string, string>("sba_desc");
        _auctionType = new StorageMap<string, byte>("sba_type");
        _auctionStatus = new StorageMap<string, string>("sba_status");
        _auctionReserve = new StorageMap<string, UInt256>("sba_reserve");
        _auctionComplianceReq = new StorageMap<string, bool>("sba_compliance");
        _auctionMinDeposit = new StorageMap<string, UInt256>("sba_mindeposit");

        _biddingStart = new StorageMap<string, long>("sba_bstart");
        _biddingEnd = new StorageMap<string, long>("sba_bend");
        _revealEnd = new StorageMap<string, long>("sba_rend");

        _assetType = new StorageMap<string, string>("sba_atype");
        _assetContract = new StorageMap<string, string>("sba_acontract");
        _assetTokenId = new StorageMap<string, ulong>("sba_atokenid");
        _assetAmount = new StorageMap<string, UInt256>("sba_aamount");

        _bidCount = new StorageMap<string, ulong>("sba_bcount");
        _bidCommitment = new StorageMap<string, string>("sba_bcommit");
        _bidEscrowId = new StorageMap<string, ulong>("sba_bescrow");
        _bidDeposit = new StorageMap<string, UInt256>("sba_bdeposit");
        _bidRevealed = new StorageMap<string, bool>("sba_brevealed");
        _bidRevealedAmount = new StorageMap<string, UInt256>("sba_bamount");

        _winner = new StorageMap<string, string>("sba_winner");
        _winningBid = new StorageMap<string, UInt256>("sba_winbid");
        _settlementPrice = new StorageMap<string, UInt256>("sba_settle");
        _secondHighest = new StorageMap<string, string>("sba_2nd");
        _secondHighestBid = new StorageMap<string, UInt256>("sba_2ndbid");

        _bidSchemaId = new StorageMap<string, string>("sba_schema");

        _admin = new StorageMap<string, string>("sba_admin");
        _admin.Set("admin", Convert.ToHexString(Context.Caller));

        _protocolFeeBps = new StorageValue<ulong>("sba_pfee");
        _protocolFeeBalance = new StorageValue<UInt256>("sba_pfeebal");
        _protocolFeeBps.Set(protocolFeeBps);

        _escrowAddress = new byte[20];
        _escrowAddress[18] = 0x10;
        _escrowAddress[19] = 0x03;

        _schemaRegistryAddress = new byte[20];
        _schemaRegistryAddress[18] = 0x10;
        _schemaRegistryAddress[19] = 0x06;

        _vcRegistryAddress = new byte[20]; // Set post-deploy
    }

    // ========================================================
    // Auction Creation
    // ========================================================

    /// <summary>
    /// Create a new sealed-bid auction.
    /// Type 1 = first-price, Type 2 = Vickrey (second-price).
    /// </summary>
    [BasaltEntrypoint]
    public ulong CreateAuction(
        string title, string description, byte auctionType,
        UInt256 reservePrice, UInt256 minDeposit, bool complianceRequired,
        long biddingStartTimestamp, long biddingEndTimestamp, long revealEndTimestamp,
        string assetType)
    {
        Context.Require(!string.IsNullOrEmpty(title), "SBA: title required");
        Context.Require(auctionType == 1 || auctionType == 2, "SBA: type must be 1 or 2");
        Context.Require(biddingEndTimestamp > biddingStartTimestamp, "SBA: invalid bidding period");
        Context.Require(revealEndTimestamp > biddingEndTimestamp, "SBA: reveal must be after bidding");

        var auctionId = _nextAuctionId.Get();
        _nextAuctionId.Set(auctionId + 1);

        var key = auctionId.ToString();
        _auctioneer.Set(key, Convert.ToHexString(Context.Caller));
        _auctionTitle.Set(key, title);
        _auctionDescription.Set(key, description);
        _auctionType.Set(key, auctionType);
        _auctionStatus.Set(key, "created");
        _auctionReserve.Set(key, reservePrice);
        _auctionMinDeposit.Set(key, minDeposit);
        _auctionComplianceReq.Set(key, complianceRequired);
        _biddingStart.Set(key, biddingStartTimestamp);
        _biddingEnd.Set(key, biddingEndTimestamp);
        _revealEnd.Set(key, revealEndTimestamp);
        _assetType.Set(key, assetType);

        Context.Emit(new AuctionCreatedEvent
        {
            AuctionId = auctionId,
            Auctioneer = Context.Caller,
            Title = title,
            AuctionType = auctionType,
            BiddingEnd = biddingEndTimestamp,
            RevealEnd = revealEndTimestamp,
        });

        return auctionId;
    }

    // ========================================================
    // Bidding Phase
    // ========================================================

    /// <summary>
    /// Submit a sealed bid. The bid amount is hidden in a Pedersen commitment.
    /// Bidder must deposit at least minDeposit via TxValue.
    /// Commitment = H(amount || blinding_factor).
    /// </summary>
    [BasaltEntrypoint]
    public void SubmitBid(ulong auctionId, byte[] commitment, byte[] kycCredentialHash)
    {
        var key = auctionId.ToString();
        Context.Require(_auctionStatus.Get(key) == "created", "SBA: auction not active");
        Context.Require(Context.BlockTimestamp >= _biddingStart.Get(key), "SBA: bidding not started");
        Context.Require(Context.BlockTimestamp <= _biddingEnd.Get(key), "SBA: bidding ended");
        Context.Require(commitment.Length == 32, "SBA: invalid commitment");

        var bidderHex = Convert.ToHexString(Context.Caller);
        var bidKey = key + ":" + bidderHex;
        Context.Require(string.IsNullOrEmpty(_bidCommitment.Get(bidKey)), "SBA: already bid");

        // Check minimum deposit
        var minDeposit = _auctionMinDeposit.Get(key);
        Context.Require(Context.TxValue >= minDeposit, "SBA: insufficient deposit");

        // Check compliance if required
        if (_auctionComplianceReq.Get(key))
        {
            Context.Require(kycCredentialHash.Length > 0, "SBA: KYC credential required");
            // In production: verify credential via BSTVCRegistry
        }

        // Deposit into escrow
        var releaseBlock = Context.BlockHeight + 200000;
        var escrowId = Context.CallContract<ulong>(
            _escrowAddress, "Create", Context.Caller, releaseBlock);

        _bidCommitment.Set(bidKey, Convert.ToHexString(commitment));
        _bidEscrowId.Set(bidKey, escrowId);
        _bidDeposit.Set(bidKey, Context.TxValue);

        var count = _bidCount.Get(key);
        _bidCount.Set(key, count + 1);

        Context.Emit(new BidSubmittedEvent
        {
            AuctionId = auctionId,
            Bidder = Context.Caller,
            DepositAmount = Context.TxValue,
        });
    }

    // ========================================================
    // Reveal Phase
    // ========================================================

    /// <summary>
    /// Reveal a sealed bid. Bidder provides the actual amount and blinding factor.
    /// A ZK proof demonstrates that the revealed values match the original commitment.
    /// </summary>
    [BasaltEntrypoint]
    public void RevealBid(
        ulong auctionId, UInt256 bidAmount, byte[] blindingFactor, byte[] proof)
    {
        var key = auctionId.ToString();
        Context.Require(Context.BlockTimestamp > _biddingEnd.Get(key), "SBA: bidding not ended");
        Context.Require(Context.BlockTimestamp <= _revealEnd.Get(key), "SBA: reveal phase ended");

        var bidderHex = Convert.ToHexString(Context.Caller);
        var bidKey = key + ":" + bidderHex;
        Context.Require(!string.IsNullOrEmpty(_bidCommitment.Get(bidKey)), "SBA: no bid found");
        Context.Require(!_bidRevealed.Get(bidKey), "SBA: already revealed");
        Context.Require(proof.Length > 0, "SBA: proof required");

        // In production: verify ZK proof that amount + blinding_factor
        // produces the stored commitment

        // Check reserve price
        var reservePrice = _auctionReserve.Get(key);
        var meetsReserve = bidAmount >= reservePrice;

        _bidRevealed.Set(bidKey, true);
        _bidRevealedAmount.Set(bidKey, bidAmount);

        // Update winner tracking if bid meets reserve
        if (meetsReserve)
        {
            var currentWinnerHex = _winner.Get(key);
            var currentHighest = _winningBid.Get(key);

            if (string.IsNullOrEmpty(currentWinnerHex) || bidAmount > currentHighest)
            {
                // New highest bid -- previous highest becomes second
                if (!string.IsNullOrEmpty(currentWinnerHex))
                {
                    _secondHighest.Set(key, currentWinnerHex);
                    _secondHighestBid.Set(key, currentHighest);
                }
                _winner.Set(key, bidderHex);
                _winningBid.Set(key, bidAmount);
            }
            else if (bidAmount > _secondHighestBid.Get(key))
            {
                // New second highest
                _secondHighest.Set(key, bidderHex);
                _secondHighestBid.Set(key, bidAmount);
            }
        }

        Context.Emit(new BidRevealedEvent
        {
            AuctionId = auctionId,
            Bidder = Context.Caller,
            BidAmount = bidAmount,
            MeetsReserve = meetsReserve,
        });
    }

    // ========================================================
    // Settlement
    // ========================================================

    /// <summary>
    /// Settle the auction after the reveal phase ends.
    /// Determines winner, calculates settlement price, transfers funds.
    /// Can be called by anyone after reveal phase ends.
    /// </summary>
    [BasaltEntrypoint]
    public void Settle(ulong auctionId)
    {
        var key = auctionId.ToString();
        Context.Require(
            _auctionStatus.Get(key) == "created",
            "SBA: auction already settled or cancelled");
        Context.Require(Context.BlockTimestamp > _revealEnd.Get(key), "SBA: reveal phase not ended");

        var winnerHex = _winner.Get(key);

        if (string.IsNullOrEmpty(winnerHex))
        {
            // No valid bids met reserve price
            _auctionStatus.Set(key, "settled");

            Context.Emit(new AuctionSettledEvent
            {
                AuctionId = auctionId,
                Winner = new byte[20],
                SettlementPrice = UInt256.Zero,
                HasWinner = false,
            });
            return;
        }

        // Determine settlement price
        var auctionType = _auctionType.Get(key);
        UInt256 price;

        if (auctionType == 2) // Vickrey: pay second-highest price
        {
            var secondBid = _secondHighestBid.Get(key);
            price = secondBid.IsZero ? _auctionReserve.Get(key) : secondBid;
        }
        else // First-price: pay own bid
        {
            price = _winningBid.Get(key);
        }

        _settlementPrice.Set(key, price);
        _auctionStatus.Set(key, "settled");

        // Calculate protocol fee
        var feeBps = _protocolFeeBps.Get();
        var fee = price * new UInt256(feeBps) / new UInt256(10000);
        var netToAuctioneer = price - fee;

        var totalFees = _protocolFeeBalance.Get();
        _protocolFeeBalance.Set(totalFees + fee);

        // Release winner's escrow to auctioneer
        var winnerBidKey = key + ":" + winnerHex;
        var winnerEscrowId = _bidEscrowId.Get(winnerBidKey);
        Context.CallContract(_escrowAddress, "Release", winnerEscrowId);

        // Transfer net proceeds to auctioneer
        var auctioneerHex = _auctioneer.Get(key);
        Context.TransferNative(Convert.FromHexString(auctioneerHex), netToAuctioneer);

        Context.Emit(new AuctionSettledEvent
        {
            AuctionId = auctionId,
            Winner = Convert.FromHexString(winnerHex),
            SettlementPrice = price,
            HasWinner = true,
        });
    }

    /// <summary>
    /// Losing bidders reclaim their deposits after settlement.
    /// </summary>
    [BasaltEntrypoint]
    public void ReclaimDeposit(ulong auctionId)
    {
        var key = auctionId.ToString();
        var status = _auctionStatus.Get(key);
        Context.Require(status == "settled" || status == "cancelled", "SBA: not settled");

        var bidderHex = Convert.ToHexString(Context.Caller);
        Context.Require(bidderHex != _winner.Get(key), "SBA: winner cannot reclaim");

        var bidKey = key + ":" + bidderHex;
        Context.Require(!string.IsNullOrEmpty(_bidCommitment.Get(bidKey)), "SBA: no bid found");

        var deposit = _bidDeposit.Get(bidKey);
        Context.Require(!deposit.IsZero, "SBA: already reclaimed");

        _bidDeposit.Set(bidKey, UInt256.Zero);

        // Refund from escrow
        var escrowId = _bidEscrowId.Get(bidKey);
        Context.CallContract(_escrowAddress, "Refund", escrowId);

        Context.Emit(new DepositReclaimedEvent
        {
            AuctionId = auctionId,
            Bidder = Context.Caller,
            Amount = deposit,
        });
    }

    // ========================================================
    // Auction Management
    // ========================================================

    /// <summary>
    /// Cancel an auction. Auctioneer only, before reveal phase starts.
    /// All deposits are refunded.
    /// </summary>
    [BasaltEntrypoint]
    public void CancelAuction(ulong auctionId)
    {
        var key = auctionId.ToString();
        Context.Require(_auctionStatus.Get(key) == "created", "SBA: cannot cancel");
        Context.Require(Context.BlockTimestamp <= _biddingEnd.Get(key), "SBA: bidding phase ended");
        Context.Require(
            Convert.ToHexString(Context.Caller) == _auctioneer.Get(key),
            "SBA: not auctioneer");

        _auctionStatus.Set(key, "cancelled");

        Context.Emit(new AuctionCancelledEvent { AuctionId = auctionId });
    }

    // ========================================================
    // Admin
    // ========================================================

    /// <summary>
    /// Set the bid commitment ZK circuit schema. Admin only.
    /// </summary>
    [BasaltEntrypoint]
    public void SetBidSchema(byte[] schemaId)
    {
        RequireAdmin();
        _bidSchemaId.Set("schema", Convert.ToHexString(schemaId));
    }

    /// <summary>
    /// Update protocol fee. Admin only.
    /// </summary>
    [BasaltEntrypoint]
    public void SetProtocolFee(ulong feeBps)
    {
        RequireAdmin();
        Context.Require(feeBps <= 1000, "SBA: fee too high");
        _protocolFeeBps.Set(feeBps);
    }

    /// <summary>
    /// Withdraw accumulated protocol fees. Admin only.
    /// </summary>
    [BasaltEntrypoint]
    public void WithdrawProtocolFees(byte[] destination)
    {
        RequireAdmin();
        var fees = _protocolFeeBalance.Get();
        Context.Require(!fees.IsZero, "SBA: no fees");
        _protocolFeeBalance.Set(UInt256.Zero);
        Context.TransferNative(destination, fees);
    }

    /// <summary>
    /// Set BSTVCRegistry address. Admin only.
    /// </summary>
    [BasaltEntrypoint]
    public void SetVcRegistry(byte[] addr)
    {
        RequireAdmin();
        Array.Copy(addr, _vcRegistryAddress, 20);
    }

    /// <summary>
    /// Transfer admin role. Admin only.
    /// </summary>
    [BasaltEntrypoint]
    public void TransferAdmin(byte[] newAdmin)
    {
        RequireAdmin();
        _admin.Set("admin", Convert.ToHexString(newAdmin));
    }

    // ========================================================
    // Views
    // ========================================================

    [BasaltView]
    public string GetAuctionStatus(ulong auctionId)
        => _auctionStatus.Get(auctionId.ToString()) ?? "unknown";

    [BasaltView]
    public byte GetAuctionType(ulong auctionId)
        => _auctionType.Get(auctionId.ToString());

    [BasaltView]
    public string GetAuctionTitle(ulong auctionId)
        => _auctionTitle.Get(auctionId.ToString()) ?? "";

    [BasaltView]
    public ulong GetBidCount(ulong auctionId)
        => _bidCount.Get(auctionId.ToString());

    [BasaltView]
    public long GetBiddingEnd(ulong auctionId)
        => _biddingEnd.Get(auctionId.ToString());

    [BasaltView]
    public long GetRevealEnd(ulong auctionId)
        => _revealEnd.Get(auctionId.ToString());

    [BasaltView]
    public UInt256 GetReservePrice(ulong auctionId)
        => _auctionReserve.Get(auctionId.ToString());

    [BasaltView]
    public UInt256 GetSettlementPrice(ulong auctionId)
        => _settlementPrice.Get(auctionId.ToString());

    [BasaltView]
    public byte[] GetWinner(ulong auctionId)
    {
        var hex = _winner.Get(auctionId.ToString());
        return string.IsNullOrEmpty(hex) ? new byte[20] : Convert.FromHexString(hex);
    }

    [BasaltView]
    public UInt256 GetWinningBid(ulong auctionId)
        => _winningBid.Get(auctionId.ToString());

    [BasaltView]
    public bool HasBidderRevealed(ulong auctionId, byte[] bidder)
        => _bidRevealed.Get(auctionId.ToString() + ":" + Convert.ToHexString(bidder));

    [BasaltView]
    public UInt256 GetMinDeposit(ulong auctionId)
        => _auctionMinDeposit.Get(auctionId.ToString());

    [BasaltView]
    public ulong GetProtocolFeeBps() => _protocolFeeBps.Get();

    [BasaltView]
    public UInt256 GetProtocolFeeBalance() => _protocolFeeBalance.Get();

    // ========================================================
    // Internal Helpers
    // ========================================================

    private void RequireAdmin()
    {
        Context.Require(
            Convert.ToHexString(Context.Caller) == _admin.Get("admin"),
            "SBA: not admin");
    }
}

// ========================================================
// Events
// ========================================================

[BasaltEvent]
public class AuctionCreatedEvent
{
    [Indexed] public ulong AuctionId { get; set; }
    [Indexed] public byte[] Auctioneer { get; set; } = null!;
    public string Title { get; set; } = "";
    public byte AuctionType { get; set; }
    public long BiddingEnd { get; set; }
    public long RevealEnd { get; set; }
}

[BasaltEvent]
public class BidSubmittedEvent
{
    [Indexed] public ulong AuctionId { get; set; }
    [Indexed] public byte[] Bidder { get; set; } = null!;
    public UInt256 DepositAmount { get; set; }
}

[BasaltEvent]
public class BidRevealedEvent
{
    [Indexed] public ulong AuctionId { get; set; }
    [Indexed] public byte[] Bidder { get; set; } = null!;
    public UInt256 BidAmount { get; set; }
    public bool MeetsReserve { get; set; }
}

[BasaltEvent]
public class AuctionSettledEvent
{
    [Indexed] public ulong AuctionId { get; set; }
    [Indexed] public byte[] Winner { get; set; } = null!;
    public UInt256 SettlementPrice { get; set; }
    public bool HasWinner { get; set; }
}

[BasaltEvent]
public class DepositReclaimedEvent
{
    [Indexed] public ulong AuctionId { get; set; }
    [Indexed] public byte[] Bidder { get; set; } = null!;
    public UInt256 Amount { get; set; }
}

[BasaltEvent]
public class AuctionCancelledEvent
{
    [Indexed] public ulong AuctionId { get; set; }
}
```

## Complexity

**Medium** -- The contract follows a well-understood sealed-bid auction pattern with three distinct phases (bid, reveal, settle). The Pedersen commitment scheme for bid hiding is standard cryptography, and the ZK proof for reveal verification is a straightforward commitment opening proof. The main complexity lies in correctly handling the Vickrey (second-price) auction variant, managing multiple escrow instances for different bidders, and ensuring the settlement logic correctly handles edge cases (no valid bids, single bidder, tie-breaking). Compliance gating adds moderate complexity through cross-contract KYC verification.

## Priority

**P2** -- Sealed-bid auctions are a valuable DeFi primitive with clear real-world applications (NFT sales, real estate tokenization, spectrum auctions, government procurement). They demonstrate Basalt's privacy features in a commercially relevant context. However, they depend on Pedersen commitment infrastructure and ZK proof circuits, and the primary use cases (regulated asset sales) require the KYC marketplace and compliance infrastructure to be operational first. The contract should be prioritized after the core privacy and compliance stack is proven, alongside or slightly after the confidential OTC desk.
