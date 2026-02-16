namespace Basalt.Sdk.Wallet.Tests;

using Xunit;
using FluentAssertions;
using Basalt.Sdk.Wallet.HdWallet;
using Basalt.Sdk.Wallet.Accounts;

public class HdWalletTests
{
    [Fact]
    public void Create_GeneratesMnemonic24Words()
    {
        using var wallet = HdWallet.Create();

        wallet.MnemonicPhrase.Should().NotBeNullOrWhiteSpace();
        wallet.MnemonicPhrase!.Split(' ').Should().HaveCount(24);
    }

    [Fact]
    public void Create_12Words()
    {
        using var wallet = HdWallet.Create(12);

        wallet.MnemonicPhrase.Should().NotBeNullOrWhiteSpace();
        wallet.MnemonicPhrase!.Split(' ').Should().HaveCount(12);
    }

    [Fact]
    public void FromMnemonic_RecoversSameAccounts()
    {
        using var original = HdWallet.Create();
        var mnemonic = original.MnemonicPhrase!;
        var originalAddress = original.GetAccount(0).Address;

        using var recovered = HdWallet.FromMnemonic(mnemonic);
        var recoveredAddress = recovered.GetAccount(0).Address;

        recoveredAddress.Should().Be(originalAddress);
    }

    [Fact]
    public void FromMnemonic_InvalidMnemonic_Throws()
    {
        var act = () => HdWallet.FromMnemonic("invalid words here that do not form a valid mnemonic phrase at all ever");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void GetAccount_DifferentIndices_ProduceDifferentAddresses()
    {
        using var wallet = HdWallet.Create();

        var account0 = wallet.GetAccount(0);
        var account1 = wallet.GetAccount(1);

        account0.Address.Should().NotBe(account1.Address);
    }

    [Fact]
    public void GetAccount_SameIndex_ReturnsCachedAccount()
    {
        using var wallet = HdWallet.Create();

        var first = wallet.GetAccount(0);
        var second = wallet.GetAccount(0);

        first.Should().BeSameAs(second);
    }

    [Fact]
    public void GetValidatorAccount_DerivesBothKeys()
    {
        using var wallet = HdWallet.Create();

        var validator = wallet.GetValidatorAccount(0);

        validator.BlsPublicKey.Should().NotBeNull();
        validator.BlsPublicKey.Should().HaveCountGreaterThan(0);
    }

    [Fact]
    public void FromSeed_64ByteSeed()
    {
        var seed = new byte[64];
        System.Security.Cryptography.RandomNumberGenerator.Fill(seed);

        using var wallet = HdWallet.FromSeed(seed);

        wallet.GetAccount(0).Address.Should().NotBe(Basalt.Core.Address.Zero);
    }

    [Fact]
    public void FromSeed_InvalidLength_Throws()
    {
        var shortSeed = new byte[32];

        var act = () => HdWallet.FromSeed(shortSeed);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Dispose_PreventsFurtherDerivation()
    {
        var wallet = HdWallet.Create();
        wallet.Dispose();

        var act = () => wallet.GetAccount(0);

        act.Should().Throw<ObjectDisposedException>();
    }
}

public class MnemonicTests
{
    [Fact]
    public void Mnemonic_Generate_ProducesValidMnemonic()
    {
        var mnemonic = Mnemonic.Generate();

        Mnemonic.Validate(mnemonic).Should().BeTrue();
    }

    [Fact]
    public void Mnemonic_Validate_InvalidChecksum_ReturnsFalse()
    {
        var mnemonic = Mnemonic.Generate();
        var words = mnemonic.Split(' ');
        words[0] = words[0] == "abandon" ? "ability" : "abandon";
        var modified = string.Join(' ', words);

        Mnemonic.Validate(modified).Should().BeFalse();
    }

    [Fact]
    public void Mnemonic_Validate_WrongWordCount_ReturnsFalse()
    {
        Mnemonic.Validate("hello world").Should().BeFalse();
    }

    [Fact]
    public void Mnemonic_ToSeed_Returns64Bytes()
    {
        var mnemonic = Mnemonic.Generate();

        var seed = Mnemonic.ToSeed(mnemonic);

        seed.Should().HaveCount(64);
    }

    [Fact]
    public void Mnemonic_ToSeed_DeterministicWithSameInputs()
    {
        var mnemonic = Mnemonic.Generate();

        var seed1 = Mnemonic.ToSeed(mnemonic);
        var seed2 = Mnemonic.ToSeed(mnemonic);

        seed1.Should().BeEquivalentTo(seed2);
    }
}

public class DerivationPathTests
{
    [Fact]
    public void DerivationPath_Basalt_ProducesCorrectPath()
    {
        var path = DerivationPath.Basalt(0);

        path.Path.Should().Be("m/44'/9000'/0'/0'/0'");
    }

    [Fact]
    public void DerivationPath_Parse_ValidPath()
    {
        var path = DerivationPath.Parse("m/44'/9000'/0'/0'/0'");

        path.Path.Should().Be("m/44'/9000'/0'/0'/0'");
        path.Indices.Should().HaveCount(5);
    }

    [Fact]
    public void DerivationPath_Parse_NonHardened_Throws()
    {
        var act = () => DerivationPath.Parse("m/44/9000'/0'/0'/0'");

        act.Should().Throw<FormatException>();
    }
}
