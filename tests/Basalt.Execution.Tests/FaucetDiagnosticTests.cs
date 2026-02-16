using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution;
using Basalt.Storage;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Basalt.Execution.Tests;

/// <summary>
/// Diagnostic tests to reproduce the faucet stuck-transaction bug on testnet.
/// The faucet creates signed transactions but they never get included in blocks.
/// </summary>
public class FaucetDiagnosticTests
{
    private readonly ITestOutputHelper _output;

    // Exact testnet configuration
    private readonly ChainParameters _chainParams = ChainParameters.FromConfiguration(4242, "basalt-testnet");

    // Faucet private key: all zeros except last byte = 0xFF
    private readonly byte[] _faucetPrivateKey;

    public FaucetDiagnosticTests(ITestOutputHelper output)
    {
        _output = output;
        _faucetPrivateKey = new byte[32];
        _faucetPrivateKey[31] = 0xFF;
    }

    [Fact]
    public void FaucetDiagnostic_ReproduceStuckTransaction()
    {
        // === Step 1: Derive the faucet address from its private key ===
        var faucetPublicKey = Ed25519Signer.GetPublicKey(_faucetPrivateKey);
        var derivedFaucetAddress = Ed25519Signer.DeriveAddress(faucetPublicKey);

        _output.WriteLine($"Derived faucet address: {derivedFaucetAddress.ToHexString()}");
        _output.WriteLine($"Expected faucet address: 0xac8cb7ae7bd407750fcb5f1f00b65be326d5d4a3");

        // Check if the derived address matches the on-chain faucet address
        var expectedAddress = Address.FromHexString("0xac8cb7ae7bd407750fcb5f1f00b65be326d5d4a3");
        var addressMatch = derivedFaucetAddress == expectedAddress;
        _output.WriteLine($"Address match: {addressMatch}");

        if (!addressMatch)
        {
            _output.WriteLine("BUG FOUND: Faucet private key derives to a DIFFERENT address than the funded account!");
            _output.WriteLine("The faucet is signing transactions with the wrong sender address,");
            _output.WriteLine("or the genesis funded a different address than what the faucet key derives to.");
        }

        // === Step 2: Set up the state DB matching testnet ===
        var stateDb = new InMemoryStateDb();

        // Set up the on-chain faucet account at the expected address (nonce=1, ~500B BSLT)
        var faucetBalance = UInt256.Parse("499999899999999999999979000");
        stateDb.SetAccount(expectedAddress, new AccountState
        {
            Nonce = 1,
            Balance = faucetBalance,
            AccountType = AccountType.ExternallyOwned,
        });

        // Also set up the derived address if different, to test both scenarios
        if (!addressMatch)
        {
            stateDb.SetAccount(derivedFaucetAddress, new AccountState
            {
                Nonce = 0,
                Balance = UInt256.Zero,
                AccountType = AccountType.ExternallyOwned,
            });
        }

        // === Step 3: Create a faucet transaction exactly as FaucetEndpoint does ===
        var recipientAddress = Address.FromHexString("0x0000000000000000000000000000000000000001");
        var dripAmount = UInt256.Parse("100000000000000000000"); // 100 BSLT

        // The faucet reads the on-chain nonce from the expected address
        var faucetAccount = stateDb.GetAccount(expectedAddress);
        var onChainNonce = faucetAccount!.Value.Nonce;
        _output.WriteLine($"On-chain nonce for expected address: {onChainNonce}");

        // The faucet uses the DERIVED address as the sender (from Ed25519Signer.DeriveAddress)
        var unsignedTx = new Transaction
        {
            Type = TransactionType.Transfer,
            Nonce = onChainNonce, // nonce = 1 from chain state
            Sender = derivedFaucetAddress, // FaucetEndpoint uses derived address
            To = recipientAddress,
            Value = dripAmount,
            GasLimit = _chainParams.TransferGasCost,
            GasPrice = _chainParams.MinGasPrice,
            Data = [],
            Priority = 0,
            ChainId = _chainParams.ChainId,
        };

        var signedTx = Transaction.Sign(unsignedTx, _faucetPrivateKey);

        _output.WriteLine($"Transaction sender: {signedTx.Sender.ToHexString()}");
        _output.WriteLine($"Transaction nonce: {signedTx.Nonce}");
        _output.WriteLine($"Transaction chainId: {signedTx.ChainId}");
        _output.WriteLine($"Transaction value: {signedTx.Value}");
        _output.WriteLine($"Transaction hash: {signedTx.Hash.ToHexString()}");
        _output.WriteLine($"Signature empty: {signedTx.Signature.IsEmpty}");
        _output.WriteLine($"PublicKey empty: {signedTx.SenderPublicKey.IsEmpty}");

        // === Step 4: Validate the transaction ===
        var validator = new TransactionValidator(_chainParams);
        var result = validator.Validate(signedTx, stateDb);

        _output.WriteLine($"Validation result: {result}");
        _output.WriteLine($"Validation success: {result.IsSuccess}");
        _output.WriteLine($"Validation error code: {result.ErrorCode}");
        _output.WriteLine($"Validation message: {result.Message}");

        // Log the specific checks
        _output.WriteLine("--- Diagnostic details ---");
        _output.WriteLine($"Chain params ChainId: {_chainParams.ChainId}");
        _output.WriteLine($"Chain params MinGasPrice: {_chainParams.MinGasPrice}");
        _output.WriteLine($"Chain params TransferGasCost: {_chainParams.TransferGasCost}");
        _output.WriteLine($"Chain params BlockGasLimit: {_chainParams.BlockGasLimit}");

        // Check signature verification independently
        var sigOk = signedTx.VerifySignature();
        _output.WriteLine($"Signature verification: {sigOk}");

        // Check sender address derivation
        var derivedFromTxPubkey = Ed25519Signer.DeriveAddress(signedTx.SenderPublicKey);
        _output.WriteLine($"Address derived from tx pubkey: {derivedFromTxPubkey.ToHexString()}");
        _output.WriteLine($"Address in tx.Sender: {signedTx.Sender.ToHexString()}");
        _output.WriteLine($"Sender matches derived: {derivedFromTxPubkey == signedTx.Sender}");

        // Check if the sender account exists in state
        var senderAccount = stateDb.GetAccount(signedTx.Sender);
        _output.WriteLine($"Sender account exists in stateDb: {senderAccount != null}");
        if (senderAccount != null)
        {
            _output.WriteLine($"Sender account nonce: {senderAccount.Value.Nonce}");
            _output.WriteLine($"Sender account balance: {senderAccount.Value.Balance}");
        }
        else
        {
            _output.WriteLine("BUG: Sender account does NOT exist in state DB!");
            _output.WriteLine("The faucet is signing from a derived address that has no balance on chain.");
        }

        // The test should fail if the transaction doesn't validate - this IS the bug
        if (!result.IsSuccess)
        {
            _output.WriteLine($"=== BUG CONFIRMED: Faucet transaction fails validation: {result} ===");
        }

        // We EXPECT the result to fail if there's a bug - assert that we found it
        // If the tx validates successfully, the bug is elsewhere (mempool, consensus, etc.)
        _output.WriteLine($"=== Test complete. Validation passed: {result.IsSuccess} ===");
    }

    [Fact]
    public void FaucetDiagnostic_BlockBuilderIncludesTx()
    {
        // === Set up identical to above ===
        var faucetPublicKey = Ed25519Signer.GetPublicKey(_faucetPrivateKey);
        var derivedFaucetAddress = Ed25519Signer.DeriveAddress(faucetPublicKey);
        var expectedAddress = Address.FromHexString("0xac8cb7ae7bd407750fcb5f1f00b65be326d5d4a3");

        _output.WriteLine($"Derived faucet address: {derivedFaucetAddress.ToHexString()}");
        _output.WriteLine($"Expected faucet address: {expectedAddress.ToHexString()}");
        _output.WriteLine($"Match: {derivedFaucetAddress == expectedAddress}");

        var stateDb = new InMemoryStateDb();
        var faucetBalance = UInt256.Parse("499999899999999999999979000");

        // Fund the DERIVED address (what the faucet actually signs from)
        stateDb.SetAccount(derivedFaucetAddress, new AccountState
        {
            Nonce = 1,
            Balance = faucetBalance,
            AccountType = AccountType.ExternallyOwned,
        });

        // Also fund the expected address
        stateDb.SetAccount(expectedAddress, new AccountState
        {
            Nonce = 1,
            Balance = faucetBalance,
            AccountType = AccountType.ExternallyOwned,
        });

        var recipientAddress = Address.FromHexString("0x0000000000000000000000000000000000000001");
        var dripAmount = UInt256.Parse("100000000000000000000");

        var unsignedTx = new Transaction
        {
            Type = TransactionType.Transfer,
            Nonce = 1, // on-chain nonce
            Sender = derivedFaucetAddress,
            To = recipientAddress,
            Value = dripAmount,
            GasLimit = _chainParams.TransferGasCost,
            GasPrice = _chainParams.MinGasPrice,
            Data = [],
            Priority = 0,
            ChainId = _chainParams.ChainId,
        };

        var signedTx = Transaction.Sign(unsignedTx, _faucetPrivateKey);

        // === Build a block with this transaction ===
        var parentHeader = new BlockHeader
        {
            Number = 0,
            ParentHash = Hash256.Zero,
            StateRoot = Hash256.Zero,
            TransactionsRoot = Hash256.Zero,
            ReceiptsRoot = Hash256.Zero,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Proposer = Address.Zero,
            ChainId = _chainParams.ChainId,
            GasUsed = 0,
            GasLimit = _chainParams.BlockGasLimit,
        };

        var blockBuilder = new BlockBuilder(_chainParams);
        var block = blockBuilder.BuildBlock(
            new List<Transaction> { signedTx },
            stateDb,
            parentHeader,
            Address.Zero);

        _output.WriteLine($"Block built with {block.Transactions.Count} transactions");
        _output.WriteLine($"Block number: {block.Number}");

        if (block.Transactions.Count == 0)
        {
            _output.WriteLine("BUG: BlockBuilder excluded the faucet transaction!");

            // Re-run validation to get the error
            var validator = new TransactionValidator(_chainParams);
            var result = validator.Validate(signedTx, stateDb);
            _output.WriteLine($"Validation result: {result}");
        }
        else
        {
            _output.WriteLine("Transaction was included in the block.");
            _output.WriteLine($"Receipt success: {block.Receipts?[0].Success}");
        }

        // Assert the tx was included
        block.Transactions.Count.Should().Be(1, "the faucet transaction should be included in the block");
    }

    [Fact]
    public void FaucetDiagnostic_AddressDerivationCheck()
    {
        // Verify what address the faucet private key actually derives to
        var faucetPublicKey = Ed25519Signer.GetPublicKey(_faucetPrivateKey);
        var derivedAddress = Ed25519Signer.DeriveAddress(faucetPublicKey);

        _output.WriteLine($"Faucet private key (hex): {Convert.ToHexString(_faucetPrivateKey).ToLowerInvariant()}");
        _output.WriteLine($"Faucet public key (hex): {Convert.ToHexString(faucetPublicKey.ToArray()).ToLowerInvariant()}");
        _output.WriteLine($"Derived address: {derivedAddress.ToHexString()}");
        _output.WriteLine($"Expected testnet address: 0xac8cb7ae7bd407750fcb5f1f00b65be326d5d4a3");

        var expectedAddress = Address.FromHexString("0xac8cb7ae7bd407750fcb5f1f00b65be326d5d4a3");
        var match = derivedAddress == expectedAddress;
        _output.WriteLine($"Addresses match: {match}");

        if (!match)
        {
            _output.WriteLine("");
            _output.WriteLine("ROOT CAUSE: The faucet private key [0x00...FF] derives to a different address");
            _output.WriteLine("than the address that was funded in genesis (0xac8cb7ae...).");
            _output.WriteLine("The faucet sends transactions FROM the derived address, but the funds");
            _output.WriteLine("are sitting at 0xac8cb7ae... which is unreachable with this key.");
            _output.WriteLine("");
            _output.WriteLine("Possible fixes:");
            _output.WriteLine("1. Update genesis to fund the DERIVED address instead");
            _output.WriteLine("2. Update the faucet to use the correct private key for 0xac8cb7ae...");
        }

        // This assertion will tell us definitively if address mismatch is the issue
        // We print both addresses regardless so we can see what happened
        derivedAddress.Should().Be(expectedAddress,
            "the faucet private key should derive to the same address that is funded on testnet");
    }
}
