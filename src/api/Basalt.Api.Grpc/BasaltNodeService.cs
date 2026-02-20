using Basalt.Core;
using Basalt.Execution;
using Basalt.Storage;
using Grpc.Core;

namespace Basalt.Api.Grpc;

/// <summary>
/// gRPC service implementation mirroring the REST API.
/// </summary>
public sealed class BasaltNodeService : BasaltNode.BasaltNodeBase
{
    private readonly ChainManager _chainManager;
    private readonly Mempool _mempool;
    private readonly TransactionValidator _validator;
    private readonly IStateDatabase _stateDb;

    public BasaltNodeService(
        ChainManager chainManager,
        Mempool mempool,
        TransactionValidator validator,
        IStateDatabase stateDb)
    {
        _chainManager = chainManager;
        _mempool = mempool;
        _validator = validator;
        _stateDb = stateDb;
    }

    public override Task<StatusReply> GetStatus(GetStatusRequest request, ServerCallContext context)
    {
        var latest = _chainManager.LatestBlock;
        return Task.FromResult(new StatusReply
        {
            BlockHeight = latest?.Number ?? 0,
            LatestBlockHash = latest?.Hash.ToHexString() ?? Hash256.Zero.ToHexString(),
            MempoolSize = _mempool.Count,
            ProtocolVersion = 1,
        });
    }

    public override Task<BlockReply> GetBlock(GetBlockRequest request, ServerCallContext context)
    {
        Block? block = request.IdentifierCase switch
        {
            GetBlockRequest.IdentifierOneofCase.Number => _chainManager.GetBlockByNumber(request.Number),
            GetBlockRequest.IdentifierOneofCase.Hash when Hash256.TryFromHexString(request.Hash, out var hash)
                => _chainManager.GetBlockByHash(hash),
            _ => null,
        };

        if (block == null)
            throw new RpcException(new Status(StatusCode.NotFound, "Block not found"));

        return Task.FromResult(ToBlockReply(block));
    }

    public override Task<AccountReply> GetAccount(GetAccountRequest request, ServerCallContext context)
    {
        if (!Address.TryFromHexString(request.Address, out var addr))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid address format"));

        var account = _stateDb.GetAccount(addr);
        if (account == null)
            throw new RpcException(new Status(StatusCode.NotFound, "Account not found"));

        return Task.FromResult(new AccountReply
        {
            Address = addr.ToHexString(),
            Balance = account.Value.Balance.ToString(),
            Nonce = account.Value.Nonce,
            AccountType = account.Value.AccountType.ToString(),
        });
    }

    public override Task<TransactionReply> SubmitTransaction(SubmitTransactionRequest request, ServerCallContext context)
    {
        try
        {
            // MEDIUM-11: Validate TransactionType enum
            if (!Enum.IsDefined(typeof(TransactionType), (TransactionType)request.Type))
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid transaction type"));

            var tx = new Transaction
            {
                Type = (TransactionType)request.Type,
                Nonce = request.Nonce,
                Sender = Address.FromHexString(request.Sender),
                To = Address.FromHexString(request.To),
                Value = UInt256.Parse(request.Value),
                GasLimit = request.GasLimit,
                GasPrice = UInt256.Parse(request.GasPrice),
                Data = request.Data.ToByteArray(),
                Priority = (byte)request.Priority,
                ChainId = request.ChainId,
                Signature = new Signature(request.Signature.ToByteArray()),
                SenderPublicKey = new PublicKey(request.SenderPublicKey.ToByteArray()),
            };

            // MEDIUM-1: Pass current base fee to validation
            var baseFee = _chainManager.LatestBlock?.Header.BaseFee ?? UInt256.Zero;
            var validationResult = _validator.Validate(tx, _stateDb, baseFee);
            if (!validationResult.IsSuccess)
                throw new RpcException(new Status(StatusCode.InvalidArgument,
                    validationResult.Message ?? validationResult.ErrorCode.ToString()));

            if (!_mempool.Add(tx))
                throw new RpcException(new Status(StatusCode.AlreadyExists,
                    "Transaction already in mempool or mempool is full"));

            return Task.FromResult(new TransactionReply
            {
                Hash = tx.Hash.ToHexString(),
                Status = "pending",
            });
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new RpcException(new Status(StatusCode.Internal, "Transaction submission failed"));
        }
    }

    public override async Task SubscribeBlocks(
        SubscribeBlocksRequest request,
        IServerStreamWriter<BlockReply> responseStream,
        ServerCallContext context)
    {
        var lastSentBlock = _chainManager.LatestBlockNumber;

        // Send current latest block immediately
        var latest = _chainManager.LatestBlock;
        if (latest != null)
        {
            await responseStream.WriteAsync(ToBlockReply(latest));
        }

        // Poll for new blocks until the client disconnects
        while (!context.CancellationToken.IsCancellationRequested)
        {
            await Task.Delay(200, context.CancellationToken);

            var currentNumber = _chainManager.LatestBlockNumber;
            while (lastSentBlock < currentNumber)
            {
                lastSentBlock++;
                var block = _chainManager.GetBlockByNumber(lastSentBlock);
                if (block != null)
                {
                    await responseStream.WriteAsync(ToBlockReply(block));
                }
            }
        }
    }

    private static BlockReply ToBlockReply(Block block)
    {
        return new BlockReply
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
