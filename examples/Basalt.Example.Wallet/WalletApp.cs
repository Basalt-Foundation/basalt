using Basalt.Core;
using Basalt.Sdk.Wallet;
using Basalt.Sdk.Wallet.Accounts;
using Basalt.Sdk.Wallet.HdWallet;
using Basalt.Example.Wallet.Commands;
using Spectre.Console;

namespace Basalt.Example.Wallet;

public static class WalletApp
{
    public static async Task RunAsync(BasaltProvider provider, HdWallet wallet, AccountManager manager, string nodeUrl)
    {
        AnsiConsole.Write(new Rule("[bold blue]Basalt Interactive Wallet[/]").RuleStyle("blue"));
        AnsiConsole.WriteLine();

        try
        {
            var status = await provider.GetStatusAsync();
            AnsiConsole.MarkupLine($"[green]Connected[/] to [cyan]{Markup.Escape(nodeUrl)}[/] (chain {provider.ChainId})");
            AnsiConsole.MarkupLine($"Block [cyan]#{status.BlockHeight}[/], protocol [dim]v{status.ProtocolVersion}[/]");
        }
        catch (Exception ex)
        {
            Helpers.ShowError($"Could not connect: {ex.Message}");
            return;
        }

        Address? deployedToken = null;

        while (true)
        {
            AnsiConsole.WriteLine();

            // Show account info header
            var active = manager.ActiveAccount!;
            try
            {
                var balance = await provider.GetBalanceAsync(active.Address);
                AnsiConsole.Write(new Panel(
                    $"[bold]{Helpers.FormatAddressFull(active.Address)}[/]\n" +
                    $"Balance: [green]{Helpers.FormatBslt(balance)}[/]" +
                    (deployedToken.HasValue ? $"\nToken:   [cyan]{Helpers.FormatAddress(deployedToken.Value)}[/]" : ""))
                    .Header("[bold yellow]Active Account[/]")
                    .Border(BoxBorder.Rounded)
                    .BorderColor(Color.Yellow));
            }
            catch
            {
                AnsiConsole.Write(new Panel(
                    $"[bold]{Helpers.FormatAddressFull(active.Address)}[/]")
                    .Header("[bold yellow]Active Account[/]")
                    .Border(BoxBorder.Rounded)
                    .BorderColor(Color.Yellow));
            }

            AnsiConsole.WriteLine();

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]What would you like to do?[/]")
                    .PageSize(25)
                    .HighlightStyle(new Style(Color.Cyan1))
                    .AddChoiceGroup("[blue]General[/]",
                        "Show status", "List accounts", "Switch account", "Request faucet", "Transfer BSLT")
                    .AddChoiceGroup("[magenta]Tokens[/]",
                        "Deploy BST-20", "Deploy BST-721", "Token: Transfer", "Token: BalanceOf", "Token: Metadata")
                    .AddChoiceGroup("[green]WBSLT[/]",
                        "WBSLT: Deposit (wrap)", "WBSLT: Withdraw (unwrap)", "WBSLT: Balance")
                    .AddChoiceGroup("[cyan]BNS[/]",
                        "BNS: Register name", "BNS: Resolve name", "BNS: Set address", "BNS: Set reverse",
                        "BNS: Reverse lookup", "BNS: Transfer name", "BNS: Owner of")
                    .AddChoiceGroup("[yellow]Governance[/]",
                        "Gov: Create proposal", "Gov: Vote", "Gov: Execute proposal", "Gov: Proposal status", "Gov: Vote counts")
                    .AddChoiceGroup("[red]Escrow[/]",
                        "Escrow: Create", "Escrow: Release", "Escrow: Refund", "Escrow: Info")
                    .AddChoiceGroup("[olive]Staking Pools[/]",
                        "Pool: Create", "Pool: Delegate", "Pool: Undelegate", "Pool: Add rewards", "Pool: Claim rewards", "Pool: Info")
                    .AddChoiceGroup("[grey]Utilities[/]",
                        "Transaction lookup", "Subscribe to blocks")
                    .AddChoices("Exit"));

            if (choice == "Exit") break;

            AnsiConsole.WriteLine();

            try
            {
                switch (choice)
                {
                    // General
                    case "Show status":
                        await GeneralCommands.ShowStatus(provider);
                        break;
                    case "List accounts":
                        await GeneralCommands.ListAccounts(provider, manager);
                        break;
                    case "Switch account":
                        GeneralCommands.SwitchAccount(manager, wallet);
                        break;
                    case "Request faucet":
                        await GeneralCommands.RequestFaucet(provider, manager.ActiveAccount!);
                        break;
                    case "Transfer BSLT":
                        await GeneralCommands.Transfer(provider, manager, wallet);
                        break;

                    // Tokens
                    case "Deploy BST-20":
                        deployedToken = await TokenCommands.DeployBST20(provider, manager.ActiveAccount!);
                        break;
                    case "Deploy BST-721":
                        deployedToken = await TokenCommands.DeployBST721(provider, manager.ActiveAccount!);
                        break;
                    case "Token: Transfer":
                        await TokenCommands.TokenTransfer(provider, manager.ActiveAccount!, wallet, deployedToken);
                        break;
                    case "Token: BalanceOf":
                        await TokenCommands.TokenBalanceOf(provider, wallet, deployedToken);
                        break;
                    case "Token: Metadata":
                        await TokenCommands.TokenMetadata(provider, deployedToken);
                        break;

                    // WBSLT
                    case "WBSLT: Deposit (wrap)":
                        await WbsltCommands.Deposit(provider, manager.ActiveAccount!);
                        break;
                    case "WBSLT: Withdraw (unwrap)":
                        await WbsltCommands.Withdraw(provider, manager.ActiveAccount!);
                        break;
                    case "WBSLT: Balance":
                        await WbsltCommands.BalanceOf(provider, wallet);
                        break;

                    // BNS
                    case "BNS: Register name":
                        await BnsCommands.Register(provider, manager.ActiveAccount!);
                        break;
                    case "BNS: Resolve name":
                        await BnsCommands.Resolve(provider);
                        break;
                    case "BNS: Set address":
                        await BnsCommands.SetAddress(provider, manager.ActiveAccount!, wallet);
                        break;
                    case "BNS: Set reverse":
                        await BnsCommands.SetReverse(provider, manager.ActiveAccount!);
                        break;
                    case "BNS: Reverse lookup":
                        await BnsCommands.ReverseLookup(provider, wallet);
                        break;
                    case "BNS: Transfer name":
                        await BnsCommands.TransferName(provider, manager.ActiveAccount!, wallet);
                        break;
                    case "BNS: Owner of":
                        await BnsCommands.OwnerOf(provider);
                        break;

                    // Governance
                    case "Gov: Create proposal":
                        await GovernanceCommands.CreateProposal(provider, manager.ActiveAccount!);
                        break;
                    case "Gov: Vote":
                        await GovernanceCommands.Vote(provider, manager.ActiveAccount!);
                        break;
                    case "Gov: Execute proposal":
                        await GovernanceCommands.Execute(provider, manager.ActiveAccount!);
                        break;
                    case "Gov: Proposal status":
                        await GovernanceCommands.GetStatus(provider);
                        break;
                    case "Gov: Vote counts":
                        await GovernanceCommands.GetVotes(provider);
                        break;

                    // Escrow
                    case "Escrow: Create":
                        await EscrowCommands.Create(provider, manager.ActiveAccount!, wallet);
                        break;
                    case "Escrow: Release":
                        await EscrowCommands.Release(provider, manager.ActiveAccount!);
                        break;
                    case "Escrow: Refund":
                        await EscrowCommands.Refund(provider, manager.ActiveAccount!);
                        break;
                    case "Escrow: Info":
                        await EscrowCommands.GetInfo(provider);
                        break;

                    // Staking Pools
                    case "Pool: Create":
                        await StakingPoolCommands.CreatePool(provider, manager.ActiveAccount!);
                        break;
                    case "Pool: Delegate":
                        await StakingPoolCommands.Delegate(provider, manager.ActiveAccount!);
                        break;
                    case "Pool: Undelegate":
                        await StakingPoolCommands.Undelegate(provider, manager.ActiveAccount!);
                        break;
                    case "Pool: Add rewards":
                        await StakingPoolCommands.AddRewards(provider, manager.ActiveAccount!);
                        break;
                    case "Pool: Claim rewards":
                        await StakingPoolCommands.ClaimRewards(provider, manager.ActiveAccount!);
                        break;
                    case "Pool: Info":
                        await StakingPoolCommands.GetPoolInfo(provider, wallet);
                        break;

                    // Utilities
                    case "Transaction lookup":
                        await UtilityCommands.TxLookup(provider);
                        break;
                    case "Subscribe to blocks":
                        await UtilityCommands.SubscribeBlocks(nodeUrl);
                        break;
                }
            }
            catch (Exception ex)
            {
                Helpers.ShowError(ex.Message);
            }
        }

        AnsiConsole.Write(new Rule("[dim]Goodbye[/]").RuleStyle("grey"));
    }
}
