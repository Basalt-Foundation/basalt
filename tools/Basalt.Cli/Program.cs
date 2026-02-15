using System.CommandLine;
using Basalt.Cli;
using Basalt.Core;
using Basalt.Crypto;

var nodeUrlOption = new Option<string>(
    "--node",
    getDefaultValue: () => "http://localhost:5000",
    description: "Basalt node URL");

var rootCommand = new RootCommand("Basalt CLI v1.0 - Basalt blockchain developer tools")
{
    nodeUrlOption,
};

// ── basalt init ──
var initCommand = new Command("init", "Initialize a new Basalt smart contract project");
var projectNameArg = new Argument<string>("name", "Project name");
initCommand.AddArgument(projectNameArg);

initCommand.SetHandler(async (string name) =>
{
    var dir = Path.Combine(Directory.GetCurrentDirectory(), name);
    if (Directory.Exists(dir))
    {
        Console.Error.WriteLine($"Directory '{name}' already exists.");
        return;
    }

    Directory.CreateDirectory(dir);
    var contractDir = Path.Combine(dir, "Contracts");
    var testDir = Path.Combine(dir, "Tests");
    Directory.CreateDirectory(contractDir);
    Directory.CreateDirectory(testDir);

    var csprojContent = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net9.0</TargetFramework>
            <Nullable>enable</Nullable>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Basalt.Sdk.Contracts" Version="*" />
          </ItemGroup>
        </Project>
        """;
    await File.WriteAllTextAsync(Path.Combine(dir, $"{name}.csproj"), csprojContent);

    var contractContent = $"using Basalt.Sdk.Contracts;\n\nnamespace {name}.Contracts;\n\n" +
        "[BasaltContract]\npublic class MyToken : BST20Token\n{\n" +
        "    [BasaltConstructor]\n    public void Initialize(string tokenName, string tokenSymbol, byte tokenDecimals)\n    {\n" +
        "        Init(tokenName, tokenSymbol, tokenDecimals);\n    }\n\n" +
        "    [BasaltEntrypoint]\n    public void Mint(byte[] to, ulong amount)\n    {\n" +
        "        MintInternal(to, amount);\n    }\n}\n";
    await File.WriteAllTextAsync(Path.Combine(contractDir, "MyToken.cs"), contractContent);

    Console.WriteLine($"Initialized Basalt project '{name}'");
    Console.WriteLine($"  {contractDir}/MyToken.cs");
    Console.WriteLine();
    Console.WriteLine("Next steps:");
    Console.WriteLine($"  cd {name}");
    Console.WriteLine("  dotnet build");
    Console.WriteLine("  basalt compile");
}, projectNameArg);

rootCommand.AddCommand(initCommand);

// ── basalt account ──
var accountCommand = new Command("account", "Account management");

var accountCreateCommand = new Command("create", "Create a new account (key pair)");
var outputOption = new Option<string?>("--output", "File path to save the key pair");
accountCreateCommand.AddOption(outputOption);

accountCreateCommand.SetHandler(async (string? output) =>
{
    var (privateKey, publicKey) = Ed25519Signer.GenerateKeyPair();
    var address = Ed25519Signer.DeriveAddress(publicKey);

    var privHex = Convert.ToHexString(privateKey).ToLowerInvariant();
    var pubHex = publicKey.ToString();
    var addrHex = address.ToHexString();

    Console.WriteLine("Account created:");
    Console.WriteLine($"  Address:     {addrHex}");
    Console.WriteLine($"  Public Key:  {pubHex}");
    Console.WriteLine($"  Private Key: 0x{privHex}");
    Console.WriteLine();
    Console.WriteLine("WARNING: Store your private key securely. Never share it.");

    if (output != null)
    {
        var content = $"address={addrHex}\npublicKey={pubHex}\nprivateKey=0x{privHex}\n";
        await File.WriteAllTextAsync(output, content);
        Console.WriteLine($"\nKey pair saved to: {output}");
    }
}, outputOption);

var accountBalanceCommand = new Command("balance", "Get account balance");
var addressArg = new Argument<string>("address", "Account address (hex)");
accountBalanceCommand.AddArgument(addressArg);
accountBalanceCommand.AddOption(nodeUrlOption);

accountBalanceCommand.SetHandler(async (string address, string nodeUrl) =>
{
    using var client = new NodeClient(nodeUrl);
    var account = await client.GetAccountAsync(address);
    if (account == null)
    {
        Console.Error.WriteLine($"Account not found: {address}");
        return;
    }
    Console.WriteLine($"Address:  {account.Address}");
    Console.WriteLine($"Balance:  {account.Balance}");
    Console.WriteLine($"Nonce:    {account.Nonce}");
    Console.WriteLine($"Type:     {account.AccountType}");
}, addressArg, nodeUrlOption);

accountCommand.AddCommand(accountCreateCommand);
accountCommand.AddCommand(accountBalanceCommand);
rootCommand.AddCommand(accountCommand);

// ── basalt tx ──
var txCommand = new Command("tx", "Transaction operations");

var txSendCommand = new Command("send", "Send a transfer transaction");
var txToOption = new Option<string>("--to", "Recipient address") { IsRequired = true };
var txValueOption = new Option<string>("--value", "Amount to send") { IsRequired = true };
var txKeyOption = new Option<string>("--key", "Sender private key (hex)") { IsRequired = true };
var txGasOption = new Option<ulong>("--gas", getDefaultValue: () => 21000, "Gas limit");
var txChainIdOption = new Option<uint>("--chain-id", getDefaultValue: () => 1, "Chain ID");
txSendCommand.AddOption(txToOption);
txSendCommand.AddOption(txValueOption);
txSendCommand.AddOption(txKeyOption);
txSendCommand.AddOption(txGasOption);
txSendCommand.AddOption(txChainIdOption);
txSendCommand.AddOption(nodeUrlOption);

txSendCommand.SetHandler(async (string to, string value, string key, ulong gas, uint chainId, string nodeUrl) =>
{
    var privKeyHex = key.StartsWith("0x") ? key[2..] : key;
    var privateKey = Convert.FromHexString(privKeyHex);
    var publicKey = Ed25519Signer.GetPublicKey(privateKey);
    var senderAddress = Ed25519Signer.DeriveAddress(publicKey);

    using var client = new NodeClient(nodeUrl);

    // Get current nonce
    var account = await client.GetAccountAsync(senderAddress.ToHexString());
    var nonce = account?.Nonce ?? 0;

    var tx = new TxRequest
    {
        Type = 0, // Transfer
        Nonce = nonce,
        Sender = senderAddress.ToHexString(),
        To = to,
        Value = value,
        GasLimit = gas,
        GasPrice = "1",
        ChainId = chainId,
        SenderPublicKey = publicKey.ToString().TrimStart('0', 'x'),
    };

    // Sign the transaction
    var txBytes = System.Text.Encoding.UTF8.GetBytes(
        $"{tx.Type}{tx.Nonce}{tx.Sender}{tx.To}{tx.Value}{tx.GasLimit}{tx.GasPrice}{tx.ChainId}");
    var txHash = Blake3Hasher.Hash(txBytes);
    var sig = Ed25519Signer.Sign(privateKey, txHash.ToArray());
    tx.Signature = Convert.ToHexString(sig.ToArray()).ToLowerInvariant();
    tx.SenderPublicKey = Convert.ToHexString(publicKey.ToArray()).ToLowerInvariant();

    var result = await client.SendTransactionAsync(tx);
    if (result?.Error != null)
    {
        Console.Error.WriteLine($"Error: {result.Error}");
        return;
    }

    Console.WriteLine($"Transaction sent:");
    Console.WriteLine($"  Hash:   {result?.Hash}");
    Console.WriteLine($"  Status: {result?.Status}");
}, txToOption, txValueOption, txKeyOption, txGasOption, txChainIdOption, nodeUrlOption);

var txStatusCommand = new Command("status", "Get transaction status");
var txHashArg = new Argument<string>("hash", "Transaction hash");
txStatusCommand.AddArgument(txHashArg);
txStatusCommand.AddOption(nodeUrlOption);

txStatusCommand.SetHandler((string hash, string nodeUrl) =>
{
    // Transaction lookup by hash would require a dedicated API endpoint
    // For now, direct users to the block explorer
    Console.WriteLine($"Transaction: {hash}");
    Console.WriteLine("Use the block explorer for detailed transaction status.");
    Console.WriteLine($"  Explorer: {nodeUrl.Replace("5000", "5001")}/tx/{hash}");
}, txHashArg, nodeUrlOption);

txCommand.AddCommand(txSendCommand);
txCommand.AddCommand(txStatusCommand);
rootCommand.AddCommand(txCommand);

// ── basalt node ──
var nodeCommand = new Command("node", "Node operations");

var nodeStatusCommand = new Command("status", "Get node status");
nodeStatusCommand.AddOption(nodeUrlOption);

nodeStatusCommand.SetHandler(async (string nodeUrl) =>
{
    using var client = new NodeClient(nodeUrl);
    try
    {
        var status = await client.GetStatusAsync();
        if (status == null)
        {
            Console.Error.WriteLine("Failed to connect to node.");
            return;
        }
        Console.WriteLine("Node Status:");
        Console.WriteLine($"  Block Height:     {status.BlockHeight}");
        Console.WriteLine($"  Latest Block:     {status.LatestBlockHash}");
        Console.WriteLine($"  Mempool Size:     {status.MempoolSize}");
        Console.WriteLine($"  Protocol Version: {status.ProtocolVersion}");
    }
    catch (HttpRequestException ex)
    {
        Console.Error.WriteLine($"Cannot connect to node at {nodeUrl}: {ex.Message}");
    }
}, nodeUrlOption);

nodeCommand.AddCommand(nodeStatusCommand);
rootCommand.AddCommand(nodeCommand);

// ── basalt block ──
var blockCommand = new Command("block", "Block operations");

var blockGetCommand = new Command("get", "Get block by number or hash");
var blockIdArg = new Argument<string>("id", "Block number or hash");
blockGetCommand.AddArgument(blockIdArg);
blockGetCommand.AddOption(nodeUrlOption);

blockGetCommand.SetHandler(async (string id, string nodeUrl) =>
{
    using var client = new NodeClient(nodeUrl);
    var block = await client.GetBlockAsync(id);
    if (block == null)
    {
        Console.Error.WriteLine($"Block not found: {id}");
        return;
    }
    Console.WriteLine($"Block #{block.Number}");
    Console.WriteLine($"  Hash:         {block.Hash}");
    Console.WriteLine($"  Parent:       {block.ParentHash}");
    Console.WriteLine($"  State Root:   {block.StateRoot}");
    Console.WriteLine($"  Timestamp:    {DateTimeOffset.FromUnixTimeSeconds(block.Timestamp):u}");
    Console.WriteLine($"  Proposer:     {block.Proposer}");
    Console.WriteLine($"  Gas Used:     {block.GasUsed} / {block.GasLimit}");
    Console.WriteLine($"  Transactions: {block.TransactionCount}");
}, blockIdArg, nodeUrlOption);

var blockLatestCommand = new Command("latest", "Get the latest block");
blockLatestCommand.AddOption(nodeUrlOption);

blockLatestCommand.SetHandler(async (string nodeUrl) =>
{
    using var client = new NodeClient(nodeUrl);
    var block = await client.GetLatestBlockAsync();
    if (block == null)
    {
        Console.Error.WriteLine("No blocks available.");
        return;
    }
    Console.WriteLine($"Block #{block.Number}");
    Console.WriteLine($"  Hash:         {block.Hash}");
    Console.WriteLine($"  Timestamp:    {DateTimeOffset.FromUnixTimeSeconds(block.Timestamp):u}");
    Console.WriteLine($"  Transactions: {block.TransactionCount}");
    Console.WriteLine($"  Gas:          {block.GasUsed} / {block.GasLimit}");
}, nodeUrlOption);

blockCommand.AddCommand(blockGetCommand);
blockCommand.AddCommand(blockLatestCommand);
rootCommand.AddCommand(blockCommand);

// ── basalt faucet ──
var faucetCommand = new Command("faucet", "Request test tokens from the faucet");
var faucetAddressArg = new Argument<string>("address", "Recipient address");
faucetCommand.AddArgument(faucetAddressArg);
faucetCommand.AddOption(nodeUrlOption);

faucetCommand.SetHandler(async (string address, string nodeUrl) =>
{
    using var client = new NodeClient(nodeUrl);
    try
    {
        var result = await client.RequestFaucetAsync(address);
        if (result == null || !result.Success)
        {
            Console.Error.WriteLine($"Faucet request failed: {result?.Message ?? "unknown error"}");
            return;
        }
        Console.WriteLine($"Faucet: {result.Message}");
        if (result.TxHash != null)
            Console.WriteLine($"  TX Hash: {result.TxHash}");
    }
    catch (HttpRequestException ex)
    {
        Console.Error.WriteLine($"Faucet error: {ex.Message}");
    }
}, faucetAddressArg, nodeUrlOption);

rootCommand.AddCommand(faucetCommand);

// ── basalt compile ──
var compileCommand = new Command("compile", "Compile a Basalt smart contract project");
var compilePathArg = new Argument<string>("path", getDefaultValue: () => ".", "Project directory");
compileCommand.AddArgument(compilePathArg);

compileCommand.SetHandler(async (string path) =>
{
    var fullPath = Path.GetFullPath(path);
    if (!Directory.Exists(fullPath))
    {
        Console.Error.WriteLine($"Directory not found: {fullPath}");
        return;
    }

    var csprojFiles = Directory.GetFiles(fullPath, "*.csproj");
    if (csprojFiles.Length == 0)
    {
        Console.Error.WriteLine("No .csproj file found in the specified directory.");
        return;
    }

    Console.WriteLine($"Compiling: {csprojFiles[0]}");
    var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
        FileName = "dotnet",
        Arguments = $"build \"{csprojFiles[0]}\" -c Release",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    });

    if (process == null)
    {
        Console.Error.WriteLine("Failed to start dotnet build.");
        return;
    }

    var output = await process.StandardOutput.ReadToEndAsync();
    var errors = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();

    Console.Write(output);
    if (!string.IsNullOrEmpty(errors))
        Console.Error.Write(errors);

    Console.WriteLine(process.ExitCode == 0 ? "\nCompilation succeeded." : "\nCompilation failed.");
}, compilePathArg);

rootCommand.AddCommand(compileCommand);

// ── basalt test ──
var testCommand = new Command("test", "Run smart contract tests");
var testPathArg = new Argument<string>("path", getDefaultValue: () => ".", "Test project directory");
testCommand.AddArgument(testPathArg);

testCommand.SetHandler(async (string path) =>
{
    var fullPath = Path.GetFullPath(path);
    Console.WriteLine($"Running tests in: {fullPath}");

    var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
        FileName = "dotnet",
        Arguments = $"test \"{fullPath}\" --verbosity normal",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    });

    if (process == null)
    {
        Console.Error.WriteLine("Failed to start dotnet test.");
        return;
    }

    var output = await process.StandardOutput.ReadToEndAsync();
    var errors = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();

    Console.Write(output);
    if (!string.IsNullOrEmpty(errors))
        Console.Error.Write(errors);
}, testPathArg);

rootCommand.AddCommand(testCommand);

return await rootCommand.InvokeAsync(args);
