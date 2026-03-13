using System.Buffers.Binary;
using Basalt.Core;
using Basalt.Execution;
using Basalt.Execution.Dex;

namespace Basalt.Sdk.Wallet.Transactions;

/// <summary>
/// Fluent builder for DEX swap intent transactions (plaintext and encrypted).
/// Encodes the 114-byte intent payload matching <see cref="ParsedIntent.Parse"/>.
/// </summary>
public sealed class DexSwapIntentBuilder
{
    private readonly TransactionBuilder _inner;
    private readonly bool _encrypted;
    private BlsPublicKey _groupPublicKey;
    private ulong _epoch;

    private Address _tokenIn;
    private Address _tokenOut;
    private UInt256 _amountIn;
    private UInt256 _minAmountOut;
    private ulong _deadline;
    private bool _allowPartialFill;

    private DexSwapIntentBuilder(TransactionBuilder inner, bool encrypted)
    {
        _inner = inner;
        _encrypted = encrypted;
        _inner.WithGasLimit(80_000);
        _inner.WithTo(DexState.DexAddress);
    }

    /// <summary>
    /// Creates a builder for a plaintext swap intent (type 10).
    /// </summary>
    public static DexSwapIntentBuilder Create()
    {
        return new DexSwapIntentBuilder(TransactionBuilder.DexSwapIntent(), encrypted: false);
    }

    /// <summary>
    /// Creates a builder for an encrypted swap intent (type 18).
    /// The intent payload is encrypted with the DKG group public key for MEV protection.
    /// </summary>
    /// <param name="groupPublicKey">The DKG group public key (48-byte compressed G1 point).</param>
    /// <param name="epoch">The DKG epoch number.</param>
    public static DexSwapIntentBuilder CreateEncrypted(BlsPublicKey groupPublicKey, ulong epoch)
    {
        var builder = new DexSwapIntentBuilder(TransactionBuilder.DexEncryptedSwapIntent(), encrypted: true);
        builder._groupPublicKey = groupPublicKey;
        builder._epoch = epoch;
        return builder;
    }

    /// <summary>Sets the input token address.</summary>
    public DexSwapIntentBuilder WithTokenIn(Address tokenIn)
    {
        _tokenIn = tokenIn;
        return this;
    }

    /// <summary>Sets the output token address.</summary>
    public DexSwapIntentBuilder WithTokenOut(Address tokenOut)
    {
        _tokenOut = tokenOut;
        return this;
    }

    /// <summary>Sets the input amount.</summary>
    public DexSwapIntentBuilder WithAmountIn(UInt256 amountIn)
    {
        _amountIn = amountIn;
        return this;
    }

    /// <summary>Sets the minimum acceptable output amount (slippage protection).</summary>
    public DexSwapIntentBuilder WithMinAmountOut(UInt256 minAmountOut)
    {
        _minAmountOut = minAmountOut;
        return this;
    }

    /// <summary>Sets the block deadline (0 = no deadline).</summary>
    public DexSwapIntentBuilder WithDeadline(ulong deadline)
    {
        _deadline = deadline;
        return this;
    }

    /// <summary>Sets whether partial fills are allowed.</summary>
    public DexSwapIntentBuilder WithAllowPartialFill(bool allowPartialFill)
    {
        _allowPartialFill = allowPartialFill;
        return this;
    }

    /// <summary>Sets the gas limit. Default: 80,000.</summary>
    public DexSwapIntentBuilder WithGasLimit(ulong gasLimit)
    {
        _inner.WithGasLimit(gasLimit);
        return this;
    }

    /// <summary>Sets the gas price.</summary>
    public DexSwapIntentBuilder WithGasPrice(UInt256 gasPrice)
    {
        _inner.WithGasPrice(gasPrice);
        return this;
    }

    /// <summary>Sets the maximum fee per gas (EIP-1559).</summary>
    public DexSwapIntentBuilder WithMaxFeePerGas(UInt256 maxFeePerGas)
    {
        _inner.WithMaxFeePerGas(maxFeePerGas);
        return this;
    }

    /// <summary>Sets the maximum priority fee per gas (EIP-1559).</summary>
    public DexSwapIntentBuilder WithMaxPriorityFeePerGas(UInt256 maxPriorityFeePerGas)
    {
        _inner.WithMaxPriorityFeePerGas(maxPriorityFeePerGas);
        return this;
    }

    /// <summary>Sets the chain ID.</summary>
    public DexSwapIntentBuilder WithChainId(uint chainId)
    {
        _inner.WithChainId(chainId);
        return this;
    }

    /// <summary>Sets the transaction nonce.</summary>
    public DexSwapIntentBuilder WithNonce(ulong nonce)
    {
        _inner.WithNonce(nonce);
        return this;
    }

    /// <summary>Sets the sender address.</summary>
    public DexSwapIntentBuilder WithSender(Address sender)
    {
        _inner.WithSender(sender);
        return this;
    }

    /// <summary>
    /// Builds the unsigned swap intent transaction.
    /// Encodes the 114-byte payload: [1B version][20B tokenIn][20B tokenOut][32B amountIn LE][32B minAmountOut LE][8B deadline BE][1B flags]
    /// For encrypted intents, the payload is wrapped with EC-ElGamal + AES-256-GCM.
    /// </summary>
    public Transaction Build()
    {
        var payload = new byte[114];
        payload[0] = 1; // version

        _tokenIn.ToArray().CopyTo(payload.AsSpan(1, 20));
        _tokenOut.ToArray().CopyTo(payload.AsSpan(21, 20));
        _amountIn.WriteTo(payload.AsSpan(41, 32)); // LE default
        _minAmountOut.WriteTo(payload.AsSpan(73, 32)); // LE default
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(105, 8), _deadline);
        payload[113] = (byte)(_allowPartialFill ? 0x01 : 0x00);

        byte[] data;
        if (_encrypted)
        {
            data = EncryptedIntent.Encrypt(payload, _groupPublicKey, _epoch);
        }
        else
        {
            data = payload;
        }

        _inner.WithData(data);
        return _inner.Build();
    }
}
