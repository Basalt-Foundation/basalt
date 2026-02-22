using Basalt.Core;
using Basalt.Execution;

namespace Basalt.Api.GraphQL;

public sealed class StatusResult
{
    public ulong BlockHeight { get; set; }
    public string LatestBlockHash { get; set; } = "";
    public int MempoolSize { get; set; }
    public uint ProtocolVersion { get; set; }
}

public sealed class BlockResult
{
    public ulong Number { get; set; }
    public string Hash { get; set; } = "";
    public string ParentHash { get; set; } = "";
    public string StateRoot { get; set; } = "";
    public long Timestamp { get; set; }
    public string Proposer { get; set; } = "";
    public ulong GasUsed { get; set; }
    public ulong GasLimit { get; set; }
    public string BaseFee { get; set; } = "0";
    public int TransactionCount { get; set; }

    public static BlockResult FromBlock(Block block) => new()
    {
        Number = block.Number,
        Hash = block.Hash.ToHexString(),
        ParentHash = block.Header.ParentHash.ToHexString(),
        StateRoot = block.Header.StateRoot.ToHexString(),
        Timestamp = block.Header.Timestamp,
        Proposer = block.Header.Proposer.ToHexString(),
        GasUsed = block.Header.GasUsed,
        GasLimit = block.Header.GasLimit,
        BaseFee = block.Header.BaseFee.ToString(),
        TransactionCount = block.Transactions.Count,
    };
}

public sealed class AccountResult
{
    public string Address { get; set; } = "";
    public string Balance { get; set; } = "0";
    public ulong Nonce { get; set; }
    public string AccountType { get; set; } = "";
}

public sealed class TransactionResult
{
    public bool Success { get; set; }
    public string? Hash { get; set; }
    public string? Status { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class TransactionInput
{
    public byte Type { get; set; }
    public ulong Nonce { get; set; }
    public string Sender { get; set; } = "";
    public string To { get; set; } = "";
    public string Value { get; set; } = "0";
    public ulong GasLimit { get; set; }
    public string GasPrice { get; set; } = "1";
    public string? MaxFeePerGas { get; set; }
    public string? MaxPriorityFeePerGas { get; set; }
    public string? Data { get; set; }
    public byte Priority { get; set; }
    public uint ChainId { get; set; }
    public string Signature { get; set; } = "";
    public string SenderPublicKey { get; set; } = "";

    public Transaction ToTransaction()
    {
        // NEW-1: Validate signature and public key lengths (same as REST TransactionRequest)
        var sigHex = Signature.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Signature[2..] : Signature;
        var sigBytes = Convert.FromHexString(sigHex);
        if (sigBytes.Length != 64)
            throw new ArgumentException("Signature must be exactly 64 bytes");

        var pkHex = SenderPublicKey.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? SenderPublicKey[2..] : SenderPublicKey;
        var pkBytes = Convert.FromHexString(pkHex);
        if (pkBytes.Length != 32)
            throw new ArgumentException("SenderPublicKey must be exactly 32 bytes");

        return new Transaction
        {
            Type = (TransactionType)Type,
            Nonce = Nonce,
            Sender = Address.FromHexString(Sender),
            To = Address.FromHexString(To),
            Value = UInt256.Parse(Value),
            GasLimit = GasLimit,
            GasPrice = UInt256.Parse(GasPrice),
            MaxFeePerGas = string.IsNullOrEmpty(MaxFeePerGas) ? UInt256.Zero : UInt256.Parse(MaxFeePerGas),
            MaxPriorityFeePerGas = string.IsNullOrEmpty(MaxPriorityFeePerGas) ? UInt256.Zero : UInt256.Parse(MaxPriorityFeePerGas),
            Data = string.IsNullOrEmpty(Data) ? [] : Convert.FromHexString(Data.StartsWith("0x") ? Data[2..] : Data),
            Priority = Priority,
            ChainId = ChainId,
            Signature = new Basalt.Core.Signature(sigBytes),
            SenderPublicKey = new PublicKey(pkBytes),
        };
    }
}

public sealed class ReceiptResult
{
    public string TransactionHash { get; set; } = "";
    public string BlockHash { get; set; } = "";
    public ulong BlockNumber { get; set; }
    public int TransactionIndex { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public ulong GasUsed { get; set; }
    public bool Success { get; set; }
    public string ErrorCode { get; set; } = "";
    public string PostStateRoot { get; set; } = "";
    public string EffectiveGasPrice { get; set; } = "0";
    public List<EventLogResult> Logs { get; set; } = [];
}

public sealed class EventLogResult
{
    public string Contract { get; set; } = "";
    public string EventSignature { get; set; } = "";
    public List<string> Topics { get; set; } = [];
    public string? Data { get; set; }
}

public sealed class TransactionDetailResult
{
    public string Hash { get; set; } = "";
    public string Type { get; set; } = "";
    public ulong Nonce { get; set; }
    public string Sender { get; set; } = "";
    public string To { get; set; } = "";
    public string Value { get; set; } = "0";
    public ulong GasLimit { get; set; }
    public string GasPrice { get; set; } = "0";
    public string? MaxFeePerGas { get; set; }
    public string? MaxPriorityFeePerGas { get; set; }
    public ulong? BlockNumber { get; set; }
    public string? BlockHash { get; set; }
    public int? TransactionIndex { get; set; }
    // Receipt fields
    public ulong? GasUsed { get; set; }
    public bool? Success { get; set; }
    public string? ErrorCode { get; set; }
    public string? EffectiveGasPrice { get; set; }
    public List<EventLogResult>? Logs { get; set; }
}
