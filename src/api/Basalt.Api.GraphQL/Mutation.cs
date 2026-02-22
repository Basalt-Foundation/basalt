using Basalt.Core;
using Basalt.Execution;
using Basalt.Storage;
using Microsoft.Extensions.Logging;

namespace Basalt.Api.GraphQL;

public class Mutation
{
    private readonly ILogger<Mutation> _logger;

    public Mutation(ILogger<Mutation> logger)
    {
        _logger = logger;
    }

    public TransactionResult SubmitTransaction(
        TransactionInput input,
        [Service] ChainManager chainManager,
        [Service] Mempool mempool,
        [Service] TransactionValidator validator,
        [Service] IStateDatabase stateDb)
    {
        try
        {
            var tx = input.ToTransaction();
            var baseFee = chainManager.LatestBlock?.Header.BaseFee ?? UInt256.Zero;
            var validationResult = validator.Validate(tx, stateDb, baseFee);
            if (!validationResult.IsSuccess)
            {
                return new TransactionResult
                {
                    Success = false,
                    ErrorMessage = validationResult.Message ?? validationResult.ErrorCode.ToString(),
                };
            }

            if (!mempool.Add(tx))
            {
                return new TransactionResult
                {
                    Success = false,
                    ErrorMessage = "Transaction already in mempool or mempool is full.",
                };
            }

            return new TransactionResult
            {
                Success = true,
                Hash = tx.Hash.ToHexString(),
                Status = "pending",
            };
        }
        catch (Exception ex)
        {
            // R3-NEW-6: Log the exception server-side for diagnostics
            _logger.LogWarning(ex, "GraphQL SubmitTransaction failed");
            // NEW-7: Don't leak exception type name to clients
            return new TransactionResult
            {
                Success = false,
                ErrorMessage = "Transaction submission failed",
            };
        }
    }
}
