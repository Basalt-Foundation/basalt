using Basalt.Core;
using Basalt.Execution;
using Basalt.Storage;

namespace Basalt.Api.GraphQL;

public class Mutation
{
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
        catch (Exception)
        {
            return new TransactionResult
            {
                Success = false,
                ErrorMessage = "Transaction submission failed",
            };
        }
    }
}
