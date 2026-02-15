using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests;

public class TransactionValidatorTests
{
    private readonly ChainParameters _chainParams = ChainParameters.Devnet;
    private readonly TransactionValidator _validator;

    public TransactionValidatorTests()
    {
        _validator = new TransactionValidator(_chainParams);
    }

    private (byte[] PrivateKey, PublicKey PublicKey, Address Address) CreateKeyPair()
    {
        var (privateKey, publicKey) = Ed25519Signer.GenerateKeyPair();
        var address = Ed25519Signer.DeriveAddress(publicKey);
        return (privateKey, publicKey, address);
    }

    private InMemoryStateDb CreateStateDbWithAccount(Address address, UInt256 balance, ulong nonce = 0)
    {
        var stateDb = new InMemoryStateDb();
        stateDb.SetAccount(address, new AccountState
        {
            Balance = balance,
            Nonce = nonce,
            AccountType = AccountType.ExternallyOwned,
        });
        return stateDb;
    }

    private Transaction CreateSignedTransaction(
        byte[] privateKey,
        Address sender,
        ulong nonce = 0,
        UInt256? value = null,
        ulong gasLimit = 21_000,
        UInt256? gasPrice = null,
        uint? chainId = null,
        byte[]? data = null)
    {
        return Transaction.Sign(new Transaction
        {
            Type = TransactionType.Transfer,
            Nonce = nonce,
            Sender = sender,
            To = Address.Zero,
            Value = value ?? new UInt256(100),
            GasLimit = gasLimit,
            GasPrice = gasPrice ?? new UInt256(1),
            ChainId = chainId ?? _chainParams.ChainId,
            Data = data ?? [],
        }, privateKey);
    }

    [Fact]
    public void Validate_ValidTransaction_Succeeds()
    {
        var (privateKey, _, sender) = CreateKeyPair();
        var stateDb = CreateStateDbWithAccount(sender, new UInt256(1_000_000));

        var tx = CreateSignedTransaction(privateKey, sender);

        var result = _validator.Validate(tx, stateDb);

        result.IsSuccess.Should().BeTrue();
        result.ErrorCode.Should().Be(BasaltErrorCode.Success);
    }

    [Fact]
    public void Validate_InvalidSignature_Fails()
    {
        var (privateKey, _, sender) = CreateKeyPair();
        var stateDb = CreateStateDbWithAccount(sender, new UInt256(1_000_000));

        // Create a transaction with a tampered signature
        var tx = CreateSignedTransaction(privateKey, sender);

        // Create a new transaction object with an empty signature to simulate invalid sig
        var tamperedTx = new Transaction
        {
            Type = tx.Type,
            Nonce = tx.Nonce,
            Sender = tx.Sender,
            To = tx.To,
            Value = tx.Value,
            GasLimit = tx.GasLimit,
            GasPrice = tx.GasPrice,
            ChainId = tx.ChainId,
            Signature = new Signature(new byte[64]), // Invalid signature
            SenderPublicKey = tx.SenderPublicKey,
        };

        var result = _validator.Validate(tamperedTx, stateDb);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.InvalidSignature);
    }

    [Fact]
    public void Validate_MissingSenderPublicKey_Fails()
    {
        var (privateKey, _, sender) = CreateKeyPair();
        var stateDb = CreateStateDbWithAccount(sender, new UInt256(1_000_000));

        // Transaction without a public key / signature
        var tx = new Transaction
        {
            Type = TransactionType.Transfer,
            Nonce = 0,
            Sender = sender,
            To = Address.Zero,
            Value = new UInt256(100),
            GasLimit = 21_000,
            GasPrice = new UInt256(1),
            ChainId = _chainParams.ChainId,
        };

        var result = _validator.Validate(tx, stateDb);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.InvalidSignature);
    }

    [Fact]
    public void Validate_InsufficientBalance_Fails()
    {
        var (privateKey, _, sender) = CreateKeyPair();
        // Account with very low balance
        var stateDb = CreateStateDbWithAccount(sender, new UInt256(10));

        var tx = CreateSignedTransaction(privateKey, sender, value: new UInt256(1000));

        var result = _validator.Validate(tx, stateDb);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.InsufficientBalance);
    }

    [Fact]
    public void Validate_InsufficientBalance_IncludesGasCost()
    {
        var (privateKey, _, sender) = CreateKeyPair();
        // Balance is enough for value (100) but not for value + gas (100 + 21_000 * 1 = 21_100)
        var stateDb = CreateStateDbWithAccount(sender, new UInt256(500));

        var tx = CreateSignedTransaction(privateKey, sender, value: new UInt256(100));

        var result = _validator.Validate(tx, stateDb);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.InsufficientBalance);
    }

    [Fact]
    public void Validate_NonceTooLow_Fails()
    {
        var (privateKey, _, sender) = CreateKeyPair();
        // Account nonce is 5
        var stateDb = CreateStateDbWithAccount(sender, new UInt256(1_000_000), nonce: 5);

        // Transaction uses nonce 3 (too low)
        var tx = CreateSignedTransaction(privateKey, sender, nonce: 3);

        var result = _validator.Validate(tx, stateDb);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.InvalidNonce);
    }

    [Fact]
    public void Validate_NonceTooHigh_Fails()
    {
        var (privateKey, _, sender) = CreateKeyPair();
        // Account nonce is 5
        var stateDb = CreateStateDbWithAccount(sender, new UInt256(1_000_000), nonce: 5);

        // Transaction uses nonce 10 (too high)
        var tx = CreateSignedTransaction(privateKey, sender, nonce: 10);

        var result = _validator.Validate(tx, stateDb);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.InvalidNonce);
    }

    [Fact]
    public void Validate_GasLimitExceedsBlockGasLimit_Fails()
    {
        var (privateKey, _, sender) = CreateKeyPair();
        var stateDb = CreateStateDbWithAccount(sender, UInt256.Parse("999999999999999999999999999"));

        // Gas limit exceeds block gas limit
        var tx = CreateSignedTransaction(privateKey, sender, gasLimit: _chainParams.BlockGasLimit + 1);

        var result = _validator.Validate(tx, stateDb);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.GasLimitExceeded);
    }

    [Fact]
    public void Validate_GasPriceBelowMinimum_Fails()
    {
        var (privateKey, _, sender) = CreateKeyPair();
        var stateDb = CreateStateDbWithAccount(sender, new UInt256(1_000_000));

        // Gas price of 0, which is below MinGasPrice (1)
        var tx = CreateSignedTransaction(privateKey, sender, gasPrice: UInt256.Zero);

        var result = _validator.Validate(tx, stateDb);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.InsufficientGas);
    }

    [Fact]
    public void Validate_InvalidChainId_Fails()
    {
        var (privateKey, _, sender) = CreateKeyPair();
        var stateDb = CreateStateDbWithAccount(sender, new UInt256(1_000_000));

        // Use a different chain ID
        var tx = CreateSignedTransaction(privateKey, sender, chainId: 999);

        var result = _validator.Validate(tx, stateDb);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.InvalidChainId);
    }

    [Fact]
    public void Validate_DataTooLarge_Fails()
    {
        var (privateKey, _, sender) = CreateKeyPair();
        var stateDb = CreateStateDbWithAccount(sender, UInt256.Parse("999999999999999999999999999"));

        // Create data larger than MaxTransactionDataBytes (128 KB)
        var largeData = new byte[_chainParams.MaxTransactionDataBytes + 1];

        var tx = CreateSignedTransaction(privateKey, sender, data: largeData);

        var result = _validator.Validate(tx, stateDb);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.DataTooLarge);
    }

    [Fact]
    public void Validate_NonExistentAccount_NonceMustBeZero()
    {
        var (privateKey, _, sender) = CreateKeyPair();
        // Do NOT add the account to the state DB
        var stateDb = new InMemoryStateDb();

        // Transaction with nonce=1 should fail (account doesn't exist, expected nonce is 0)
        var tx = CreateSignedTransaction(privateKey, sender, nonce: 1);

        var result = _validator.Validate(tx, stateDb);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.InvalidNonce);
    }

    [Fact]
    public void Validate_NonExistentAccount_InsufficientBalance()
    {
        var (privateKey, _, sender) = CreateKeyPair();
        // Do NOT add the account to the state DB (balance = 0)
        var stateDb = new InMemoryStateDb();

        var tx = CreateSignedTransaction(privateKey, sender, nonce: 0, value: new UInt256(100));

        var result = _validator.Validate(tx, stateDb);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.InsufficientBalance);
    }

    [Fact]
    public void Validate_ExactBalance_Succeeds()
    {
        var (privateKey, _, sender) = CreateKeyPair();
        // Exact balance for value (100) + gas (21_000 * 1) = 21_100
        var stateDb = CreateStateDbWithAccount(sender, new UInt256(21_100));

        var tx = CreateSignedTransaction(privateKey, sender, value: new UInt256(100), gasPrice: new UInt256(1));

        var result = _validator.Validate(tx, stateDb);

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Validate_SenderMismatchWithPublicKey_Fails()
    {
        var (privateKey, publicKey, realSender) = CreateKeyPair();
        var (_, _, fakeSender) = CreateKeyPair();

        var stateDb = CreateStateDbWithAccount(fakeSender, new UInt256(1_000_000));

        // Sign with privateKey (which derives to realSender), but set sender to fakeSender
        var unsignedTx = new Transaction
        {
            Type = TransactionType.Transfer,
            Nonce = 0,
            Sender = fakeSender,
            To = Address.Zero,
            Value = new UInt256(100),
            GasLimit = 21_000,
            GasPrice = new UInt256(1),
            ChainId = _chainParams.ChainId,
        };

        var signedTx = Transaction.Sign(unsignedTx, privateKey);

        // The signature will verify against the payload, but the derived address from the
        // public key won't match the declared sender
        var result = _validator.Validate(signedTx, stateDb);

        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be(BasaltErrorCode.InvalidSignature);
    }
}
