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
    public string? Data { get; set; }
    public byte Priority { get; set; }
    public uint ChainId { get; set; }
    public string Signature { get; set; } = "";
    public string SenderPublicKey { get; set; } = "";

    public Transaction ToTransaction()
    {
        return new Transaction
        {
            Type = (TransactionType)Type,
            Nonce = Nonce,
            Sender = Address.FromHexString(Sender),
            To = Address.FromHexString(To),
            Value = UInt256.Parse(Value),
            GasLimit = GasLimit,
            GasPrice = UInt256.Parse(GasPrice),
            Data = string.IsNullOrEmpty(Data) ? [] : Convert.FromHexString(Data.StartsWith("0x") ? Data[2..] : Data),
            Priority = Priority,
            ChainId = ChainId,
            Signature = new Basalt.Core.Signature(Convert.FromHexString(Signature.StartsWith("0x") ? Signature[2..] : Signature)),
            SenderPublicKey = new PublicKey(Convert.FromHexString(SenderPublicKey.StartsWith("0x") ? SenderPublicKey[2..] : SenderPublicKey)),
        };
    }
}
