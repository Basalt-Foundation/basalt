using Basalt.Core;
using Basalt.Sdk.Wallet;
using Basalt.Sdk.Wallet.Accounts;
using Basalt.Sdk.Wallet.Contracts;
using Basalt.Sdk.Wallet.HdWallet;
using Spectre.Console;

namespace Basalt.Example.Wallet.Commands;

public static class StakingPoolCommands
{
    private static ContractClient GetContract(BasaltProvider provider) =>
        provider.GetContract(BasaltProvider.SystemContracts.StakingPool);

    public static async Task CreatePool(BasaltProvider provider, IAccount account)
    {
        var initialStake = Helpers.PromptBsltAmount("Initial stake in BSLT");

        // Dry-run to get the pool ID that will be assigned
        var contract = GetContract(provider);
        ulong? poolId = null;
        var dryRun = await contract.ReadSdkAsync("CreatePool", gasLimit: 200_000);
        if (dryRun.Success && dryRun.ReturnData is { Length: > 0 })
            poolId = SdkContractEncoder.DecodeUInt64(Convert.FromHexString(dryRun.ReturnData));

        var result = await contract.CallSdkAsync(
            account, "CreatePool", gasLimit: 200_000, value: initialStake);
        var txInfo = await Helpers.SubmitAndTrackAsync(provider, result, "Create Staking Pool");

        if (txInfo?.BlockNumber is not null && poolId.HasValue)
        {
            // Verify by querying pool state
            var stakeResult = await contract.ReadSdkAsync(
                "GetPoolStake", gasLimit: 100_000,
                args: [SdkContractEncoder.EncodeUInt64(poolId.Value)]);

            var totalStake = (stakeResult.Success && stakeResult.ReturnData is { Length: > 0 })
                ? $"{SdkContractEncoder.DecodeUInt64(Convert.FromHexString(stakeResult.ReturnData))} wei"
                : "pending";

            Helpers.ShowDetailPanel("Staking Pool Created", Color.Olive,
                ("Pool ID", $"#{poolId.Value}"),
                ("Operator", Helpers.FormatAddress(account.Address)),
                ("Total Stake", totalStake));
        }
        else if (txInfo?.BlockNumber is not null)
        {
            AnsiConsole.MarkupLine("[green]Pool created.[/] Use [cyan]Pool: Info[/] to query details.");
        }
    }

    public static async Task Delegate(BasaltProvider provider, IAccount account)
    {
        var poolId = Helpers.PromptUInt64("Pool ID");
        var amount = Helpers.PromptBsltAmount("Amount to delegate in BSLT");

        var contract = GetContract(provider);
        var delegationBefore = await ReadDelegation(contract, poolId, account.Address);

        var result = await contract.CallSdkAsync(
            account, "Delegate", gasLimit: 200_000, value: amount,
            args: [SdkContractEncoder.EncodeUInt64(poolId)]);
        var txInfo = await Helpers.SubmitAndTrackAsync(provider, result, "Delegate to Pool");

        if (txInfo?.BlockNumber is not null)
        {
            var delegationAfter = await ReadDelegation(contract, poolId, account.Address);
            var totalStake = await ReadPoolStake(contract, poolId);

            Helpers.ShowDetailPanel($"Pool #{poolId} — Delegated", Color.Olive,
                ("Your Delegation", $"{delegationBefore} -> [green]{delegationAfter}[/] wei"),
                ("Pool Total Stake", $"{totalStake} wei"));
        }
    }

    public static async Task Undelegate(BasaltProvider provider, IAccount account)
    {
        var poolId = Helpers.PromptUInt64("Pool ID");
        var undelegateAmount = Helpers.PromptUInt64("Amount to undelegate (in wei)");

        var contract = GetContract(provider);
        var delegationBefore = await ReadDelegation(contract, poolId, account.Address);

        var result = await contract.CallSdkAsync(
            account, "Undelegate", gasLimit: 200_000,
            args: [SdkContractEncoder.EncodeUInt64(poolId), SdkContractEncoder.EncodeUInt64(undelegateAmount)]);
        var txInfo = await Helpers.SubmitAndTrackAsync(provider, result, "Undelegate from Pool");

        if (txInfo?.BlockNumber is not null)
        {
            var delegationAfter = await ReadDelegation(contract, poolId, account.Address);
            var totalStake = await ReadPoolStake(contract, poolId);

            Helpers.ShowDetailPanel($"Pool #{poolId} — Undelegated", Color.Olive,
                ("Your Delegation", $"{delegationBefore} -> [green]{delegationAfter}[/] wei"),
                ("Pool Total Stake", $"{totalStake} wei"));
        }
    }

    public static async Task AddRewards(BasaltProvider provider, IAccount account)
    {
        var poolId = Helpers.PromptUInt64("Pool ID");
        var amount = Helpers.PromptBsltAmount("Reward amount in BSLT");

        var contract = GetContract(provider);
        var rewardsBefore = await ReadPoolRewards(contract, poolId);

        var result = await contract.CallSdkAsync(
            account, "AddRewards", gasLimit: 200_000, value: amount,
            args: [SdkContractEncoder.EncodeUInt64(poolId)]);
        var txInfo = await Helpers.SubmitAndTrackAsync(provider, result, "Add Rewards");

        if (txInfo?.BlockNumber is not null)
        {
            var rewardsAfter = await ReadPoolRewards(contract, poolId);

            Helpers.ShowDetailPanel($"Pool #{poolId} — Rewards Added", Color.Olive,
                ("Rewards Before", $"{rewardsBefore} wei"),
                ("Rewards After", $"[green]{rewardsAfter} wei[/]"));
        }
    }

    public static async Task ClaimRewards(BasaltProvider provider, IAccount account)
    {
        var poolId = Helpers.PromptUInt64("Pool ID");

        var balanceBefore = await provider.GetBalanceAsync(account.Address);

        var contract = GetContract(provider);
        var result = await contract.CallSdkAsync(
            account, "ClaimRewards", gasLimit: 200_000,
            args: [SdkContractEncoder.EncodeUInt64(poolId)]);
        var txInfo = await Helpers.SubmitAndTrackAsync(provider, result, "Claim Rewards");

        if (txInfo?.BlockNumber is not null)
        {
            var balanceAfter = await provider.GetBalanceAsync(account.Address);

            var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Olive);
            table.AddColumn("[bold]BSLT Balance[/]");
            table.AddColumn("[bold]Before[/]");
            table.AddColumn("[bold]After[/]");
            table.AddRow(Helpers.FormatAddress(account.Address),
                $"[dim]{Helpers.FormatBslt(balanceBefore)}[/]",
                $"[green]{Helpers.FormatBslt(balanceAfter)}[/]");
            AnsiConsole.Write(table);
        }
    }

    public static async Task GetPoolInfo(BasaltProvider provider, HdWallet wallet)
    {
        var poolId = Helpers.PromptUInt64("Pool ID");
        var contract = GetContract(provider);

        var totalStake = await ReadPoolStake(contract, poolId);
        var totalRewards = await ReadPoolRewards(contract, poolId);

        Helpers.ShowDetailPanel($"Pool #{poolId}", Color.Olive,
            ("Total Stake", $"{totalStake} wei"),
            ("Total Rewards", $"{totalRewards} wei"));

        if (AnsiConsole.Confirm("Query delegation for a specific address?", defaultValue: false))
        {
            var delegator = Helpers.PromptAddress("Delegator address", wallet);
            var delegation = await ReadDelegation(contract, poolId, delegator);
            AnsiConsole.MarkupLine($"[green]Delegation:[/] {delegation} wei");
        }
    }

    private static async Task<ulong> ReadPoolStake(ContractClient contract, ulong poolId)
    {
        var result = await contract.ReadSdkAsync(
            "GetPoolStake", gasLimit: 100_000,
            args: [SdkContractEncoder.EncodeUInt64(poolId)]);
        return (result.Success && result.ReturnData is { Length: > 0 })
            ? SdkContractEncoder.DecodeUInt64(Convert.FromHexString(result.ReturnData)) : 0;
    }

    private static async Task<ulong> ReadPoolRewards(ContractClient contract, ulong poolId)
    {
        var result = await contract.ReadSdkAsync(
            "GetPoolRewards", gasLimit: 100_000,
            args: [SdkContractEncoder.EncodeUInt64(poolId)]);
        return (result.Success && result.ReturnData is { Length: > 0 })
            ? SdkContractEncoder.DecodeUInt64(Convert.FromHexString(result.ReturnData)) : 0;
    }

    private static async Task<ulong> ReadDelegation(ContractClient contract, ulong poolId, Address delegator)
    {
        var result = await contract.ReadSdkAsync(
            "GetDelegation", gasLimit: 100_000,
            args: [SdkContractEncoder.EncodeUInt64(poolId), SdkContractEncoder.EncodeBytes(delegator.ToArray())]);
        return (result.Success && result.ReturnData is { Length: > 0 })
            ? SdkContractEncoder.DecodeUInt64(Convert.FromHexString(result.ReturnData)) : 0;
    }
}
