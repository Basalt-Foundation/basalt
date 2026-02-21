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
/// L-1: Data and ComplianceProofs arrays MUST NOT be mutated after construction.
/// Mutating them after Hash has been accessed invalidates the cached hash.
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
    /// <summary>
    /// L-3: Reserved for future use (e.g., transaction priority lanes).
    /// Included in signing payload for forward compatibility.
    /// </summary>
    public byte Priority { get; init; }
    public uint ChainId { get; init; }
    public Signature Signature { get; init; }
    public PublicKey SenderPublicKey { get; init; }

    /// <summary>
    /// Optional ZK compliance proofs attached to this transaction.
    /// Each proof demonstrates the sender satisfies a credential schema requirement
    /// without revealing identity, issuer, or credential details.
    /// A BLAKE3 hash of serialized proofs is included in the signing payload (COMPL-02)
    /// to prevent relay nodes from stripping or modifying proofs.
    /// </summary>
    public ComplianceProof[] ComplianceProofs { get; init; } = [];

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
        // H-3: Use WrittenSpan to hash only the actual payload, not buffer padding
        Span<byte> buffer = stackalloc byte[GetSigningPayloadSize()];
        var written = WriteSigningPayload(buffer);
        return Blake3Hasher.Hash(written);
    }

    /// <summary>
    /// Compute the maximum buffer size needed for the signing payload.
    /// H-3: Uses correct VarInt size calculation for the Data length prefix.
    /// </summary>
    public int GetSigningPayloadSize()
    {
        // COMPL-02: +32 bytes for compliance proofs hash
        // Data is length-prefixed with LEB128 varint (1-10 bytes)
        var dataVarIntSize = VarIntSize((ulong)Data.Length);
        return 1 + 8 + Address.Size + Address.Size + 32 + 8 + 32 + 32 + 32 + dataVarIntSize + Data.Length + 1 + 4 + Hash256.Size;
    }

    /// <summary>
    /// Calculate the number of bytes required to encode a value as a LEB128 varint.
    /// </summary>
    private static int VarIntSize(ulong value)
    {
        int size = 1;
        while (value >= 0x80)
        {
            size++;
            value >>= 7;
        }
        return size;
    }

    /// <summary>
    /// H-3: Returns the actual written span (not the full buffer) for correct hashing.
    /// </summary>
    public ReadOnlySpan<byte> WriteSigningPayload(Span<byte> buffer)
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

        // COMPL-02: Include hash of compliance proofs to prevent stripping/modification
        writer.WriteHash256(ComputeComplianceProofsHash());

        return writer.WrittenSpan;
    }

    /// <summary>
    /// Compute a BLAKE3 hash of all compliance proofs (COMPL-02).
    /// Returns Hash256.Zero if no proofs are attached.
    /// </summary>
    private Hash256 ComputeComplianceProofsHash()
    {
        if (ComplianceProofs.Length == 0)
            return Hash256.Zero;

        using var hasher = Blake3Hasher.CreateIncremental();
        Span<byte> hashBuffer = stackalloc byte[Hash256.Size];
        foreach (var proof in ComplianceProofs)
        {
            proof.SchemaId.WriteTo(hashBuffer);
            hasher.Update(hashBuffer);

            if (proof.Proof.Length > 0)
                hasher.Update(proof.Proof);
            if (proof.PublicInputs.Length > 0)
                hasher.Update(proof.PublicInputs);

            proof.Nullifier.WriteTo(hashBuffer);
            hasher.Update(hashBuffer);
        }

        return hasher.Finalize();
    }

    /// <summary>
    /// Sign this transaction with the given private key.
    /// </summary>
    public static Transaction Sign(Transaction unsignedTx, byte[] privateKey)
    {
        Span<byte> buffer = stackalloc byte[unsignedTx.GetSigningPayloadSize()];
        var payload = unsignedTx.WriteSigningPayload(buffer);

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
            ComplianceProofs = unsignedTx.ComplianceProofs,
        };
    }

    /// <summary>
    /// Verify the signature of this transaction.
    /// </summary>
    public bool VerifySignature()
    {
        if (Signature.IsEmpty || SenderPublicKey.IsEmpty)
            return false;

        // H-3: Hash only the written span (exact payload), not the full buffer
        Span<byte> buffer = stackalloc byte[GetSigningPayloadSize()];
        var payload = WriteSigningPayload(buffer);

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
