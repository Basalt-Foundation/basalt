# Caldera Fusion — Protocol-Native DEX Design

## Overview

Caldera Fusion is a protocol-native decentralized exchange embedded directly into the Basalt blockchain's execution layer. Unlike smart-contract-based DEXes that inherit the host chain's limitations (reentrancy risks, contract dispatch overhead, gas inefficiency), Caldera Fusion operates as a first-class protocol feature — on par with transfers and staking.

The design combines:
- **Batch auctions** (inspired by CoW Protocol / fm-AMM) for MEV elimination
- **Hybrid AMM + order book** (inspired by Hyperliquid) for capital efficiency
- **Dynamic fees** (inspired by Ambient Finance) for LP protection
- **Intent-based execution** (inspired by UniswapX) for optimal routing
- **Concentrated liquidity** (inspired by Uniswap v3) for capital efficiency
- **Encrypted intents** (EC-ElGamal + AES-256-GCM) for information-theoretic MEV protection
- **Competitive solver network** for optimal settlement

## Architecture

```
 ┌─────────────────────────────────────────────────────────────────┐
 │                         BlockBuilder                            │
 │  ┌──────────────┐  ┌──────────────────┐  ┌──────────────────┐  │
 │  │   Phase A    │  │     Phase B      │  │    Phase C       │  │
 │  │  Non-DEX +   │→ │  Batch Auction   │→ │   Settlement     │  │
 │  │  Immediate   │  │  ComputeSettl()  │  │  ExecuteSettl()  │  │
 │  │  DEX Ops     │  │  Clearing Price  │  │  Fills + TWAP    │  │
 │  └──────────────┘  └──────────────────┘  └──────────────────┘  │
 └────────────────────────────┬────────────────────────────────────┘
                              │
                    ┌─────────┴─────────┐
                    │     DexState      │
                    │   (0x000...1009)  │
                    │  Binary key schema│
                    └─────────┬─────────┘
                              │
                    ┌─────────┴─────────┐
                    │  IStateDatabase   │
                    │  (Merkle trie)    │
                    └───────────────────┘
```

## State Storage Model

All DEX state lives at a well-known system address (`0x000...1009`) using the standard `IStateDatabase.SetStorage()` / `GetStorage()` interface. This provides:

- **Merkle proof support** — storage is in the state trie
- **RocksDB persistence** — survives node restarts
- **Fork-merge atomicity** — same as contract execution
- **Direct access** — no contract dispatch needed

### Key Schema

All keys are 32-byte `Hash256` values. The first byte determines the data type:

```
Prefix  Layout                                              Data Type
──────  ──────────────────────────────────────────────────  ──────────────
0x01    [prefix(1B)][poolId(8B)][padding(23B)]               Pool metadata
0x02    [prefix(1B)][poolId(8B)][padding(23B)]               Pool reserves
0x03    [prefix(1B)][poolId(8B)][owner(20B)][pad(3B)]        LP balance
0x04    [prefix(1B)][orderId(8B)][padding(23B)]              Limit order
0x05    [prefix(1B)][poolId(8B)][padding(23B)]               TWAP accumulator
0x06    [prefix(1B)][padding(31B)]                           Pool count
0x07    [prefix(1B)][padding(31B)]                           Order count
0x09    [prefix(1B)][token0(20B)][token1(9B)][fee(2B)]       Pool lookup
0x0A    [prefix(1B)][poolId(8B)][tick(4B signed BE)][pad]    Tick info (E2)
0x0B    [prefix(1B)][positionId(8B)][padding(23B)]           Position (E2)
0x0C    [prefix(1B)][poolId(8B)][padding(23B)]               Concentrated pool state (E2)
0x0D    [prefix(1B)][padding(31B)]                           Global position count
0x0E    [prefix(1B)][poolId(8B)][blockNumber(8B)][pad]       TWAP snapshot
0x0F    [prefix(1B)][poolId(8B)][wordPos(4B signed BE)][pad] Tick bitmap word (E2)
0x10    [prefix(1B)][poolId(8B)][padding(23B)]               Pool order list HEAD
0x11    [prefix(1B)][orderId(8B)][padding(23B)]              Order "next" pointer
0x12    [prefix(1B)][padding(31B)]                           Emergency pause flag
0x13    [prefix(1B)][paramId(1B)][padding(30B)]              Governance parameter
0x14    [prefix(1B)][blockNumber(8B BE)][padding(23B)]       Pool creations per block
```

All values use binary serialization with big-endian integers and little-endian UInt256 fields. No reflection, no JSON — fully AOT-safe.

## Transaction Types

```
Type 7:  DexCreatePool             [20B token0][20B token1][4B feeBps]
Type 8:  DexAddLiquidity           [8B poolId][32B amt0][32B amt1][32B min0][32B min1]
Type 9:  DexRemoveLiquidity        [8B poolId][32B shares][32B min0][32B min1]
Type 10: DexSwapIntent             [1B ver][20B tokenIn][20B tokenOut][32B amtIn][32B minOut][8B deadline][1B flags]
Type 11: DexLimitOrder             [8B poolId][32B price][32B amount][1B isBuy][8B expiry]
Type 12: DexCancelOrder            [8B orderId]
Type 13: DexTransferLp             [8B poolId][20B to][32B amount]
Type 14: DexApproveLp              [8B poolId][20B spender][32B amount]
Type 15: DexMintPosition           [8B poolId][4B tickLower][4B tickUpper][32B amt0][32B amt1]
Type 16: DexBurnPosition           [8B positionId][32B liquidityToBurn]
Type 17: DexCollectFees            [8B positionId]
Type 18: DexEncryptedSwapIntent    [8B epoch][48B C1][12B GCM_nonce][114B ciphertext][16B GCM_tag]
Type 19: DexAdminPause             [1B action]  (0=unpause, 1=pause)
Type 20: DexSetParameter           [1B paramId][8B value BE]
```

Types 7-9, 11-14 execute immediately in the standard transaction pipeline. Types 10 and 18 (swap intents) are collected and settled in batch during block production. Type 18 is decrypted by the proposer using the threshold-reconstructed group secret key. Types 19-20 are admin-only operations gated by `ChainParameters.DexAdminAddress`.

All DEX operations (types 7-18) check the emergency pause flag before executing — if paused, they return `DexPaused (10023)`.

## Three-Phase Block Production

### Phase A: Immediate Execution
All non-intent transactions execute sequentially:
- Transfers, staking, contract calls (types 0-6)
- Pool creation, liquidity operations, limit orders, cancellations (types 7-9, 11-12)
- Admin operations (types 19-20)

### Phase B: Batch Auction
Swap intents (types 10 and 18) are grouped by trading pair and processed through `BatchAuctionSolver`. Encrypted intents (type 18) are first decrypted using the threshold-reconstructed DKG group secret key, then merged with plaintext intents.

1. **Collect critical prices** from all intent limit prices, limit order prices, and AMM spot price (supports both constant-product and concentrated liquidity pools)
2. **Linear scan** for equilibrium: find price P* where aggregate buy volume meets aggregate sell volume
3. AMM reserves serve as passive liquidity of last resort — for concentrated pools, tick-walking simulation (`ConcentratedPool.SimulateSwap`) computes output at each candidate price
4. **Solver competition**: external solvers may submit competing solutions during the solver window; the solution with the highest surplus wins

### Phase C: Settlement
`BatchSettlementExecutor` applies the settlement:
1. Execute fills — debit input, credit output for each participant
2. Update limit orders — reduce amounts for partial fills, delete fully filled
3. Update AMM reserves — adjust for residual routed through the pool
4. Pay solver reward — if an external solver won the auction, compute `reward = (AmmVolume * feeBps / 10000) * SolverRewardBps / 10000`, deduct from pool reserve0, credit to solver
5. Update TWAP accumulator with the clearing price
6. Serialize TWAP snapshots into block header `ExtraData`

## Batch Auction Solver

The solver finds a uniform clearing price where supply meets demand. The key insight is that with a single clearing price, there is no ordering advantage — all participants receive the same execution price regardless of when they submitted their intent.

### Price Representation

Prices are expressed as token1-per-token0 in fixed-point format, scaled by 2^64 (`PriceScale`). This avoids floating-point entirely while providing 18+ decimal digits of precision.

### Volume Computation

At each candidate price P:
- **Buy volume**: sum of all buy intents/orders whose limit >= P, converted to token0 units
- **Sell volume**: sum of all sell intents/orders whose limit <= P, plus AMM contribution
- **AMM sell volume**: the solver auto-detects the pool type. For constant-product pools, computed from x*y=k formula. For concentrated liquidity pools, computed via read-only tick-walking simulation (`SimulateSwap`) that walks through initialized ticks up to the target price without mutating state

### Fill Generation

Once the clearing price P* is found:
1. Fill sell-side intents and orders (providing token0)
2. Fill buy-side intents and orders (wanting token0)
3. Route any residual imbalance through the AMM

## AMM (Constant Product)

The AMM uses the standard x*y=k invariant (Uniswap v2 model):

```
amountOut = (amountIn * (10000 - feeBps) * reserveOut) /
            (reserveIn * 10000 + amountIn * (10000 - feeBps))
```

LP shares are computed as:
- **First deposit**: `sqrt(amount0 * amount1) - MINIMUM_LIQUIDITY`
- **Subsequent**: `min(amount0 * totalSupply / reserve0, amount1 * totalSupply / reserve1)`

The minimum liquidity (1000 shares) is permanently locked to prevent the first-depositor manipulation attack.

## Concentrated Liquidity (E2)

Uniswap v3-style tick-based positions for improved capital efficiency:

- Liquidity deployed within `[tickLower, tickUpper]` price ranges
- Ticks must satisfy `TickMath.MinTick <= tickLower < tickUpper <= TickMath.MaxTick`
- Liquidity values must fit in `long` for `LiquidityNet` signed delta tracking
- Position management via `MintPosition`, `BurnPosition`, `CollectFees`
- `SimulateSwap`: read-only tick-walking for batch solver integration — walks through initialized ticks up to the target price without mutating state
- Tick bitmap (prefix `0x0F`) for efficient traversal of initialized ticks

## Order Book

Limit orders persist on-chain until filled, expired, or canceled:

- **Buy orders**: maximum price willing to pay (token1 per token0)
- **Sell orders**: minimum price willing to accept (token1 per token0)
- **Crossing**: buy price >= sell price at the clearing price
- **Execution**: all crossed orders execute at the uniform batch clearing price
- **Per-pool indexing**: linked-list structure (prefixes `0x10`, `0x11`) for efficient traversal with 10,000 order scan limit

Input tokens are escrowed at order placement and returned on cancellation or expiry.

## TWAP Oracle

The Time-Weighted Average Price oracle uses cumulative price accumulators:

```
TWAP = (accumulator[now] - accumulator[start]) / (block[now] - block[start])
```

Updated each block a pool has activity. Provides O(1) queries for any window length.

### TWAP Snapshots

Per-block snapshots (prefix `0x0E`) store accumulator values, enabling windowed TWAP queries without scanning entire block history. Default window: 7200 blocks (~4 hours at 2s blocks), configurable via governance.

### Block Header Integration

TWAP snapshots are serialized into `BlockHeader.ExtraData`:
```
[8B poolId][32B clearingPrice][32B twap]  (72 bytes per pool)
```
Multiple pools concatenated up to `MaxExtraDataBytes` (256). This enables light clients to verify price data without processing the full state trie.

## Dynamic Fees

Fees adjust based on recent price volatility:

```
if volatility <= threshold:
    fee = baseFee
else:
    excess = volatility - threshold
    feeIncrease = excess * growthFactor * baseFee / threshold
    fee = baseFee + feeIncrease

fee = clamp(fee, 1 bps, 500 bps)
```

Default parameters:
- Threshold: 100 bps (1% deviation triggers increase)
- Growth factor: 2 (each threshold multiple adds 2x base fee)
- Max fee: 500 bps (5% cap)
- Min fee: 1 bps (0.01% floor)

Volatility is estimated from the absolute deviation of spot price from TWAP, expressed in basis points.

## Emergency Pause

The DEX admin (`ChainParameters.DexAdminAddress`) can pause/unpause all DEX operations:

- **Pause** (type 19, data `[0x01]`): sets pause flag at prefix `0x12`; all DEX operations (types 7-18) return `DexPaused (10023)`
- **Unpause** (type 19, data `[0x00]`): clears the pause flag
- Admin address is **required** for mainnet/testnet (`ChainId <= 2`); validated at startup by `ChainParameters.Validate()`

## Governance Parameters

On-chain governance allows the admin to override DEX parameters without a protocol upgrade. Parameters are stored at prefix `0x13 + paramId` and read via a fallback chain: governance override → `ChainParameters` default.

| Param ID | Name | Default | Description |
|----------|------|---------|-------------|
| `0x01` | `SolverRewardBps` | 500 (5%) | Fraction of AMM fees rewarded to winning solver |
| `0x02` | `MaxIntentsPerBatch` | 500 | Maximum swap intents per batch auction per block |
| `0x03` | `TwapWindowBlocks` | 7200 (~4h) | TWAP oracle window in blocks |
| `0x04` | `MaxPoolCreationsPerBlock` | 10 | Pool creation rate limit per block (DoS protection) |

Set via type 20 (`DexSetParameter`) transaction from the admin address.

### Pool Creation Rate Limiting

Per-block pool creation counter (prefix `0x14 + blockNumber`) prevents DoS via mass pool creation. `DexEngine.CreatePool()` checks the counter against `MaxPoolCreationsPerBlock` (governance-overridable) and returns `DexPoolCreationLimitReached (10024)` when exceeded.

## MEV Elimination

The batch auction design eliminates the primary MEV vectors:

| Attack | Why it fails |
|--------|-------------|
| Front-running | Uniform clearing price — order within batch doesn't matter |
| Sandwich | No individual execution — all intents settle at same price |
| Backrunning | Price is determined by aggregate supply/demand, not individual trades |
| JIT liquidity | Liquidity must be committed before the batch is computed |
| Information leakage | Encrypted intents (EC-ElGamal + AES-GCM) hide intent contents from proposer |
| Proposer extraction | Solver competition ensures surplus goes to users; solvers earn fee-based rewards |

## Solver Network (E4)

External solvers compete to provide optimal batch settlements:

- **Registration**: solvers register via REST API with Ed25519 public key and endpoint
- **Solution window**: proposer opens a time-limited window (`SolverWindowMs`, default 500ms) for external solutions
- **Scoring**: surplus-based — highest `sum(amountOut - minAmountOut)` wins
- **Feasibility validation**: solutions must pass constant-product invariant check, clearing price > 0, non-empty fills
- **Solver rewards**: winning solvers receive `SolverRewardBps` (default 5%) of AMM fees generated during settlement, deducted from pool reserves
- **Revert tracking**: `SolverManager` tracks `RevertCount` per solver — repeated settlement execution failures degrade solver reputation
- **Fallback**: built-in solver runs when no valid external solution exists (no reward paid)
- **Signature verification**: solutions are Ed25519-signed with `ComputeSolutionSignData(blockNumber, poolId, clearingPrice)` to prevent spoofing

## Gas Costs

| Operation | Gas |
|-----------|-----|
| CreatePool | 100,000 |
| AddLiquidity | 80,000 |
| RemoveLiquidity | 80,000 |
| SwapIntent | 80,000 |
| LimitOrder | 60,000 |
| CancelOrder | 40,000 |
| TransferLp | 40,000 |
| ApproveLp | 30,000 |
| MintPosition | 120,000 |
| BurnPosition | 100,000 |
| CollectFees | 60,000 |
| EncryptedSwapIntent | 100,000 |

## Error Codes

| Code | Name | Description |
|------|------|-------------|
| 10001 | DexPoolNotFound | Referenced pool does not exist |
| 10002 | DexPoolAlreadyExists | Pool with same pair and fee tier exists |
| 10003 | DexInvalidPair | Identical tokens or invalid token addresses |
| 10004 | DexInvalidFeeTier | Fee tier not in allowed set [1, 5, 30, 100] |
| 10005 | DexInsufficientLiquidity | Pool has insufficient reserves |
| 10006 | DexSlippageExceeded | Output below minimum acceptable amount |
| 10007 | DexInvalidAmount | Zero amount or price in order |
| 10008 | DexOrderNotFound | Referenced order does not exist |
| 10009 | DexUnauthorized | Sender is not order owner |
| 10010 | DexDeadlineExpired | Swap intent deadline has passed |
| 10011 | DexInvalidData | Malformed transaction data |
| 10012 | DexOrderExpired | Order has passed its expiry block |
| 10013 | DexInsufficientLpBalance | Insufficient LP token balance for transfer |
| 10014 | DexInsufficientLpAllowance | Insufficient LP allowance for transferFrom |
| 10015 | DexInvalidTick | Tick out of valid range |
| 10016 | DexInvalidTickRange | Tick range invalid (lower >= upper) |
| 10017 | DexPositionNotFound | Concentrated liquidity position not found |
| 10018 | DexPositionNotOwner | Sender is not position owner |
| 10019 | DexDecryptionFailed | Encrypted intent decryption failed |
| 10020 | DexInvalidEpoch | Unknown or expired DKG epoch |
| 10021 | DexTransferFailed | BST-20 token transfer failed during execution |
| 10022 | DexInsufficientBalance | Insufficient balance for debit |
| 10023 | DexPaused | DEX is paused by admin |
| 10024 | DexPoolCreationLimitReached | Max pool creations per block exceeded |
| 10025 | DexAdminUnauthorized | Sender is not DEX admin |
| 10026 | DexInvalidParameter | Invalid governance parameter ID |

## REST API

| Method | Path | Description |
|--------|------|-------------|
| GET | `/v1/dex/pools` | List all pools (max 100) |
| GET | `/v1/dex/pools/{id}` | Pool details with reserves |
| GET | `/v1/dex/pools/{id}/orders` | Active orders for a pool |
| GET | `/v1/dex/orders/{id}` | Single order details |
| GET | `/v1/dex/pools/{id}/twap?window=N` | TWAP, spot price, volatility |

## File Structure

```
src/execution/Basalt.Execution/Dex/
    Math/
        FullMath.cs              256-bit safe multiplication
        DexLibrary.cs            AMM primitives
        TickMath.cs              Tick ↔ sqrt price conversion (E2)
        SqrtPriceMath.cs         Concentrated liquidity price math (E2)
        LiquidityMath.cs         Signed liquidity delta arithmetic (E2)
    BatchAuctionSolver.cs        Clearing price computation
    BatchResult.cs               Settlement result + fill records
    BatchSettlementExecutor.cs   Applies settlements to state
    ConcentratedPool.cs          Tick-based position management (E2)
    DexEngine.cs                 Core pool/swap/order logic + BST-20 (E1)
    DexResult.cs                 Operation result type
    DexState.cs                  State reader/writer + governance + pause (E1/E5)
    DynamicFeeCalculator.cs      Volatility-adjusted fees
    EncryptedIntent.cs           EC-ElGamal + AES-256-GCM encryption (E3)
    OrderBook.cs                 Limit order matching
    ParsedIntent.cs              Swap intent parsing
    PoolMetadata.cs              Data structs (pool, order, position, tick)
    TwapOracle.cs                Price oracle + block header serialization

src/core/Basalt.Crypto/
    BlsCrypto.cs                 G1 scalar multiplication for EC-ElGamal (E3)

src/consensus/Basalt.Consensus/Dkg/
    DkgProtocol.cs               Feldman VSS state machine (E3)
    ThresholdCrypto.cs           BLS12-381 threshold cryptography (E3)

src/node/Basalt.Node/Solver/
    SolverManager.cs             Registration, window, selection, revert tracking (E4)
    SolverScoring.cs             Surplus scoring + feasibility (E4)
    SolverSolution.cs            Signed solution struct (E4)
    SolverInfoAdapter.cs         REST API bridge (E4)

tests/Basalt.Execution.Tests/Dex/
    BatchAuctionSolverTests.cs   Solver, parsing, mempool partitioning
    ConcentratedPoolTests.cs     Concentrated liquidity positions (E2)
    DexEngineTests.cs            Engine + executor + BST-20 integration
    DexFuzzTests.cs              Fuzz testing for settlement invariants
    DexMathTests.cs              FullMath + DexLibrary
    DexStateTests.cs             State CRUD, serialization, LP allowances
    DynamicFeeTests.cs           Fee computation
    EncryptedIntentTests.cs      EC-ElGamal + AES-GCM round-trip (E3)
    FeeTrackingTests.cs          Fee collection and LP tracking
    IntegrationTests.cs          End-to-end flows (concentrated batch, encrypted E2E, solver rewards, mixed pools)
    LpTokenTests.cs              LP transfer/approve (E1)
    MainnetHardeningTests.cs     Admin validation, parameter checks
    MainnetReadinessTests.cs     Production readiness assertions
    MainnetReadinessTests2.cs    Additional production readiness tests
    OrderBookTests.cs            Order matching
    SqrtPriceMathTests.cs        Concentrated liquidity price math (E2)
    TickMathTests.cs             Tick math tests (E2)
    TwapOracleTests.cs           Oracle + serialization

tests/Basalt.Consensus.Tests/Dkg/
    DkgProtocolTests.cs          Full DKG lifecycle (E3)
    ThresholdCryptoTests.cs      Polynomial, shares, reconstruction (E3)

tests/Basalt.Node.Tests/Solver/
    SolverManagerTests.cs        Registration, window, submission, revert tracking (E4)
    SolverScoringTests.cs        Surplus scoring, selection (E4)
```

## Phase E Features

### E1: BST-20 Token Integration + LP Token Transfers
- Trade any BST-20 token pair via `ManagedContractRuntime` dispatch
- LP shares are transferable with standard `TransferLp`/`ApproveLp` pattern
- Transaction types 13 (TransferLp), 14 (ApproveLp)

### E2: Concentrated Liquidity
- Uniswap v3-style tick-based positions for improved capital efficiency
- Liquidity deployed within `[tickLower, tickUpper]` price ranges
- Tick math, sqrt price math, position management
- Tick bitmap (prefix `0x0F`) for efficient traversal of initialized ticks
- `SimulateSwap`: read-only tick-walking for batch solver integration (no state mutation)
- Batch auction solver auto-detects concentrated pools via `GetConcentratedPoolState()` and uses tick-walking instead of constant-product math
- Validation: ticks must be within `[MinTick, MaxTick]`, liquidity must fit in `long`
- Transaction types 15 (MintPosition), 16 (BurnPosition), 17 (CollectFees)

### E3: Encrypted Intents (EC-ElGamal + AES-256-GCM)
- DKG (Feldman VSS) generates group public key (GPK in G1) shared by validators
- **Encryption**: EC-ElGamal key exchange (`C1 = r * G1`, `SharedPoint = r * GPK`) + AES-256-GCM authenticated encryption
- **Decryption**: requires threshold-reconstructed group secret `s` → `SharedPoint = s * C1` → derive AES key → decrypt + authenticate
- **Security**: IND-CCA2 (semantic security + ciphertext authentication); the public GPK alone cannot decrypt
- Transaction format: `[8B epoch][48B C1][12B GCM_nonce][114B ciphertext][16B GCM_tag]` = 198 bytes
- Transaction type 18 (DexEncryptedSwapIntent)

### E4: Solver Network
- External solvers compete to provide optimal batch settlements
- Surplus-based scoring: highest sum(amountOut - minAmountOut) wins
- **Solver rewards**: winning solvers receive `SolverRewardBps` (default 5%) of AMM fees generated during settlement, deducted from pool reserves
- **Revert tracking**: `SolverManager.IncrementRevertCount()` degrades reputation for solvers whose settlements revert during execution
- `SolverManager.GetBestSettlement()` tags `BatchResult.WinningSolver` when external solver wins; `BatchSettlementExecutor.PaySolverReward()` handles payout
- Built-in solver fallback when no external solution is valid (no reward paid)
- REST API for solver registration and pending intent queries

### E5: Mainnet Readiness
- **Emergency pause**: admin-controlled pause/unpause (type 19) halts all DEX operations
- **Governance parameters**: on-chain overrides for SolverRewardBps, MaxIntentsPerBatch, TwapWindowBlocks, MaxPoolCreationsPerBlock (type 20)
- **Pool creation rate limiting**: per-block counter prevents DoS via mass pool creation
- **TWAP window**: extended to 7200 blocks (~4h) for robust price oracle
- **Admin address required** for mainnet/testnet (enforced by `ChainParameters.Validate()`)
