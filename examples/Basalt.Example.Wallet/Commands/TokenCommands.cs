using Basalt.Core;
using Basalt.Sdk.Wallet;
using Basalt.Sdk.Wallet.Accounts;
using Basalt.Sdk.Wallet.Contracts;
using Basalt.Sdk.Wallet.HdWallet;
using Spectre.Console;

namespace Basalt.Example.Wallet.Commands;

public static class TokenCommands
{
    public static async Task<Address?> DeployBST20(BasaltProvider provider, IAccount account)
    {
        var name = Helpers.PromptString("Token name");
        var symbol = Helpers.PromptString("Token symbol");
        var decimals = Helpers.PromptByte("Decimals", 18);

        var manifest = SdkContractEncoder.BuildBST20Manifest(name, symbol, decimals);
        AnsiConsole.MarkupLine($"[dim]Manifest: {manifest.Length} bytes (type 0x0001, magic 0xBA5A)[/]");

        var deployNonce = await provider.GetNonceAsync(account.Address);
        var result = await provider.DeploySdkContractAsync(account, manifest, gasLimit: 1_000_000);
        var contractAddress = Helpers.DeriveContractAddress(account.Address, deployNonce);

        var txInfo = await Helpers.SubmitAndTrackAsync(provider, result, "Deploy BST-20");

        if (txInfo?.BlockNumber is not null)
        {
            Helpers.ShowDetailPanel($"BST-20 Token Deployed", Color.Magenta1,
                ("Contract", Helpers.FormatAddressFull(contractAddress)),
                ("Name", name),
                ("Symbol", symbol),
                ("Decimals", decimals.ToString()));
        }

        return contractAddress;
    }

    public static async Task<Address?> DeployBST721(BasaltProvider provider, IAccount account)
    {
        var name = Helpers.PromptString("Token name");
        var symbol = Helpers.PromptString("Token symbol");

        var manifest = SdkContractEncoder.BuildBST721Manifest(name, symbol);
        AnsiConsole.MarkupLine($"[dim]Manifest: {manifest.Length} bytes (type 0x0002, magic 0xBA5A)[/]");

        var deployNonce = await provider.GetNonceAsync(account.Address);
        var result = await provider.DeploySdkContractAsync(account, manifest, gasLimit: 1_000_000);
        var contractAddress = Helpers.DeriveContractAddress(account.Address, deployNonce);

        var txInfo = await Helpers.SubmitAndTrackAsync(provider, result, "Deploy BST-721");

        if (txInfo?.BlockNumber is not null)
        {
            Helpers.ShowDetailPanel($"BST-721 Token Deployed", Color.Magenta1,
                ("Contract", Helpers.FormatAddressFull(contractAddress)),
                ("Name", name),
                ("Symbol", symbol));
        }

        return contractAddress;
    }

    public static async Task TokenTransfer(BasaltProvider provider, IAccount account, HdWallet wallet, Address? contractAddress)
    {
        if (!contractAddress.HasValue)
        {
            Helpers.ShowError("No token deployed yet. Deploy a BST-20 or BST-721 first.");
            return;
        }

        var recipientAddr = Helpers.PromptAddress("Recipient", wallet);
        var tokenAmount = Helpers.PromptUInt64("Amount (token units)");

        // Check balances before
        var contract = provider.GetContract(contractAddress.Value);
        var senderBalBefore = await ReadTokenBalance(contract, account.Address);
        var recipBalBefore = await ReadTokenBalance(contract, recipientAddr);

        var result = await contract.CallSdkAsync(
            account, "Transfer", gasLimit: 200_000,
            args: [
                SdkContractEncoder.EncodeBytes(recipientAddr.ToArray()),
                SdkContractEncoder.EncodeUInt64(tokenAmount),
            ]);
        var txInfo = await Helpers.SubmitAndTrackAsync(provider, result, "Token Transfer");

        // Check balances after
        var senderBalAfter = await ReadTokenBalance(contract, account.Address);
        var recipBalAfter = await ReadTokenBalance(contract, recipientAddr);

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Magenta1);
        table.AddColumn("[bold]Account[/]");
        table.AddColumn("[bold]Before[/]");
        table.AddColumn("[bold]After[/]");
        table.AddRow("Sender", $"[dim]{senderBalBefore}[/]", $"[green]{senderBalAfter}[/]");
        table.AddRow("Recipient", $"[dim]{recipBalBefore}[/]", $"[green]{recipBalAfter}[/]");
        AnsiConsole.Write(table);
    }

    public static async Task TokenBalanceOf(BasaltProvider provider, HdWallet wallet, Address? contractAddress)
    {
        if (!contractAddress.HasValue)
        {
            Helpers.ShowError("No token deployed yet. Deploy a BST-20 or BST-721 first.");
            return;
        }

        var targetAddr = Helpers.PromptAddress("Address to query", wallet);
        var contract = provider.GetContract(contractAddress.Value);
        var result = await contract.ReadSdkAsync(
            "BalanceOf", gasLimit: 100_000,
            args: [SdkContractEncoder.EncodeBytes(targetAddr.ToArray())]);

        Helpers.ShowReadResult(result, data => $"{SdkContractEncoder.DecodeUInt64(data)} tokens");
    }

    public static async Task TokenMetadata(BasaltProvider provider, Address? contractAddress)
    {
        if (!contractAddress.HasValue)
        {
            Helpers.ShowError("No token deployed yet. Deploy a BST-20 or BST-721 first.");
            return;
        }

        var contract = provider.GetContract(contractAddress.Value);

        var rows = new List<(string, string)>();

        var nameResult = await contract.ReadSdkAsync("Name", gasLimit: 100_000);
        rows.Add(("Name", nameResult.Success && nameResult.ReturnData is { Length: > 0 }
            ? SdkContractEncoder.DecodeString(Convert.FromHexString(nameResult.ReturnData))
            : $"({nameResult.Error ?? "error"})"));

        var symbolResult = await contract.ReadSdkAsync("Symbol", gasLimit: 100_000);
        rows.Add(("Symbol", symbolResult.Success && symbolResult.ReturnData is { Length: > 0 }
            ? SdkContractEncoder.DecodeString(Convert.FromHexString(symbolResult.ReturnData))
            : $"({symbolResult.Error ?? "error"})"));

        var supplyResult = await contract.ReadSdkAsync("TotalSupply", gasLimit: 100_000);
        if (supplyResult.Success && supplyResult.ReturnData is { Length: > 0 })
            rows.Add(("Total Supply", SdkContractEncoder.DecodeUInt64(Convert.FromHexString(supplyResult.ReturnData)).ToString()));
        else if (supplyResult.Error?.Contains("Unknown selector") != true)
            rows.Add(("Total Supply", $"({supplyResult.Error ?? "error"})"));

        var decResult = await contract.ReadSdkAsync("Decimals", gasLimit: 100_000);
        if (decResult.Success && decResult.ReturnData is { Length: > 0 })
            rows.Add(("Decimals", SdkContractEncoder.DecodeByte(Convert.FromHexString(decResult.ReturnData)).ToString()));
        else if (decResult.Error?.Contains("Unknown selector") != true)
            rows.Add(("Decimals", $"({decResult.Error ?? "error"})"));

        Helpers.ShowDetailPanel("Token Metadata", Color.Magenta1, rows.ToArray());
    }

    private static async Task<string> ReadTokenBalance(ContractClient contract, Address addr)
    {
        var result = await contract.ReadSdkAsync(
            "BalanceOf", gasLimit: 100_000,
            args: [SdkContractEncoder.EncodeBytes(addr.ToArray())]);

        if (result.Success && result.ReturnData is { Length: > 0 })
            return SdkContractEncoder.DecodeUInt64(Convert.FromHexString(result.ReturnData)).ToString();
        return "0";
    }
}
