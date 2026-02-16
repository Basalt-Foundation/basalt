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
//    3. Transaction building (transfers, staking, contracts)
//    4. ABI encoding round-trips
//    5. Interactive wallet (transfers, contract deploy/call, subscriptions)
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

// Contract call
var contractAddr = new Address(new byte[20] { 0xCA, 0xFE, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x01 });
var callTx = new ContractCallBuilder(contractAddr, "storage_set")
    .WithArgs(new byte[32], new byte[] { 1, 2, 3 }) // key + value
    .WithGasLimit(100_000)
    .WithChainId(4242)
    .WithNonce(2)
    .Build();
Console.WriteLine($"Contract call:   {callTx.Type} | selector: {Convert.ToHexString(callTx.Data[..4]).ToLowerInvariant()} | data: {callTx.Data.Length} bytes");

// Contract deploy
var bytecode = new byte[] { 0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00 }; // minimal wasm header
var deployTx = new ContractDeployBuilder(bytecode)
    .WithGasLimit(500_000)
    .WithChainId(4242)
    .WithNonce(3)
    .Build();
Console.WriteLine($"Contract deploy: {deployTx.Type} | bytecode: {deployTx.Data.Length} bytes");

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

var selector = AbiEncoder.ComputeSelector("transfer");
Console.WriteLine($"Selector for 'transfer':    0x{Convert.ToHexString(selector).ToLowerInvariant()}");
Console.WriteLine($"Selector for 'storage_set': 0x{Convert.ToHexString(AbiEncoder.ComputeSelector("storage_set")).ToLowerInvariant()}");

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

// Full call encoding
var callData = AbiEncoder.EncodeCall("transfer",
    AbiEncoder.EncodeAddress(recipient),
    AbiEncoder.EncodeUInt256(amount));
Console.WriteLine($"\nFull call data:     {callData.Length} bytes (4 selector + 20 addr + 32 uint256)");

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
    await InteractiveLoop(provider, wallet, manager, nodeUrl);
}

Console.WriteLine("=== Done ===");
return;

// ═════════════════════════════════════════════════════════════════════
//  Interactive Wallet Loop
// ═════════════════════════════════════════════════════════════════════

async Task InteractiveLoop(BasaltProvider provider, HdWallet hdWallet, AccountManager acctMgr, string baseUrl)
{
    Address? deployedContract = null;

    Console.WriteLine("=== Basalt Interactive Wallet ===\n");

    var status = await provider.GetStatusAsync();
    Console.WriteLine($"Connected to {baseUrl} (chain {provider.ChainId})");
    Console.WriteLine($"Node at block #{status.BlockHeight}, protocol v{status.ProtocolVersion}\n");

    while (true)
    {
        var active = acctMgr.ActiveAccount!;
        Console.WriteLine($"Active: {FormatAddress(active.Address)}");
        if (deployedContract.HasValue)
            Console.WriteLine($"Contract: {FormatAddress(deployedContract.Value)}");
        Console.WriteLine();
        Console.WriteLine("  1) Show status");
        Console.WriteLine("  2) List accounts & balances");
        Console.WriteLine("  3) Switch account");
        Console.WriteLine("  4) Request faucet");
        Console.WriteLine("  5) Transfer BSLT");
        Console.WriteLine("  6) Deploy contract");
        Console.WriteLine("  7) Contract: storage_set");
        Console.WriteLine("  8) Contract: storage_get");
        Console.WriteLine("  9) Transaction lookup");
        Console.WriteLine(" 10) Subscribe to blocks");
        Console.WriteLine("  0) Exit");
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
                case "6": deployedContract = await CmdDeployContract(provider, acctMgr.ActiveAccount!); break;
                case "7": await CmdStorageSet(provider, acctMgr.ActiveAccount!, deployedContract); break;
                case "8": await CmdStorageGet(provider, acctMgr.ActiveAccount!, deployedContract); break;
                case "9": await CmdTxLookup(provider); break;
                case "10": await CmdSubscribe(baseUrl); break;
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

    // Recipient
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

    // Amount
    Console.Write("Amount in BSLT (e.g. 1, 0.5, 10): ");
    var amountInput = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(amountInput) || !TryParseBsltToWei(amountInput, out var weiAmount))
    {
        Console.WriteLine("Invalid amount.");
        return;
    }

    // Show balances before
    var senderBal = await provider.GetBalanceAsync(senderAccount.Address);
    var recipBal = await provider.GetBalanceAsync(recipientAddr);
    Console.WriteLine($"\nBefore:");
    Console.WriteLine($"  Sender:    {FormatBslt(senderBal)}");
    Console.WriteLine($"  Recipient: {FormatBslt(recipBal)}");

    // Send
    Console.WriteLine($"\nSending {amountInput} BSLT to {FormatAddress(recipientAddr)}...");
    var result = await provider.TransferAsync(senderAccount, recipientAddr, weiAmount);
    Console.WriteLine($"  Tx hash: {result.Hash}");
    Console.WriteLine($"  Status:  {result.Status}");

    // Wait and show after
    Console.WriteLine("  Waiting 5s for block inclusion...");
    await Task.Delay(5000);

    senderBal = await provider.GetBalanceAsync(senderAccount.Address);
    recipBal = await provider.GetBalanceAsync(recipientAddr);
    Console.WriteLine($"\nAfter:");
    Console.WriteLine($"  Sender:    {FormatBslt(senderBal)}");
    Console.WriteLine($"  Recipient: {FormatBslt(recipBal)}");
}

// ── 6) Deploy Contract ──────────────────────────────────────────────

async Task<Address?> CmdDeployContract(BasaltProvider provider, IAccount account)
{
    // Basalt Phase 1 VM: any bytecode works, dispatch is selector-based
    var code = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE };

    Console.WriteLine("Deploying storage contract (4 bytes)...");

    // Get the on-chain nonce before deploying — the chain uses this to derive the contract address
    var deployNonce = await provider.GetNonceAsync(account.Address);

    var result = await provider.DeployContractAsync(account, code, gasLimit: 500_000);
    Console.WriteLine($"  Tx hash: {result.Hash}");
    Console.WriteLine($"  Status:  {result.Status}");

    // Derive contract address: BLAKE3(sender || nonce) last 20 bytes
    var contractAddress = DeriveContractAddress(account.Address, deployNonce);
    Console.WriteLine($"  Contract address: {FormatAddress(contractAddress)}");

    Console.WriteLine("  Waiting 5s for block inclusion...");
    await Task.Delay(5000);

    return contractAddress;
}

// ── 7) Contract: storage_set ────────────────────────────────────────

async Task CmdStorageSet(BasaltProvider provider, IAccount account, Address? contractAddress)
{
    if (!contractAddress.HasValue)
    {
        Console.WriteLine("No contract deployed yet. Use option 6 first.");
        return;
    }

    Console.Write("Key (string): ");
    var keyStr = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(keyStr))
    {
        Console.WriteLine("Cancelled.");
        return;
    }

    Console.Write("Value (string): ");
    var valueStr = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(valueStr))
    {
        Console.WriteLine("Cancelled.");
        return;
    }

    // Hash the key string to get a 32-byte storage key
    var keyHash = Blake3Hasher.Hash(System.Text.Encoding.UTF8.GetBytes(keyStr));
    var keyBytes = new byte[Hash256.Size];
    keyHash.WriteTo(keyBytes);
    var valueBytes = System.Text.Encoding.UTF8.GetBytes(valueStr);

    Console.WriteLine($"\n  Key hash: 0x{Convert.ToHexString(keyBytes[..8]).ToLowerInvariant()}...");
    Console.WriteLine($"  Value:    \"{valueStr}\" ({valueBytes.Length} bytes)");

    var contract = provider.GetContract(contractAddress.Value);
    var result = await contract.CallAsync(
        account,
        "storage_set",
        gasLimit: 100_000,
        args: [keyBytes, AbiEncoder.EncodeBytes(valueBytes)]);

    Console.WriteLine($"  Tx hash: {result.Hash}");
    Console.WriteLine($"  Status:  {result.Status}");
}

// ── 8) Contract: storage_get ────────────────────────────────────────

async Task CmdStorageGet(BasaltProvider provider, IAccount account, Address? contractAddress)
{
    if (!contractAddress.HasValue)
    {
        Console.WriteLine("No contract deployed yet. Use option 6 first.");
        return;
    }

    Console.Write("Key (string): ");
    var keyStr = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(keyStr))
    {
        Console.WriteLine("Cancelled.");
        return;
    }

    // Hash the key string to get a 32-byte storage key
    var keyHash = Blake3Hasher.Hash(System.Text.Encoding.UTF8.GetBytes(keyStr));
    var keyBytes = new byte[Hash256.Size];
    keyHash.WriteTo(keyBytes);

    Console.WriteLine($"\n  Key hash: 0x{Convert.ToHexString(keyBytes[..8]).ToLowerInvariant()}...");

    var contract = provider.GetContract(contractAddress.Value);
    var result = await contract.CallAsync(
        account,
        "storage_get",
        gasLimit: 100_000,
        args: [keyBytes]);

    Console.WriteLine($"  Tx hash: {result.Hash}");
    Console.WriteLine($"  Status:  {result.Status}");
    Console.WriteLine("  (storage_get is a write tx in Phase 1 — no read-only RPC yet)");
}

// ── 9) Transaction Lookup ───────────────────────────────────────────

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

// ── 10) Subscribe to Blocks ─────────────────────────────────────────

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

    // UInt256 doesn't support division, so do string-based formatting
    // 1 BSLT = 10^18 wei
    const int decimals = 18;
    var padded = weiBalance.PadLeft(decimals + 1, '0');
    var integerPart = padded[..^decimals];
    var fractionalPart = padded[^decimals..].TrimEnd('0');

    if (fractionalPart.Length == 0)
        return $"{integerPart} BSLT";

    // Show at most 4 decimal places
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

    // Pad fractional to 18 digits
    if (fractionalStr.Length > 18) return false;
    fractionalStr = fractionalStr.PadRight(18, '0');

    var fullStr = integerStr + fractionalStr;
    // Strip leading zeros but keep at least "0"
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
/// BLAKE3(sender || nonce) → last 20 bytes.
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
