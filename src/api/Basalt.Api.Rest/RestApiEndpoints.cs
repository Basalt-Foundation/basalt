using System.Text.Json;
using System.Text.Json.Serialization;
using Basalt.Core;
using Basalt.Execution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Basalt.Api.Rest;

/// <summary>
/// REST API endpoint definitions using ASP.NET Minimal APIs.
/// </summary>
public static class RestApiEndpoints
{
    public static void MapBasaltEndpoints(
        Microsoft.AspNetCore.Routing.IEndpointRouteBuilder app,
        ChainManager chainManager,
        Mempool mempool,
        TransactionValidator validator,
        Storage.IStateDatabase stateDb)
    {
        // POST /v1/transactions
        app.MapPost("/v1/transactions", (TransactionRequest request) =>
        {
            try
            {
                var tx = request.ToTransaction();
                var validationResult = validator.Validate(tx, stateDb);
                if (!validationResult.IsSuccess)
                {
                    return Microsoft.AspNetCore.Http.Results.BadRequest(new ErrorResponse
                    {
                        Code = (int)validationResult.ErrorCode,
                        Message = validationResult.Message ?? validationResult.ErrorCode.ToString(),
                    });
                }

                if (!mempool.Add(tx))
                {
                    return Microsoft.AspNetCore.Http.Results.BadRequest(new ErrorResponse
                    {
                        Code = (int)BasaltErrorCode.DuplicateTransaction,
                        Message = "Transaction already in mempool or mempool is full.",
                    });
                }

                return Microsoft.AspNetCore.Http.Results.Ok(new TransactionResponse
                {
                    Hash = tx.Hash.ToHexString(),
                    Status = "pending",
                });
            }
            catch (Exception ex)
            {
                return Microsoft.AspNetCore.Http.Results.BadRequest(new ErrorResponse
                {
                    Code = (int)BasaltErrorCode.InternalError,
                    Message = ex.Message,
                });
            }
        });

        // GET /v1/blocks/latest
        app.MapGet("/v1/blocks/latest", () =>
        {
            var block = chainManager.LatestBlock;
            if (block == null)
                return Microsoft.AspNetCore.Http.Results.NotFound();
            return Microsoft.AspNetCore.Http.Results.Ok(BlockResponse.FromBlock(block));
        });

        // GET /v1/blocks/{id}
        app.MapGet("/v1/blocks/{id}", (string id) =>
        {
            Block? block;
            if (ulong.TryParse(id, out var number))
                block = chainManager.GetBlockByNumber(number);
            else if (Hash256.TryFromHexString(id, out var hash))
                block = chainManager.GetBlockByHash(hash);
            else
                return Microsoft.AspNetCore.Http.Results.BadRequest(new ErrorResponse
                {
                    Code = 400,
                    Message = "Invalid block identifier. Provide a block number or hash.",
                });

            if (block == null)
                return Microsoft.AspNetCore.Http.Results.NotFound();
            return Microsoft.AspNetCore.Http.Results.Ok(BlockResponse.FromBlock(block));
        });

        // GET /v1/accounts/{address}
        app.MapGet("/v1/accounts/{address}", (string address) =>
        {
            if (!Address.TryFromHexString(address, out var addr))
                return Microsoft.AspNetCore.Http.Results.BadRequest(new ErrorResponse
                {
                    Code = 400,
                    Message = "Invalid address format.",
                });

            var account = stateDb.GetAccount(addr);
            if (account == null)
                return Microsoft.AspNetCore.Http.Results.NotFound();

            return Microsoft.AspNetCore.Http.Results.Ok(new AccountResponse
            {
                Address = addr.ToHexString(),
                Balance = account.Value.Balance.ToString(),
                Nonce = account.Value.Nonce,
                AccountType = account.Value.AccountType.ToString(),
            });
        });

        // GET /v1/status
        app.MapGet("/v1/status", () =>
        {
            var latest = chainManager.LatestBlock;
            return Microsoft.AspNetCore.Http.Results.Ok(new StatusResponse
            {
                BlockHeight = latest?.Number ?? 0,
                LatestBlockHash = latest?.Hash.ToHexString() ?? Hash256.Zero.ToHexString(),
                MempoolSize = mempool.Count,
                ProtocolVersion = 1,
            });
        });
    }
}

// DTO classes
public sealed class TransactionRequest
{
    [JsonPropertyName("type")] public byte Type { get; set; }
    [JsonPropertyName("nonce")] public ulong Nonce { get; set; }
    [JsonPropertyName("sender")] public string Sender { get; set; } = "";
    [JsonPropertyName("to")] public string To { get; set; } = "";
    [JsonPropertyName("value")] public string Value { get; set; } = "0";
    [JsonPropertyName("gasLimit")] public ulong GasLimit { get; set; }
    [JsonPropertyName("gasPrice")] public string GasPrice { get; set; } = "1";
    [JsonPropertyName("data")] public string? Data { get; set; }
    [JsonPropertyName("priority")] public byte Priority { get; set; }
    [JsonPropertyName("chainId")] public uint ChainId { get; set; }
    [JsonPropertyName("signature")] public string Signature { get; set; } = "";
    [JsonPropertyName("senderPublicKey")] public string SenderPublicKey { get; set; } = "";

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
            Signature = new Core.Signature(Convert.FromHexString(Signature.StartsWith("0x") ? Signature[2..] : Signature)),
            SenderPublicKey = new PublicKey(Convert.FromHexString(SenderPublicKey.StartsWith("0x") ? SenderPublicKey[2..] : SenderPublicKey)),
        };
    }
}

public sealed class TransactionResponse
{
    [JsonPropertyName("hash")] public required string Hash { get; set; }
    [JsonPropertyName("status")] public required string Status { get; set; }
}

public sealed class BlockResponse
{
    [JsonPropertyName("number")] public ulong Number { get; set; }
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("parentHash")] public string ParentHash { get; set; } = "";
    [JsonPropertyName("stateRoot")] public string StateRoot { get; set; } = "";
    [JsonPropertyName("timestamp")] public long Timestamp { get; set; }
    [JsonPropertyName("proposer")] public string Proposer { get; set; } = "";
    [JsonPropertyName("gasUsed")] public ulong GasUsed { get; set; }
    [JsonPropertyName("gasLimit")] public ulong GasLimit { get; set; }
    [JsonPropertyName("transactionCount")] public int TransactionCount { get; set; }

    public static BlockResponse FromBlock(Block block)
    {
        return new BlockResponse
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
}

public sealed class AccountResponse
{
    [JsonPropertyName("address")] public required string Address { get; set; }
    [JsonPropertyName("balance")] public required string Balance { get; set; }
    [JsonPropertyName("nonce")] public required ulong Nonce { get; set; }
    [JsonPropertyName("accountType")] public required string AccountType { get; set; }
}

public sealed class StatusResponse
{
    [JsonPropertyName("blockHeight")] public required ulong BlockHeight { get; set; }
    [JsonPropertyName("latestBlockHash")] public required string LatestBlockHash { get; set; }
    [JsonPropertyName("mempoolSize")] public required int MempoolSize { get; set; }
    [JsonPropertyName("protocolVersion")] public required uint ProtocolVersion { get; set; }
}

public sealed class ErrorResponse
{
    [JsonPropertyName("code")] public required int Code { get; set; }
    [JsonPropertyName("message")] public required string Message { get; set; }
}

[JsonSerializable(typeof(TransactionRequest))]
[JsonSerializable(typeof(TransactionResponse))]
[JsonSerializable(typeof(BlockResponse))]
[JsonSerializable(typeof(AccountResponse))]
[JsonSerializable(typeof(StatusResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(FaucetRequest))]
[JsonSerializable(typeof(FaucetResponse))]
public partial class BasaltApiJsonContext : JsonSerializerContext;
