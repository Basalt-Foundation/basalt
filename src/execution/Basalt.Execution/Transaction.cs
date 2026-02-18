using Basalt.Codec;
using Basalt.Core;
using Basalt.Crypto;

namespace Basalt.Execution;

/// <summary>
/// Transaction types in the Basalt blockchain.
/// </summary>
public enum TransactionType : byte
{
    Transfer = 0,
    ContractDeploy = 1,
    ContractCall = 2,
    StakeDeposit = 3,
    StakeWithdraw = 4,
    ValidatorRegister = 5,
    ValidatorExit = 6,
}

/// <summary>
/// A transaction in the Basalt blockchain.
/// </summary>
public sealed class Transaction
{
    public TransactionType Type { get; init; }
    public ulong Nonce { get; init; }
    public Address Sender { get; init; }
    public Address To { get; init; }
    public UInt256 Value { get; init; }
    public ulong GasLimit { get; init; }
    public UInt256 GasPrice { get; init; }
    public UInt256 MaxFeePerGas { get; init; } = UInt256.Zero;
    public UInt256 MaxPriorityFeePerGas { get; init; } = UInt256.Zero;
    public byte[] Data { get; init; } = [];
    public byte Priority { get; init; }
    public uint ChainId { get; init; }
    public Signature Signature { get; init; }
    public PublicKey SenderPublicKey { get; init; }

    /// <summary>Whether this is an EIP-1559 transaction (has explicit MaxFeePerGas).</summary>
    public bool IsEip1559 => !MaxFeePerGas.IsZero;

    /// <summary>Effective max fee: MaxFeePerGas for EIP-1559, GasPrice for legacy.</summary>
    public UInt256 EffectiveMaxFee => IsEip1559 ? MaxFeePerGas : GasPrice;

    /// <summary>
    /// Compute the effective gas price given a base fee.
    /// effectiveGasPrice = min(MaxFeePerGas, BaseFee + MaxPriorityFeePerGas)
    /// For legacy transactions, returns GasPrice.
    /// </summary>
    public UInt256 EffectiveGasPrice(UInt256 baseFee)
    {
        if (!IsEip1559) return GasPrice;
        var basePlusTip = baseFee + MaxPriorityFeePerGas;
        return MaxFeePerGas < basePlusTip ? MaxFeePerGas : basePlusTip;
    }

    private Hash256? _hash;

    /// <summary>
    /// Compute the transaction hash (BLAKE3 of the serialized signing payload).
    /// </summary>
    public Hash256 Hash
    {
        get
        {
            _hash ??= ComputeHash();
            return _hash.Value;
        }
    }

    private Hash256 ComputeHash()
    {
        Span<byte> buffer = stackalloc byte[GetSigningPayloadSize()];
        WriteSigningPayload(buffer);
        return Blake3Hasher.Hash(buffer);
    }

    /// <summary>
    /// Compute the payload that should be signed (everything except the signature).
    /// </summary>
    public int GetSigningPayloadSize()
    {
        return 1 + 8 + Address.Size + Address.Size + 32 + 8 + 32 + 32 + 32 + 4 + Data.Length + 1 + 4;
    }

    public void WriteSigningPayload(Span<byte> buffer)
    {
        var writer = new BasaltWriter(buffer);
        writer.WriteByte((byte)Type);
        writer.WriteUInt64(Nonce);
        writer.WriteAddress(Sender);
        writer.WriteAddress(To);
        writer.WriteUInt256(Value);
        writer.WriteUInt64(GasLimit);
        writer.WriteUInt256(GasPrice);
        writer.WriteUInt256(MaxFeePerGas);
        writer.WriteUInt256(MaxPriorityFeePerGas);
        writer.WriteBytes(Data);
        writer.WriteByte(Priority);
        writer.WriteUInt32(ChainId);
    }

    /// <summary>
    /// Sign this transaction with the given private key.
    /// </summary>
    public static Transaction Sign(Transaction unsignedTx, byte[] privateKey)
    {
        Span<byte> payload = stackalloc byte[unsignedTx.GetSigningPayloadSize()];
        unsignedTx.WriteSigningPayload(payload);

        var signature = Ed25519Signer.Sign(privateKey, payload);
        var publicKey = Ed25519Signer.GetPublicKey(privateKey);

        return new Transaction
        {
            Type = unsignedTx.Type,
            Nonce = unsignedTx.Nonce,
            Sender = unsignedTx.Sender,
            To = unsignedTx.To,
            Value = unsignedTx.Value,
            GasLimit = unsignedTx.GasLimit,
            GasPrice = unsignedTx.GasPrice,
            MaxFeePerGas = unsignedTx.MaxFeePerGas,
            MaxPriorityFeePerGas = unsignedTx.MaxPriorityFeePerGas,
            Data = unsignedTx.Data,
            Priority = unsignedTx.Priority,
            ChainId = unsignedTx.ChainId,
            Signature = signature,
            SenderPublicKey = publicKey,
        };
    }

    /// <summary>
    /// Verify the signature of this transaction.
    /// </summary>
    public bool VerifySignature()
    {
        if (Signature.IsEmpty || SenderPublicKey.IsEmpty)
            return false;

        Span<byte> payload = stackalloc byte[GetSigningPayloadSize()];
        WriteSigningPayload(payload);

        return Ed25519Signer.Verify(SenderPublicKey, payload, Signature);
    }
}

/// <summary>
/// Transaction receipt generated after execution.
/// </summary>
public sealed class TransactionReceipt
{
    public required Hash256 TransactionHash { get; init; }
    public required Hash256 BlockHash { get; init; }
    public required ulong BlockNumber { get; init; }
    public required int TransactionIndex { get; init; }
    public required Address From { get; init; }
    public required Address To { get; init; }
    public required ulong GasUsed { get; init; }
    public required bool Success { get; init; }
    public required BasaltErrorCode ErrorCode { get; init; }
    public required Hash256 PostStateRoot { get; init; }
    public UInt256 EffectiveGasPrice { get; init; } = UInt256.Zero;
    public List<EventLog> Logs { get; init; } = [];
}

/// <summary>
/// An event log emitted during contract execution.
/// </summary>
public sealed class EventLog
{
    public required Address Contract { get; init; }
    public required Hash256 EventSignature { get; init; }
    public required Hash256[] Topics { get; init; }
    public required byte[] Data { get; init; }
}
