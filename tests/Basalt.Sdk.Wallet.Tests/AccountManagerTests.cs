namespace Basalt.Sdk.Wallet.Tests;

using Xunit;
using FluentAssertions;
using Basalt.Sdk.Wallet.Accounts;
using Basalt.Core;
using Basalt.Execution;

public class AccountManagerTests
{
    [Fact]
    public void Add_SetsActiveAccount_WhenFirst()
    {
        using var manager = new AccountManager();
        using var account = Account.Create();

        manager.Add(account);

        manager.ActiveAccount.Should().BeSameAs(account);
    }

    [Fact]
    public void Add_DoesNotChangeActive_WhenNotFirst()
    {
        using var manager = new AccountManager();
        using var first = Account.Create();
        using var second = Account.Create();

        manager.Add(first);
        manager.Add(second);

        manager.ActiveAccount.Should().BeSameAs(first);
    }

    [Fact]
    public void SetActive_ChangesActiveAccount()
    {
        using var manager = new AccountManager();
        using var first = Account.Create();
        using var second = Account.Create();

        manager.Add(first);
        manager.Add(second);
        manager.SetActive(second.Address);

        manager.ActiveAccount.Should().BeSameAs(second);
    }

    [Fact]
    public void Get_ReturnsAccount_ByAddress()
    {
        using var manager = new AccountManager();
        using var account = Account.Create();

        manager.Add(account);

        manager.Get(account.Address).Should().BeSameAs(account);
    }

    [Fact]
    public void Get_ReturnsNull_ForUnknownAddress()
    {
        using var manager = new AccountManager();

        manager.Get(Address.Zero).Should().BeNull();
    }

    [Fact]
    public void Remove_RemovesAccount()
    {
        using var manager = new AccountManager();
        using var account = Account.Create();

        manager.Add(account);
        manager.Remove(account.Address);

        manager.Get(account.Address).Should().BeNull();
    }

    [Fact]
    public void Dispose_DisposesAllAccounts()
    {
        var account = Account.Create();
        var manager = new AccountManager();
        manager.Add(account);
        manager.Dispose();

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
