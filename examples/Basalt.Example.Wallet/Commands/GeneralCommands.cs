using Basalt.Core;
using Basalt.Sdk.Wallet;
using Basalt.Sdk.Wallet.Accounts;
using Basalt.Sdk.Wallet.HdWallet;
using Basalt.Sdk.Wallet.Rpc.Models;
using Spectre.Console;

namespace Basalt.Example.Wallet.Commands;

public static class GeneralCommands
{
    public static async Task ShowStatus(BasaltProvider provider)
    {
        var s = await provider.GetStatusAsync();
        var block = await provider.GetLatestBlockAsync();

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Blue);
        table.AddColumn("[bold]Field[/]");
        table.AddColumn("[bold]Value[/]");

        table.AddRow("Block height", $"[cyan]{s.BlockHeight}[/]");
        table.AddRow("Latest hash", $"[dim]{s.LatestBlockHash}[/]");
        table.AddRow("Mempool size", s.MempoolSize.ToString());
        table.AddRow("Protocol", $"v{s.ProtocolVersion}");

        table.AddEmptyRow();
        table.AddRow("[bold]Latest Block[/]", $"[bold]#{block.Number}[/]");
        table.AddRow("Hash", $"[dim]{block.Hash}[/]");
        table.AddRow("Proposer", $"[dim]{block.Proposer}[/]");
        table.AddRow("Transactions", block.TransactionCount.ToString());
        table.AddRow("Gas", $"{block.GasUsed} / {block.GasLimit}");

        AnsiConsole.Write(table);
    }

    public static async Task ListAccounts(BasaltProvider provider, AccountManager manager)
    {
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Green);
        table.AddColumn("[bold]#[/]");
        table.AddColumn("[bold]Address[/]");
        table.AddColumn("[bold]Balance[/]");
        table.AddColumn("[bold]Nonce[/]");
        table.AddColumn("[bold]Active[/]");

        var index = 0;
        foreach (var acct in manager.GetAll())
        {
            var balance = await provider.GetBalanceAsync(acct.Address);
            var nonce = await provider.GetNonceAsync(acct.Address);
            var isActive = acct.Address.Equals(manager.ActiveAccount!.Address);

            table.AddRow(
                index.ToString(),
                $"[cyan]{Helpers.FormatAddressFull(acct.Address)}[/]",
                $"[green]{Helpers.FormatBslt(balance)}[/]",
                nonce.ToString(),
                isActive ? "[yellow]*[/]" : "");

            index++;
        }

        AnsiConsole.Write(table);
    }

    public static void SwitchAccount(AccountManager manager, HdWallet wallet)
    {
        var accounts = manager.GetAll().ToList();
        var choices = new List<string>();
        for (var i = 0; i < accounts.Count; i++)
        {
            var marker = accounts[i].Address.Equals(manager.ActiveAccount!.Address) ? " (active)" : "";
            choices.Add($"[{i}] {Helpers.FormatAddress(accounts[i].Address)}{marker}");
        }

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[green]Select account[/]")
                .AddChoices(choices));

        var selectedIndex = int.Parse(selected[1..selected.IndexOf(']')]);
        manager.SetActive(accounts[selectedIndex].Address);
        AnsiConsole.MarkupLine($"[green]Switched to account {selectedIndex}:[/] [cyan]{Helpers.FormatAddress(manager.ActiveAccount!.Address)}[/]");
    }

    public static async Task RequestFaucet(BasaltProvider provider, IAccount account)
    {
        var balanceBefore = await provider.GetBalanceAsync(account.Address);

        FaucetResult? faucetResult = null;
        await AnsiConsole.Status().StartAsync("Requesting testnet tokens...", async _ =>
        {
            faucetResult = await provider.RequestFaucetAsync(account.Address);
        });

        if (!faucetResult!.Success)
        {
            Helpers.ShowError(faucetResult.Message);
            return;
        }

        AnsiConsole.MarkupLine($"[green]Faucet:[/] {Markup.Escape(faucetResult.Message)}");

        if (faucetResult.TxHash is not null)
            await Helpers.SubmitAndTrackAsync(provider,
                new TransactionSubmitResult { Hash = faucetResult.TxHash, Status = "accepted" },
                "Faucet");

        var balanceAfter = await provider.GetBalanceAsync(account.Address);

        var table = new Table().Border(TableBorder.Simple).BorderColor(Color.Grey);
        table.AddColumn("");
        table.AddColumn("Balance");
        table.AddRow("Before", $"[dim]{Helpers.FormatBslt(balanceBefore)}[/]");
        table.AddRow("After", $"[green]{Helpers.FormatBslt(balanceAfter)}[/]");
        AnsiConsole.Write(table);
    }

    public static async Task Transfer(BasaltProvider provider, AccountManager manager, HdWallet wallet)
    {
        var senderAccount = manager.ActiveAccount!;
        var recipientAddr = Helpers.PromptAddress("Recipient", wallet);
        var weiAmount = Helpers.PromptBsltAmount("Amount in BSLT");

        var senderBefore = await provider.GetBalanceAsync(senderAccount.Address);
        var recipBefore = await provider.GetBalanceAsync(recipientAddr);

        var result = await provider.TransferAsync(senderAccount, recipientAddr, weiAmount);
        var txInfo = await Helpers.SubmitAndTrackAsync(provider, result, "Transfer");

        var senderAfter = await provider.GetBalanceAsync(senderAccount.Address);
        var recipAfter = await provider.GetBalanceAsync(recipientAddr);

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Green);
        table.AddColumn("[bold]Account[/]");
        table.AddColumn("[bold]Before[/]");
        table.AddColumn("[bold]After[/]");
        table.AddRow("Sender", $"[dim]{Helpers.FormatBslt(senderBefore)}[/]", $"[green]{Helpers.FormatBslt(senderAfter)}[/]");
        table.AddRow("Recipient", $"[dim]{Helpers.FormatBslt(recipBefore)}[/]", $"[green]{Helpers.FormatBslt(recipAfter)}[/]");
        AnsiConsole.Write(table);
    }
}
