namespace Basalt.Core;

/// <summary>
/// Standardized error codes used throughout the Basalt blockchain.
/// </summary>
public enum BasaltErrorCode
{
    // Success
    Success = 0,

    // Transaction validation errors (1xxx)
    InvalidSignature = 1001,
    InvalidNonce = 1002,
    InsufficientBalance = 1003,
    InsufficientGas = 1004,
    GasLimitExceeded = 1005,
    DataTooLarge = 1006,
    InvalidChainId = 1007,
    InvalidTransactionType = 1008,
    DuplicateTransaction = 1009,
    TransactionExpired = 1010,

    // Block errors (2xxx)
    InvalidBlockHash = 2001,
    InvalidParentHash = 2002,
    InvalidStateRoot = 2003,
    InvalidBlockNumber = 2004,
    InvalidTimestamp = 2005,
    BlockTooLarge = 2006,
    InvalidProposer = 2007,
    MissingTransactions = 2008,

    // Consensus errors (3xxx)
    InvalidVote = 3001,
    InvalidProposal = 3002,
    ViewChangeTimeout = 3003,
    InvalidBLSSignature = 3004,
    InsufficientVotes = 3005,
    InvalidValidatorSet = 3006,

    // Execution errors (4xxx)
    ContractNotFound = 4001,
    OutOfGas = 4002,
    StackOverflow = 4003,
    InvalidOpcode = 4004,
    RevertCalled = 4005,
    ContractDeployFailed = 4006,
    MemoryLimitExceeded = 4007,
    CpuTimeLimitExceeded = 4008,
    StorageLimitExceeded = 4009,
    ReentrancyDetected = 4010,
    ContractCallFailed = 4011,
    ContractReverted = 4012,

    // Storage errors (5xxx)
    StorageReadFailed = 5001,
    StorageWriteFailed = 5002,
    InvalidMerkleProof = 5003,
    StateRootMismatch = 5004,
    DatabaseCorrupted = 5005,

    // Network errors (6xxx)
    PeerNotFound = 6001,
    ConnectionFailed = 6002,
    MessageTooLarge = 6003,
    InvalidProtocolVersion = 6004,
    PeerBanned = 6005,

    // Compliance errors (7xxx)
    KycRequired = 7001,
    SanctionedAddress = 7002,
    TransferRestricted = 7003,
    HoldingLimitExceeded = 7004,
    LockupPeriodActive = 7005,
    GeoRestricted = 7006,
    AttestationExpired = 7007,
    ComplianceProofInvalid = 7008,
    ComplianceProofMissing = 7009,
    NullifierReplay = 7010,

    // Staking errors (8xxx)
    StakeBelowMinimum = 8001,
    ValidatorAlreadyRegistered = 8002,
    ValidatorNotRegistered = 8003,
    StakingNotAvailable = 8004,

    // DEX errors (10xxx)
    /// <summary>Pool does not exist for the given ID.</summary>
    DexPoolNotFound = 10001,
    /// <summary>Pool already exists for this token pair and fee tier.</summary>
    DexPoolAlreadyExists = 10002,
    /// <summary>Token pair is invalid (e.g. identical addresses).</summary>
    DexInvalidPair = 10003,
    /// <summary>Fee tier not in allowed set.</summary>
    DexInvalidFeeTier = 10004,
    /// <summary>Pool has insufficient liquidity for the operation.</summary>
    DexInsufficientLiquidity = 10005,
    /// <summary>Output amount is below the specified minimum (slippage protection).</summary>
    DexSlippageExceeded = 10006,
    /// <summary>Amount or price is invalid (e.g. zero).</summary>
    DexInvalidAmount = 10007,
    /// <summary>Limit order does not exist.</summary>
    DexOrderNotFound = 10008,
    /// <summary>Caller is not authorized for this operation.</summary>
    DexUnauthorized = 10009,
    /// <summary>Swap intent deadline has passed.</summary>
    DexDeadlineExpired = 10010,
    /// <summary>Transaction data is malformed for the specified DEX operation.</summary>
    DexInvalidData = 10011,
    /// <summary>Limit order has expired.</summary>
    DexOrderExpired = 10012,
    /// <summary>Insufficient LP token balance for transfer.</summary>
    DexInsufficientLpBalance = 10013,
    /// <summary>LP allowance is insufficient for transferFrom operation.</summary>
    DexInsufficientLpAllowance = 10014,
    /// <summary>Tick is out of the valid range.</summary>
    DexInvalidTick = 10015,
    /// <summary>Tick range is invalid (lower >= upper or not aligned to tick spacing).</summary>
    DexInvalidTickRange = 10016,
    /// <summary>Position does not exist.</summary>
    DexPositionNotFound = 10017,
    /// <summary>Not the owner of the position.</summary>
    DexPositionNotOwner = 10018,
    /// <summary>Encrypted intent decryption failed (malformed ciphertext or wrong epoch key).</summary>
    DexDecryptionFailed = 10019,
    /// <summary>Encrypted intent references an unknown or expired DKG epoch.</summary>
    DexInvalidEpoch = 10020,
    /// <summary>BST-20 token transfer failed during DEX operation.</summary>
    DexTransferFailed = 10021,
    /// <summary>Insufficient native token balance for DEX debit.</summary>
    DexInsufficientBalance = 10022,
    /// <summary>DEX is paused by admin — all DEX operations are rejected.</summary>
    DexPaused = 10023,
    /// <summary>Maximum pool creations per block reached.</summary>
    DexPoolCreationLimitReached = 10024,
    /// <summary>Sender is not the DEX admin.</summary>
    DexAdminUnauthorized = 10025,
    /// <summary>Invalid governance parameter ID.</summary>
    DexInvalidParameter = 10026,

    // Internal errors (9xxx)
    InternalError = 9001,
    NotImplemented = 9002,
    InvalidConfiguration = 9003,
}

/// <summary>
/// Result type for operations that can fail with a specific error code.
/// </summary>
public readonly struct BasaltResult
{
    public BasaltErrorCode ErrorCode { get; }
    public string? Message { get; }
    public bool IsSuccess => ErrorCode == BasaltErrorCode.Success;

    private BasaltResult(BasaltErrorCode errorCode, string? message)
    {
        ErrorCode = errorCode;
        Message = message;
    }

    public static readonly BasaltResult Ok = new(BasaltErrorCode.Success, null);
    public static BasaltResult Error(BasaltErrorCode code, string? message = null) => new(code, message);

    public override string ToString() => IsSuccess ? "Success" : $"Error({ErrorCode}): {Message}";
}

/// <summary>
/// Result type for operations that return a value or fail with an error.
/// Accessing <see cref="Value"/> on a failed result throws <see cref="InvalidOperationException"/>.
/// </summary>
public readonly struct BasaltResult<T>
{
    private readonly T? _value;

    /// <summary>
    /// The result value. Throws if <see cref="IsSuccess"/> is false.
    /// </summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException(
            $"Cannot access Value on a failed result. Error: {ErrorCode} - {Message}");

    public BasaltErrorCode ErrorCode { get; }
    public string? Message { get; }
    public bool IsSuccess => ErrorCode == BasaltErrorCode.Success;

    private BasaltResult(T? value, BasaltErrorCode errorCode, string? message)
    {
        _value = value;
        ErrorCode = errorCode;
        Message = message;
    }

    public static BasaltResult<T> Ok(T value) => new(value, BasaltErrorCode.Success, null);
    public static BasaltResult<T> Error(BasaltErrorCode code, string? message = null) => new(default, code, message);

    public override string ToString() => IsSuccess ? $"Ok({_value})" : $"Error({ErrorCode}): {Message}";
}

/// <summary>
/// Exception type for unrecoverable Basalt errors.
/// </summary>
public class BasaltException : Exception
{
    public BasaltErrorCode ErrorCode { get; }

    public BasaltException(BasaltErrorCode errorCode, string? message = null, Exception? innerException = null)
        : base(message ?? errorCode.ToString(), innerException)
    {
        ErrorCode = errorCode;
    }
}
