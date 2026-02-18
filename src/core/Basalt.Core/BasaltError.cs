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

    // Staking errors (8xxx)
    StakeBelowMinimum = 8001,
    ValidatorAlreadyRegistered = 8002,
    ValidatorNotRegistered = 8003,
    StakingNotAvailable = 8004,

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
/// </summary>
public readonly struct BasaltResult<T>
{
    public T? Value { get; }
    public BasaltErrorCode ErrorCode { get; }
    public string? Message { get; }
    public bool IsSuccess => ErrorCode == BasaltErrorCode.Success;

    private BasaltResult(T? value, BasaltErrorCode errorCode, string? message)
    {
        Value = value;
        ErrorCode = errorCode;
        Message = message;
    }

    public static BasaltResult<T> Ok(T value) => new(value, BasaltErrorCode.Success, null);
    public static BasaltResult<T> Error(BasaltErrorCode code, string? message = null) => new(default, code, message);

    public override string ToString() => IsSuccess ? $"Ok({Value})" : $"Error({ErrorCode}): {Message}";
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
