# Bonding Curve Token

## Category

DeFi / Token Issuance / Automated Market Making

## Summary

A continuous token model where the price of a token is algorithmically determined by its current supply along a mathematical curve (linear, polynomial, or sigmoid). Users buy tokens by depositing BST into the curve reserve and sell tokens back to the curve at the current price. This enables automated price discovery without requiring external liquidity providers or order books.

## Why It's Useful

- **Fair Token Launches**: Eliminates the need for centralized token sales or IDO platforms. Anyone can buy at the current algorithmically-determined price, removing insider advantages and front-running on initial allocations.
- **Continuous Liquidity**: The bonding curve itself acts as an always-available counterparty. There is never a situation where a token has "no liquidity" -- the curve guarantees buy and sell availability at all times.
- **Curation Markets**: Communities can issue tokens whose price reflects collective interest. Rising demand naturally increases price, rewarding early supporters. This pattern is ideal for content curation, prediction markets, and community governance tokens.
- **No Impermanent Loss**: Unlike AMM liquidity pools, bonding curves do not expose liquidity providers to impermanent loss because the reserve is managed by the contract itself, not by external LPs.
- **Transparent Pricing**: The price function is deterministic and on-chain. Users can verify the exact price they will pay before transacting, and there is no hidden spread or manipulation.
- **Bootstrap Mechanism**: New projects can bootstrap token distribution and initial treasury funding simultaneously without relying on centralized exchanges or complex multi-round fundraising.

## Key Features

- **Multiple Curve Types**: Support for linear (price = m * supply + b), polynomial (price = a * supply^n), and sigmoid (price = maxPrice / (1 + e^(-k*(supply - midpoint)))) curves. Each curve type serves different economic goals.
- **Buy and Sell Against the Curve**: Users deposit BST to mint curve tokens; users burn curve tokens to withdraw BST from the reserve. The contract enforces the price function in both directions.
- **Reserve Ratio Management**: A configurable reserve ratio determines what percentage of deposited BST is held in the curve reserve versus forwarded to a beneficiary address (e.g., project treasury).
- **Spread Fee**: An optional buy/sell spread (in basis points) that captures a fee on each trade. This fee can be directed to the curve creator, a DAO treasury, or burned.
- **Supply Cap**: Optional maximum supply parameter. Once the cap is reached, no more tokens can be minted (the curve terminates).
- **Hatch Phase**: An optional initial phase where tokens are sold at a fixed price up to a funding threshold, after which the bonding curve activates. This prevents extreme price volatility at very low supply.
- **Front-Running Protection**: A maximum slippage parameter on buy/sell operations. If the price moves beyond the user's tolerance between submission and execution, the transaction reverts.
- **Curve Migration**: Admin-controlled mechanism to migrate from one curve type to another (e.g., from hatch phase to polynomial curve) at a specific supply checkpoint.

## Basalt-Specific Advantages

- **AOT-Compiled Execution**: Bonding curve math (exponentiation, sigmoid functions) executes as native AOT-compiled code rather than interpreted bytecode. This makes complex curve calculations significantly faster and cheaper in gas than equivalent Solidity implementations on EVM chains.
- **UInt256 Precision**: Basalt's native UInt256 support for token amounts avoids the overflow and precision issues common in Solidity, where developers must carefully manage fixed-point arithmetic with SafeMath libraries. The bonding curve integral calculations benefit directly from this.
- **ZK Compliance Integration**: Curve tokens can optionally require ZK compliance proofs for purchase, enabling regulated token launches where only verified participants can buy. The SchemaRegistry and IssuerRegistry validate credential proofs without revealing buyer identity.
- **BLAKE3 Hashing for Randomness**: The sigmoid curve midpoint or other parameters can be derived from BLAKE3 hashes for deterministic pseudo-randomness when needed in parameterized launches.
- **Ed25519 Signature Efficiency**: Transaction signing with Ed25519 is faster and produces smaller signatures than ECDSA (EVM), reducing the per-trade overhead for high-frequency curve interactions.
- **BST-20 Compatibility**: Curve tokens are standard BST-20 tokens, immediately tradeable on any Basalt DEX or transferable to any Basalt wallet without additional wrapping.
- **Confidential Reserve Amounts**: Using Pedersen commitments, the reserve balance can optionally be hidden from public view while still allowing the contract to prove solvency via range proofs. This is useful for projects that want to obscure their treasury size.

## Token Standards Used

- **BST-20**: The bonding curve token itself is a standard BST-20 fungible token, enabling seamless integration with wallets, DEXes, and other DeFi contracts.
- **BST-4626 (Vault)**: The reserve pool can optionally be wrapped in a BST-4626 vault to earn yield on idle reserve BST (e.g., via StakingPool), with yield accruing to the curve reserve and effectively increasing the floor price.

## Integration Points

- **StakingPool (0x...1005)**: Reserve BST can be staked via StakingPool to generate yield. This yield either increases the reserve (raising the floor price) or is distributed as dividends to curve token holders.
- **Governance (0x...1002)**: Curve parameters (spread fee, reserve ratio, supply cap) can be governed by a DAO through the Governance contract, allowing the community to adjust the curve economics over time.
- **BNS (0x...1001)**: Each bonding curve instance can register a human-readable name via BNS (e.g., "community.curve.bst") for discoverability.
- **SchemaRegistry (0x...1006)**: When ZK compliance is enabled, the curve references credential schemas from the SchemaRegistry to validate buyer eligibility.
- **IssuerRegistry (0x...1007)**: Validates that compliance proofs were issued by trusted credential issuers registered in the IssuerRegistry.
- **Escrow (0x...1003)**: The hatch phase can use the Escrow contract to hold initial contributions until the funding threshold is met. If the threshold is not met, contributors can reclaim their BST.

## Technical Sketch

```csharp
// Contract type ID: 0x0108
[BasaltContract(0x0108)]
public partial class BondingCurve : SdkContract, IDispatchable
{
    // --- Enums ---

    public enum CurveType : byte
    {
        Linear = 0,      // price = slope * supply + intercept
        Polynomial = 1,  // price = coefficient * supply^exponent
        Sigmoid = 2      // price = maxPrice / (1 + e^(-steepness * (supply - midpoint)))
    }

    public enum Phase : byte
    {
        Hatch = 0,
        Active = 1,
        Terminated = 2
    }

    // --- Storage ---

    private StorageValue<Address> _admin;
    private StorageValue<Address> _beneficiary;
    private StorageValue<byte> _curveType;
    private StorageValue<byte> _phase;
    private StorageValue<UInt256> _totalSupply;
    private StorageValue<UInt256> _reserveBalance;
    private StorageValue<UInt256> _supplyCap;
    private StorageValue<UInt256> _hatchThreshold;
    private StorageValue<UInt256> _hatchPrice;
    private StorageValue<ulong> _reserveRatioBps;    // basis points (0-10000)
    private StorageValue<ulong> _spreadFeeBps;        // basis points
    private StorageValue<UInt256> _curveParam1;       // slope / coefficient / maxPrice
    private StorageValue<UInt256> _curveParam2;       // intercept / exponent / steepness
    private StorageValue<UInt256> _curveParam3;       // unused / unused / midpoint
    private StorageValue<bool> _complianceRequired;
    private StorageMap<Address, UInt256> _balances;
    private StorageMap<Address, UInt256> _hatchContributions;

    // --- Constructor ---

    public void Initialize(
        Address admin,
        Address beneficiary,
        byte curveType,
        UInt256 supplyCap,
        ulong reserveRatioBps,
        ulong spreadFeeBps,
        UInt256 curveParam1,
        UInt256 curveParam2,
        UInt256 curveParam3,
        UInt256 hatchThreshold,
        UInt256 hatchPrice,
        bool complianceRequired)
    {
        Require(reserveRatioBps <= 10000, "Invalid reserve ratio");
        Require(spreadFeeBps <= 1000, "Spread fee too high");

        _admin.Set(admin);
        _beneficiary.Set(beneficiary);
        _curveType.Set(curveType);
        _phase.Set((byte)Phase.Hatch);
        _supplyCap.Set(supplyCap);
        _reserveRatioBps.Set(reserveRatioBps);
        _spreadFeeBps.Set(spreadFeeBps);
        _curveParam1.Set(curveParam1);
        _curveParam2.Set(curveParam2);
        _curveParam3.Set(curveParam3);
        _hatchThreshold.Set(hatchThreshold);
        _hatchPrice.Set(hatchPrice);
        _complianceRequired.Set(complianceRequired);
    }

    // --- Buy / Sell ---

    public UInt256 Buy(UInt256 minTokensOut)
    {
        UInt256 deposit = Context.TxValue;
        Require(!deposit.IsZero, "Must send BST");

        if (_complianceRequired.Get())
            RequireCompliance(Context.Caller);

        UInt256 fee = deposit * _spreadFeeBps.Get() / 10000;
        UInt256 netDeposit = deposit - fee;

        Phase phase = (Phase)_phase.Get();
        UInt256 tokensToMint;

        if (phase == Phase.Hatch)
        {
            tokensToMint = netDeposit / _hatchPrice.Get();
            _hatchContributions.Set(Context.Caller,
                _hatchContributions.Get(Context.Caller) + netDeposit);

            if (_reserveBalance.Get() + netDeposit >= _hatchThreshold.Get())
                _phase.Set((byte)Phase.Active);
        }
        else if (phase == Phase.Active)
        {
            tokensToMint = CalculateBuyReturn(netDeposit);
        }
        else
        {
            Revert("Curve terminated");
            return UInt256.Zero;
        }

        Require(tokensToMint >= minTokensOut, "Slippage exceeded");
        EnforceSupplyCap(tokensToMint);

        _totalSupply.Set(_totalSupply.Get() + tokensToMint);
        _balances.Set(Context.Caller,
            _balances.Get(Context.Caller) + tokensToMint);

        UInt256 toReserve = netDeposit * _reserveRatioBps.Get() / 10000;
        UInt256 toBeneficiary = netDeposit - toReserve;
        _reserveBalance.Set(_reserveBalance.Get() + toReserve);

        if (!toBeneficiary.IsZero)
            Context.TransferNative(_beneficiary.Get(), toBeneficiary);

        if (!fee.IsZero)
            Context.TransferNative(_beneficiary.Get(), fee);

        return tokensToMint;
    }

    public UInt256 Sell(UInt256 tokenAmount, UInt256 minBstOut)
    {
        Require(!tokenAmount.IsZero, "Amount must be > 0");
        Require(_balances.Get(Context.Caller) >= tokenAmount, "Insufficient balance");
        Require((Phase)_phase.Get() != Phase.Hatch, "Cannot sell during hatch");

        UInt256 bstReturn = CalculateSellReturn(tokenAmount);
        UInt256 fee = bstReturn * _spreadFeeBps.Get() / 10000;
        UInt256 netReturn = bstReturn - fee;

        Require(netReturn >= minBstOut, "Slippage exceeded");
        Require(_reserveBalance.Get() >= bstReturn, "Insufficient reserve");

        _totalSupply.Set(_totalSupply.Get() - tokenAmount);
        _balances.Set(Context.Caller,
            _balances.Get(Context.Caller) - tokenAmount);
        _reserveBalance.Set(_reserveBalance.Get() - bstReturn);

        Context.TransferNative(Context.Caller, netReturn);

        return netReturn;
    }

    // --- Price Calculation ---

    public UInt256 GetCurrentPrice()
    {
        UInt256 supply = _totalSupply.Get();
        CurveType curve = (CurveType)_curveType.Get();
        return ComputeSpotPrice(supply, curve);
    }

    public UInt256 EstimateBuy(UInt256 depositAmount)
    {
        UInt256 fee = depositAmount * _spreadFeeBps.Get() / 10000;
        return CalculateBuyReturn(depositAmount - fee);
    }

    public UInt256 EstimateSell(UInt256 tokenAmount)
    {
        UInt256 gross = CalculateSellReturn(tokenAmount);
        UInt256 fee = gross * _spreadFeeBps.Get() / 10000;
        return gross - fee;
    }

    // --- Internal Curve Math ---

    private UInt256 ComputeSpotPrice(UInt256 supply, CurveType curve)
    {
        UInt256 p1 = _curveParam1.Get();
        UInt256 p2 = _curveParam2.Get();

        if (curve == CurveType.Linear)
            return p1 * supply / Precision + p2;

        if (curve == CurveType.Polynomial)
            return p1 * Pow(supply, p2) / Precision;

        // Sigmoid approximation via piecewise linear segments
        return ComputeSigmoidApprox(supply, p1, p2, _curveParam3.Get());
    }

    private UInt256 CalculateBuyReturn(UInt256 deposit)
    {
        // Numerical integration: tokens = integral from S to S' of (1/price) dReserve
        // Implemented as iterative summation for AOT safety
        // ...
        return UInt256.Zero; // placeholder
    }

    private UInt256 CalculateSellReturn(UInt256 tokenAmount)
    {
        // Reverse integral: BST = integral from S-amount to S of price dSupply
        // ...
        return UInt256.Zero; // placeholder
    }

    // --- Query ---

    public UInt256 BalanceOf(Address account) => _balances.Get(account);
    public UInt256 TotalSupply() => _totalSupply.Get();
    public UInt256 ReserveBalance() => _reserveBalance.Get();
    public byte CurrentPhase() => _phase.Get();
    public UInt256 SupplyCap() => _supplyCap.Get();

    // --- Admin ---

    public void UpdateBeneficiary(Address newBeneficiary)
    {
        Require(Context.Caller == _admin.Get(), "Not admin");
        _beneficiary.Set(newBeneficiary);
    }

    public void TerminateCurve()
    {
        Require(Context.Caller == _admin.Get(), "Not admin");
        _phase.Set((byte)Phase.Terminated);
    }

    public void UpdateSpreadFee(ulong newFeeBps)
    {
        Require(Context.Caller == _admin.Get(), "Not admin");
        Require(newFeeBps <= 1000, "Fee too high");
        _spreadFeeBps.Set(newFeeBps);
    }

    // --- Compliance ---

    private void RequireCompliance(Address account)
    {
        // Cross-contract call to SchemaRegistry + IssuerRegistry
        // to validate ZK compliance proof attached to the transaction
    }

    // --- Helpers ---

    private void EnforceSupplyCap(UInt256 mintAmount)
    {
        UInt256 cap = _supplyCap.Get();
        if (!cap.IsZero)
            Require(_totalSupply.Get() + mintAmount <= cap, "Supply cap reached");
    }

    private static readonly UInt256 Precision = new UInt256(1_000_000_000_000_000_000UL);

    private static UInt256 Pow(UInt256 baseVal, UInt256 exp)
    {
        // Integer exponentiation via repeated squaring
        UInt256 result = UInt256.One;
        UInt256 b = baseVal;
        while (!exp.IsZero)
        {
            if ((exp & UInt256.One) == UInt256.One)
                result = result * b / Precision;
            b = b * b / Precision;
            exp >>= 1;
        }
        return result;
    }

    private static UInt256 ComputeSigmoidApprox(
        UInt256 supply, UInt256 maxPrice, UInt256 steepness, UInt256 midpoint)
    {
        // Piecewise linear approximation of sigmoid for AOT safety
        // Divides the curve into 16 segments for accuracy
        // ...
        return UInt256.Zero; // placeholder
    }
}
```

## Complexity

**Medium** -- The core buy/sell logic is straightforward, but the curve math (especially sigmoid approximation and numerical integration for buy/sell return calculations) requires careful fixed-point arithmetic. AOT constraints rule out arbitrary-precision floating-point libraries, so all math must be implemented with integer-only UInt256 operations. Testing edge cases around supply cap boundaries, hatch-to-active transitions, and precision loss at extreme supply values adds additional complexity.

## Priority

**P1** -- Bonding curves are a foundational DeFi primitive used by many higher-level protocols (curation markets, continuous organizations, fair launch platforms). Having a native bonding curve contract enables a wide range of ecosystem applications and is a strong signal of DeFi maturity. However, it is not strictly required before the DEX (AMM) and lending contracts, which serve more immediate trading needs.
