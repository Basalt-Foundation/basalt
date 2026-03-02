# Basalt.Execution.Dex — Protocol-Native DEX Module

The Caldera Fusion DEX is a first-class protocol feature of the Basalt blockchain, implementing a hybrid AMM + order book exchange with batch auction settlement, concentrated liquidity, encrypted intents, and dynamic fee pricing. Unlike smart-contract-based DEXes, all operations execute directly against the state trie with no contract dispatch overhead and no reentrancy risks.

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
     Transfers, Staking  Decrypt intents          Apply fills, TWAP
     Liquidity, Orders   Group by pair            Solver reward
     Admin (pause/param) ComputeSettlement()      Gas accounting
              |                  |                  |
              v                  v                  v
        +----------+    +------------------+   +-------------+
        | DexEngine|    |BatchAuctionSolver|   |BatchSettle  |
        +----+-----+    +--------+---------+   | Executor    |
             |                   |              +------+------+
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

| Prefix | Key Format | Data |
|--------|-----------|------|
| `0x01` | `poolId(8B)` | Pool metadata (token0, token1, feeBps) |
| `0x02` | `poolId(8B)` | Pool reserves (reserve0, reserve1, totalSupply, kLast) |
| `0x03` | `poolId(8B) + owner(20B)` | LP balance (UInt256) |
| `0x04` | `orderId(8B)` | Limit order data (owner, pool, price, amount, side, expiry) |
| `0x05` | `poolId(8B)` | TWAP accumulator (cumulative price, last block) |
| `0x06` | — | Global pool count (ulong) |
| `0x07` | — | Global order count (ulong) |
| `0x08` | `poolId(8B) + BLAKE3(owner+spender)[0..23]` | LP allowance (UInt256) |
| `0x09` | `BLAKE3(0x09+token0+token1+feeBps)` | Pool lookup by pair+fee (full BLAKE3 hash) |
| `0x0A` | `poolId(8B) + tick(4B signed BE)` | Tick info (concentrated liquidity) |
| `0x0B` | `positionId(8B)` | Concentrated liquidity position |
| `0x0C` | `poolId(8B)` | Concentrated pool state (sqrtPrice, currentTick, totalLiquidity, feeGrowth) |
| `0x0D` | — | Global position count (ulong) |
| `0x0E` | `poolId(8B) + blockNumber(8B)` | TWAP snapshot (per-block accumulator for windowed queries) |
| `0x0F` | `poolId(8B) + wordPos(4B signed BE)` | Tick bitmap word (256 ticks per word) |
| `0x10` | `poolId(8B)` | Per-pool order linked list HEAD pointer |
| `0x11` | `orderId(8B)` | Order "next" pointer for linked list |
| `0x12` | — | Emergency pause flag (1 byte: 0=unpaused, 1=paused) |
| `0x13` | `paramId(1B)` | Governance parameter override (ulong BE) |
| `0x14` | `blockNumber(8B)` | Pool creation count per block (rate limit) |

### DexEngine
Core protocol logic for pool creation, liquidity management, single swaps, and limit orders. Handles token transfers via direct account state modification for native BST pairs, and via `ManagedContractRuntime` FNV-1a `Transfer(Address,UInt256)` dispatch for BST-20 tokens.

Pool creation supports a per-block rate limit (default 10, governance-overridable). Tokens are canonically sorted (`token0 < token1`). Only allowed fee tiers: `[1, 5, 30, 100]` bps.

Limit orders store `Amount` in **token0 units** for both buy and sell sides. For buy orders, the escrowed token1 is computed as `amount × price / PriceScale` at placement. Cancellation and expiry refunds reverse this computation to return the correct token1 amount.

### BatchAuctionSolver
Computes uniform clearing prices for batch auction settlements. This is the core MEV-elimination mechanism: all swap intents in a block receive the same price, eliminating front-running and sandwich attacks.

**Algorithm:**
1. Filter expired intents by deadline
2. Collect critical prices from all intents (direction-aware), limit orders, AMM spot price, and concentrated pool spot price
3. Sweep ALL prices, selecting the one that **maximizes matched volume** (`min(buyVol, totalSell)`); ties broken by highest price
4. Generate fills at P* — peer-to-peer first, residual through AMM based on net imbalance
5. Enforce `AllowPartialFill` flag — intents requiring full fills are skipped if insufficient volume

Supports both constant-product and concentrated liquidity pools. When a pool has concentrated liquidity state, the solver uses read-only tick-walking simulation (`ConcentratedPool.SimulateSwap`) to compute AMM output at each candidate price. Pool type detection is automatic via `DexState.GetConcentratedPoolState()`.

All prices use fixed-point representation scaled by 2^64 (`PriceScale`).

### BatchSettlementExecutor
Applies batch auction results to the state: debits/credits participant balances, updates AMM reserves, pays solver rewards, refreshes TWAP accumulators, and generates transaction receipts.

For limit order fills, uses `FillRecord.IsBuy` to determine correct token directions (buy orders receive token0, sell orders receive token1). Escrowed input tokens are transferred from the DEX address to the pool/counterparty. Buy order remaining amounts are decremented by the token0 received (`fill.AmountOut`), and price improvement refunds are issued when `clearingPrice < limitPrice` (excess escrowed token1 is returned to the buyer).

**Solver reward flow:** When `BatchResult.WinningSolver` is set and `AmmVolume > 0`, the executor computes the reward as a fraction of AMM fee revenue: `reward = (AmmVolume * feeBps / 10000) * SolverRewardBps / 10000`. The reward is deducted from the correct reserve based on `AmmBoughtToken0` (sell pressure → token0 fees → Reserve0; buy pressure → token1 fees → Reserve1) and credited to the solver's account. `SolverRewardBps` is governance-overridable (default 500 = 5%).

Individual fill failures are caught and do not abort remaining settlements.

### OrderBook
Limit order matching using per-pool linked lists for efficient traversal. Orders are indexed per-pool via `GetPoolOrderHead`/`GetOrderNext` pointers, providing O(orders-in-pool) scan instead of O(total-orders-globally).

`FindCrossingOrders` walks the linked list, collecting buy orders with price >= clearing price and sell orders with price <= clearing price. Results are capped at `maxOrders` (default 100) per side and sorted by price priority.

`CleanupExpiredOrders` walks the same linked list, removing expired orders and returning escrowed tokens to owners. Buy order refunds are computed as `Amount × Price / PriceScale` (converting remaining token0 back to token1 at the order's limit price).

### TwapOracle
On-chain Time-Weighted Average Price oracle using cumulative price accumulators and per-block snapshots. Default window: 7200 blocks (~4 hours at 2s blocks), governance-overridable.

**TWAP formula:**
```
twap = (accumulator[now] - accumulator[start]) / (block[now] - block[start])
```

The oracle stores per-block accumulator snapshots (prefix `0x0E`) and searches backward from the target start block to find the nearest snapshot. Serializes price data into block header `ExtraData` for light client consumption (72 bytes per pool: `[8B poolId][32B clearingPrice][32B twap]`).

`ComputeVolatilityBps` returns instantaneous deviation from TWAP: `|spot - twap| / twap * 10000`, capped at 10,000 bps. Used as input to the dynamic fee calculator.

### DynamicFeeCalculator
Computes volatility-adjusted swap fees inspired by Ambient Finance's dynamic fee model. Fees increase during high volatility to compensate LPs for impermanent loss, and decrease during stable periods to attract volume.

**Formula:**
```
effectiveFee = baseFee + (excess / threshold) * growthFactor * baseFee
effectiveFee = clamp(effectiveFee, 1 bps, 500 bps)
```

Where `excess = max(0, volatilityBps - VolatilityThresholdBps)`, `VolatilityThresholdBps = 100`, `GrowthFactor = 2`.

### Math Library
- **FullMath**: Safe 256-bit multiplication with `BigInteger` intermediates. `MulDiv`, `MulDivRoundingUp`, `Sqrt` (Newton's method).
- **DexLibrary**: AMM primitives — `GetAmountOut`, `GetAmountIn`, `Quote`, `ComputeInitialLiquidity` (Uniswap v2 formula with MINIMUM_LIQUIDITY = 1000 lock), `ComputeLiquidity`. Fee tier validation.
- **TickMath**: `GetSqrtRatioAtTick()` (1.0001^tick via binary decomposition), `GetTickAtSqrtRatio()` (binary search). Tick range [-887272, 887272].
- **SqrtPriceMath**: `GetAmount0Delta` (two-step: `L*(sqrtB-sqrtA)/sqrtB * Q96/sqrtA`), `GetAmount1Delta` (`L*(sqrtB-sqrtA)/Q96`), `GetNextSqrtPriceFromInput`. All use `FullMath.MulDiv` for overflow safety.
- **LiquidityMath**: `GetLiquidityForAmounts` (three-case: below/in/above range), `AddDelta` (signed liquidity adjustments).

## Transaction Types

| Type | Value | Phase | Description |
|------|-------|-------|-------------|
| `DexCreatePool` | 7 | A | Create a new liquidity pool |
| `DexAddLiquidity` | 8 | A | Deposit tokens for LP shares |
| `DexRemoveLiquidity` | 9 | A | Burn LP shares for tokens |
| `DexSwapIntent` | 10 | B/C | Batch-auctionable swap intent |
| `DexLimitOrder` | 11 | A | Persistent limit order |
| `DexCancelOrder` | 12 | A | Cancel an existing order |
| `DexTransferLp` | 13 | A | Transfer LP shares |
| `DexApproveLp` | 14 | A | Approve LP spend allowance |
| `DexMintPosition` | 15 | A | Mint concentrated liquidity position |
| `DexBurnPosition` | 16 | A | Burn concentrated liquidity position |
| `DexCollectFees` | 17 | A | Collect fees from concentrated position |
| `DexEncryptedSwapIntent` | 18 | B/C | Encrypted batch-auctionable swap intent |
| `DexAdminPause` | 19 | A | Emergency pause/unpause (admin only) |
| `DexSetParameter` | 20 | A | Set governance parameter override (admin only) |

Types 7–9, 11–17, 19–20 execute immediately in Phase A. Types 10 and 18 (swap intents) are collected and settled in batch in Phases B and C. Type 18 is decrypted using the threshold-reconstructed DKG group secret key before settlement.

## Emergency Pause

The DEX supports an admin-controlled emergency pause via `DexAdminPause` (type 19). When paused, all DEX operations except `DexAdminPause` and `DexSetParameter` are blocked. The pause flag is stored on-chain at prefix `0x12`.

- **Admin address**: `ChainParameters.DexAdminAddress` (mainnet/testnet: `0x...100A`)
- **Pause**: `tx.Data[0] = 1`
- **Unpause**: `tx.Data[0] = 0`
- Validation: `ChainId <= 2` requires `DexAdminAddress` to be set

## Governance Parameters

Four DEX parameters are overridable on-chain via `DexSetParameter` (type 20), with fallback to compile-time `ChainParameters` defaults:

| Param ID | Name | Default | Bounds | Description |
|----------|------|---------|--------|-------------|
| `0x01` | `SolverRewardBps` | 500 (5%) | 0–10,000 | Fraction of AMM fees paid to winning solver |
| `0x02` | `MaxIntentsPerBatch` | 500 | 1–10,000 | Max swap intents per batch auction |
| `0x03` | `TwapWindowBlocks` | 7200 (~4h) | 100–100,000 | TWAP oracle window in blocks |
| `0x04` | `MaxPoolCreationsPerBlock` | 10 | 1–1,000 | Pool creation rate limit per block |

The fallback chain is: governance override (on-chain, prefix `0x13`) → `ChainParameters` (compile-time).

## Concentrated Liquidity (Phase E2)

Uniswap v3-style tick-based liquidity positions. LPs deploy capital within specific `[tickLower, tickUpper]` price ranges for dramatically improved capital efficiency.

- **TickMath**: `GetSqrtRatioAtTick()`, `GetTickAtSqrtRatio()` using 1.0001^tick representation
- **SqrtPriceMath**: `GetAmount0Delta()`, `GetAmount1Delta()`, price movement calculations
- **ConcentratedPool**: Position minting/burning, fee collection, tick crossing during swaps
- **Tick bitmap**: Bitmap-based O(words) initialized tick lookup. Each word covers 256 ticks, stored at prefix `0x0F`. Scans up to 400 words (~102,400 ticks) using `MostSignificantBit`/`LeastSignificantBit` via `BitOperations`.
- **Fee tracking**: Global cumulative fees per unit liquidity (`FeeGrowthGlobal0X128`, `FeeGrowthGlobal1X128` in Q128 fixed-point). Per-tick `FeeGrowthOutside` flipped on tick crossing. Per-position `FeeGrowthInsideLast` snapshot for uncollected fee computation. Fees deducted from input before each swap step.
- **Swap loop**: Max 100,000 iterations. Finds next initialized tick via bitmap, computes swap step to that boundary, crosses tick and updates active liquidity, repeats until input exhausted or price limit reached.

## Encrypted Intents (Phase E3)

EC-ElGamal threshold encryption eliminates information asymmetry — the block proposer cannot read swap intents before settlement. Provides IND-CCA2 security with authenticated encryption.

- **DKG Protocol**: Feldman VSS state machine (Deal → Complaint → Justify → Finalize) generates a group public key (GPK) shared by validators. Real G1 point verification using `BlsCrypto.ScalarMultG1` and `BlsCrypto.AddG1`. ECDH-based share encryption with AES-256-GCM.
- **ThresholdCrypto**: Polynomial evaluation, Lagrange interpolation, share encryption over BLS12-381 scalar field
- **Encryption scheme**: EC-ElGamal in G1 + AES-256-GCM
  1. Encrypt: generate random scalar `r`, compute `C1 = r * G1`, shared point `S = r * GPK`, derive AES key via `BLAKE3("basalt-ecies-v1\0" || S)`, encrypt payload with AES-256-GCM
  2. Decrypt: compute `S = s * C1` (requires group secret `s`), derive same AES key, decrypt + authenticate
- **Threshold property**: decryption requires the group secret (reconstructed from `t+1` validator shares via Lagrange interpolation); the public GPK alone cannot decrypt
- **Transaction format**: `[8B epoch][48B C1][12B GCM_nonce][114B ciphertext][16B GCM_tag]` = 198 bytes
- Transaction type 18 (`DexEncryptedSwapIntent`)

## Solver Network (Phase E4)

External solvers compete to provide optimal batch settlements. The proposer selects the solution with the highest surplus for users. Winning solvers receive a reward from AMM fee revenue.

- **SolverManager**: Registration (max 32), solution window (500ms default), Ed25519 signature verification (covers `blockNumber + poolId + clearingPrice + BLAKE3(fills)`), best-solution selection. Tags `BatchResult.WinningSolver` when an external solver wins
- **SolverScoring**: Surplus = sum(amountOut - minAmountOut) for all fills; feasibility validation including constant-product invariant check (BigInteger k-comparison, 0.1% tolerance); max 10,000 fills per solution
- **Solver rewards**: `reward = ammFee * SolverRewardBps / 10000`, deducted from the correct pool reserve (direction-aware) and credited to solver
- **Fallback**: If no valid external solution, built-in `BatchAuctionSolver` is used (no reward paid)
- REST API: `GET /v1/solvers`, `POST /v1/solvers/register`, `GET /v1/dex/intents/pending`

## BST-20 Token Integration (Phase E1)

Full support for trading BST-20 tokens via `ManagedContractRuntime`. Token transfers dispatch through FNV-1a selector `Transfer(Address,UInt256)` and `TransferFrom(Address,Address,UInt256)`. LP shares are transferable via `DexTransferLp`/`DexApproveLp` with standard approve-transferFrom pattern.

## MEV Elimination

1. **Batch execution** — swap intents are not executed individually; the proposer cannot reorder for profit
2. **Maximum-volume clearing** — selects the price that maximizes total matched volume; all intents receive the same price
3. **Peer-to-peer matching first** — reduces AMM price impact and loss-versus-rebalancing
4. **Limit order depth** — adds liquidity at each price level, reducing slippage
5. **Encrypted intents** — proposer cannot see intent contents before settlement (EC-ElGamal + AES-256-GCM threshold encryption; requires group secret to decrypt)
6. **Solver competition** — external solvers compete for best execution; surplus goes to users, not the proposer. Solvers are incentivized via fee-based rewards

## Gas Costs

| Operation | Gas |
|-----------|-----|
| Create pool | 100,000 |
| Add/remove liquidity | 80,000 |
| Swap intent (batch) | 80,000 |
| Encrypted swap intent | 100,000 |
| Limit order (place) | 60,000 |
| Cancel order | 40,000 |
| LP transfer | 40,000 |
| LP approve | 30,000 |
| Mint concentrated position | 120,000 |
| Burn concentrated position | 100,000 |
| Collect concentrated fees | 60,000 |

## REST API Endpoints

| Method | Path | Description |
|--------|------|-------------|
| GET | `/v1/dex/pools` | List all liquidity pools |
| GET | `/v1/dex/pools/{poolId}` | Get pool details |
| GET | `/v1/dex/pools/{poolId}/lp/{address}` | Get LP token balance for an address |
| GET | `/v1/dex/pools/{poolId}/orders` | List orders for a pool |
| GET | `/v1/dex/orders/{orderId}` | Get order details |
| GET | `/v1/dex/pools/{poolId}/twap?window=100` | TWAP and volatility data |
| GET | `/v1/dex/pools/{poolId}/price-history` | Historical price data with configurable interval |
| GET | `/v1/solvers` | List registered solvers |
| POST | `/v1/solvers/register` | Register an external solver |
| GET | `/v1/dex/intents/pending` | Pending intent hashes (for solvers) |

## Integration

The DEX is initialized at genesis by `GenesisContractDeployer`, which creates the system account at `0x000...1009`. The `NodeCoordinator` routes swap intent transactions through `BuildBlockWithDex()` for three-phase block production. All other DEX transaction types (pool creation, liquidity, orders, admin) are executed in the standard transaction pipeline.

### Crypto Dependencies

- `BlsCrypto` (`Basalt.Crypto`): G1 scalar multiplication and point addition for EC-ElGamal encryption/decryption and Feldman VSS. Wraps blst's P1 operations via `Nethermind.Crypto.Bls`.
- `BlsSigner` (`Basalt.Crypto`): BLS12-381 signing for DKG key generation.
- `AesGcm` (`System.Security.Cryptography`): AES-256-GCM authenticated encryption for intent payloads and ECDH-based share encryption. AOT-safe, available in .NET 9.
