using Basalt.Core;
using Basalt.Sdk.Wallet;
using Basalt.Sdk.Wallet.Accounts;
using Basalt.Sdk.Wallet.Contracts;
using Basalt.Sdk.Wallet.HdWallet;
using Spectre.Console;

namespace Basalt.Example.Wallet.Commands;

public static class EscrowCommands
{
    private static ContractClient GetContract(BasaltProvider provider) =>
        provider.GetContract(BasaltProvider.SystemContracts.Escrow);

    public static async Task Create(BasaltProvider provider, IAccount account, HdWallet wallet)
    {
        var recipient = Helpers.PromptAddress("Recipient", wallet);
        var releaseBlock = Helpers.PromptUInt64("Release block number");
        var amount = Helpers.PromptBsltAmount("Escrow amount in BSLT");

        // Dry-run to get the escrow ID that will be assigned
        var contract = GetContract(provider);
        ulong? escrowId = null;
        var dryRun = await contract.ReadSdkAsync(
            "Create", gasLimit: 200_000,
            args: [SdkContractEncoder.EncodeBytes(recipient.ToArray()), SdkContractEncoder.EncodeUInt64(releaseBlock)]);
        if (dryRun.Success && dryRun.ReturnData is { Length: > 0 })
            escrowId = SdkContractEncoder.DecodeUInt64(Convert.FromHexString(dryRun.ReturnData));

        var result = await contract.CallSdkAsync(
            account, "Create", gasLimit: 200_000, value: amount,
            args: [SdkContractEncoder.EncodeBytes(recipient.ToArray()), SdkContractEncoder.EncodeUInt64(releaseBlock)]);
        var txInfo = await Helpers.SubmitAndTrackAsync(provider, result, "Create Escrow");

        if (txInfo?.BlockNumber is not null)
        {
            // If dry-run didn't give us the ID, try to verify from state
            if (escrowId.HasValue)
            {
                var statusResult = await contract.ReadSdkAsync(
                    "GetStatus", gasLimit: 100_000,
                    args: [SdkContractEncoder.EncodeUInt64(escrowId.Value)]);
                var status = (statusResult.Success && statusResult.ReturnData is { Length: > 0 })
                    ? SdkContractEncoder.DecodeString(Convert.FromHexString(statusResult.ReturnData))
                    : "locked";

                Helpers.ShowDetailPanel("Escrow Created", Color.Red,
                    ("Escrow ID", $"#{escrowId.Value}"),
                    ("Depositor", Helpers.FormatAddress(account.Address)),
                    ("Recipient", Helpers.FormatAddress(recipient)),
                    ("Release Block", $"#{releaseBlock}"),
                    ("Status", status));
            }
            else
            {
                AnsiConsole.MarkupLine("[green]Escrow created.[/] Use [cyan]Escrow: Info[/] to query details.");
            }
        }
    }

    public static async Task Release(BasaltProvider provider, IAccount account)
    {
        var escrowId = Helpers.PromptUInt64("Escrow ID");

        var contract = GetContract(provider);
        var result = await contract.CallSdkAsync(
            account, "Release", gasLimit: 200_000,
            args: [SdkContractEncoder.EncodeUInt64(escrowId)]);
        var txInfo = await Helpers.SubmitAndTrackAsync(provider, result, "Release Escrow");

        if (txInfo?.BlockNumber is not null)
            await ShowEscrowInfo(contract, escrowId);
    }

    public static async Task Refund(BasaltProvider provider, IAccount account)
    {
        var escrowId = Helpers.PromptUInt64("Escrow ID");

        var contract = GetContract(provider);
        var result = await contract.CallSdkAsync(
            account, "Refund", gasLimit: 200_000,
            args: [SdkContractEncoder.EncodeUInt64(escrowId)]);
        var txInfo = await Helpers.SubmitAndTrackAsync(provider, result, "Refund Escrow");

        if (txInfo?.BlockNumber is not null)
            await ShowEscrowInfo(contract, escrowId);
    }

    public static async Task GetInfo(BasaltProvider provider)
    {
        var escrowId = Helpers.PromptUInt64("Escrow ID");
        var contract = GetContract(provider);
        await ShowEscrowInfo(contract, escrowId);
    }

    private static async Task ShowEscrowInfo(ContractClient contract, ulong escrowId)
    {
        var statusResult = await contract.ReadSdkAsync(
            "GetStatus", gasLimit: 100_000,
            args: [SdkContractEncoder.EncodeUInt64(escrowId)]);

        var amountResult = await contract.ReadSdkAsync(
            "GetAmount", gasLimit: 100_000,
            args: [SdkContractEncoder.EncodeUInt64(escrowId)]);

        var status = (statusResult.Success && statusResult.ReturnData is { Length: > 0 })
            ? SdkContractEncoder.DecodeString(Convert.FromHexString(statusResult.ReturnData))
            : statusResult.Error ?? "unknown";

        var amountStr = (amountResult.Success && amountResult.ReturnData is { Length: > 0 })
            ? $"{SdkContractEncoder.DecodeUInt64(Convert.FromHexString(amountResult.ReturnData))} wei"
            : amountResult.Error ?? "unknown";

        var statusColor = status switch
        {
            "locked" => "yellow",
            "released" => "green",
            "refunded" => "red",
            _ => "dim",
        };

        Helpers.ShowDetailPanel($"Escrow #{escrowId}", Color.Red,
            ("Status", $"[{statusColor}]{Markup.Escape(status)}[/]"),
            ("Amount", amountStr));
    }
}
