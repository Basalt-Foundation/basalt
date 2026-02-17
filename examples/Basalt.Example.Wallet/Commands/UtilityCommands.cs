using Basalt.Sdk.Wallet;
using Basalt.Sdk.Wallet.Subscriptions;
using Spectre.Console;

namespace Basalt.Example.Wallet.Commands;

public static class UtilityCommands
{
    public static async Task TxLookup(BasaltProvider provider)
    {
        var hash = Helpers.PromptString("Tx hash (0x...)");

        var tx = await provider.GetTransactionAsync(hash);
        if (tx is null)
        {
            AnsiConsole.MarkupLine("[yellow]Transaction not found.[/]");
            return;
        }

        var rows = new List<(string, string)>
        {
            ("Hash", $"[dim]{Markup.Escape(hash)}[/]"),
            ("Type", tx.Type),
            ("From", $"[dim]{tx.Sender}[/]"),
            ("To", $"[dim]{tx.To}[/]"),
            ("Value", tx.Value),
            ("Nonce", tx.Nonce.ToString()),
            ("Gas Limit", tx.GasLimit.ToString()),
        };
        if (tx.BlockNumber is not null)
            rows.Add(("Block", $"[cyan]#{tx.BlockNumber}[/]"));
        if (tx.TransactionIndex is not null)
            rows.Add(("Index", tx.TransactionIndex.ToString()!));

        Helpers.ShowDetailPanel("Transaction", Color.Grey, rows.ToArray());
    }

    public static async Task SubscribeBlocks(string nodeUrl)
    {
        var seconds = AnsiConsole.Prompt(
            new TextPrompt<int>("[green]Duration in seconds[/]:")
                .PromptStyle("cyan")
                .DefaultValue(15));

        await using var subscription = BasaltProvider.CreateBlockSubscription(nodeUrl,
            new SubscriptionOptions { AutoReconnect = false });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Blue);
        table.AddColumn("[bold]Type[/]");
        table.AddColumn("[bold]Block[/]");
        table.AddColumn("[bold]Txs[/]");
        table.AddColumn("[bold]Gas Used[/]");
        table.AddColumn("[bold]Proposer[/]");

        AnsiConsole.MarkupLine($"[dim]Listening for {seconds}s...[/]");

        try
        {
            await foreach (var blockEvent in subscription.SubscribeAsync(cts.Token))
            {
                table.AddRow(
                    blockEvent.Type == "new_block" ? "[green]new[/]" : "[dim]current[/]",
                    $"[cyan]#{blockEvent.Block.Number}[/]",
                    blockEvent.Block.TransactionCount.ToString(),
                    blockEvent.Block.GasUsed.ToString(),
                    $"[dim]{blockEvent.Block.Proposer[..18]}...[/]");
            }
        }
        catch (OperationCanceledException)
        {
            // Expected timeout
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine("[dim](subscription ended)[/]");
    }
}
