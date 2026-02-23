using Basalt.Core;

namespace Basalt.Execution.Dex.Math;

/// <summary>
/// Pure AMM math functions for constant-product pools.
/// All functions are stateless and use <see cref="FullMath"/> for overflow-safe intermediates.
/// Ported from Caldera.Core.Math.CalderaLibrary with namespace and naming changes
/// for protocol-native integration.
/// </summary>
public static class DexLibrary
{
    /// <summary>
    /// Permanently locked on first liquidity deposit to prevent LP share manipulation.
    /// This ensures the total supply can never be zero after the first deposit,
    /// preventing division-by-zero in subsequent LP share calculations.
    /// </summary>
    public static readonly UInt256 MinimumLiquidity = new(1000);

    /// <summary>
    /// Basis points denominator: 100% = 10,000 bps.
    /// </summary>
    public static readonly UInt256 BpsDenominator = new(10_000);

    /// <summary>
    /// Allowed fee tiers in basis points.
    /// <list type="bullet">
    /// <item><description>1 bps (0.01%) — stablecoin pairs</description></item>
    /// <item><description>5 bps (0.05%) — correlated assets</description></item>
    /// <item><description>30 bps (0.30%) — standard pairs</description></item>
    /// <item><description>100 bps (1.00%) — exotic/volatile pairs</description></item>
    /// </list>
    /// </summary>
    public static readonly uint[] AllowedFeeTiers = [1, 5, 30, 100];

    /// <summary>
    /// Default swap fee: 0.3% (30 basis points).
    /// </summary>
    public const uint DefaultFeeBps = 30;

    /// <summary>
    /// Given an input amount and pair reserves, returns the maximum output amount
    /// after deducting the swap fee.
    /// <para>
    /// Uses the constant-product formula:
    /// <c>amountOut = (amountIn * (10000 - feeBps) * reserveOut) / (reserveIn * 10000 + amountIn * (10000 - feeBps))</c>
    /// </para>
    /// <para>
    /// This preserves the invariant <c>k = reserveIn * reserveOut</c> after accounting for fees,
    /// meaning the post-swap product of reserves is always >= the pre-swap product.
    /// </para>
    /// </summary>
    /// <param name="amountIn">The input token amount. Must be non-zero.</param>
    /// <param name="reserveIn">The reserve of the input token. Must be non-zero.</param>
    /// <param name="reserveOut">The reserve of the output token. Must be non-zero.</param>
    /// <param name="feeBps">The swap fee in basis points (e.g. 30 = 0.3%).</param>
    /// <returns>The output token amount after fees.</returns>
    public static UInt256 GetAmountOut(
        UInt256 amountIn, UInt256 reserveIn, UInt256 reserveOut, uint feeBps)
    {
        if (amountIn.IsZero)
            throw new ArgumentException("DexLibrary: INSUFFICIENT_INPUT_AMOUNT");
        if (reserveIn.IsZero || reserveOut.IsZero)
            throw new ArgumentException("DexLibrary: INSUFFICIENT_LIQUIDITY");

        var feeComplement = new UInt256(10_000 - feeBps);
        var feeDenom = new UInt256(10_000);

        var amountInWithFee = UInt256.CheckedMul(amountIn, feeComplement);
        var denominator = UInt256.CheckedAdd(UInt256.CheckedMul(reserveIn, feeDenom), amountInWithFee);

        return FullMath.MulDiv(amountInWithFee, reserveOut, denominator);
    }

    /// <summary>
    /// Given a desired output amount and pair reserves, returns the required input amount
    /// including the swap fee.
    /// <para>
    /// Inverse of <see cref="GetAmountOut"/>:
    /// <c>amountIn = (reserveIn * amountOut * 10000) / ((reserveOut - amountOut) * (10000 - feeBps)) + 1</c>
    /// </para>
    /// <para>
    /// The <c>+ 1</c> ensures rounding up so the swap always receives at least <paramref name="amountOut"/>.
    /// </para>
    /// </summary>
    /// <param name="amountOut">The desired output amount. Must be non-zero and less than <paramref name="reserveOut"/>.</param>
    /// <param name="reserveIn">The reserve of the input token. Must be non-zero.</param>
    /// <param name="reserveOut">The reserve of the output token. Must be non-zero.</param>
    /// <param name="feeBps">The swap fee in basis points.</param>
    /// <returns>The required input token amount including fees.</returns>
    public static UInt256 GetAmountIn(
        UInt256 amountOut, UInt256 reserveIn, UInt256 reserveOut, uint feeBps)
    {
        if (amountOut.IsZero)
            throw new ArgumentException("DexLibrary: INSUFFICIENT_OUTPUT_AMOUNT");
        if (reserveIn.IsZero || reserveOut.IsZero)
            throw new ArgumentException("DexLibrary: INSUFFICIENT_LIQUIDITY");
        if (amountOut >= reserveOut)
            throw new ArgumentException("DexLibrary: INSUFFICIENT_LIQUIDITY");

        var feeComplement = new UInt256(10_000 - feeBps);
        var feeDenom = new UInt256(10_000);

        var numerator = UInt256.CheckedMul(reserveIn, UInt256.CheckedMul(amountOut, feeDenom));
        var denominator = UInt256.CheckedMul(reserveOut - amountOut, feeComplement);

        return FullMath.MulDiv(numerator, UInt256.One, denominator) + UInt256.One;
    }

    /// <summary>
    /// Given some amount of one token, returns the equivalent amount of the other token
    /// at the current reserve ratio (no fee applied).
    /// Used for computing optimal liquidity deposit amounts.
    /// </summary>
    /// <param name="amountA">The known amount of token A.</param>
    /// <param name="reserveA">The reserve of token A. Must be non-zero.</param>
    /// <param name="reserveB">The reserve of token B. Must be non-zero.</param>
    /// <returns>The equivalent amount of token B at the current ratio.</returns>
    public static UInt256 Quote(UInt256 amountA, UInt256 reserveA, UInt256 reserveB)
    {
        if (amountA.IsZero)
            throw new ArgumentException("DexLibrary: INSUFFICIENT_AMOUNT");
        if (reserveA.IsZero || reserveB.IsZero)
            throw new ArgumentException("DexLibrary: INSUFFICIENT_LIQUIDITY");

        return FullMath.MulDiv(amountA, reserveB, reserveA);
    }

    /// <summary>
    /// Computes initial LP shares for the first liquidity deposit.
    /// <para>
    /// <c>shares = sqrt(amount0 * amount1) - MINIMUM_LIQUIDITY</c>
    /// </para>
    /// <para>
    /// The geometric mean ensures that LP share value is independent of the ratio between
    /// token0 and token1 amounts. MINIMUM_LIQUIDITY (1000) is permanently locked to prevent
    /// the total supply from ever reaching zero.
    /// </para>
    /// </summary>
    /// <param name="amount0">The amount of token0 deposited.</param>
    /// <param name="amount1">The amount of token1 deposited.</param>
    /// <returns>The LP shares to mint (excluding the locked minimum).</returns>
    public static UInt256 ComputeInitialLiquidity(UInt256 amount0, UInt256 amount1)
    {
        var product = FullMath.MulDiv(amount0, amount1, UInt256.One);
        var shares = FullMath.Sqrt(product);

        if (shares <= MinimumLiquidity)
            throw new InvalidOperationException("DexLibrary: INSUFFICIENT_INITIAL_LIQUIDITY");

        return shares - MinimumLiquidity;
    }

    /// <summary>
    /// Computes LP shares for subsequent liquidity deposits.
    /// <para>
    /// <c>shares = min(amount0 * totalSupply / reserve0, amount1 * totalSupply / reserve1)</c>
    /// </para>
    /// <para>
    /// Taking the minimum ensures that the provider cannot dilute existing LPs by providing
    /// a lopsided deposit. The provider receives shares proportional to the less-valuable
    /// side of their deposit.
    /// </para>
    /// </summary>
    /// <param name="amount0">The amount of token0 deposited.</param>
    /// <param name="amount1">The amount of token1 deposited.</param>
    /// <param name="reserve0">The current reserve of token0.</param>
    /// <param name="reserve1">The current reserve of token1.</param>
    /// <param name="totalSupply">The current total supply of LP shares.</param>
    /// <returns>The LP shares to mint.</returns>
    public static UInt256 ComputeLiquidity(
        UInt256 amount0, UInt256 amount1,
        UInt256 reserve0, UInt256 reserve1,
        UInt256 totalSupply)
    {
        var shares0 = FullMath.MulDiv(amount0, totalSupply, reserve0);
        var shares1 = FullMath.MulDiv(amount1, totalSupply, reserve1);
        return shares0 < shares1 ? shares0 : shares1;
    }
}
