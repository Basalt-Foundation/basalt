using Basalt.Core;
using Basalt.Sdk.Wallet;
using Basalt.Sdk.Wallet.Accounts;
using Basalt.Sdk.Wallet.Contracts;
using Basalt.Sdk.Wallet.HdWallet;
using Spectre.Console;

namespace Basalt.Example.Wallet.Commands;

public static class BnsCommands
{
    private static ContractClient GetContract(BasaltProvider provider) =>
        provider.GetContract(BasaltProvider.SystemContracts.NameService);

    public static async Task Register(BasaltProvider provider, IAccount account)
    {
        var name = Helpers.PromptString("Name to register");
        var fee = new UInt256(1_000_000_000);

        AnsiConsole.MarkupLine($"[dim]Registration fee: {fee} wei[/]");

        var contract = GetContract(provider);
        var result = await contract.CallSdkAsync(
            account, "Register", gasLimit: 200_000,
            value: fee, args: [SdkContractEncoder.EncodeString(name)]);
        var txInfo = await Helpers.SubmitAndTrackAsync(provider, result, "BNS Register");

        // Verify registration by resolving the name
        if (txInfo?.BlockNumber is not null)
        {
            var resolveResult = await contract.ReadSdkAsync(
                "Resolve", gasLimit: 100_000,
                args: [SdkContractEncoder.EncodeString(name)]);

            if (resolveResult.Success && resolveResult.ReturnData is { Length: > 0 })
            {
                var addrBytes = SdkContractEncoder.DecodeByteArray(Convert.FromHexString(resolveResult.ReturnData));
                var resolved = addrBytes.Length == 20 ? Helpers.FormatAddressFull(new Address(addrBytes)) : "0x" + Convert.ToHexString(addrBytes).ToLowerInvariant();
                Helpers.ShowDetailPanel("Name Registered", Color.Cyan1,
                    ("Name", name),
                    ("Owner", Helpers.FormatAddressFull(account.Address)),
                    ("Resolves to", resolved));
            }
        }
    }

    public static async Task Resolve(BasaltProvider provider)
    {
        var name = Helpers.PromptString("Name to resolve");

        var contract = GetContract(provider);
        var result = await contract.ReadSdkAsync(
            "Resolve", gasLimit: 100_000,
            args: [SdkContractEncoder.EncodeString(name)]);

        if (result.Success && result.ReturnData is { Length: > 0 })
        {
            var addrBytes = SdkContractEncoder.DecodeByteArray(Convert.FromHexString(result.ReturnData));
            if (addrBytes.Length == 20)
                AnsiConsole.MarkupLine($"[green]{Markup.Escape(name)}[/] -> [cyan]{Helpers.FormatAddressFull(new Address(addrBytes))}[/]");
            else
                AnsiConsole.MarkupLine($"[green]{Markup.Escape(name)}[/] -> 0x{Convert.ToHexString(addrBytes).ToLowerInvariant()}");
        }
        else if (result.Success)
        {
            AnsiConsole.MarkupLine("[yellow]Name not found.[/]");
        }
        else
        {
            Helpers.ShowError(result.Error ?? "unknown error");
        }
    }

    public static async Task SetAddress(BasaltProvider provider, IAccount account, HdWallet wallet)
    {
        var name = Helpers.PromptString("Name");
        var target = Helpers.PromptAddress("Target address", wallet);

        var contract = GetContract(provider);
        var result = await contract.CallSdkAsync(
            account, "SetAddress", gasLimit: 200_000,
            args: [
                SdkContractEncoder.EncodeString(name),
                SdkContractEncoder.EncodeBytes(target.ToArray()),
            ]);
        var txInfo = await Helpers.SubmitAndTrackAsync(provider, result, "BNS SetAddress");

        if (txInfo?.BlockNumber is not null)
            AnsiConsole.MarkupLine($"[green]{Markup.Escape(name)}[/] now resolves to [cyan]{Helpers.FormatAddressFull(target)}[/]");
    }

    public static async Task SetReverse(BasaltProvider provider, IAccount account)
    {
        var name = Helpers.PromptString("Name for reverse lookup");

        var contract = GetContract(provider);
        var result = await contract.CallSdkAsync(
            account, "SetReverse", gasLimit: 200_000,
            args: [SdkContractEncoder.EncodeString(name)]);
        var txInfo = await Helpers.SubmitAndTrackAsync(provider, result, "BNS SetReverse");

        if (txInfo?.BlockNumber is not null)
            AnsiConsole.MarkupLine($"[cyan]{Helpers.FormatAddress(account.Address)}[/] -> [green]{Markup.Escape(name)}[/]");
    }

    public static async Task ReverseLookup(BasaltProvider provider, HdWallet wallet)
    {
        var addr = Helpers.PromptAddress("Address to look up", wallet);

        var contract = GetContract(provider);
        var result = await contract.ReadSdkAsync(
            "ReverseLookup", gasLimit: 100_000,
            args: [SdkContractEncoder.EncodeBytes(addr.ToArray())]);

        if (result.Success && result.ReturnData is { Length: > 0 })
        {
            var name = SdkContractEncoder.DecodeString(Convert.FromHexString(result.ReturnData));
            AnsiConsole.MarkupLine($"[cyan]{Helpers.FormatAddressFull(addr)}[/] -> [green]{Markup.Escape(name)}[/]");
        }
        else if (result.Success)
        {
            AnsiConsole.MarkupLine("[yellow]No reverse record found.[/]");
        }
        else
        {
            Helpers.ShowError(result.Error ?? "unknown error");
        }
    }

    public static async Task TransferName(BasaltProvider provider, IAccount account, HdWallet wallet)
    {
        var name = Helpers.PromptString("Name to transfer");
        var newOwner = Helpers.PromptAddress("New owner", wallet);

        var contract = GetContract(provider);
        var result = await contract.CallSdkAsync(
            account, "TransferName", gasLimit: 200_000,
            args: [
                SdkContractEncoder.EncodeString(name),
                SdkContractEncoder.EncodeBytes(newOwner.ToArray()),
            ]);
        var txInfo = await Helpers.SubmitAndTrackAsync(provider, result, "BNS Transfer");

        if (txInfo?.BlockNumber is not null)
            AnsiConsole.MarkupLine($"[green]{Markup.Escape(name)}[/] transferred to [cyan]{Helpers.FormatAddressFull(newOwner)}[/]");
    }

    public static async Task OwnerOf(BasaltProvider provider)
    {
        var name = Helpers.PromptString("Name to query");

        var contract = GetContract(provider);
        var result = await contract.ReadSdkAsync(
            "OwnerOf", gasLimit: 100_000,
            args: [SdkContractEncoder.EncodeString(name)]);

        if (result.Success && result.ReturnData is { Length: > 0 })
        {
            var ownerBytes = SdkContractEncoder.DecodeByteArray(Convert.FromHexString(result.ReturnData));
            if (ownerBytes.Length == 20)
                AnsiConsole.MarkupLine($"Owner of [green]{Markup.Escape(name)}[/]: [cyan]{Helpers.FormatAddressFull(new Address(ownerBytes))}[/]");
            else
                AnsiConsole.MarkupLine($"Owner of [green]{Markup.Escape(name)}[/]: 0x{Convert.ToHexString(ownerBytes).ToLowerInvariant()}");
        }
        else if (result.Success)
        {
            AnsiConsole.MarkupLine("[yellow]Name not registered.[/]");
        }
        else
        {
            Helpers.ShowError(result.Error ?? "unknown error");
        }
    }
}
