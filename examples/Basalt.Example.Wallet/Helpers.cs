using System.Buffers.Binary;
using Basalt.Core;
using Basalt.Crypto;
using Basalt.Sdk.Wallet;
using Basalt.Sdk.Wallet.Contracts;
using Basalt.Sdk.Wallet.HdWallet;
using Basalt.Sdk.Wallet.Rpc.Models;
using Spectre.Console;

namespace Basalt.Example.Wallet;

public static class Helpers
{
    // ── Formatting ───────────────────────────────────────────────────

    public static string FormatAddress(Address addr)
    {
        var hex = Convert.ToHexString(addr.ToArray()).ToLowerInvariant();
        return $"0x{hex[..8]}...{hex[^8..]}";
    }

    public static string FormatAddressFull(Address addr)
    {
        return "0x" + Convert.ToHexString(addr.ToArray()).ToLowerInvariant();
    }

    public static string FormatBslt(string weiBalance)
    {
        if (weiBalance == "0") return "0 BSLT";

        const int decimals = 18;
        var padded = weiBalance.PadLeft(decimals + 1, '0');
        var integerPart = padded[..^decimals];
        var fractionalPart = padded[^decimals..].TrimEnd('0');

        if (fractionalPart.Length == 0)
            return $"{integerPart} BSLT";

        if (fractionalPart.Length > 4)
            fractionalPart = fractionalPart[..4];

        return $"{integerPart}.{fractionalPart} BSLT";
    }

    public static bool TryParseBsltToWei(string bslt, out UInt256 wei)
    {
        wei = UInt256.Zero;

        var parts = bslt.Split('.');
        if (parts.Length > 2) return false;

        var integerStr = parts[0];
        var fractionalStr = parts.Length == 2 ? parts[1] : "";

        if (fractionalStr.Length > 18) return false;
        fractionalStr = fractionalStr.PadRight(18, '0');

        var fullStr = integerStr + fractionalStr;
        fullStr = fullStr.TrimStart('0');
        if (fullStr.Length == 0) fullStr = "0";

        try
        {
            wei = UInt256.Parse(fullStr);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static Address DeriveContractAddress(Address senderAddr, ulong nonce)
    {
        Span<byte> input = stackalloc byte[Address.Size + 8];
        senderAddr.WriteTo(input[..Address.Size]);
        BinaryPrimitives.WriteUInt64LittleEndian(input[Address.Size..], nonce);
        var hash = Blake3Hasher.Hash(input);
        Span<byte> hashBytes = stackalloc byte[Hash256.Size];
        hash.WriteTo(hashBytes);
        return new Address(hashBytes[12..]);
    }

    // ── Spectre Prompts ──────────────────────────────────────────────

    public static Address PromptAddress(string label, HdWallet wallet)
    {
        var input = AnsiConsole.Prompt(
            new TextPrompt<string>($"[green]{label}[/] [dim](account index 0-2, or 0x hex)[/]:")
                .PromptStyle("cyan"));

        if (uint.TryParse(input, out var idx) && idx <= 2)
            return wallet.GetAccount(idx).Address;

        if (input.StartsWith("0x") && input.Length == 42)
            return new Address(Convert.FromHexString(input[2..]));

        throw new ArgumentException("Invalid address. Use account index (0-2) or 0x hex address.");
    }

    public static UInt256 PromptBsltAmount(string label)
    {
        var input = AnsiConsole.Prompt(
            new TextPrompt<string>($"[green]{label}[/] [dim](e.g. 1, 0.5, 10)[/]:")
                .PromptStyle("cyan")
                .Validate(v =>
                    TryParseBsltToWei(v, out _)
                        ? ValidationResult.Success()
                        : ValidationResult.Error("[red]Invalid BSLT amount[/]")));

        TryParseBsltToWei(input, out var wei);
        return wei;
    }

    public static ulong PromptUInt64(string label)
    {
        return AnsiConsole.Prompt(
            new TextPrompt<ulong>($"[green]{label}[/]:")
                .PromptStyle("cyan"));
    }

    public static string PromptString(string label)
    {
        return AnsiConsole.Prompt(
            new TextPrompt<string>($"[green]{label}[/]:")
                .PromptStyle("cyan"));
    }

    public static string PromptString(string label, string defaultValue)
    {
        return AnsiConsole.Prompt(
            new TextPrompt<string>($"[green]{label}[/]:")
                .PromptStyle("cyan")
                .DefaultValue(defaultValue));
    }

    public static byte PromptByte(string label, byte defaultValue)
    {
        return AnsiConsole.Prompt(
            new TextPrompt<byte>($"[green]{label}[/]:")
                .PromptStyle("cyan")
                .DefaultValue(defaultValue));
    }

    // ── Transaction Submission & Tracking ────────────────────────────

    /// <summary>
    /// Submit-and-track: shows the tx hash, polls for block inclusion,
    /// and returns the confirmed TransactionInfo (or null on timeout).
    /// All callers get a consistent spinner + confirmed panel.
    /// </summary>
    public static async Task<TransactionInfo?> SubmitAndTrackAsync(
        BasaltProvider provider,
        TransactionSubmitResult submitResult,
        string actionLabel)
    {
        AnsiConsole.MarkupLine($"[dim]Tx hash:[/] [cyan]{submitResult.Hash}[/]");

        if (submitResult.Status != "accepted")
        {
            AnsiConsole.MarkupLine($"[red]Rejected:[/] {Markup.Escape(submitResult.Status)}");
            return null;
        }

        TransactionInfo? txInfo = null;

        await AnsiConsole.Status().StartAsync($"Waiting for block inclusion...", async ctx =>
        {
            // Poll every 2s for up to 20s
            for (var attempt = 0; attempt < 10; attempt++)
            {
                await Task.Delay(2000);
                ctx.Status($"Waiting for block inclusion... ({(attempt + 1) * 2}s)");

                txInfo = await provider.GetTransactionAsync(submitResult.Hash);
                if (txInfo?.BlockNumber is not null)
                    break;
            }
        });

        // Build result panel
        var table = new Table().Border(TableBorder.Rounded);

        if (txInfo?.BlockNumber is not null)
        {
            table.BorderColor(Color.Green);
            table.AddColumn(new TableColumn("[bold green]Confirmed[/]"));
            table.AddColumn(new TableColumn($"[bold]{Markup.Escape(actionLabel)}[/]"));
            table.AddRow("Block", $"[cyan]#{txInfo.BlockNumber}[/]");
            table.AddRow("Tx Hash", $"[dim]{txInfo.Hash}[/]");
            table.AddRow("Index", txInfo.TransactionIndex?.ToString() ?? "-");
        }
        else
        {
            table.BorderColor(Color.Yellow);
            table.AddColumn(new TableColumn("[bold yellow]Pending[/]"));
            table.AddColumn(new TableColumn($"[bold]{Markup.Escape(actionLabel)}[/]"));
            table.AddRow("Tx Hash", $"[dim]{submitResult.Hash}[/]");
            table.AddRow("Status", "[yellow]Not yet included — check later with Tx Lookup[/]");
        }

        AnsiConsole.Write(table);
        return txInfo;
    }

    // ── Display Helpers ──────────────────────────────────────────────

    public static void ShowReadResult(CallResult result, Func<byte[], string>? decoder = null)
    {
        if (result.Success && result.ReturnData is { Length: > 0 })
        {
            var returnBytes = Convert.FromHexString(result.ReturnData);
            var display = decoder != null ? decoder(returnBytes) : Convert.ToHexString(returnBytes).ToLowerInvariant();
            AnsiConsole.MarkupLine($"  [green]Result:[/] {Markup.Escape(display)}");
        }
        else if (result.Success)
        {
            AnsiConsole.MarkupLine("  [dim]Empty return (no data)[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"  [red]Error:[/] {Markup.Escape(result.Error ?? "unknown")}");
        }

        AnsiConsole.MarkupLine($"  [dim]Gas used: {result.GasUsed}[/]");
    }

    /// <summary>
    /// Display a key-value detail panel for an entity (pool info, escrow info, etc.)
    /// </summary>
    public static void ShowDetailPanel(string title, Color borderColor, params (string Label, string Value)[] rows)
    {
        var table = new Table().Border(TableBorder.Rounded).BorderColor(borderColor);
        table.AddColumn("[bold]Field[/]");
        table.AddColumn("[bold]Value[/]");

        foreach (var (label, value) in rows)
            table.AddRow(label, value);

        AnsiConsole.Write(new Panel(table).Header($"[bold]{Markup.Escape(title)}[/]").Border(BoxBorder.Rounded).BorderColor(borderColor));
    }

    public static void ShowError(string message)
    {
        AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(message)}");
    }
}
