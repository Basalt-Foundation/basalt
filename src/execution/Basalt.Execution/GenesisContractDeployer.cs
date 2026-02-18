using Basalt.Core;
using Basalt.Execution.VM;
using Basalt.Storage;
using Microsoft.Extensions.Logging;

namespace Basalt.Execution;

/// <summary>
/// Deploys system contracts into the state database at genesis.
/// Each system contract gets a well-known address and is initialized via the ContractBridge.
/// </summary>
public static class GenesisContractDeployer
{
    /// <summary>
    /// System contract addresses — deterministic, well-known.
    /// </summary>
    public static class Addresses
    {
        public static readonly Address WBSLT = MakeSystemAddress(0x1001);
        public static readonly Address NameService = MakeSystemAddress(0x1002);
        public static readonly Address Governance = MakeSystemAddress(0x1003);
        public static readonly Address Escrow = MakeSystemAddress(0x1004);
        public static readonly Address StakingPool = MakeSystemAddress(0x1005);
        public static readonly Address SchemaRegistry = MakeSystemAddress(0x1006);
        public static readonly Address IssuerRegistry = MakeSystemAddress(0x1007);

        private static Address MakeSystemAddress(ushort id)
        {
            var bytes = new byte[20];
            bytes[18] = (byte)(id >> 8);
            bytes[19] = (byte)(id & 0xFF);
            return new Address(bytes);
        }
    }

    /// <summary>
    /// Deploy all system contracts into the state database.
    /// </summary>
    public static void DeployAll(IStateDatabase stateDb, uint chainId = 31337, ILogger? logger = null)
    {
        var registry = ContractRegistry.CreateDefault();

        // WBSLT (0x0100) — no constructor args
        DeploySystemContract(stateDb, registry, Addresses.WBSLT, 0x0100, [], chainId, logger);

        // Basalt Name Service (0x0101) — default fee
        DeploySystemContract(stateDb, registry, Addresses.NameService, 0x0101, [], chainId, logger);

        // Simple Governance (0x0102) — default quorum
        DeploySystemContract(stateDb, registry, Addresses.Governance, 0x0102, [], chainId, logger);

        // Escrow (0x0103) — no constructor args
        DeploySystemContract(stateDb, registry, Addresses.Escrow, 0x0103, [], chainId, logger);

        // Staking Pool (0x0104) — no constructor args
        DeploySystemContract(stateDb, registry, Addresses.StakingPool, 0x0104, [], chainId, logger);

        // Schema Registry (0x0105) — ZK compliance schema definitions
        DeploySystemContract(stateDb, registry, Addresses.SchemaRegistry, 0x0105, [], chainId, logger);

        // Issuer Registry (0x0106) — ZK compliance issuer management
        DeploySystemContract(stateDb, registry, Addresses.IssuerRegistry, 0x0106, [], chainId, logger);

        logger?.LogInformation("Deployed {Count} system contracts at genesis", 7);
    }

    private static void DeploySystemContract(
        IStateDatabase stateDb,
        ContractRegistry registry,
        Address address,
        ushort typeId,
        byte[] constructorArgs,
        uint chainId,
        ILogger? logger)
    {
        // Build manifest
        var manifest = ContractRegistry.BuildManifest(typeId, constructorArgs);

        // Create contract account
        var codeHash = Basalt.Crypto.Blake3Hasher.Hash(manifest);
        stateDb.SetAccount(address, new AccountState
        {
            Nonce = 0,
            Balance = UInt256.Zero,
            StorageRoot = Hash256.Zero,
            CodeHash = codeHash,
            AccountType = AccountType.SystemContract,
            ComplianceHash = Hash256.Zero,
        });

        // Store manifest at 0xFF01
        Span<byte> keyBytes = stackalloc byte[32];
        keyBytes.Clear();
        keyBytes[0] = 0xFF;
        keyBytes[1] = 0x01;
        var storageKey = new Hash256(keyBytes);
        stateDb.SetStorage(address, storageKey, manifest);

        // Initialize the contract (runs constructor, writes initial storage)
        var gasMeter = new GasMeter(10_000_000);
        var ctx = new VmExecutionContext
        {
            Caller = address,
            ContractAddress = address,
            Value = UInt256.Zero,
            BlockTimestamp = 0,
            BlockNumber = 0,
            BlockProposer = address,
            ChainId = chainId,
            GasMeter = gasMeter,
            StateDb = stateDb,
            CallDepth = 0,
        };

        var host = new HostInterface(ctx);
        using var scope = ContractBridge.Setup(ctx, host);
        registry.CreateInstance(typeId, constructorArgs);

        var name = registry.GetName(typeId) ?? $"0x{typeId:X4}";
        logger?.LogInformation("Deployed system contract {Name} at {Address}", name, address);
    }
}
