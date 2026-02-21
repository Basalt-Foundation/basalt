# Dollar Cost Averaging (DCA) Contract

## Category

DeFi / Trading Automation / Portfolio Management

## Summary

A smart contract that enables users to set up automated recurring buy orders, depositing a source token (e.g., a stablecoin or BST) and specifying a target token, interval, and amount per execution. At each interval, a keeper triggers the contract to execute a swap via an on-chain AMM. This removes the need for manual trading and reduces the impact of price volatility through systematic time-weighted purchasing.

## Why It's Useful

- **Reduces Timing Risk**: Dollar cost averaging is one of the most well-established investment strategies. By spreading purchases over time, users avoid the risk of buying at a local peak. The contract automates this strategy entirely on-chain.
- **Eliminates Emotional Trading**: Automated execution removes the psychological burden of timing the market. Users set their strategy once and the contract executes dispassionately at each interval, regardless of market sentiment.
- **Accessible to All Skill Levels**: DCA is simple to understand even for users with no trading experience. "Deposit X, buy Y every Z blocks" is one of the most approachable DeFi interactions.
- **Keeper Economy**: The keeper incentive mechanism creates an on-chain economy where bot operators earn fees for triggering executions. This is a sustainable model that does not require centralized infrastructure.
- **Gas Efficiency**: By batching multiple users' DCA orders that execute at the same interval, the contract can achieve gas savings compared to each user executing individual swaps.
- **Portfolio Automation**: Users can set up multiple DCA positions targeting different tokens, creating a diversified automated portfolio strategy without any ongoing management.
- **Stablecoin Utility**: DCA contracts are one of the primary use cases for stablecoins in DeFi. They drive stablecoin adoption and trading volume on the chain.

## Key Features

- **Configurable DCA Orders**: Users specify source token, target token, amount per execution, interval (in blocks), total number of executions (or unlimited), and optional price limits.
- **AMM Integration**: Swaps are executed via the chain's AMM (DEX contract). The contract calls the AMM's swap function with appropriate slippage protection.
- **Keeper Incentive**: Anyone can call the `Execute()` function for eligible orders. The keeper receives a configurable bounty (in basis points of the swap amount) for triggering execution. This decentralizes the execution layer.
- **Slippage Protection**: Each order has a maximum slippage tolerance (in basis points). If the AMM price deviates beyond this tolerance from the expected price, the execution is skipped (not reverted) and retried at the next interval.
- **Price Limits**: Optional minimum and maximum price bounds. The order only executes when the target token's price falls within the specified range, allowing users to combine DCA with limit-order logic.
- **Partial Execution**: If the remaining source balance is less than the configured amount per execution, the contract executes a partial swap using the remaining balance.
- **Order Cancellation**: Users can cancel their DCA orders at any time and withdraw any remaining source tokens.
- **Order Modification**: Users can modify the interval, amount per execution, or price limits of an active order without withdrawing and re-depositing.
- **Execution History**: Each execution is logged with the block number, amount swapped, tokens received, price achieved, and keeper address. Users can query their full execution history.
- **Batch Execution**: Keepers can trigger multiple eligible orders in a single transaction, improving gas efficiency and incentivizing batch operation.

## Basalt-Specific Advantages

- **AOT-Compiled Batch Processing**: When a keeper triggers batch execution for multiple orders, the iteration and swap logic runs as native AOT-compiled code. On EVM chains, batch processing is severely limited by gas costs for loop iteration; Basalt's compilation model allows processing dozens of orders in a single transaction efficiently.
- **BLAKE3 Order Hashing**: Each DCA order is identified by a BLAKE3 hash of its parameters, providing a compact, collision-resistant identifier that is cheaper to compute and store than Keccak-256 used on Ethereum.
- **Ed25519 Signature Efficiency**: The keeper's execution transactions use Ed25519 signatures, which verify faster than ECDSA. For keepers processing hundreds of executions per epoch, this signature verification savings is material.
- **Pedersen Commitment Privacy**: DCA order parameters (amount per execution, total budget) can optionally be hidden using Pedersen commitments. The contract verifies the commitment and range proof without revealing the user's investment strategy to other market participants. This prevents front-running and copy-trading of whale DCA strategies.
- **ZK Compliance Integration**: For regulated tokens (e.g., security tokens), the DCA contract can require ZK compliance proofs from order creators, ensuring only verified participants set up automated purchases of restricted assets.
- **BST-3525 Position Tokens**: DCA positions are represented as BST-3525 semi-fungible tokens. The slot represents the token pair, and the value represents the remaining source balance. These position tokens can be transferred, allowing users to sell their DCA strategy to another party.
- **UInt256 Precision**: All amount calculations use native UInt256, avoiding the precision loss and overflow risks that plague Solidity DCA implementations using uint256 with manual safe-math wrappers.

## Token Standards Used

- **BST-20**: Source tokens (stablecoins, BST) and target tokens are BST-20 fungible tokens. The keeper bounty is paid in the source token (BST-20).
- **BST-3525 (SFT)**: DCA positions are BST-3525 semi-fungible tokens, enabling position transfer, fractionalization, and composability with other DeFi protocols.

## Integration Points

- **AMM / DEX Contract**: The primary integration point. The DCA contract calls the AMM's swap function to execute trades. It reads the AMM's price oracle for slippage calculations and price limit enforcement.
- **Governance (0x...1002)**: DCA parameters (maximum keeper bounty, minimum interval, supported token pairs) are governed via Governance proposals.
- **BNS (0x...1001)**: The DCA contract registers a BNS name (e.g., "dca.bst"). Users can reference token pairs by BNS names instead of raw addresses.
- **SchemaRegistry (0x...1006)**: When compliance-gated tokens are involved, credential schemas are referenced from the SchemaRegistry.
- **IssuerRegistry (0x...1007)**: Validates credential issuers for compliance-gated DCA orders.
- **BridgeETH (0x...1008)**: Source tokens bridged from Ethereum (e.g., USDC via the bridge) can be used directly in DCA orders targeting Basalt-native tokens.

## Technical Sketch

```csharp
// Contract type ID: 0x010B
[BasaltContract(0x010B)]
public partial class DcaBot : SdkContract, IDispatchable
{
    // --- Structs (flattened in storage) ---

    // Order data is stored across multiple maps keyed by orderId

    // --- Storage ---

    private StorageValue<Address> _admin;
    private StorageValue<Address> _ammAddress;
    private StorageValue<ulong> _nextOrderId;
    private StorageValue<ulong> _keeperBountyBps;         // basis points of swap amount
    private StorageValue<ulong> _minIntervalBlocks;
    private StorageValue<bool> _paused;

    // Order fields (keyed by orderId)
    private StorageMap<ulong, Address> _orderOwner;
    private StorageMap<ulong, Address> _orderSourceToken;
    private StorageMap<ulong, Address> _orderTargetToken;
    private StorageMap<ulong, UInt256> _orderAmountPerExec;
    private StorageMap<ulong, UInt256> _orderSourceBalance;    // remaining source tokens
    private StorageMap<ulong, UInt256> _orderTargetAccumulated; // total target tokens received
    private StorageMap<ulong, ulong> _orderInterval;           // blocks between executions
    private StorageMap<ulong, ulong> _orderLastExecBlock;
    private StorageMap<ulong, ulong> _orderMaxExecutions;      // 0 = unlimited
    private StorageMap<ulong, ulong> _orderExecutionCount;
    private StorageMap<ulong, ulong> _orderSlippageBps;
    private StorageMap<ulong, UInt256> _orderMinPrice;         // 0 = no minimum
    private StorageMap<ulong, UInt256> _orderMaxPrice;         // 0 = no maximum
    private StorageMap<ulong, bool> _orderActive;

    // Per-owner order tracking
    private StorageMap<Address, ulong> _ownerOrderCount;

    // --- Constructor ---

    public void Initialize(
        Address admin,
        Address ammAddress,
        ulong keeperBountyBps,
        ulong minIntervalBlocks)
    {
        _admin.Set(admin);
        _ammAddress.Set(ammAddress);
        _keeperBountyBps.Set(keeperBountyBps);
        _minIntervalBlocks.Set(minIntervalBlocks);
        _nextOrderId.Set(1);
    }

    // --- Create Order ---

    public ulong CreateOrder(
        Address sourceToken,
        Address targetToken,
        UInt256 amountPerExec,
        ulong intervalBlocks,
        ulong maxExecutions,
        ulong slippageBps,
        UInt256 minPrice,
        UInt256 maxPrice)
    {
        Require(!_paused.Get(), "DCA paused");
        UInt256 deposit = Context.TxValue;
        Require(!deposit.IsZero, "Must deposit source tokens");
        Require(!amountPerExec.IsZero, "Amount per execution must be > 0");
        Require(intervalBlocks >= _minIntervalBlocks.Get(), "Interval too short");
        Require(slippageBps <= 5000, "Slippage too high"); // max 50%
        Require(sourceToken != targetToken, "Same token pair");

        ulong orderId = _nextOrderId.Get();
        _nextOrderId.Set(orderId + 1);

        _orderOwner.Set(orderId, Context.Caller);
        _orderSourceToken.Set(orderId, sourceToken);
        _orderTargetToken.Set(orderId, targetToken);
        _orderAmountPerExec.Set(orderId, amountPerExec);
        _orderSourceBalance.Set(orderId, deposit);
        _orderInterval.Set(orderId, intervalBlocks);
        _orderLastExecBlock.Set(orderId, Context.BlockNumber);
        _orderMaxExecutions.Set(orderId, maxExecutions);
        _orderSlippageBps.Set(orderId, slippageBps);
        _orderMinPrice.Set(orderId, minPrice);
        _orderMaxPrice.Set(orderId, maxPrice);
        _orderActive.Set(orderId, true);

        ulong ownerCount = _ownerOrderCount.Get(Context.Caller);
        _ownerOrderCount.Set(Context.Caller, ownerCount + 1);

        return orderId;
    }

    // --- Execute (called by keepers) ---

    public bool Execute(ulong orderId)
    {
        Require(_orderActive.Get(orderId), "Order not active");
        Require(!_paused.Get(), "DCA paused");

        ulong currentBlock = Context.BlockNumber;
        ulong lastExec = _orderLastExecBlock.Get(orderId);
        ulong interval = _orderInterval.Get(orderId);
        Require(currentBlock >= lastExec + interval, "Not yet eligible");

        // Check max executions
        ulong maxExec = _orderMaxExecutions.Get(orderId);
        ulong execCount = _orderExecutionCount.Get(orderId);
        if (maxExec > 0)
            Require(execCount < maxExec, "Max executions reached");

        UInt256 sourceBalance = _orderSourceBalance.Get(orderId);
        Require(!sourceBalance.IsZero, "No source balance");

        UInt256 amountPerExec = _orderAmountPerExec.Get(orderId);
        UInt256 swapAmount = sourceBalance < amountPerExec ? sourceBalance : amountPerExec;

        // Calculate keeper bounty
        ulong bountyBps = _keeperBountyBps.Get();
        UInt256 keeperBounty = swapAmount * bountyBps / 10000;
        UInt256 netSwapAmount = swapAmount - keeperBounty;

        // Check price limits
        UInt256 currentPrice = GetAmmPrice(
            _orderSourceToken.Get(orderId),
            _orderTargetToken.Get(orderId));

        UInt256 minPrice = _orderMinPrice.Get(orderId);
        UInt256 maxPrice = _orderMaxPrice.Get(orderId);

        if (!minPrice.IsZero && currentPrice < minPrice)
            return false; // Price below minimum, skip execution

        if (!maxPrice.IsZero && currentPrice > maxPrice)
            return false; // Price above maximum, skip execution

        // Execute swap via AMM
        UInt256 minOutput = CalculateMinOutput(
            netSwapAmount, currentPrice, _orderSlippageBps.Get(orderId));

        UInt256 tokensReceived = ExecuteSwap(
            _orderSourceToken.Get(orderId),
            _orderTargetToken.Get(orderId),
            netSwapAmount,
            minOutput);

        // Update order state
        _orderSourceBalance.Set(orderId, sourceBalance - swapAmount);
        _orderTargetAccumulated.Set(orderId,
            _orderTargetAccumulated.Get(orderId) + tokensReceived);
        _orderExecutionCount.Set(orderId, execCount + 1);
        _orderLastExecBlock.Set(orderId, currentBlock);

        // Transfer received tokens to order owner
        TransferTokens(
            _orderTargetToken.Get(orderId),
            _orderOwner.Get(orderId),
            tokensReceived);

        // Pay keeper bounty
        if (!keeperBounty.IsZero)
            Context.TransferNative(Context.Caller, keeperBounty);

        // Deactivate if source balance depleted or max executions reached
        if (_orderSourceBalance.Get(orderId).IsZero ||
            (maxExec > 0 && execCount + 1 >= maxExec))
        {
            _orderActive.Set(orderId, false);
        }

        return true;
    }

    // --- Batch Execute (for keepers) ---

    public ulong BatchExecute(ulong orderIdStart, ulong orderIdEnd)
    {
        ulong executed = 0;
        for (ulong id = orderIdStart; id <= orderIdEnd; id++)
        {
            if (!_orderActive.Get(id)) continue;

            ulong currentBlock = Context.BlockNumber;
            ulong lastExec = _orderLastExecBlock.Get(id);
            ulong interval = _orderInterval.Get(id);
            if (currentBlock < lastExec + interval) continue;

            if (Execute(id))
                executed++;
        }
        return executed;
    }

    // --- Order Management ---

    public void CancelOrder(ulong orderId)
    {
        Require(_orderOwner.Get(orderId) == Context.Caller, "Not order owner");
        Require(_orderActive.Get(orderId), "Order not active");

        _orderActive.Set(orderId, false);

        // Return remaining source balance
        UInt256 remaining = _orderSourceBalance.Get(orderId);
        if (!remaining.IsZero)
        {
            _orderSourceBalance.Set(orderId, UInt256.Zero);
            Context.TransferNative(Context.Caller, remaining);
        }
    }

    public void TopUp(ulong orderId)
    {
        Require(_orderOwner.Get(orderId) == Context.Caller, "Not order owner");
        UInt256 amount = Context.TxValue;
        Require(!amount.IsZero, "Must deposit tokens");

        _orderSourceBalance.Set(orderId,
            _orderSourceBalance.Get(orderId) + amount);

        // Reactivate if was deactivated due to empty balance
        if (!_orderActive.Get(orderId))
        {
            ulong maxExec = _orderMaxExecutions.Get(orderId);
            ulong execCount = _orderExecutionCount.Get(orderId);
            if (maxExec == 0 || execCount < maxExec)
                _orderActive.Set(orderId, true);
        }
    }

    public void ModifyOrder(
        ulong orderId,
        UInt256 newAmountPerExec,
        ulong newInterval,
        ulong newSlippageBps,
        UInt256 newMinPrice,
        UInt256 newMaxPrice)
    {
        Require(_orderOwner.Get(orderId) == Context.Caller, "Not order owner");
        Require(_orderActive.Get(orderId), "Order not active");
        Require(newInterval >= _minIntervalBlocks.Get(), "Interval too short");
        Require(newSlippageBps <= 5000, "Slippage too high");

        _orderAmountPerExec.Set(orderId, newAmountPerExec);
        _orderInterval.Set(orderId, newInterval);
        _orderSlippageBps.Set(orderId, newSlippageBps);
        _orderMinPrice.Set(orderId, newMinPrice);
        _orderMaxPrice.Set(orderId, newMaxPrice);
    }

    // --- Query ---

    public UInt256 GetSourceBalance(ulong orderId) => _orderSourceBalance.Get(orderId);
    public UInt256 GetTargetAccumulated(ulong orderId) => _orderTargetAccumulated.Get(orderId);
    public ulong GetExecutionCount(ulong orderId) => _orderExecutionCount.Get(orderId);
    public bool IsOrderActive(ulong orderId) => _orderActive.Get(orderId);
    public Address GetOrderOwner(ulong orderId) => _orderOwner.Get(orderId);
    public ulong GetOwnerOrderCount(Address owner) => _ownerOrderCount.Get(owner);

    public bool IsEligibleForExecution(ulong orderId)
    {
        if (!_orderActive.Get(orderId)) return false;
        ulong currentBlock = Context.BlockNumber;
        ulong lastExec = _orderLastExecBlock.Get(orderId);
        ulong interval = _orderInterval.Get(orderId);
        return currentBlock >= lastExec + interval;
    }

    public UInt256 GetAveragePrice(ulong orderId)
    {
        UInt256 totalSource = Context.TxValue; // original deposit - remaining
        UInt256 totalTarget = _orderTargetAccumulated.Get(orderId);
        if (totalTarget.IsZero) return UInt256.Zero;
        // Average price = totalSource / totalTarget (scaled by Precision)
        return totalSource * Precision / totalTarget;
    }

    // --- Admin ---

    public void SetKeeperBounty(ulong newBountyBps)
    {
        Require(Context.Caller == _admin.Get(), "Not admin");
        Require(newBountyBps <= 500, "Bounty too high"); // max 5%
        _keeperBountyBps.Set(newBountyBps);
    }

    public void SetAmmAddress(Address newAmm)
    {
        Require(Context.Caller == _admin.Get(), "Not admin");
        _ammAddress.Set(newAmm);
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

    // --- Internal ---

    private UInt256 GetAmmPrice(Address sourceToken, Address targetToken)
    {
        // Cross-contract call to AMM.GetSpotPrice(sourceToken, targetToken)
        return UInt256.Zero; // placeholder
    }

    private UInt256 ExecuteSwap(
        Address sourceToken, Address targetToken,
        UInt256 amountIn, UInt256 minAmountOut)
    {
        // Cross-contract call to AMM.Swap(sourceToken, targetToken, amountIn, minAmountOut)
        return UInt256.Zero; // placeholder
    }

    private void TransferTokens(Address token, Address to, UInt256 amount)
    {
        // Cross-contract call to BST-20 token.Transfer(to, amount)
    }

    private UInt256 CalculateMinOutput(
        UInt256 inputAmount, UInt256 price, ulong slippageBps)
    {
        UInt256 expectedOutput = inputAmount * Precision / price;
        UInt256 slippageAmount = expectedOutput * slippageBps / 10000;
        return expectedOutput - slippageAmount;
    }

    private static readonly UInt256 Precision = new UInt256(1_000_000_000_000_000_000UL);
}
```

## Complexity

**Medium** -- The core DCA logic (deposit, execute swap at interval, track state) is relatively straightforward. The complexity comes from the keeper incentive economics (ensuring bounties are attractive enough to incentivize execution but not so large as to erode user returns), slippage protection (handling cases where the AMM price has moved significantly), and batch execution optimization. Price limit enforcement and partial execution edge cases add additional testing surface. The AMM integration requires careful handling of approval flows and return value parsing.

## Priority

**P2** -- DCA is a widely desired feature in DeFi but depends on having a functioning AMM (DEX contract) to execute swaps against. It should be built after the DEX is operational and has sufficient liquidity in key trading pairs. DCA contracts are a strong driver of recurring trading volume and stablecoin utility, making them valuable for ecosystem growth once the foundational trading infrastructure is in place.
