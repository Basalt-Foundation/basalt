# Caldera Fusion — Protocol-Native DEX Design

## Overview

Caldera Fusion is a protocol-native decentralized exchange embedded directly into the Basalt blockchain's execution layer. Unlike smart-contract-based DEXes that inherit the host chain's limitations (reentrancy risks, contract dispatch overhead, gas inefficiency), Caldera Fusion operates as a first-class protocol feature — on par with transfers and staking.

The design combines:
- **Batch auctions** (inspired by CoW Protocol / fm-AMM) for MEV elimination
- **Hybrid AMM + order book** (inspired by Hyperliquid) for capital efficiency
- **Dynamic fees** (inspired by Ambient Finance) for LP protection
- **Intent-based execution** (inspired by UniswapX) for optimal routing

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
Prefix  Layout                                         Data Type
──────  ─────────────────────────────────────────────  ──────────────
0x01    [prefix(1B)][poolId(8B)][padding(23B)]         Pool metadata
0x02    [prefix(1B)][poolId(8B)][padding(23B)]         Pool reserves
0x03    [prefix(1B)][poolId(8B)][owner(20B)][pad(3B)]  LP balance
0x04    [prefix(1B)][orderId(8B)][padding(23B)]        Limit order
0x05    [prefix(1B)][poolId(8B)][padding(23B)]         TWAP accumulator
0x06    [prefix(1B)][padding(31B)]                     Pool count
0x07    [prefix(1B)][padding(31B)]                     Order count
0x09    [prefix(1B)][token0(20B)][token1(9B)][fee(2B)] Pool lookup
```

All values use binary serialization with big-endian integers and little-endian UInt256 fields. No reflection, no JSON — fully AOT-safe.

## Transaction Types

```
Type 7:  DexCreatePool      [20B token0][20B token1][4B feeBps]
Type 8:  DexAddLiquidity    [8B poolId][32B amt0][32B amt1][32B min0][32B min1]
Type 9:  DexRemoveLiquidity [8B poolId][32B shares][32B min0][32B min1]
Type 10: DexSwapIntent      [1B ver][20B tokenIn][20B tokenOut][32B amtIn][32B minOut][8B deadline][1B flags]
Type 11: DexLimitOrder      [8B poolId][32B price][32B amount][1B isBuy][8B expiry]
Type 12: DexCancelOrder     [8B orderId]
```

Types 7-9, 11-12 execute immediately in the standard transaction pipeline. Type 10 (swap intents) are collected and settled in batch during block production.

## Three-Phase Block Production

### Phase A: Immediate Execution
All non-intent transactions execute sequentially:
- Transfers, staking, contract calls (types 0-6)
- Pool creation, liquidity operations, limit orders, cancellations (types 7-9, 11-12)

### Phase B: Batch Auction
Swap intents (type 10) are grouped by trading pair and processed through `BatchAuctionSolver`:

1. **Collect critical prices** from all intent limit prices, limit order prices, and AMM spot price
2. **Linear scan** for equilibrium: find price P* where aggregate buy volume meets aggregate sell volume
3. AMM reserves serve as passive liquidity of last resort

### Phase C: Settlement
`BatchSettlementExecutor` applies the settlement:
1. Execute fills — debit input, credit output for each participant
2. Update limit orders — reduce amounts for partial fills, delete fully filled
3. Update AMM reserves — adjust for residual routed through the pool
4. Update TWAP accumulator with the clearing price
5. Serialize TWAP snapshots into block header `ExtraData`

## Batch Auction Solver

The solver finds a uniform clearing price where supply meets demand. The key insight is that with a single clearing price, there is no ordering advantage — all participants receive the same execution price regardless of when they submitted their intent.

### Price Representation

Prices are expressed as token1-per-token0 in fixed-point format, scaled by 2^64 (`PriceScale`). This avoids floating-point entirely while providing 18+ decimal digits of precision.

### Volume Computation

At each candidate price P:
- **Buy volume**: sum of all buy intents/orders whose limit >= P, converted to token0 units
- **Sell volume**: sum of all sell intents/orders whose limit <= P, plus AMM contribution
- **AMM sell volume**: computed from constant-product formula — how much token0 the AMM can output if the price moves from spot to P

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

## Order Book

Limit orders persist on-chain until filled, expired, or canceled:

- **Buy orders**: maximum price willing to pay (token1 per token0)
- **Sell orders**: minimum price willing to accept (token1 per token0)
- **Crossing**: buy price >= sell price at the clearing price
- **Execution**: all crossed orders execute at the uniform batch clearing price

Input tokens are escrowed at order placement and returned on cancellation or expiry.

## TWAP Oracle

The Time-Weighted Average Price oracle uses cumulative price accumulators:

```
TWAP = (accumulator[now] - accumulator[start]) / (block[now] - block[start])
```

Updated each block a pool has activity. Provides O(1) queries for any window length.

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

## MEV Elimination

The batch auction design eliminates the primary MEV vectors:

| Attack | Why it fails |
|--------|-------------|
| Front-running | Uniform clearing price — order within batch doesn't matter |
| Sandwich | No individual execution — all intents settle at same price |
| Backrunning | Price is determined by aggregate supply/demand, not individual trades |
| JIT liquidity | Liquidity must be committed before the batch is computed |

## Gas Costs

| Operation | Gas |
|-----------|-----|
| CreatePool | 100,000 |
| AddLiquidity | 80,000 |
| RemoveLiquidity | 80,000 |
| SwapIntent | 80,000 |
| LimitOrder | 60,000 |
| CancelOrder | 40,000 |

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
    BatchAuctionSolver.cs        Clearing price computation
    BatchResult.cs               Settlement result + fill records
    BatchSettlementExecutor.cs   Applies settlements to state
    DexEngine.cs                 Core pool/swap/order logic
    DexResult.cs                 Operation result type
    DexState.cs                  State reader/writer
    DynamicFeeCalculator.cs      Volatility-adjusted fees
    OrderBook.cs                 Limit order matching
    ParsedIntent.cs              Swap intent parsing
    PoolMetadata.cs              Data structs (pool, order, TWAP)
    TwapOracle.cs                Price oracle + block header serialization

tests/Basalt.Execution.Tests/Dex/
    BatchAuctionSolverTests.cs   Solver, parsing, mempool partitioning
    DexEngineTests.cs            Engine + executor integration
    DexMathTests.cs              FullMath + DexLibrary
    DexStateTests.cs             State CRUD, serialization
    DynamicFeeTests.cs           Fee computation
    IntegrationTests.cs          End-to-end flows
    OrderBookTests.cs            Order matching
    TwapOracleTests.cs           Oracle + serialization
```
