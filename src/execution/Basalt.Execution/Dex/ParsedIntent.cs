using Basalt.Core;

namespace Basalt.Execution.Dex;

/// <summary>
/// A parsed swap intent extracted from a <see cref="TransactionType.DexSwapIntent"/> transaction.
/// Swap intents are collected per-block and settled via batch auction in the BlockBuilder,
/// rather than being executed individually. This eliminates MEV by ensuring all intents
/// in a block get the same uniform clearing price.
/// </summary>
public readonly struct ParsedIntent
{
    /// <summary>The address submitting the swap intent.</summary>
    public Address Sender { get; init; }

    /// <summary>The input token address.</summary>
    public Address TokenIn { get; init; }

    /// <summary>The output token address.</summary>
    public Address TokenOut { get; init; }

    /// <summary>The amount of input tokens to swap.</summary>
    public UInt256 AmountIn { get; init; }

    /// <summary>Minimum acceptable output (slippage protection).</summary>
    public UInt256 MinAmountOut { get; init; }

    /// <summary>Block number deadline (0 = no deadline).</summary>
    public ulong Deadline { get; init; }

    /// <summary>Whether partial fills are allowed (from flags byte bit 0).</summary>
    public bool AllowPartialFill { get; init; }

    /// <summary>The original transaction hash (for receipt tracking).</summary>
    public Hash256 TxHash { get; init; }

    /// <summary>The original transaction (needed for receipt generation).</summary>
    public Transaction OriginalTx { get; init; }

    /// <summary>
    /// The implicit limit price of this intent: amountIn / minAmountOut.
    /// For buy intents (buying token0), this is the max price willing to pay.
    /// For sell intents (selling token0), this is the min price willing to accept.
    /// Stored as a UInt256 scaled by 2^64 for comparison precision.
    /// </summary>
    public UInt256 LimitPrice => MinAmountOut.IsZero
        ? UInt256.MaxValue
        : Math.FullMath.MulDiv(AmountIn, new UInt256(1UL << 32) * new UInt256(1UL << 32), MinAmountOut);

    /// <summary>
    /// Determine whether this intent is a "buy" (buying token0) based on canonical token ordering.
    /// </summary>
    /// <param name="token0">The canonical token0 of the pool.</param>
    /// <returns>True if this intent buys token0 (inputs token1).</returns>
    public bool IsBuyingSide(Address token0) => TokenOut == token0;

    /// <summary>
    /// Parse a swap intent from raw transaction data.
    /// Data format: <c>[1B version][20B tokenIn][20B tokenOut][32B amountIn][32B minAmountOut][8B deadline][1B flags]</c>
    /// </summary>
    /// <param name="tx">The source transaction.</param>
    /// <returns>The parsed intent, or null if data is malformed.</returns>
    public static ParsedIntent? Parse(Transaction tx)
    {
        if (tx.Data.Length < 114)
            return null;

        return new ParsedIntent
        {
            Sender = tx.Sender,
            TokenIn = new Address(tx.Data.AsSpan(1, 20)),
            TokenOut = new Address(tx.Data.AsSpan(21, 20)),
            AmountIn = new UInt256(tx.Data.AsSpan(41, 32)),
            MinAmountOut = new UInt256(tx.Data.AsSpan(73, 32)),
            Deadline = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(tx.Data.AsSpan(105, 8)),
            AllowPartialFill = (tx.Data[113] & 0x01) != 0,
            TxHash = tx.Hash,
            OriginalTx = tx,
        };
    }
}
