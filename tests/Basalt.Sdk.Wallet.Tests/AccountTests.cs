namespace Basalt.Sdk.Wallet.Tests;

using Xunit;
using FluentAssertions;
using Basalt.Sdk.Wallet.Accounts;
using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution;

public class AccountTests
{
    [Fact]
    public void Create_GeneratesUniqueAccounts()
    {
        using var account1 = Account.Create();
        using var account2 = Account.Create();

        account1.Address.Should().NotBe(account2.Address);
    }

    [Fact]
    public void FromPrivateKey_Bytes_DerivesSameAddress()
    {
        var (privateKey, _) = Ed25519Signer.GenerateKeyPair();

        using var account1 = Account.FromPrivateKey(privateKey);
        using var account2 = Account.FromPrivateKey(privateKey);

        account1.Address.Should().Be(account2.Address);
        account1.PublicKey.Should().Be(account2.PublicKey);
    }

    [Fact]
    public void FromPrivateKey_Hex_WithPrefix()
    {
        var (privateKey, _) = Ed25519Signer.GenerateKeyPair();
        var hex = "0x" + Convert.ToHexString(privateKey).ToLowerInvariant();

        using var fromBytes = Account.FromPrivateKey(privateKey);
        using var fromHex = Account.FromPrivateKey(hex);

        fromHex.Address.Should().Be(fromBytes.Address);
        fromHex.PublicKey.Should().Be(fromBytes.PublicKey);
    }

    [Fact]
    public void FromPrivateKey_Hex_WithoutPrefix()
    {
        var (privateKey, _) = Ed25519Signer.GenerateKeyPair();
        var hex = Convert.ToHexString(privateKey).ToLowerInvariant();

        using var fromBytes = Account.FromPrivateKey(privateKey);
        using var fromHex = Account.FromPrivateKey(hex);

        fromHex.Address.Should().Be(fromBytes.Address);
        fromHex.PublicKey.Should().Be(fromBytes.PublicKey);
    }

    [Fact]
    public void FromPrivateKey_InvalidLength_Throws()
    {
        var shortKey = new byte[16];

        var act = () => Account.FromPrivateKey(shortKey);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FromPrivateKey_NullBytes_Throws()
    {
        var act = () => Account.FromPrivateKey((byte[])null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void SignTransaction_ProducesValidSignature()
    {
        using var account = Account.Create();

        var tx = new Transaction
        {
            Type = TransactionType.Transfer,
            Nonce = 1,
            Sender = account.Address,
            To = Address.Zero,
            Value = 1000UL,
            GasLimit = 21000,
            GasPrice = 1UL,
            ChainId = 1,
        };

        var signed = account.SignTransaction(tx);

        signed.Signature.IsEmpty.Should().BeFalse();
        signed.VerifySignature().Should().BeTrue();
    }

    [Fact]
    public void SignMessage_ProducesSignature()
    {
        using var account = Account.Create();
        var message = new byte[] { 1, 2, 3, 4, 5 };

        var signature = account.SignMessage(message);

        signature.Should().NotBe(Signature.Empty);
    }

    [Fact]
    public void Dispose_PreventsSigningAfterDispose()
    {
        var account = Account.Create();
        account.Dispose();

        var tx = new Transaction
        {
            Type = TransactionType.Transfer,
            Nonce = 0,
            Sender = Address.Zero,
            To = Address.Zero,
            Value = 0UL,
            GasLimit = 21000,
            GasPrice = 1UL,
            ChainId = 1,
        };

        var act = () => account.SignTransaction(tx);

        act.Should().Throw<ObjectDisposedException>();
    }
}
