using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution;
using Basalt.Execution.VM;
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
        Storage.IStateDatabase stateDb,
        IContractRuntime? contractRuntime = null,
        Storage.RocksDb.ReceiptStore? receiptStore = null)
    {
        // Helper: look up a receipt by tx hash (persistent store first, then in-memory fallback)
        Storage.RocksDb.ReceiptData? LookupReceipt(Hash256 txHash)
        {
            if (receiptStore != null)
            {
                var stored = receiptStore.GetReceipt(txHash);
                if (stored != null) return stored;
            }

            // Fallback: scan recent blocks for in-memory receipts (standalone mode)
            var latestNum = chainManager.LatestBlockNumber;
            var scanDepth = Math.Min(latestNum + 1, 1000UL);
            for (ulong i = 0; i < scanDepth; i++)
            {
                var block = chainManager.GetBlockByNumber(latestNum - i);
                if (block?.Receipts == null) continue;
                foreach (var r in block.Receipts)
                {
                    if (r.TransactionHash == txHash)
                    {
                        return new Storage.RocksDb.ReceiptData
                        {
                            TransactionHash = r.TransactionHash,
                            BlockHash = r.BlockHash,
                            BlockNumber = r.BlockNumber,
                            TransactionIndex = r.TransactionIndex,
                            From = r.From,
                            To = r.To,
                            GasUsed = r.GasUsed,
                            Success = r.Success,
                            ErrorCode = (int)r.ErrorCode,
                            PostStateRoot = r.PostStateRoot,
                            EffectiveGasPrice = r.EffectiveGasPrice,
                            Logs = (r.Logs ?? []).Select(l => new Storage.RocksDb.LogData
                            {
                                Contract = l.Contract,
                                EventSignature = l.EventSignature,
                                Topics = l.Topics ?? [],
                                Data = l.Data ?? [],
                            }).ToArray(),
                        };
                    }
                }
            }
            return null;
        }

        // Helper: get receipt for a tx at a known block index (fast path)
        Storage.RocksDb.ReceiptData? GetReceiptForTx(Block block, int txIndex, Hash256 txHash)
        {
            if (block.Receipts != null && txIndex < block.Receipts.Count)
            {
                var r = block.Receipts[txIndex];
                return new Storage.RocksDb.ReceiptData
                {
                    TransactionHash = r.TransactionHash,
                    BlockHash = r.BlockHash,
                    BlockNumber = r.BlockNumber,
                    TransactionIndex = r.TransactionIndex,
                    From = r.From,
                    To = r.To,
                    GasUsed = r.GasUsed,
                    Success = r.Success,
                    ErrorCode = (int)r.ErrorCode,
                    PostStateRoot = r.PostStateRoot,
                    EffectiveGasPrice = r.EffectiveGasPrice,
                    Logs = (r.Logs ?? []).Select(l => new Storage.RocksDb.LogData
                    {
                        Contract = l.Contract,
                        EventSignature = l.EventSignature,
                        Topics = l.Topics ?? [],
                        Data = l.Data ?? [],
                    }).ToArray(),
                };
            }
            return receiptStore?.GetReceipt(txHash);
        }

        // Helper: get the current base fee from the latest block
        UInt256 GetCurrentBaseFee() => chainManager.LatestBlock?.Header.BaseFee ?? UInt256.Zero;

        // POST /v1/transactions
        app.MapPost("/v1/transactions", (TransactionRequest request) =>
        {
            try
            {
                var tx = request.ToTransaction();
                var validationResult = validator.Validate(tx, stateDb, GetCurrentBaseFee());
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

        // GET /v1/accounts/{address}/transactions
        app.MapGet("/v1/accounts/{address}/transactions", (string address, int? count) =>
        {
            if (!Address.TryFromHexString(address, out var addr))
                return Microsoft.AspNetCore.Http.Results.BadRequest(new ErrorResponse
                {
                    Code = 400,
                    Message = "Invalid address format.",
                });

            var maxTxs = Math.Clamp(count ?? 25, 1, 100);
            var latestNum = chainManager.LatestBlockNumber;
            var scanDepth = Math.Min(latestNum + 1, 1000UL);
            var transactions = new List<TransactionDetailResponse>();

            for (ulong i = 0; i < scanDepth && transactions.Count < maxTxs; i++)
            {
                var block = chainManager.GetBlockByNumber(latestNum - i);
                if (block == null) continue;
                for (int j = 0; j < block.Transactions.Count && transactions.Count < maxTxs; j++)
                {
                    var tx = block.Transactions[j];
                    if (tx.Sender == addr || tx.To == addr)
                        transactions.Add(TransactionDetailResponse.FromTransaction(tx, block, j, GetReceiptForTx(block, j, tx.Hash)));
                }
            }

            return Microsoft.AspNetCore.Http.Results.Ok(transactions.ToArray());
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

        // GET /v1/blocks?page=1&pageSize=20
        app.MapGet("/v1/blocks", (int? page, int? pageSize) =>
        {
            var p = Math.Max(1, page ?? 1);
            var size = Math.Clamp(pageSize ?? 20, 1, 100);
            var latest = chainManager.LatestBlockNumber;
            var totalItems = (long)latest + 1;

            var startBlock = (long)latest - (long)(p - 1) * size;
            var blocks = new List<BlockResponse>();

            for (int i = 0; i < size && startBlock - i >= 0; i++)
            {
                var block = chainManager.GetBlockByNumber((ulong)(startBlock - i));
                if (block != null)
                    blocks.Add(BlockResponse.FromBlock(block));
            }

            return Microsoft.AspNetCore.Http.Results.Ok(new PaginatedBlocksResponse
            {
                Items = blocks.ToArray(),
                Page = p,
                PageSize = size,
                TotalItems = totalItems,
            });
        });

        // GET /v1/blocks/{number}/transactions
        app.MapGet("/v1/blocks/{number}/transactions", (string number) =>
        {
            if (!ulong.TryParse(number, out var blockNum))
                return Microsoft.AspNetCore.Http.Results.BadRequest(new ErrorResponse
                {
                    Code = 400,
                    Message = "Invalid block number.",
                });

            var block = chainManager.GetBlockByNumber(blockNum);
            if (block == null)
                return Microsoft.AspNetCore.Http.Results.NotFound();

            var txs = block.Transactions.Select((tx, i) =>
                TransactionDetailResponse.FromTransaction(tx, block, i, GetReceiptForTx(block, i, tx.Hash))).ToArray();

            return Microsoft.AspNetCore.Http.Results.Ok(txs);
        });

        // GET /v1/transactions/recent?count=50
        app.MapGet("/v1/transactions/recent", (int? count) =>
        {
            var maxTxs = Math.Clamp(count ?? 50, 1, 200);
            var latestNum = chainManager.LatestBlockNumber;
            var transactions = new List<TransactionDetailResponse>();

            for (ulong i = 0; i <= latestNum && transactions.Count < maxTxs; i++)
            {
                var block = chainManager.GetBlockByNumber(latestNum - i);
                if (block == null) continue;
                for (int j = 0; j < block.Transactions.Count && transactions.Count < maxTxs; j++)
                    transactions.Add(TransactionDetailResponse.FromTransaction(block.Transactions[j], block, j, GetReceiptForTx(block, j, block.Transactions[j].Hash)));
            }

            return Microsoft.AspNetCore.Http.Results.Ok(transactions.ToArray());
        });

        // GET /v1/transactions/{hash}
        app.MapGet("/v1/transactions/{hash}", (string hash) =>
        {
            var normalized = hash.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hash[2..] : hash;
            if (!Hash256.TryFromHexString(normalized, out var targetHash))
                return Microsoft.AspNetCore.Http.Results.BadRequest(new ErrorResponse
                {
                    Code = 400,
                    Message = "Invalid transaction hash.",
                });

            var latestNum = chainManager.LatestBlockNumber;
            var scanDepth = Math.Min(latestNum + 1, 1000UL);

            for (ulong i = 0; i < scanDepth; i++)
            {
                var block = chainManager.GetBlockByNumber(latestNum - i);
                if (block == null) continue;

                for (int j = 0; j < block.Transactions.Count; j++)
                {
                    var tx = block.Transactions[j];
                    if (tx.Hash == targetHash)
                        return Microsoft.AspNetCore.Http.Results.Ok(
                            TransactionDetailResponse.FromTransaction(tx, block, j, GetReceiptForTx(block, j, tx.Hash)));
                }
            }

            return Microsoft.AspNetCore.Http.Results.NotFound();
        });

        // GET /v1/receipts/{hash}
        app.MapGet("/v1/receipts/{hash}", (string hash) =>
        {
            var normalized = hash.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hash[2..] : hash;
            if (!Hash256.TryFromHexString(normalized, out var targetHash))
                return Microsoft.AspNetCore.Http.Results.BadRequest(new ErrorResponse
                {
                    Code = 400,
                    Message = "Invalid transaction hash.",
                });

            var receipt = LookupReceipt(targetHash);
            if (receipt == null)
                return Microsoft.AspNetCore.Http.Results.NotFound();

            return Microsoft.AspNetCore.Http.Results.Ok(ReceiptResponse.FromReceiptData(receipt));
        });

        // POST /v1/call — read-only contract call (eth_call equivalent)
        if (contractRuntime != null)
        {
            app.MapPost("/v1/call", (CallRequest request) =>
            {
                try
                {
                    var dataHex = request.Data.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                        ? request.Data[2..] : request.Data;

                    if (!Address.TryFromHexString(request.To, out var contractAddr))
                        return Microsoft.AspNetCore.Http.Results.BadRequest(new ErrorResponse
                        {
                            Code = 400,
                            Message = "Invalid 'to' address format.",
                        });

                    var contractState = stateDb.GetAccount(contractAddr);
                    if (contractState == null || contractState.Value.AccountType is not (Storage.AccountType.Contract or Storage.AccountType.SystemContract))
                        return Microsoft.AspNetCore.Http.Results.BadRequest(new ErrorResponse
                        {
                            Code = 400,
                            Message = "Address is not a contract account.",
                        });

                    // Load contract code from storage (0xFF01 key)
                    Span<byte> codeKeySpan = stackalloc byte[32];
                    codeKeySpan.Clear();
                    codeKeySpan[0] = 0xFF;
                    codeKeySpan[1] = 0x01;
                    var codeStorageKey = new Hash256(codeKeySpan);
                    var code = stateDb.GetStorage(contractAddr, codeStorageKey) ?? [];

                    if (code.Length == 0)
                        return Microsoft.AspNetCore.Http.Results.BadRequest(new ErrorResponse
                        {
                            Code = 400,
                            Message = "Contract has no code.",
                        });

                    var callData = string.IsNullOrEmpty(dataHex) ? [] : Convert.FromHexString(dataHex);

                    // Derive caller address (optional)
                    var caller = Address.Zero;
                    if (!string.IsNullOrEmpty(request.From) && Address.TryFromHexString(request.From, out var fromAddr))
                        caller = fromAddr;

                    var latestBlock = chainManager.LatestBlock;
                    var gasMeter = new GasMeter(request.GasLimit > 0 ? request.GasLimit : 1_000_000);

                    var ctx = new VmExecutionContext
                    {
                        Caller = caller,
                        ContractAddress = contractAddr,
                        Value = UInt256.Zero,
                        BlockTimestamp = latestBlock != null ? (ulong)latestBlock.Header.Timestamp : 0,
                        BlockNumber = latestBlock?.Number ?? 0,
                        BlockProposer = latestBlock?.Header.Proposer ?? Address.Zero,
                        ChainId = latestBlock?.Header.ChainId ?? 1,
                        GasMeter = gasMeter,
                        StateDb = stateDb,
                        CallDepth = 0,
                    };

                    var result = contractRuntime.Execute(code, callData, ctx);

                    return Microsoft.AspNetCore.Http.Results.Ok(new CallResponse
                    {
                        Success = result.Success,
                        ReturnData = result.ReturnData is { Length: > 0 }
                            ? Convert.ToHexString(result.ReturnData)
                            : null,
                        GasUsed = gasMeter.GasUsed,
                        Error = result.ErrorMessage,
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
        }

        // GET /v1/contracts/{address} — contract metadata
        if (contractRuntime != null)
        {
            app.MapGet("/v1/contracts/{address}", (string address) =>
            {
                try
                {
                    if (!Address.TryFromHexString(address, out var contractAddr))
                        return Microsoft.AspNetCore.Http.Results.BadRequest(new ErrorResponse
                        {
                            Code = 400,
                            Message = "Invalid address format.",
                        });

                    var account = stateDb.GetAccount(contractAddr);
                    if (account == null || account.Value.AccountType is not (Storage.AccountType.Contract or Storage.AccountType.SystemContract))
                        return Microsoft.AspNetCore.Http.Results.NotFound();

                    // Load contract code from storage (0xFF01 key)
                    Span<byte> codeKeySpan = stackalloc byte[32];
                    codeKeySpan.Clear();
                    codeKeySpan[0] = 0xFF;
                    codeKeySpan[1] = 0x01;
                    var codeStorageKey = new Hash256(codeKeySpan);
                    var code = stateDb.GetStorage(contractAddr, codeStorageKey) ?? [];

                    var codeHashHex = "";
                    if (code.Length > 0)
                    {
                        var codeHash = Blake3Hasher.Hash(code);
                        codeHashHex = codeHash.ToHexString();
                    }

                    // Scan blocks for the ContractDeploy tx targeting this address
                    string? deployer = null;
                    string? deployTxHash = null;
                    ulong? deployBlockNumber = null;

                    var latestNum = chainManager.LatestBlockNumber;
                    var scanDepth = Math.Min(latestNum + 1, 5000UL);

                    for (ulong i = 0; i < scanDepth; i++)
                    {
                        var block = chainManager.GetBlockByNumber(latestNum - i);
                        if (block == null) continue;

                        foreach (var tx in block.Transactions)
                        {
                            if (tx.Type == TransactionType.ContractDeploy && tx.To == contractAddr)
                            {
                                deployer = tx.Sender.ToHexString();
                                deployTxHash = tx.Hash.ToHexString();
                                deployBlockNumber = block.Number;
                                break;
                            }
                        }

                        if (deployer != null) break;
                    }

                    return Microsoft.AspNetCore.Http.Results.Ok(new ContractInfoResponse
                    {
                        Address = contractAddr.ToHexString(),
                        CodeSize = code.Length,
                        CodeHash = codeHashHex,
                        Deployer = deployer,
                        DeployTxHash = deployTxHash,
                        DeployBlockNumber = deployBlockNumber,
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

            // GET /v1/contracts/{address}/storage?key={stringKey} — read storage with server-side BLAKE3 hashing
            app.MapGet("/v1/contracts/{address}/storage", (string address, string? key) =>
            {
                try
                {
                    if (string.IsNullOrEmpty(key))
                        return Microsoft.AspNetCore.Http.Results.BadRequest(new ErrorResponse
                        {
                            Code = 400,
                            Message = "Missing 'key' query parameter.",
                        });

                    if (!Address.TryFromHexString(address, out var contractAddr))
                        return Microsoft.AspNetCore.Http.Results.BadRequest(new ErrorResponse
                        {
                            Code = 400,
                            Message = "Invalid address format.",
                        });

                    var account = stateDb.GetAccount(contractAddr);
                    if (account == null || account.Value.AccountType is not (Storage.AccountType.Contract or Storage.AccountType.SystemContract))
                        return Microsoft.AspNetCore.Http.Results.NotFound();

                    // Load contract code from storage (0xFF01 key)
                    Span<byte> codeKeySpan = stackalloc byte[32];
                    codeKeySpan.Clear();
                    codeKeySpan[0] = 0xFF;
                    codeKeySpan[1] = 0x01;
                    var codeStorageKey = new Hash256(codeKeySpan);
                    var code = stateDb.GetStorage(contractAddr, codeStorageKey) ?? [];

                    if (code.Length == 0)
                        return Microsoft.AspNetCore.Http.Results.BadRequest(new ErrorResponse
                        {
                            Code = 400,
                            Message = "Contract has no code.",
                        });

                    // BLAKE3-hash the key string to get a 32-byte storage key
                    var keyHash = Blake3Hasher.Hash(Encoding.UTF8.GetBytes(key));

                    // Build storage_get call data: [4-byte selector][32-byte key hash]
                    var storageGetSelector = ManagedContractRuntime.ComputeSelector("storage_get");
                    var callData = new byte[4 + 32];
                    storageGetSelector.CopyTo(callData, 0);
                    keyHash.WriteTo(callData.AsSpan(4));

                    // Create execution context for read-only call
                    var latestBlock = chainManager.LatestBlock;
                    var gasMeter = new GasMeter(100_000);

                    var ctx = new VmExecutionContext
                    {
                        Caller = Address.Zero,
                        ContractAddress = contractAddr,
                        Value = UInt256.Zero,
                        BlockTimestamp = latestBlock != null ? (ulong)latestBlock.Header.Timestamp : 0,
                        BlockNumber = latestBlock?.Number ?? 0,
                        BlockProposer = latestBlock?.Header.Proposer ?? Address.Zero,
                        ChainId = latestBlock?.Header.ChainId ?? 1,
                        GasMeter = gasMeter,
                        StateDb = stateDb,
                        CallDepth = 0,
                    };

                    var result = contractRuntime.Execute(code, callData, ctx);

                    string? valueHex = null;
                    string? valueUtf8 = null;
                    var valueSize = 0;
                    var found = false;

                    if (result.Success && result.ReturnData is { Length: > 0 })
                    {
                        found = true;
                        valueHex = Convert.ToHexString(result.ReturnData);
                        valueSize = result.ReturnData.Length;

                        // Try to decode as UTF-8 (return data is length-prefixed: [4-byte BE length][raw bytes])
                        try
                        {
                            if (result.ReturnData.Length >= 4)
                            {
                                var len = (result.ReturnData[0] << 24) | (result.ReturnData[1] << 16) |
                                          (result.ReturnData[2] << 8) | result.ReturnData[3];
                                if (len > 0 && 4 + len <= result.ReturnData.Length)
                                    valueUtf8 = Encoding.UTF8.GetString(result.ReturnData, 4, len);
                            }
                        }
                        catch { /* Not valid UTF-8, leave null */ }
                    }

                    return Microsoft.AspNetCore.Http.Results.Ok(new StorageReadResponse
                    {
                        Key = key,
                        KeyHash = keyHash.ToHexString(),
                        Found = found,
                        ValueHex = valueHex,
                        ValueUtf8 = valueUtf8,
                        ValueSize = valueSize,
                        GasUsed = gasMeter.GasUsed,
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
        }

        // GET /v1/pools — list all staking pools from the StakingPool contract
        app.MapGet("/v1/pools", () =>
        {
            var contractAddr = new Address(new byte[]
            {
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0x10, 0x05,
            });

            // Read _nextPoolId (key "sp_next")
            var nextIdRaw = stateDb.GetStorage(contractAddr, Blake3Hasher.Hash(Encoding.UTF8.GetBytes("sp_next")));
            ulong poolCount = 0;
            if (nextIdRaw is { Length: >= 9 } && nextIdRaw[0] == 0x01)
                poolCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(nextIdRaw.AsSpan(1));

            var pools = new List<PoolInfoResponse>();
            for (ulong i = 0; i < poolCount; i++)
            {
                var idStr = i.ToString();

                // Operator (key "sp_ops:{id}", tag 0x07 = string)
                var opsRaw = stateDb.GetStorage(contractAddr, Blake3Hasher.Hash(Encoding.UTF8.GetBytes("sp_ops:" + idStr)));
                var operatorHex = opsRaw is { Length: > 1 } && opsRaw[0] == 0x07
                    ? Encoding.UTF8.GetString(opsRaw.AsSpan(1))
                    : "";

                // Total stake (key "sp_total:{id}", tag 0x0A = UInt256)
                var stakeRaw = stateDb.GetStorage(contractAddr, Blake3Hasher.Hash(Encoding.UTF8.GetBytes("sp_total:" + idStr)));
                var totalStake = stakeRaw is { Length: >= 33 } && stakeRaw[0] == 0x0A
                    ? new UInt256(stakeRaw.AsSpan(1, 32)).ToString()
                    : "0";

                // Total rewards (key "sp_rewards:{id}", tag 0x0A = UInt256)
                var rewardsRaw = stateDb.GetStorage(contractAddr, Blake3Hasher.Hash(Encoding.UTF8.GetBytes("sp_rewards:" + idStr)));
                var totalRewards = rewardsRaw is { Length: >= 33 } && rewardsRaw[0] == 0x0A
                    ? new UInt256(rewardsRaw.AsSpan(1, 32)).ToString()
                    : "0";

                pools.Add(new PoolInfoResponse
                {
                    PoolId = i,
                    Operator = operatorHex,
                    TotalStake = totalStake,
                    TotalRewards = totalRewards,
                });
            }

            return Microsoft.AspNetCore.Http.Results.Ok(pools.ToArray());
        });

        // GET /v1/debug/mempool — diagnostic: show mempool txs with validation results
        app.MapGet("/v1/debug/mempool", () =>
        {
            var pending = mempool.GetPending(100);
            var currentBaseFee = GetCurrentBaseFee();
            var results = pending.Select(tx =>
            {
                var validation = validator.Validate(tx, stateDb, currentBaseFee);
                var senderAccount = stateDb.GetAccount(tx.Sender);
                return new
                {
                    hash = tx.Hash.ToHexString(),
                    type = tx.Type.ToString(),
                    nonce = tx.Nonce,
                    sender = tx.Sender.ToHexString(),
                    to = tx.To.ToHexString(),
                    value = tx.Value.ToString(),
                    gasLimit = tx.GasLimit,
                    gasPrice = tx.GasPrice.ToString(),
                    chainId = tx.ChainId,
                    signatureValid = tx.VerifySignature(),
                    validationOk = validation.IsSuccess,
                    validationError = validation.IsSuccess ? null : validation.Message,
                    senderExists = senderAccount.HasValue,
                    senderNonce = senderAccount?.Nonce ?? 0,
                    senderBalance = senderAccount?.Balance.ToString() ?? "0",
                };
            }).ToArray();

            return Microsoft.AspNetCore.Http.Results.Ok(new { count = pending.Count, transactions = results });
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
    [JsonPropertyName("maxFeePerGas")] public string? MaxFeePerGas { get; set; }
    [JsonPropertyName("maxPriorityFeePerGas")] public string? MaxPriorityFeePerGas { get; set; }
    [JsonPropertyName("data")] public string? Data { get; set; }
    [JsonPropertyName("priority")] public byte Priority { get; set; }
    [JsonPropertyName("chainId")] public uint ChainId { get; set; }
    [JsonPropertyName("signature")] public string Signature { get; set; } = "";
    [JsonPropertyName("senderPublicKey")] public string SenderPublicKey { get; set; } = "";
    [JsonPropertyName("complianceProofs")] public ComplianceProofDto[]? ComplianceProofs { get; set; }

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
            MaxFeePerGas = string.IsNullOrEmpty(MaxFeePerGas) ? UInt256.Zero : UInt256.Parse(MaxFeePerGas),
            MaxPriorityFeePerGas = string.IsNullOrEmpty(MaxPriorityFeePerGas) ? UInt256.Zero : UInt256.Parse(MaxPriorityFeePerGas),
            Data = string.IsNullOrEmpty(Data) ? [] : Convert.FromHexString(Data.StartsWith("0x") ? Data[2..] : Data),
            Priority = Priority,
            ChainId = ChainId,
            Signature = new Core.Signature(Convert.FromHexString(Signature.StartsWith("0x") ? Signature[2..] : Signature)),
            SenderPublicKey = new PublicKey(Convert.FromHexString(SenderPublicKey.StartsWith("0x") ? SenderPublicKey[2..] : SenderPublicKey)),
            ComplianceProofs = ComplianceProofs?.Select(p => p.ToComplianceProof()).ToArray() ?? [],
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
    [JsonPropertyName("baseFee")] public string BaseFee { get; set; } = "0";
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
            BaseFee = block.Header.BaseFee.ToString(),
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

public sealed class TransactionDetailResponse
{
    [JsonPropertyName("hash")] public string Hash { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("nonce")] public ulong Nonce { get; set; }
    [JsonPropertyName("sender")] public string Sender { get; set; } = "";
    [JsonPropertyName("to")] public string To { get; set; } = "";
    [JsonPropertyName("value")] public string Value { get; set; } = "0";
    [JsonPropertyName("gasLimit")] public ulong GasLimit { get; set; }
    [JsonPropertyName("gasPrice")] public string GasPrice { get; set; } = "0";
    [JsonPropertyName("maxFeePerGas")] public string? MaxFeePerGas { get; set; }
    [JsonPropertyName("maxPriorityFeePerGas")] public string? MaxPriorityFeePerGas { get; set; }
    [JsonPropertyName("priority")] public byte Priority { get; set; }
    [JsonPropertyName("blockNumber")] public ulong? BlockNumber { get; set; }
    [JsonPropertyName("blockHash")] public string? BlockHash { get; set; }
    [JsonPropertyName("transactionIndex")] public int? TransactionIndex { get; set; }
    [JsonPropertyName("data")] public string? Data { get; set; }
    [JsonPropertyName("dataSize")] public int DataSize { get; set; }
    [JsonPropertyName("complianceProofCount")] public int ComplianceProofCount { get; set; }
    // Receipt fields (populated when receipt is available)
    [JsonPropertyName("gasUsed")] public ulong? GasUsed { get; set; }
    [JsonPropertyName("success")] public bool? Success { get; set; }
    [JsonPropertyName("errorCode")] public string? ErrorCode { get; set; }
    [JsonPropertyName("effectiveGasPrice")] public string? EffectiveGasPrice { get; set; }
    [JsonPropertyName("logs")] public LogResponse[]? Logs { get; set; }

    public static TransactionDetailResponse FromTransaction(Transaction tx, Block? block = null, int? index = null, Storage.RocksDb.ReceiptData? receipt = null)
    {
        var response = new TransactionDetailResponse
        {
            Hash = tx.Hash.ToHexString(),
            Type = tx.Type.ToString(),
            Nonce = tx.Nonce,
            Sender = tx.Sender.ToHexString(),
            To = tx.To.ToHexString(),
            Value = tx.Value.ToString(),
            GasLimit = tx.GasLimit,
            GasPrice = tx.GasPrice.ToString(),
            MaxFeePerGas = tx.IsEip1559 ? tx.MaxFeePerGas.ToString() : null,
            MaxPriorityFeePerGas = tx.IsEip1559 ? tx.MaxPriorityFeePerGas.ToString() : null,
            Priority = tx.Priority,
            BlockNumber = block?.Number,
            BlockHash = block?.Hash.ToHexString(),
            TransactionIndex = index,
            Data = tx.Data.Length > 0 ? Convert.ToHexString(tx.Data) : null,
            DataSize = tx.Data.Length,
            ComplianceProofCount = tx.ComplianceProofs.Length,
        };

        if (receipt != null)
        {
            response.GasUsed = receipt.GasUsed;
            response.Success = receipt.Success;
            response.ErrorCode = ((BasaltErrorCode)receipt.ErrorCode).ToString();
            response.EffectiveGasPrice = receipt.EffectiveGasPrice.ToString();
            response.Logs = receipt.Logs.Select(l => new LogResponse
            {
                Contract = l.Contract.ToHexString(),
                EventSignature = l.EventSignature.ToHexString(),
                Topics = l.Topics.Select(t => t.ToHexString()).ToArray(),
                Data = l.Data.Length > 0 ? Convert.ToHexString(l.Data) : null,
            }).ToArray();
        }

        return response;
    }
}

public sealed class PaginatedBlocksResponse
{
    [JsonPropertyName("items")] public BlockResponse[] Items { get; set; } = [];
    [JsonPropertyName("page")] public int Page { get; set; }
    [JsonPropertyName("pageSize")] public int PageSize { get; set; }
    [JsonPropertyName("totalItems")] public long TotalItems { get; set; }
}

public sealed class ValidatorInfoResponse
{
    [JsonPropertyName("address")] public string Address { get; set; } = "";
    [JsonPropertyName("stake")] public string Stake { get; set; } = "0";
    [JsonPropertyName("selfStake")] public string SelfStake { get; set; } = "0";
    [JsonPropertyName("delegatedStake")] public string DelegatedStake { get; set; } = "0";
    [JsonPropertyName("publicKey")] public string PublicKey { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "active";
}

public sealed class PoolInfoResponse
{
    [JsonPropertyName("poolId")] public ulong PoolId { get; set; }
    [JsonPropertyName("operator")] public string Operator { get; set; } = "";
    [JsonPropertyName("totalStake")] public string TotalStake { get; set; } = "0";
    [JsonPropertyName("totalRewards")] public string TotalRewards { get; set; } = "0";
}

public sealed class CallRequest
{
    [JsonPropertyName("to")] public string To { get; set; } = "";
    [JsonPropertyName("data")] public string Data { get; set; } = "";
    [JsonPropertyName("from")] public string? From { get; set; }
    [JsonPropertyName("gasLimit")] public ulong GasLimit { get; set; } = 1_000_000;
}

public sealed class CallResponse
{
    [JsonPropertyName("success")] public required bool Success { get; set; }
    [JsonPropertyName("returnData")] public string? ReturnData { get; set; }
    [JsonPropertyName("gasUsed")] public ulong GasUsed { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public sealed class ContractInfoResponse
{
    [JsonPropertyName("address")] public string Address { get; set; } = "";
    [JsonPropertyName("codeSize")] public int CodeSize { get; set; }
    [JsonPropertyName("codeHash")] public string CodeHash { get; set; } = "";
    [JsonPropertyName("deployer")] public string? Deployer { get; set; }
    [JsonPropertyName("deployTxHash")] public string? DeployTxHash { get; set; }
    [JsonPropertyName("deployBlockNumber")] public ulong? DeployBlockNumber { get; set; }
}

public sealed class StorageReadResponse
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("keyHash")] public string KeyHash { get; set; } = "";
    [JsonPropertyName("found")] public bool Found { get; set; }
    [JsonPropertyName("valueHex")] public string? ValueHex { get; set; }
    [JsonPropertyName("valueUtf8")] public string? ValueUtf8 { get; set; }
    [JsonPropertyName("valueSize")] public int ValueSize { get; set; }
    [JsonPropertyName("gasUsed")] public ulong GasUsed { get; set; }
}

public sealed class ReceiptResponse
{
    [JsonPropertyName("transactionHash")] public string TransactionHash { get; set; } = "";
    [JsonPropertyName("blockHash")] public string BlockHash { get; set; } = "";
    [JsonPropertyName("blockNumber")] public ulong BlockNumber { get; set; }
    [JsonPropertyName("transactionIndex")] public int TransactionIndex { get; set; }
    [JsonPropertyName("from")] public string From { get; set; } = "";
    [JsonPropertyName("to")] public string To { get; set; } = "";
    [JsonPropertyName("gasUsed")] public ulong GasUsed { get; set; }
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("errorCode")] public string ErrorCode { get; set; } = "";
    [JsonPropertyName("postStateRoot")] public string PostStateRoot { get; set; } = "";
    [JsonPropertyName("effectiveGasPrice")] public string EffectiveGasPrice { get; set; } = "0";
    [JsonPropertyName("logs")] public LogResponse[] Logs { get; set; } = [];

    public static ReceiptResponse FromReceiptData(Storage.RocksDb.ReceiptData receipt)
    {
        return new ReceiptResponse
        {
            TransactionHash = receipt.TransactionHash.ToHexString(),
            BlockHash = receipt.BlockHash.ToHexString(),
            BlockNumber = receipt.BlockNumber,
            TransactionIndex = receipt.TransactionIndex,
            From = receipt.From.ToHexString(),
            To = receipt.To.ToHexString(),
            GasUsed = receipt.GasUsed,
            Success = receipt.Success,
            ErrorCode = ((BasaltErrorCode)receipt.ErrorCode).ToString(),
            PostStateRoot = receipt.PostStateRoot.ToHexString(),
            EffectiveGasPrice = receipt.EffectiveGasPrice.ToString(),
            Logs = receipt.Logs.Select(l => new LogResponse
            {
                Contract = l.Contract.ToHexString(),
                EventSignature = l.EventSignature.ToHexString(),
                Topics = l.Topics.Select(t => t.ToHexString()).ToArray(),
                Data = l.Data.Length > 0 ? Convert.ToHexString(l.Data) : null,
            }).ToArray(),
        };
    }
}

public sealed class LogResponse
{
    [JsonPropertyName("contract")] public string Contract { get; set; } = "";
    [JsonPropertyName("eventSignature")] public string EventSignature { get; set; } = "";
    [JsonPropertyName("topics")] public string[] Topics { get; set; } = [];
    [JsonPropertyName("data")] public string? Data { get; set; }
}

public sealed class ComplianceProofDto
{
    [JsonPropertyName("schemaId")] public string SchemaId { get; set; } = "";
    [JsonPropertyName("proof")] public string Proof { get; set; } = "";
    [JsonPropertyName("publicInputs")] public string PublicInputs { get; set; } = "";
    [JsonPropertyName("nullifier")] public string Nullifier { get; set; } = "";

    public ComplianceProof ToComplianceProof()
    {
        return new ComplianceProof
        {
            SchemaId = Hash256.FromHexString(SchemaId),
            Proof = Convert.FromHexString(Proof.StartsWith("0x") ? Proof[2..] : Proof),
            PublicInputs = Convert.FromHexString(PublicInputs.StartsWith("0x") ? PublicInputs[2..] : PublicInputs),
            Nullifier = Hash256.FromHexString(Nullifier),
        };
    }
}

[JsonSerializable(typeof(TransactionRequest))]
[JsonSerializable(typeof(TransactionResponse))]
[JsonSerializable(typeof(BlockResponse))]
[JsonSerializable(typeof(AccountResponse))]
[JsonSerializable(typeof(StatusResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(FaucetRequest))]
[JsonSerializable(typeof(FaucetResponse))]
[JsonSerializable(typeof(TransactionDetailResponse))]
[JsonSerializable(typeof(TransactionDetailResponse[]))]
[JsonSerializable(typeof(PaginatedBlocksResponse))]
[JsonSerializable(typeof(ValidatorInfoResponse))]
[JsonSerializable(typeof(ValidatorInfoResponse[]))]
[JsonSerializable(typeof(PoolInfoResponse))]
[JsonSerializable(typeof(PoolInfoResponse[]))]
[JsonSerializable(typeof(CallRequest))]
[JsonSerializable(typeof(CallResponse))]
[JsonSerializable(typeof(ContractInfoResponse))]
[JsonSerializable(typeof(StorageReadResponse))]
[JsonSerializable(typeof(ReceiptResponse))]
[JsonSerializable(typeof(LogResponse))]
[JsonSerializable(typeof(LogResponse[]))]
[JsonSerializable(typeof(ComplianceProofDto))]
[JsonSerializable(typeof(ComplianceProofDto[]))]
public partial class BasaltApiJsonContext : JsonSerializerContext;
