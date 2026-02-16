using System.Buffers.Binary;
using Basalt.Core;
using Basalt.Crypto;
using Basalt.Sdk.Wallet;
using Basalt.Sdk.Wallet.Accounts;
using Basalt.Sdk.Wallet.Contracts;
using Basalt.Sdk.Wallet.HdWallet;
using Basalt.Sdk.Wallet.Rpc;
using Basalt.Sdk.Wallet.Subscriptions;
using Basalt.Sdk.Wallet.Transactions;

// ─────────────────────────────────────────────────────────────────────
//  Basalt Wallet SDK — Example Application
//
//  This example demonstrates the main features of Basalt.Sdk.Wallet:
//    1. HD Wallet creation and account derivation
//    2. Account import/export and keystore encryption
//    3. Transaction building (transfers, staking, SDK contracts)
//    4. ABI encoding round-trips
//    5. Interactive wallet (deploy tokens, WBSLT, BNS, subscriptions)
//
//  Usage:
//    dotnet run                     — run offline examples only
//    dotnet run -- --node <url>     — run offline + enter interactive mode
// ─────────────────────────────────────────────────────────────────────

var nodeUrl = GetArg(args, "--node");

Console.WriteLine("=== Basalt Wallet SDK Example ===\n");

// ─── 1. HD Wallet ───────────────────────────────────────────────────

Console.WriteLine("--- 1. HD Wallet ---\n");

using var wallet = HdWallet.Create(24);
Console.WriteLine($"Mnemonic ({wallet.MnemonicPhrase!.Split(' ').Length} words):");
Console.WriteLine($"  {wallet.MnemonicPhrase}\n");

// Derive several accounts
for (uint i = 0; i < 3; i++)
{
    var acct = wallet.GetAccount(i);
    Console.WriteLine($"Account {i}: {FormatAddress(acct.Address)}  (path: m/44'/9000'/0'/0'/{i}')");
}

Console.WriteLine();

// Recover the same wallet and verify determinism
using var recovered = HdWallet.FromMnemonic(wallet.MnemonicPhrase);
var original0 = wallet.GetAccount(0).Address;
var recovered0 = recovered.GetAccount(0).Address;
Console.WriteLine($"Recovery check: {(original0.Equals(recovered0) ? "OK — addresses match" : "MISMATCH!")}\n");

// Derive a validator account (Ed25519 + BLS)
var validator = wallet.GetValidatorAccount(0);
Console.WriteLine($"Validator account: {FormatAddress(validator.Address)}");
Console.WriteLine($"  BLS public key:  {Convert.ToHexString(validator.BlsPublicKey).ToLowerInvariant()[..32]}...");
Console.WriteLine($"  BLS key size:    {validator.BlsPublicKey.Length} bytes (compressed G1)\n");

// ─── 2. Account Management ─────────────────────────────────────────

Console.WriteLine("--- 2. Account Management ---\n");

// Standalone account
using var standalone = Account.Create();
Console.WriteLine($"New account:     {FormatAddress(standalone.Address)}");

// Import from hex key
var (privKey, _) = Ed25519Signer.GenerateKeyPair();
using var imported = Account.FromPrivateKey(Convert.ToHexString(privKey));
Console.WriteLine($"Imported account: {FormatAddress(imported.Address)}");

// Sign a message
var message = "Hello, Basalt!"u8;
var sig = standalone.SignMessage(message);
Console.WriteLine($"Message signature: {sig.ToString()[..32]}...");

// Keystore encryption
var keystorePath = Path.Combine(Path.GetTempPath(), $"basalt-example-{Guid.NewGuid():N}.json");
await KeystoreManager.SaveAsync(privKey, imported.Address, "demo-password", keystorePath);
Console.WriteLine($"\nKeystore saved:  {keystorePath}");

using var loaded = await KeystoreManager.LoadAsync(keystorePath, "demo-password");
Console.WriteLine($"Keystore loaded: {FormatAddress(loaded.Address)}");
Console.WriteLine($"Address match:   {(loaded.Address.Equals(imported.Address) ? "OK" : "MISMATCH!")}");

File.Delete(keystorePath);
Console.WriteLine();

// Multi-account manager
using var manager = new AccountManager();
manager.Add(wallet.GetAccount(0));
manager.Add(wallet.GetAccount(1));
manager.Add(wallet.GetAccount(2));
Console.WriteLine($"Account manager: {manager.GetAll().Count} accounts");
Console.WriteLine($"Active account:  {FormatAddress(manager.ActiveAccount!.Address)}");
manager.SetActive(wallet.GetAccount(2).Address);
Console.WriteLine($"Switched active: {FormatAddress(manager.ActiveAccount!.Address)}\n");

// ─── 3. Transaction Building ────────────────────────────────────────

Console.WriteLine("--- 3. Transaction Building ---\n");

var sender = wallet.GetAccount(0);
var recipient = wallet.GetAccount(1).Address;

// Transfer
var transferTx = TransactionBuilder.Transfer()
    .WithNonce(0)
    .WithSender(sender.Address)
    .WithTo(recipient)
    .WithValue(new UInt256(1_000_000_000_000_000_000)) // 1 BSLT
    .WithGasLimit(21_000)
    .WithGasPrice(UInt256.One)
    .WithChainId(4242)
    .Build();

var signedTransfer = sender.SignTransaction(transferTx);
Console.WriteLine($"Transfer tx:     {signedTransfer.Type} | {signedTransfer.Value} → {FormatAddress(signedTransfer.To)}");

// Convenience transfer builder
var quickTx = new TransferBuilder(recipient, new UInt256(500))
    .WithChainId(4242)
    .WithNonce(1)
    .Build();
Console.WriteLine($"Quick transfer:  {quickTx.Value} to {FormatAddress(quickTx.To)}");

// SDK contract deploy — BST-20 token
var bst20Manifest = SdkContractEncoder.BuildBST20Manifest("ExampleToken", "EXT", 18);
var deployTx = new ContractDeployBuilder(bst20Manifest)
    .WithGasLimit(500_000)
    .WithChainId(4242)
    .WithNonce(2)
    .Build();
Console.WriteLine($"BST-20 deploy:   {deployTx.Type} | manifest: {deployTx.Data.Length} bytes (0xBA5A magic)");

// SDK contract call — FNV-1a selector
var contractAddr = new Address(new byte[20] { 0xCA, 0xFE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01 });
var fnvSelector = SdkContractEncoder.ComputeFnvSelector("Transfer");
var sdkCallTx = new ContractCallBuilder(contractAddr, fnvSelector)
    .WithArgs(SdkContractEncoder.EncodeBytes(recipient.ToArray()), SdkContractEncoder.EncodeUInt64(1000))
    .WithGasLimit(100_000)
    .WithChainId(4242)
    .WithNonce(3)
    .Build();
Console.WriteLine($"SDK call tx:     {sdkCallTx.Type} | FNV-1a selector: 0x{Convert.ToHexString(sdkCallTx.Data[..4]).ToLowerInvariant()} | data: {sdkCallTx.Data.Length} bytes");

// Staking
var stakeTx = StakingBuilder.Deposit(new UInt256(100_000))
    .WithChainId(4242)
    .WithNonce(4)
    .Build();
Console.WriteLine($"Stake deposit:   {stakeTx.Type} | amount: {stakeTx.Value}");

var registerTx = StakingBuilder.RegisterValidator(validator.BlsPublicKey)
    .WithChainId(4242)
    .WithNonce(5)
    .Build();
Console.WriteLine($"Register val:    {registerTx.Type} | BLS key in data: {registerTx.Data.Length} bytes");

// ─── 4. ABI Encoding ────────────────────────────────────────────────

Console.WriteLine("\n--- 4. ABI Encoding ---\n");

// BLAKE3 selectors (built-in methods)
var blake3Selector = AbiEncoder.ComputeSelector("transfer");
Console.WriteLine($"BLAKE3 selector for 'transfer':    0x{Convert.ToHexString(blake3Selector).ToLowerInvariant()}");

// FNV-1a selectors (SDK contracts)
var fnv1aSelector = SdkContractEncoder.ComputeFnvSelector("Transfer");
Console.WriteLine($"FNV-1a selector for 'Transfer':    0x{Convert.ToHexString(fnv1aSelector).ToLowerInvariant()}");

// Round-trip encoding
var amount = new UInt256(42_000_000);
var encodedAmount = AbiEncoder.EncodeUInt256(amount);
var offset = 0;
var decoded = AbiEncoder.DecodeUInt256(encodedAmount, ref offset);
Console.WriteLine($"\nUInt256 round-trip: {amount} → {encodedAmount.Length} bytes → {decoded} (match: {amount == decoded})");

var encodedAddr = AbiEncoder.EncodeAddress(recipient);
offset = 0;
var decodedAddr = AbiEncoder.DecodeAddress(encodedAddr, ref offset);
Console.WriteLine($"Address round-trip: {FormatAddress(recipient)} → {encodedAddr.Length} bytes → {FormatAddress(decodedAddr)} (match: {recipient.Equals(decodedAddr)})");

var encodedStr = AbiEncoder.EncodeString("Hello, Basalt!");
offset = 0;
var decodedStr = AbiEncoder.DecodeString(encodedStr, ref offset);
Console.WriteLine($"String round-trip:  \"{decodedStr}\" ({encodedStr.Length} bytes, 4-byte length prefix + UTF-8)");

Console.WriteLine();

// ─── 5. Interactive Wallet ──────────────────────────────────────────

if (nodeUrl is null)
{
    Console.WriteLine("--- 5. Interactive Wallet ---\n");
    Console.WriteLine("Skipped — pass --node <url> to enter interactive mode.");
    Console.WriteLine("Example: dotnet run -- --node http://localhost:5100\n");
}
else
{
    manager.SetActive(wallet.GetAccount(0).Address);
    using var provider = new BasaltProvider(nodeUrl, chainId: 31337);
    await InteractiveLoop(provider, wallet, manager, nodeUrl);
}

Console.WriteLine("=== Done ===");
return;

// ═════════════════════════════════════════════════════════════════════
//  Interactive Wallet Loop
// ═════════════════════════════════════════════════════════════════════

async Task InteractiveLoop(BasaltProvider provider, HdWallet hdWallet, AccountManager acctMgr, string baseUrl)
{
    Address? deployedToken = null;

    Console.WriteLine("=== Basalt Interactive Wallet ===\n");

    var status = await provider.GetStatusAsync();
    Console.WriteLine($"Connected to {baseUrl} (chain {provider.ChainId})");
    Console.WriteLine($"Node at block #{status.BlockHeight}, protocol v{status.ProtocolVersion}\n");

    while (true)
    {
        var active = acctMgr.ActiveAccount!;
        Console.WriteLine($"Active: {FormatAddress(active.Address)}");
        if (deployedToken.HasValue)
            Console.WriteLine($"Token:  {FormatAddress(deployedToken.Value)}");
        Console.WriteLine();
        Console.WriteLine("  -- General --");
        Console.WriteLine("   1) Show status");
        Console.WriteLine("   2) List accounts & balances");
        Console.WriteLine("   3) Switch account");
        Console.WriteLine("   4) Request faucet");
        Console.WriteLine("   5) Transfer BSLT");
        Console.WriteLine();
        Console.WriteLine("  -- Deploy Contracts --");
        Console.WriteLine("   6) Deploy BST-20 token");
        Console.WriteLine("   7) Deploy BST-721 token");
        Console.WriteLine();
        Console.WriteLine("  -- Token Interaction --");
        Console.WriteLine("   8) Token: Transfer");
        Console.WriteLine("   9) Token: BalanceOf");
        Console.WriteLine("  10) Token: Name / Symbol / TotalSupply");
        Console.WriteLine();
        Console.WriteLine("  -- System Contracts --");
        Console.WriteLine("  11) WBSLT: Deposit (wrap BSLT)");
        Console.WriteLine("  12) WBSLT: Withdraw (unwrap)");
        Console.WriteLine("  13) WBSLT: BalanceOf");
        Console.WriteLine("  14) BNS: Register name");
        Console.WriteLine("  15) BNS: Resolve name");
        Console.WriteLine();
        Console.WriteLine("  -- Utilities --");
        Console.WriteLine("  16) Transaction lookup");
        Console.WriteLine("  17) Subscribe to blocks");
        Console.WriteLine("   0) Exit");
        Console.Write("\n> ");

        var input = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(input)) continue;

        Console.WriteLine();

        try
        {
            switch (input)
            {
                case "0": return;
                case "1": await CmdShowStatus(provider); break;
                case "2": await CmdListAccounts(provider, acctMgr); break;
                case "3": CmdSwitchAccount(acctMgr, hdWallet); break;
                case "4": await CmdFaucet(provider, acctMgr.ActiveAccount!); break;
                case "5": await CmdTransfer(provider, acctMgr, hdWallet); break;
                case "6": deployedToken = await CmdDeployBST20(provider, acctMgr.ActiveAccount!); break;
                case "7": deployedToken = await CmdDeployBST721(provider, acctMgr.ActiveAccount!); break;
                case "8": await CmdTokenTransfer(provider, acctMgr.ActiveAccount!, hdWallet, deployedToken); break;
                case "9": await CmdTokenBalanceOf(provider, hdWallet, deployedToken); break;
                case "10": await CmdTokenMetadata(provider, deployedToken); break;
                case "11": await CmdWBSLTDeposit(provider, acctMgr.ActiveAccount!); break;
                case "12": await CmdWBSLTWithdraw(provider, acctMgr.ActiveAccount!); break;
                case "13": await CmdWBSLTBalanceOf(provider, hdWallet); break;
                case "14": await CmdBNSRegister(provider, acctMgr.ActiveAccount!); break;
                case "15": await CmdBNSResolve(provider); break;
                case "16": await CmdTxLookup(provider); break;
                case "17": await CmdSubscribe(baseUrl); break;
                default: Console.WriteLine("Unknown option."); break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }

        Console.WriteLine();
    }
}

// ── 1) Show Status ──────────────────────────────────────────────────

async Task CmdShowStatus(BasaltProvider provider)
{
    var s = await provider.GetStatusAsync();
    Console.WriteLine("Node Status:");
    Console.WriteLine($"  Block height:  {s.BlockHeight}");
    Console.WriteLine($"  Latest hash:   {s.LatestBlockHash}");
    Console.WriteLine($"  Mempool size:  {s.MempoolSize}");
    Console.WriteLine($"  Protocol:      v{s.ProtocolVersion}");

    var block = await provider.GetLatestBlockAsync();
    Console.WriteLine($"\nLatest block #{block.Number}:");
    Console.WriteLine($"  Hash:     {block.Hash}");
    Console.WriteLine($"  Proposer: {block.Proposer}");
    Console.WriteLine($"  Txs:      {block.TransactionCount}");
    Console.WriteLine($"  Gas:      {block.GasUsed} / {block.GasLimit}");
}

// ── 2) List Accounts ────────────────────────────────────────────────

async Task CmdListAccounts(BasaltProvider provider, AccountManager acctMgr)
{
    Console.WriteLine("Accounts:");
    var index = 0;
    foreach (var acct in acctMgr.GetAll())
    {
        var balance = await provider.GetBalanceAsync(acct.Address);
        var nonce = await provider.GetNonceAsync(acct.Address);
        var isActive = acct.Address.Equals(acctMgr.ActiveAccount!.Address) ? " *" : "";
        Console.WriteLine($"  [{index}] {FormatAddress(acct.Address)}  balance: {FormatBslt(balance)}  nonce: {nonce}{isActive}");
        index++;
    }
}

// ── 3) Switch Account ───────────────────────────────────────────────

void CmdSwitchAccount(AccountManager acctMgr, HdWallet hdWallet)
{
    Console.Write("Account index (0-2): ");
    var idx = Console.ReadLine()?.Trim();
    if (!uint.TryParse(idx, out var i) || i > 2)
    {
        Console.WriteLine("Invalid index.");
        return;
    }
    acctMgr.SetActive(hdWallet.GetAccount(i).Address);
    Console.WriteLine($"Switched to account {i}: {FormatAddress(acctMgr.ActiveAccount!.Address)}");
}

// ── 4) Request Faucet ───────────────────────────────────────────────

async Task CmdFaucet(BasaltProvider provider, IAccount account)
{
    Console.WriteLine("Requesting testnet tokens...");
    var result = await provider.RequestFaucetAsync(account.Address);
    Console.WriteLine($"  Success: {result.Success}");
    Console.WriteLine($"  Message: {result.Message}");
    if (result.TxHash is not null)
        Console.WriteLine($"  Tx hash: {result.TxHash}");

    Console.WriteLine("  Waiting 5s for block inclusion...");
    await Task.Delay(5000);

    var balance = await provider.GetBalanceAsync(account.Address);
    Console.WriteLine($"  New balance: {FormatBslt(balance)}");
}

// ── 5) Transfer ─────────────────────────────────────────────────────

async Task CmdTransfer(BasaltProvider provider, AccountManager acctMgr, HdWallet hdWallet)
{
    var senderAccount = acctMgr.ActiveAccount!;

    Console.Write("Recipient (account index 0-2, or 0x hex address): ");
    var recipInput = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(recipInput))
    {
        Console.WriteLine("Cancelled.");
        return;
    }

    Address recipientAddr;
    if (uint.TryParse(recipInput, out var recipIdx) && recipIdx <= 2)
    {
        recipientAddr = hdWallet.GetAccount(recipIdx).Address;
    }
    else if (recipInput.StartsWith("0x") && recipInput.Length == 42)
    {
        recipientAddr = new Address(Convert.FromHexString(recipInput[2..]));
    }
    else
    {
        Console.WriteLine("Invalid recipient.");
        return;
    }

    Console.Write("Amount in BSLT (e.g. 1, 0.5, 10): ");
    var amountInput = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(amountInput) || !TryParseBsltToWei(amountInput, out var weiAmount))
    {
        Console.WriteLine("Invalid amount.");
        return;
    }

    var senderBal = await provider.GetBalanceAsync(senderAccount.Address);
    var recipBal = await provider.GetBalanceAsync(recipientAddr);
    Console.WriteLine($"\nBefore:");
    Console.WriteLine($"  Sender:    {FormatBslt(senderBal)}");
    Console.WriteLine($"  Recipient: {FormatBslt(recipBal)}");

    Console.WriteLine($"\nSending {amountInput} BSLT to {FormatAddress(recipientAddr)}...");
    var result = await provider.TransferAsync(senderAccount, recipientAddr, weiAmount);
    Console.WriteLine($"  Tx hash: {result.Hash}");
    Console.WriteLine($"  Status:  {result.Status}");

    Console.WriteLine("  Waiting 5s for block inclusion...");
    await Task.Delay(5000);

    senderBal = await provider.GetBalanceAsync(senderAccount.Address);
    recipBal = await provider.GetBalanceAsync(recipientAddr);
    Console.WriteLine($"\nAfter:");
    Console.WriteLine($"  Sender:    {FormatBslt(senderBal)}");
    Console.WriteLine($"  Recipient: {FormatBslt(recipBal)}");
}

// ── 6) Deploy BST-20 Token ──────────────────────────────────────────

async Task<Address?> CmdDeployBST20(BasaltProvider provider, IAccount account)
{
    Console.Write("Token name: ");
    var name = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(name)) { Console.WriteLine("Cancelled."); return null; }

    Console.Write("Token symbol: ");
    var symbol = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(symbol)) { Console.WriteLine("Cancelled."); return null; }

    Console.Write("Decimals (default 18): ");
    var decStr = Console.ReadLine()?.Trim();
    byte decimals = string.IsNullOrEmpty(decStr) ? (byte)18 : byte.Parse(decStr);

    var manifest = SdkContractEncoder.BuildBST20Manifest(name, symbol, decimals);
    Console.WriteLine($"\n  Manifest: {manifest.Length} bytes (type 0x0001, magic 0xBA5A)");

    var deployNonce = await provider.GetNonceAsync(account.Address);
    var result = await provider.DeploySdkContractAsync(account, manifest, gasLimit: 1_000_000);
    Console.WriteLine($"  Tx hash: {result.Hash}");
    Console.WriteLine($"  Status:  {result.Status}");

    var contractAddress = DeriveContractAddress(account.Address, deployNonce);
    Console.WriteLine($"  Contract address: {FormatAddress(contractAddress)}");

    Console.WriteLine("  Waiting 5s for block inclusion...");
    await Task.Delay(5000);

    Console.WriteLine($"\n  Note: BST-20 has no public Mint. Use WBSLT.Deposit to mint wrapped tokens.");
    return contractAddress;
}

// ── 7) Deploy BST-721 Token ─────────────────────────────────────────

async Task<Address?> CmdDeployBST721(BasaltProvider provider, IAccount account)
{
    Console.Write("Token name: ");
    var name = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(name)) { Console.WriteLine("Cancelled."); return null; }

    Console.Write("Token symbol: ");
    var symbol = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(symbol)) { Console.WriteLine("Cancelled."); return null; }

    var manifest = SdkContractEncoder.BuildBST721Manifest(name, symbol);
    Console.WriteLine($"\n  Manifest: {manifest.Length} bytes (type 0x0002, magic 0xBA5A)");

    var deployNonce = await provider.GetNonceAsync(account.Address);
    var result = await provider.DeploySdkContractAsync(account, manifest, gasLimit: 1_000_000);
    Console.WriteLine($"  Tx hash: {result.Hash}");
    Console.WriteLine($"  Status:  {result.Status}");

    var contractAddress = DeriveContractAddress(account.Address, deployNonce);
    Console.WriteLine($"  Contract address: {FormatAddress(contractAddress)}");

    Console.WriteLine("  Waiting 5s for block inclusion...");
    await Task.Delay(5000);

    return contractAddress;
}

// ── 8) Token: Transfer ──────────────────────────────────────────────

async Task CmdTokenTransfer(BasaltProvider provider, IAccount account, HdWallet hdWallet, Address? contractAddress)
{
    if (!contractAddress.HasValue)
    {
        Console.WriteLine("No token deployed yet. Use option 6 or 7 first.");
        return;
    }

    Console.Write("Recipient (account index 0-2, or 0x hex): ");
    var recipInput = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(recipInput)) { Console.WriteLine("Cancelled."); return; }

    Address recipientAddr;
    if (uint.TryParse(recipInput, out var recipIdx) && recipIdx <= 2)
        recipientAddr = hdWallet.GetAccount(recipIdx).Address;
    else if (recipInput.StartsWith("0x") && recipInput.Length == 42)
        recipientAddr = new Address(Convert.FromHexString(recipInput[2..]));
    else { Console.WriteLine("Invalid recipient."); return; }

    Console.Write("Amount (token units): ");
    var amountStr = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(amountStr) || !ulong.TryParse(amountStr, out var tokenAmount))
    {
        Console.WriteLine("Invalid amount.");
        return;
    }

    Console.WriteLine($"\n  Transferring {tokenAmount} tokens to {FormatAddress(recipientAddr)}...");
    var contract = provider.GetContract(contractAddress.Value);
    var result = await contract.CallSdkAsync(
        account,
        "Transfer",
        gasLimit: 200_000,
        args: [
            SdkContractEncoder.EncodeBytes(recipientAddr.ToArray()),
            SdkContractEncoder.EncodeUInt64(tokenAmount),
        ]);
    Console.WriteLine($"  Tx hash: {result.Hash}");
    Console.WriteLine($"  Status:  {result.Status}");
}

// ── 9) Token: BalanceOf ─────────────────────────────────────────────

async Task CmdTokenBalanceOf(BasaltProvider provider, HdWallet hdWallet, Address? contractAddress)
{
    if (!contractAddress.HasValue)
    {
        Console.WriteLine("No token deployed yet. Use option 6 or 7 first.");
        return;
    }

    Console.Write("Address to query (account index 0-2, or 0x hex): ");
    var addrInput = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(addrInput)) { Console.WriteLine("Cancelled."); return; }

    Address targetAddr;
    if (uint.TryParse(addrInput, out var idx) && idx <= 2)
        targetAddr = hdWallet.GetAccount(idx).Address;
    else if (addrInput.StartsWith("0x") && addrInput.Length == 42)
        targetAddr = new Address(Convert.FromHexString(addrInput[2..]));
    else { Console.WriteLine("Invalid address."); return; }

    var contract = provider.GetContract(contractAddress.Value);
    var result = await contract.ReadSdkAsync(
        "BalanceOf",
        gasLimit: 100_000,
        args: [SdkContractEncoder.EncodeBytes(targetAddr.ToArray())]);

    if (result.Success && result.ReturnData is { Length: > 0 })
    {
        var returnBytes = Convert.FromHexString(result.ReturnData);
        var balance = SdkContractEncoder.DecodeUInt64(returnBytes);
        Console.WriteLine($"  Balance: {balance}");
    }
    else if (result.Success)
    {
        Console.WriteLine("  Balance: 0 (empty return)");
    }
    else
    {
        Console.WriteLine($"  Error: {result.Error}");
    }
    Console.WriteLine($"  Gas used: {result.GasUsed}");
}

// ── 10) Token: Metadata ─────────────────────────────────────────────

async Task CmdTokenMetadata(BasaltProvider provider, Address? contractAddress)
{
    if (!contractAddress.HasValue)
    {
        Console.WriteLine("No token deployed yet. Use option 6 or 7 first.");
        return;
    }

    var contract = provider.GetContract(contractAddress.Value);

    Console.WriteLine("Querying token metadata...\n");

    // Name
    var nameResult = await contract.ReadSdkAsync("Name", gasLimit: 100_000);
    if (nameResult.Success && nameResult.ReturnData is { Length: > 0 })
        Console.WriteLine($"  Name:         {SdkContractEncoder.DecodeString(Convert.FromHexString(nameResult.ReturnData))}");
    else
        Console.WriteLine($"  Name:         (error: {nameResult.Error})");

    // Symbol
    var symbolResult = await contract.ReadSdkAsync("Symbol", gasLimit: 100_000);
    if (symbolResult.Success && symbolResult.ReturnData is { Length: > 0 })
        Console.WriteLine($"  Symbol:       {SdkContractEncoder.DecodeString(Convert.FromHexString(symbolResult.ReturnData))}");
    else
        Console.WriteLine($"  Symbol:       (error: {symbolResult.Error})");

    // TotalSupply
    var supplyResult = await contract.ReadSdkAsync("TotalSupply", gasLimit: 100_000);
    if (supplyResult.Success && supplyResult.ReturnData is { Length: > 0 })
        Console.WriteLine($"  TotalSupply:  {SdkContractEncoder.DecodeUInt64(Convert.FromHexString(supplyResult.ReturnData))}");
    else
        Console.WriteLine($"  TotalSupply:  (error: {supplyResult.Error})");

    // Decimals
    var decResult = await contract.ReadSdkAsync("Decimals", gasLimit: 100_000);
    if (decResult.Success && decResult.ReturnData is { Length: > 0 })
        Console.WriteLine($"  Decimals:     {SdkContractEncoder.DecodeByte(Convert.FromHexString(decResult.ReturnData))}");
    else
        Console.WriteLine($"  Decimals:     (error: {decResult.Error})");
}

// ── 11) WBSLT: Deposit ──────────────────────────────────────────────

async Task CmdWBSLTDeposit(BasaltProvider provider, IAccount account)
{
    Console.Write("Amount of BSLT to wrap: ");
    var amountInput = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(amountInput) || !TryParseBsltToWei(amountInput, out var weiAmount))
    {
        Console.WriteLine("Invalid amount.");
        return;
    }

    Console.WriteLine($"\n  Wrapping {amountInput} BSLT into WBSLT...");
    var wbsltAddr = BasaltProvider.SystemContracts.WBSLT;
    var contract = provider.GetContract(wbsltAddr);
    var result = await contract.CallSdkAsync(
        account,
        "Deposit",
        gasLimit: 200_000,
        value: weiAmount);

    Console.WriteLine($"  Tx hash: {result.Hash}");
    Console.WriteLine($"  Status:  {result.Status}");
    Console.WriteLine($"  WBSLT contract: {FormatAddress(wbsltAddr)}");
}

// ── 12) WBSLT: Withdraw ─────────────────────────────────────────────

async Task CmdWBSLTWithdraw(BasaltProvider provider, IAccount account)
{
    Console.Write("Amount of WBSLT to unwrap (in wei): ");
    var amountStr = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(amountStr) || !ulong.TryParse(amountStr, out var weiAmount))
    {
        Console.WriteLine("Invalid amount.");
        return;
    }

    Console.WriteLine($"\n  Unwrapping {weiAmount} wei WBSLT back to native BSLT...");
    var contract = provider.GetContract(BasaltProvider.SystemContracts.WBSLT);
    var result = await contract.CallSdkAsync(
        account,
        "Withdraw",
        gasLimit: 200_000,
        args: [SdkContractEncoder.EncodeUInt64(weiAmount)]);

    Console.WriteLine($"  Tx hash: {result.Hash}");
    Console.WriteLine($"  Status:  {result.Status}");
}

// ── 13) WBSLT: BalanceOf ────────────────────────────────────────────

async Task CmdWBSLTBalanceOf(BasaltProvider provider, HdWallet hdWallet)
{
    Console.Write("Address to query (account index 0-2, or 0x hex): ");
    var addrInput = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(addrInput)) { Console.WriteLine("Cancelled."); return; }

    Address targetAddr;
    if (uint.TryParse(addrInput, out var idx) && idx <= 2)
        targetAddr = hdWallet.GetAccount(idx).Address;
    else if (addrInput.StartsWith("0x") && addrInput.Length == 42)
        targetAddr = new Address(Convert.FromHexString(addrInput[2..]));
    else { Console.WriteLine("Invalid address."); return; }

    var contract = provider.GetContract(BasaltProvider.SystemContracts.WBSLT);
    var result = await contract.ReadSdkAsync(
        "BalanceOf",
        gasLimit: 100_000,
        args: [SdkContractEncoder.EncodeBytes(targetAddr.ToArray())]);

    if (result.Success && result.ReturnData is { Length: > 0 })
    {
        var balance = SdkContractEncoder.DecodeUInt64(Convert.FromHexString(result.ReturnData));
        Console.WriteLine($"  WBSLT Balance: {balance} wei");
    }
    else if (result.Success)
    {
        Console.WriteLine("  WBSLT Balance: 0");
    }
    else
    {
        Console.WriteLine($"  Error: {result.Error}");
    }
}

// ── 14) BNS: Register ───────────────────────────────────────────────

async Task CmdBNSRegister(BasaltProvider provider, IAccount account)
{
    Console.Write("Name to register: ");
    var name = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(name)) { Console.WriteLine("Cancelled."); return; }

    // Default registration fee is 1_000_000_000 wei
    var fee = new UInt256(1_000_000_000);
    Console.WriteLine($"\n  Registering \"{name}\" (fee: {fee} wei)...");

    var contract = provider.GetContract(BasaltProvider.SystemContracts.NameService);
    var result = await contract.CallSdkAsync(
        account,
        "Register",
        gasLimit: 200_000,
        value: fee,
        args: [SdkContractEncoder.EncodeString(name)]);

    Console.WriteLine($"  Tx hash: {result.Hash}");
    Console.WriteLine($"  Status:  {result.Status}");
}

// ── 15) BNS: Resolve ────────────────────────────────────────────────

async Task CmdBNSResolve(BasaltProvider provider)
{
    Console.Write("Name to resolve: ");
    var name = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(name)) { Console.WriteLine("Cancelled."); return; }

    var contract = provider.GetContract(BasaltProvider.SystemContracts.NameService);
    var result = await contract.ReadSdkAsync(
        "Resolve",
        gasLimit: 100_000,
        args: [SdkContractEncoder.EncodeString(name)]);

    if (result.Success && result.ReturnData is { Length: > 0 })
    {
        var addrBytes = SdkContractEncoder.DecodeByteArray(Convert.FromHexString(result.ReturnData));
        if (addrBytes.Length == 20)
            Console.WriteLine($"  Resolved: {FormatAddress(new Address(addrBytes))}");
        else
            Console.WriteLine($"  Resolved: 0x{Convert.ToHexString(addrBytes).ToLowerInvariant()}");
    }
    else if (result.Success)
    {
        Console.WriteLine("  Name not found.");
    }
    else
    {
        Console.WriteLine($"  Error: {result.Error}");
    }
}

// ── 16) Transaction Lookup ──────────────────────────────────────────

async Task CmdTxLookup(BasaltProvider provider)
{
    Console.Write("Tx hash (0x...): ");
    var hash = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(hash))
    {
        Console.WriteLine("Cancelled.");
        return;
    }

    var tx = await provider.GetTransactionAsync(hash);
    if (tx is null)
    {
        Console.WriteLine("Transaction not found.");
        return;
    }

    Console.WriteLine($"Transaction {hash}:");
    Console.WriteLine($"  Type:   {tx.Type}");
    Console.WriteLine($"  From:   {tx.Sender}");
    Console.WriteLine($"  To:     {tx.To}");
    Console.WriteLine($"  Value:  {tx.Value}");
    Console.WriteLine($"  Nonce:  {tx.Nonce}");
    Console.WriteLine($"  Gas:    {tx.GasLimit}");
    if (tx.BlockNumber is not null)
        Console.WriteLine($"  Block:  #{tx.BlockNumber}");
}

// ── 17) Subscribe to Blocks ─────────────────────────────────────────

async Task CmdSubscribe(string baseUrl)
{
    Console.Write("Duration in seconds (default 15): ");
    var durInput = Console.ReadLine()?.Trim();
    if (!int.TryParse(durInput, out var seconds) || seconds <= 0)
        seconds = 15;

    Console.WriteLine($"Subscribing to new blocks ({seconds}s)...");

    await using var subscription = BasaltProvider.CreateBlockSubscription(baseUrl,
        new SubscriptionOptions { AutoReconnect = false });

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));

    try
    {
        await foreach (var blockEvent in subscription.SubscribeAsync(cts.Token))
        {
            Console.WriteLine($"  [{blockEvent.Type}] Block #{blockEvent.Block.Number} — {blockEvent.Block.TransactionCount} txs, gas: {blockEvent.Block.GasUsed}");
        }
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("  (timeout)");
    }
}

// ═════════════════════════════════════════════════════════════════════
//  Helpers
// ═════════════════════════════════════════════════════════════════════

static string FormatAddress(Address addr)
{
    var hex = Convert.ToHexString(addr.ToArray()).ToLowerInvariant();
    return $"0x{hex[..8]}...{hex[^8..]}";
}

static string? GetArg(string[] args, string flag)
{
    var idx = Array.IndexOf(args, flag);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}

static string FormatBslt(string weiBalance)
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

static bool TryParseBsltToWei(string bslt, out UInt256 wei)
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

/// <summary>
/// Derive a contract address from sender + nonce, matching the chain's algorithm.
/// BLAKE3(sender || nonce) last 20 bytes.
/// </summary>
static Address DeriveContractAddress(Address senderAddr, ulong nonce)
{
    Span<byte> input = stackalloc byte[Address.Size + 8];
    senderAddr.WriteTo(input[..Address.Size]);
    BinaryPrimitives.WriteUInt64LittleEndian(input[Address.Size..], nonce);
    var hash = Blake3Hasher.Hash(input);
    Span<byte> hashBytes = stackalloc byte[Hash256.Size];
    hash.WriteTo(hashBytes);
    return new Address(hashBytes[12..]);
}
