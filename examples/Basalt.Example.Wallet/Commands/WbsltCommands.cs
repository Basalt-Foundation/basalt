using Basalt.Core;
using Basalt.Sdk.Wallet;
using Basalt.Sdk.Wallet.Accounts;
using Basalt.Sdk.Wallet.Contracts;
using Basalt.Sdk.Wallet.HdWallet;
using Spectre.Console;

namespace Basalt.Example.Wallet.Commands;

public static class WbsltCommands
{
    private static ContractClient GetContract(BasaltProvider provider) =>
        provider.GetContract(BasaltProvider.SystemContracts.WBSLT);

    public static async Task Deposit(BasaltProvider provider, IAccount account)
    {
        var weiAmount = Helpers.PromptBsltAmount("Amount of BSLT to wrap");

        var nativeBefore = await provider.GetBalanceAsync(account.Address);
        var wbsltBefore = await ReadBalance(provider, account.Address);

        var contract = GetContract(provider);
        var result = await contract.CallSdkAsync(
            account, "Deposit", gasLimit: 200_000, value: weiAmount);
        await Helpers.SubmitAndTrackAsync(provider, result, "WBSLT Deposit");

        var nativeAfter = await provider.GetBalanceAsync(account.Address);
        var wbsltAfter = await ReadBalance(provider, account.Address);

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Green);
        table.AddColumn("[bold]Asset[/]");
        table.AddColumn("[bold]Before[/]");
        table.AddColumn("[bold]After[/]");
        table.AddRow("BSLT", $"[dim]{Helpers.FormatBslt(nativeBefore)}[/]", $"[green]{Helpers.FormatBslt(nativeAfter)}[/]");
        table.AddRow("WBSLT", $"[dim]{wbsltBefore} wei[/]", $"[green]{wbsltAfter} wei[/]");
        AnsiConsole.Write(table);
    }

    public static async Task Withdraw(BasaltProvider provider, IAccount account)
    {
        var weiAmount = Helpers.PromptUInt64("Amount of WBSLT to unwrap (in wei)");

        var nativeBefore = await provider.GetBalanceAsync(account.Address);
        var wbsltBefore = await ReadBalance(provider, account.Address);

        var contract = GetContract(provider);
        var result = await contract.CallSdkAsync(
            account, "Withdraw", gasLimit: 200_000,
            args: [SdkContractEncoder.EncodeUInt64(weiAmount)]);
        await Helpers.SubmitAndTrackAsync(provider, result, "WBSLT Withdraw");

        var nativeAfter = await provider.GetBalanceAsync(account.Address);
        var wbsltAfter = await ReadBalance(provider, account.Address);

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Green);
        table.AddColumn("[bold]Asset[/]");
        table.AddColumn("[bold]Before[/]");
        table.AddColumn("[bold]After[/]");
        table.AddRow("BSLT", $"[dim]{Helpers.FormatBslt(nativeBefore)}[/]", $"[green]{Helpers.FormatBslt(nativeAfter)}[/]");
        table.AddRow("WBSLT", $"[dim]{wbsltBefore} wei[/]", $"[green]{wbsltAfter} wei[/]");
        AnsiConsole.Write(table);
    }

    public static async Task BalanceOf(BasaltProvider provider, HdWallet wallet)
    {
        var targetAddr = Helpers.PromptAddress("Address to query", wallet);
        var balance = await ReadBalance(provider, targetAddr);
        AnsiConsole.MarkupLine($"[green]WBSLT Balance:[/] {balance} wei");
    }

    private static async Task<ulong> ReadBalance(BasaltProvider provider, Address addr)
    {
        var contract = GetContract(provider);
        var result = await contract.ReadSdkAsync(
            "BalanceOf", gasLimit: 100_000,
            args: [SdkContractEncoder.EncodeBytes(addr.ToArray())]);

        if (result.Success && result.ReturnData is { Length: > 0 })
            return SdkContractEncoder.DecodeUInt64(Convert.FromHexString(result.ReturnData));
        return 0;
    }
}
