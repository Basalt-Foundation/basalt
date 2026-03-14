# Basalt Smart Contract Catalog

78 contract specifications for the Basalt ecosystem. This index tracks implementation
status against the current codebase.

> Last updated: 2026-03-12

## Status Legend

| Tag | Meaning |
|-----|---------|
| **DONE** | Already implemented in the Basalt SDK or system contracts |
| **PARTIAL** | Foundation exists, spec extends beyond current implementation |
| **READY** | Buildable now with existing SDK primitives |
| **NEEDS ORACLE** | Buildable but requires the oracle contract (41) first |
| **BLOCKED** | Requires primitives that don't exist yet |

---

## Already Implemented (7)

These are **done** — the functionality already ships with Basalt.

| # | Contract | Basalt Equivalent |
|---|----------|-------------------|
| 01 | AMM DEX | **Caldera Fusion DEX** — batch auction, order book, TWAP oracle, concentrated liquidity, encrypted intents, solver network |
| 02 | Order Book DEX | **Caldera Fusion DEX** — includes full order book engine |
| 06 | Liquid Staking | **StakingPool** (0x0105) system contract |
| 39 | Multisig Wallet | **BridgeETH** (0x0107) implements M-of-N Ed25519 multisig pattern |
| 40 | DAO Treasury | **Governance** (0x0102) — stake-weighted quadratic voting, timelock, executable proposals |
| 68 | Cross-Chain Bridge | **BridgeETH** (0x0107) — lock/unlock, multisig relayers, deposit lifecycle |
| 75 | NFT Royalty Enforcer | **Policy Hooks** — ITransferPolicy on BST-721/1155/3525 with PolicyEnforcer |

## Partially Implemented (11)

Foundation exists; the spec goes further than what's currently built.

| # | Contract | What Exists | What's Missing |
|---|----------|-------------|----------------|
| 03 | Lending Protocol | BST-4626 Vault | Collateral baskets, liquidation engine, variable rates |
| 05 | Yield Aggregator | BST-4626 Vault | Multi-strategy orchestration, auto-harvest |
| 07 | Flash Loans | Escrow (0x0103) | Atomic single-tx borrow/repay, fee mechanics |
| 08 | Streaming Payments | Escrow (0x0103) | Linear streaming curves, cancellation/top-up |
| 14 | Revenue Sharing | Governance (0x0102) | Multi-source fee aggregation, epoch snapshots |
| 18 | Token Vesting | Escrow (0x0103) | Linear/cliff curves, revocation |
| 21 | Tokenized Bonds | BST-3525 SFT | Coupon distribution, maturity, credit ratings |
| 29 | KYC Marketplace | IssuerRegistry + ComplianceEngine | Provider bidding, credential pricing |
| 30 | Reputation (Soulbound) | BST-721 | Activity tracking, weight formulas, cross-protocol queries |
| 49 | Quadratic Funding | Governance (0x0102) | Matching pool, QF formula, Sybil resistance |
| 77 | Carbon Offset Marketplace | BST-3525 + BST-VC | Retirement mechanism, corporate accounting |

## Ready to Build (34)

Implementable **now** with existing SDK primitives (BST-20/721/1155/3525/4626, BST-VC, BST-DID, StorageMap, cross-contract calls, Escrow, ZK compliance).

### DeFi
| # | Contract | Key Primitives |
|---|----------|----------------|
| 13 | Bonding Curve | BST-20, StorageMap (reserve tracking) |
| 15 | Lottery | BST-20, StakingPool yield, BLAKE3 randomness |
| 16 | DCA Bot | StorageMap, Caldera DEX swaps, keeper execution |
| 19 | Atomic Swap | BLAKE3 hashlock, block-number timelock |
| 20 | Fee Distributor | StorageMap, BST-4626, epoch snapshots |
| 67 | Token Launchpad | BST-20, Escrow, ZK compliance gating, vesting |
| 69 | NFT Lending | BST-721 collateral, Escrow, liquidation auctions |
| 70 | Index Fund | BST-4626 vault, weighted basket, rebalancing |
| 71 | Conditional Escrow | Escrow (0x0103), oracle triggers, multi-party |

### Real-World Assets
| # | Contract | Key Primitives |
|---|----------|----------------|
| 22 | Real Estate Fractionalization | BST-3525 (slot=property), income distribution |
| 23 | Invoice Factoring | BST-3525 (slot=debtor), bidding, payment routing |
| 24 | Carbon Credits | BST-3525 (slot=vintage), retirement (burn), BST-VC |
| 25 | Commodity Tokens | BST-20, BST-VC proof-of-reserves |
| 26 | Music Royalties | BST-3525 (slot=song), streaming earnings |
| 28 | Art Fractionalization | BST-3525 (slot=artwork), buyout, BST-VC provenance |

### Identity & Compliance
| # | Contract | Key Primitives |
|---|----------|----------------|
| 31 | Professional Licenses | BST-VC, IssuerRegistry, ZK proofs |
| 32 | Academic Credentials | BST-VC, SchemaRegistry |
| 33 | Age Verification | BST-VC + ZK range proofs |
| 34 | Compliant Privacy Pool | ComplianceEngine, Pedersen commitments, nullifiers |
| 35 | Confidential OTC | Escrow, Pedersen commitments, range proofs |
| 36 | Private Payroll | Pedersen commitments, ZK sum proofs, BST-VC |
| 37 | Anonymous Voting | Commit-reveal or ZK voting, BST-VC eligibility |
| 38 | Sealed-Bid Auction | Pedersen commitments, Escrow deposits |
| 76 | Identity Aggregator | BridgeETH, BST-DID, W3C compatibility |

### Infrastructure & Governance
| # | Contract | Key Primitives |
|---|----------|----------------|
| 42 | Timelock Vault | Escrow extension, linear/cliff vesting |
| 43 | Payment Channels | Ed25519 signed state, on-chain open/close |
| 44 | Meta-Transactions | Ed25519 sig verification, relayer fees |
| 45 | Contract Factory | StorageMap registry, parameterized deploy |
| 46 | Dead Man's Switch | StorageMap, heartbeat tracking, time-delayed release |
| 47 | Subscription Manager | Pull payments, BST-721 subscription NFT |
| 48 | Conviction Voting | StorageMap, time-weighted conviction |
| 72 | Token Curated Registry | StorageMap, staking/challenging, governance |
| 73 | Perpetual Organization | Continuous membership, rage-quit, streaming salary |

### Social & Gaming
| # | Contract | Key Primitives |
|---|----------|----------------|
| 51 | NFT Marketplace | BST-721/1155, Escrow, royalty enforcement |
| 52 | Loot & Crafting | BST-1155, deterministic recipes, BLAKE3 random |
| 53 | Play-to-Earn | Off-chain proofs, on-chain rewards, rate limiting |
| 54 | Virtual Land | BST-721 grid parcels, merge/split, rental |
| 55 | Achievement System | BST-721 soulbound badges |
| 56 | Trading Cards | BST-1155, pack opening, deck validation |
| 57 | Social Profile | StorageMap + BNS, BST-VC verification |
| 58 | Tipping | BST-20, BNS lookup, leaderboard |
| 59 | Content Monetization | BST-721 access tokens, streaming payments |
| 60 | Crowdfunding | Escrow, goal/deadline, milestone release |
| 61 | Bounty Board | Escrow, submission workflow, soulbound reputation |
| 62 | Messaging Registry | StorageMap, public key registry, BNS |
| 63 | Data Marketplace | IPFS hashes, encrypted keys, ZK quality proofs |
| 64 | Freelance Platform | Escrow milestones, BST-VC skills, arbitration |
| 65 | Social Recovery Wallet | M-of-N guardians, time-delayed rotation |
| 66 | Whistleblower Vault | Encrypted submission, ZK employment proofs |
| 74 | Credit Scoring | Soulbound BST-721, ZK range proofs |
| 78 | Decentralized Insurance Mutual | StorageMap pool, assessor voting, claims |

## Needs Oracle (14)

Require the **Decentralized Oracle Network (41)** to be built first. The oracle is the single highest-priority infrastructure gap.

| # | Contract | Oracle Dependency |
|---|----------|-------------------|
| 41 | **Oracle Contract** | **This IS the oracle — build first** |
| 04 | Stablecoin CDP | Collateral price feed for peg stability |
| 09 | Perpetual Futures | Mark price, funding rate calculation |
| 10 | Options Protocol | Underlying asset price at expiry |
| 11 | Insurance Pool | Parametric trigger conditions (weather, etc.) |
| 12 | Prediction Market | Outcome resolution |
| 17 | Synthetic Assets | Underlying price feed |
| 27 | Supply Chain | IoT delivery confirmation |
| 50 | Futarchy | Market outcome metrics + resolution |

> Note: 22 (Real Estate), 23 (Invoice), 24 (Carbon), 25 (Commodity), 26 (Music) also benefit from oracles for RWA pricing but can function with admin-set prices initially.

---

## Implementation Priority

1. **Oracle Contract (41)** — unblocks 13+ specs, critical DeFi infrastructure
2. **NFT Marketplace (51)** — high ecosystem value, fully buildable now
3. **Token Launchpad (67)** — drives token creation and ecosystem growth
4. **Lending Protocol (03)** — core DeFi primitive (BST-4626 foundation exists)
5. **Stablecoin CDP (04)** — foundational for DeFi (needs oracle)