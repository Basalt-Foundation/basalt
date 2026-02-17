using Basalt.Core;
using Basalt.Crypto;
using Basalt.Example.Wallet;
using Basalt.Sdk.Wallet;
using Basalt.Sdk.Wallet.Accounts;
using Basalt.Sdk.Wallet.Contracts;
using Basalt.Sdk.Wallet.HdWallet;
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
    using var provider = new BasaltProvider(nodeUrl, chainId: 4242);
    await WalletApp.RunAsync(provider, wallet, manager, nodeUrl);
}

Console.WriteLine("=== Done ===");
return;

// ═════════════════════════════════════════════════════════════════════
//  Helpers (used by offline sections above)
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
