using Basalt.Sdk.Wallet;
using Basalt.Sdk.Wallet.Accounts;
using Basalt.Sdk.Wallet.Contracts;
using Spectre.Console;

namespace Basalt.Example.Wallet.Commands;

public static class GovernanceCommands
{
    private static ContractClient GetContract(BasaltProvider provider) =>
        provider.GetContract(BasaltProvider.SystemContracts.Governance);

    public static async Task CreateProposal(BasaltProvider provider, IAccount account)
    {
        var description = Helpers.PromptString("Proposal description");
        var votingPeriod = Helpers.PromptUInt64("Voting period (blocks)");

        // Dry-run to get the proposal ID that will be assigned
        var contract = GetContract(provider);
        ulong? proposalId = null;
        var dryRun = await contract.ReadSdkAsync(
            "CreateProposal", gasLimit: 200_000,
            args: [SdkContractEncoder.EncodeString(description), SdkContractEncoder.EncodeUInt64(votingPeriod)]);
        if (dryRun.Success && dryRun.ReturnData is { Length: > 0 })
            proposalId = SdkContractEncoder.DecodeUInt64(Convert.FromHexString(dryRun.ReturnData));

        var result = await contract.CallSdkAsync(
            account, "CreateProposal", gasLimit: 200_000,
            args: [SdkContractEncoder.EncodeString(description), SdkContractEncoder.EncodeUInt64(votingPeriod)]);
        var txInfo = await Helpers.SubmitAndTrackAsync(provider, result, "Create Proposal");

        if (txInfo?.BlockNumber is not null)
        {
            var rows = new List<(string, string)>();
            if (proposalId.HasValue)
                rows.Add(("Proposal ID", $"#{proposalId.Value}"));
            rows.Add(("Description", description));
            rows.Add(("Voting Period", $"{votingPeriod} blocks"));
            rows.Add(("Status", "active"));
            Helpers.ShowDetailPanel("Proposal Created", Color.Yellow, rows.ToArray());
        }
    }

    public static async Task Vote(BasaltProvider provider, IAccount account)
    {
        var proposalId = Helpers.PromptUInt64("Proposal ID");
        var support = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[green]Vote[/]")
                .AddChoices("For", "Against"));

        var poolId = Helpers.PromptUInt64("Staking pool ID");

        var contract = GetContract(provider);
        var result = await contract.CallSdkAsync(
            account, "Vote", gasLimit: 200_000,
            args: [SdkContractEncoder.EncodeUInt64(proposalId), SdkContractEncoder.EncodeBool(support == "For"), SdkContractEncoder.EncodeUInt64(poolId)]);
        var txInfo = await Helpers.SubmitAndTrackAsync(provider, result, "Cast Vote");

        // Show updated vote counts
        if (txInfo?.BlockNumber is not null)
            await ShowVoteCounts(contract, proposalId);
    }

    public static async Task Queue(BasaltProvider provider, IAccount account)
    {
        var proposalId = Helpers.PromptUInt64("Proposal ID");

        var contract = GetContract(provider);
        var result = await contract.CallSdkAsync(
            account, "QueueProposal", gasLimit: 200_000,
            args: [SdkContractEncoder.EncodeUInt64(proposalId)]);
        var txInfo = await Helpers.SubmitAndTrackAsync(provider, result, "Queue Proposal");

        if (txInfo?.BlockNumber is not null)
        {
            var statusResult = await contract.ReadSdkAsync(
                "GetStatus", gasLimit: 100_000,
                args: [SdkContractEncoder.EncodeUInt64(proposalId)]);
            var status = (statusResult.Success && statusResult.ReturnData is { Length: > 0 })
                ? SdkContractEncoder.DecodeString(Convert.FromHexString(statusResult.ReturnData))
                : "unknown";
            AnsiConsole.MarkupLine($"Proposal #{proposalId} status: [{GetStatusColor(status)}]{Markup.Escape(status)}[/]");
        }
    }

    public static async Task Execute(BasaltProvider provider, IAccount account)
    {
        var proposalId = Helpers.PromptUInt64("Proposal ID");

        var contract = GetContract(provider);
        var result = await contract.CallSdkAsync(
            account, "ExecuteProposal", gasLimit: 200_000,
            args: [SdkContractEncoder.EncodeUInt64(proposalId)]);
        var txInfo = await Helpers.SubmitAndTrackAsync(provider, result, "Execute Proposal");

        // Show final status
        if (txInfo?.BlockNumber is not null)
        {
            var statusResult = await contract.ReadSdkAsync(
                "GetStatus", gasLimit: 100_000,
                args: [SdkContractEncoder.EncodeUInt64(proposalId)]);

            var status = (statusResult.Success && statusResult.ReturnData is { Length: > 0 })
                ? SdkContractEncoder.DecodeString(Convert.FromHexString(statusResult.ReturnData))
                : "unknown";

            AnsiConsole.MarkupLine($"Proposal #{proposalId} final status: [{GetStatusColor(status)}]{Markup.Escape(status)}[/]");

            await ShowVoteCounts(contract, proposalId);
        }
    }

    public static async Task GetStatus(BasaltProvider provider)
    {
        var proposalId = Helpers.PromptUInt64("Proposal ID");

        var contract = GetContract(provider);

        var statusResult = await contract.ReadSdkAsync(
            "GetStatus", gasLimit: 100_000,
            args: [SdkContractEncoder.EncodeUInt64(proposalId)]);

        var status = (statusResult.Success && statusResult.ReturnData is { Length: > 0 })
            ? SdkContractEncoder.DecodeString(Convert.FromHexString(statusResult.ReturnData))
            : statusResult.Error ?? "unknown";

        AnsiConsole.MarkupLine($"Proposal #{proposalId}: [{GetStatusColor(status)}]{Markup.Escape(status)}[/]");
        await ShowVoteCounts(contract, proposalId);
    }

    public static async Task GetVotes(BasaltProvider provider)
    {
        var proposalId = Helpers.PromptUInt64("Proposal ID");
        var contract = GetContract(provider);
        await ShowVoteCounts(contract, proposalId);
    }

    private static string GetStatusColor(string status) => status switch
    {
        "active" => "yellow",
        "queued" => "blue",
        "executed" => "green",
        "rejected" => "red",
        "canceled" => "dim",
        _ => "dim",
    };

    private static async Task ShowVoteCounts(ContractClient contract, ulong proposalId)
    {
        var forResult = await contract.ReadSdkAsync(
            "GetVotesFor", gasLimit: 100_000,
            args: [SdkContractEncoder.EncodeUInt64(proposalId)]);

        var againstResult = await contract.ReadSdkAsync(
            "GetVotesAgainst", gasLimit: 100_000,
            args: [SdkContractEncoder.EncodeUInt64(proposalId)]);

        var votesFor = (forResult.Success && forResult.ReturnData is { Length: > 0 })
            ? SdkContractEncoder.DecodeUInt64(Convert.FromHexString(forResult.ReturnData)) : 0UL;
        var votesAgainst = (againstResult.Success && againstResult.ReturnData is { Length: > 0 })
            ? SdkContractEncoder.DecodeUInt64(Convert.FromHexString(againstResult.ReturnData)) : 0UL;

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Yellow);
        table.AddColumn("[bold]Proposal #" + proposalId + "[/]");
        table.AddColumn("[bold]Votes[/]");
        table.AddRow("[green]For[/]", votesFor.ToString());
        table.AddRow("[red]Against[/]", votesAgainst.ToString());
        table.AddRow("[bold]Total[/]", (votesFor + votesAgainst).ToString());
        AnsiConsole.Write(table);
    }
}
