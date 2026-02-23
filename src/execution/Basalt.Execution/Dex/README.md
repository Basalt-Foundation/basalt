# Basalt.Execution.Dex — Protocol-Native DEX Module

The Caldera Fusion DEX is a first-class protocol feature of the Basalt blockchain, implementing a hybrid AMM + order book exchange with batch auction settlement and dynamic fee pricing. Unlike smart-contract-based DEXes, all operations execute directly against the state trie with no contract dispatch overhead and no reentrancy risks.

## Architecture

```
                        +--------------------+
                        |   BlockBuilder     |
                        | (Three-Phase)      |
                        +--------+-----------+
                                 |
              +------------------+------------------+
              |                  |                  |
     Phase A: Non-DEX   Phase B: Batch Auction   Phase C: Settlement
     Transfers, Staking  ComputeSettlement()     ExecuteSettlement()
     Liquidity, Orders   Uniform clearing price  Apply fills, TWAP
              |                  |                  |
              v                  v                  v
        +----------+    +------------------+   +-----------+
        | DexEngine|    |BatchAuctionSolver|   |BatchSettle|
        +----+-----+    +--------+---------+   +-----+-----+
             |                   |                     |
             +-------------------+---------------------+
                                 |
                          +------+-------+
                          |   DexState   |
                          | (0x...1009)  |
                          +------+-------+
                                 |
                          +------+-------+
                          | IStateDatabase|
                          +--------------+
```

## Components

### DexState
State reader/writer for all DEX data. Uses binary-encoded storage keys at the well-known system address `0x000...1009`. Key prefix bytes determine data type:

| Prefix | Data |
|--------|------|
| `0x01` | Pool metadata (token0, token1, feeBps) |
| `0x02` | Pool reserves (reserve0, reserve1, totalSupply, kLast) |
| `0x03` | LP balance (per pool, per owner) |
| `0x04` | Limit order (owner, pool, price, amount, side, expiry) |
| `0x05` | TWAP accumulator (cumulative price, last block) |
| `0x06` | Global pool count |
| `0x07` | Global order count |
| `0x09` | Pool lookup (token pair + fee tier to pool ID) |

### DexEngine
Core protocol logic for pool creation, liquidity management, single swaps, and limit orders. Handles token transfers via direct account state modification for native BST pairs.

### BatchAuctionSolver
Computes uniform clearing prices for batch auction settlements. This is the core MEV-elimination mechanism: all swap intents in a block receive the same price, eliminating front-running and sandwich attacks.

**Algorithm:**
1. Collect critical prices from all intents, limit orders, and AMM spot price
2. Sort prices ascending
3. For each price P, compute aggregate buy volume and sell volume
4. Find equilibrium P* where supply meets demand
5. Generate fills at P* — peer-to-peer first, residual through AMM

All prices use fixed-point representation scaled by 2^64 (`PriceScale`).

### BatchSettlementExecutor
Applies batch auction results to the state: debits/credits participant balances, updates AMM reserves, refreshes TWAP accumulators, and generates transaction receipts.

### OrderBook
Limit order matching logic. Scans the order book for crossing orders (buy price >= clearing price, sell price <= clearing price), matches them, and handles partial fills.

### TwapOracle
On-chain Time-Weighted Average Price oracle. Provides O(1) TWAP queries over arbitrary windows using cumulative price accumulators. Serializes price snapshots into block header `ExtraData` for light client consumption.

### DynamicFeeCalculator
Computes volatility-adjusted swap fees inspired by Ambient Finance's dynamic fee model. Fees increase during high volatility to compensate LPs for impermanent loss, and decrease during stable periods to attract volume.

**Formula:**
```
effectiveFee = baseFee + (excess / threshold) * growthFactor * baseFee
effectiveFee = clamp(effectiveFee, 1 bps, 500 bps)
```

### Math Library
- **FullMath**: Safe 256-bit multiplication with `BigInteger` intermediates. `MulDiv`, `MulDivRoundingUp`, `Sqrt`.
- **DexLibrary**: AMM primitives — `GetAmountOut`, `GetAmountIn`, `Quote`, `ComputeInitialLiquidity`, `ComputeLiquidity`.

## Transaction Types

| Type | Value | Description |
|------|-------|-------------|
| `DexCreatePool` | 7 | Create a new liquidity pool |
| `DexAddLiquidity` | 8 | Deposit tokens for LP shares |
| `DexRemoveLiquidity` | 9 | Burn LP shares for tokens |
| `DexSwapIntent` | 10 | Batch-auctionable swap intent |
| `DexLimitOrder` | 11 | Persistent limit order |
| `DexCancelOrder` | 12 | Cancel an existing order |

Types 8, 9, 11, 12 execute immediately in Phase A. Type 10 (swap intents) are collected and settled in batch in Phases B and C.

## MEV Elimination

1. **Batch execution** — swap intents are not executed individually; the proposer cannot reorder for profit
2. **Uniform clearing price** — all intents receive the same price; no first-mover advantage
3. **Peer-to-peer matching first** — reduces AMM price impact and loss-versus-rebalancing
4. **Limit order depth** — adds liquidity at each price level, reducing slippage

## REST API Endpoints

| Method | Path | Description |
|--------|------|-------------|
| GET | `/v1/dex/pools` | List all liquidity pools |
| GET | `/v1/dex/pools/{poolId}` | Get pool details |
| GET | `/v1/dex/pools/{poolId}/orders` | List orders for a pool |
| GET | `/v1/dex/orders/{orderId}` | Get order details |
| GET | `/v1/dex/pools/{poolId}/twap?window=100` | TWAP and volatility data |

## Integration

The DEX is initialized at genesis by `GenesisContractDeployer`, which creates the system account at `0x000...1009`. The `NodeCoordinator` routes swap intent transactions through `BuildBlockWithDex()` for three-phase block production. All other DEX transaction types (pool creation, liquidity, orders) are executed in the standard transaction pipeline.
