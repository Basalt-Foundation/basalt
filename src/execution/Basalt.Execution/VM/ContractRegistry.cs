using Basalt.Sdk.Contracts;

namespace Basalt.Execution.VM;

/// <summary>
/// Registry of SDK contract types. Maps type IDs to AOT-safe factory delegates.
/// Magic bytes [0xBA, 0x5A] in contract code identify SDK contracts.
///
/// Contract manifest format (stored at 0xFF01):
///   [0xBA][0x5A][2-byte type ID big-endian][constructor args...]
/// </summary>
public sealed class ContractRegistry
{
    /// <summary>
    /// Magic bytes identifying SDK contract manifests.
    /// </summary>
    public static readonly byte[] Magic = [0xBA, 0x5A];

    private readonly Dictionary<ushort, ContractRegistration> _registrations = new();

    /// <summary>
    /// Register a contract type with an AOT-safe factory delegate.
    /// </summary>
    public void Register(ushort typeId, string name, Func<byte[], IDispatchable> factory)
    {
        _registrations[typeId] = new ContractRegistration(typeId, name, factory);
    }

    /// <summary>
    /// Check if a code blob is an SDK contract (starts with magic bytes).
    /// </summary>
    public static bool IsSdkContract(byte[] code)
    {
        return code.Length >= 4
            && code[0] == Magic[0]
            && code[1] == Magic[1];
    }

    /// <summary>
    /// Parse the manifest header from contract code.
    /// </summary>
    public static (ushort TypeId, byte[] ConstructorArgs) ParseManifest(byte[] code)
    {
        if (code.Length < 4)
            throw new ArgumentException("Code too short for SDK contract manifest");

        var typeId = (ushort)((code[2] << 8) | code[3]);
        var args = code.Length > 4 ? code[4..] : [];
        return (typeId, args);
    }

    /// <summary>
    /// Build a manifest blob from a type ID and constructor args.
    /// </summary>
    public static byte[] BuildManifest(ushort typeId, byte[] constructorArgs)
    {
        var result = new byte[4 + constructorArgs.Length];
        result[0] = Magic[0];
        result[1] = Magic[1];
        result[2] = (byte)(typeId >> 8);
        result[3] = (byte)(typeId & 0xFF);
        constructorArgs.CopyTo(result.AsSpan(4));
        return result;
    }

    /// <summary>
    /// Create an instance of a registered contract type.
    /// </summary>
    public IDispatchable CreateInstance(ushort typeId, byte[] constructorArgs)
    {
        if (!_registrations.TryGetValue(typeId, out var reg))
            throw new InvalidOperationException($"Unknown contract type ID: 0x{typeId:X4}");

        return reg.Factory(constructorArgs);
    }

    /// <summary>
    /// Check if a type ID is registered.
    /// </summary>
    public bool IsRegistered(ushort typeId) => _registrations.ContainsKey(typeId);

    /// <summary>
    /// Get the name of a registered contract type.
    /// </summary>
    public string? GetName(ushort typeId) =>
        _registrations.TryGetValue(typeId, out var reg) ? reg.Name : null;

    /// <summary>
    /// Create the default registry with all built-in contract types.
    /// </summary>
    public static ContractRegistry CreateDefault()
    {
        var registry = new ContractRegistry();

        // User-deployable standards
        registry.Register(0x0001, "BST20Token", args =>
        {
            var reader = new Basalt.Codec.BasaltReader(args);
            var name = reader.ReadString();
            var symbol = reader.ReadString();
            var decimals = reader.ReadByte();
            return new Basalt.Sdk.Contracts.Standards.BST20Token(name, symbol, decimals);
        });

        registry.Register(0x0002, "BST721Token", args =>
        {
            var reader = new Basalt.Codec.BasaltReader(args);
            var name = reader.ReadString();
            var symbol = reader.ReadString();
            return new Basalt.Sdk.Contracts.Standards.BST721Token(name, symbol);
        });

        registry.Register(0x0003, "BST1155Token", args =>
        {
            var reader = new Basalt.Codec.BasaltReader(args);
            var baseUri = reader.ReadString();
            return new Basalt.Sdk.Contracts.Standards.BST1155Token(baseUri);
        });

        registry.Register(0x0004, "BSTDIDRegistry", args =>
        {
            if (args.Length > 0)
            {
                var reader = new Basalt.Codec.BasaltReader(args);
                var prefix = reader.ReadString();
                return new Basalt.Sdk.Contracts.Standards.BSTDIDRegistry(prefix);
            }
            return new Basalt.Sdk.Contracts.Standards.BSTDIDRegistry();
        });

        // System contracts
        registry.Register(0x0100, "WBSLT", _ =>
            new Basalt.Sdk.Contracts.Standards.WBSLT());

        registry.Register(0x0101, "BasaltNameService", args =>
        {
            if (args.Length > 0)
            {
                var reader = new Basalt.Codec.BasaltReader(args);
                var fee = reader.ReadUInt64();
                return new Basalt.Sdk.Contracts.Standards.BasaltNameService(fee);
            }
            return new Basalt.Sdk.Contracts.Standards.BasaltNameService();
        });

        registry.Register(0x0102, "Governance", args =>
        {
            if (args.Length > 0)
            {
                var reader = new Basalt.Codec.BasaltReader(args);
                var quorumBps = reader.ReadUInt64();
                var proposalThreshold = reader.ReadUInt64();
                var votingPeriodBlocks = reader.ReadUInt64();
                var timelockDelayBlocks = reader.ReadUInt64();
                return new Basalt.Sdk.Contracts.Standards.Governance(
                    quorumBps, proposalThreshold, votingPeriodBlocks, timelockDelayBlocks);
            }
            return new Basalt.Sdk.Contracts.Standards.Governance();
        });

        registry.Register(0x0103, "Escrow", _ =>
            new Basalt.Sdk.Contracts.Standards.Escrow());

        registry.Register(0x0104, "StakingPool", _ =>
            new Basalt.Sdk.Contracts.Standards.StakingPool());

        registry.Register(0x0005, "BST3525Token", args =>
        {
            var reader = new Basalt.Codec.BasaltReader(args);
            var name = reader.ReadString();
            var symbol = reader.ReadString();
            var valueDecimals = reader.ReadByte();
            return new Basalt.Sdk.Contracts.Standards.BST3525Token(name, symbol, valueDecimals);
        });

        registry.Register(0x0006, "BST4626Vault", args =>
        {
            var reader = new Basalt.Codec.BasaltReader(args);
            var name = reader.ReadString();
            var symbol = reader.ReadString();
            var decimals = reader.ReadByte();
            var assetAddress = reader.ReadBytes().ToArray();
            return new Basalt.Sdk.Contracts.Standards.BST4626Vault(name, symbol, decimals, assetAddress);
        });

        registry.Register(0x0007, "BSTVCRegistry", _ =>
            new Basalt.Sdk.Contracts.Standards.BSTVCRegistry());

        // ZK compliance contracts
        registry.Register(0x0105, "SchemaRegistry", _ =>
            new Basalt.Sdk.Contracts.Standards.SchemaRegistry());

        registry.Register(0x0106, "IssuerRegistry", _ =>
            new Basalt.Sdk.Contracts.Standards.IssuerRegistry());

        registry.Register(0x0107, "BridgeETH", args =>
        {
            if (args.Length > 0)
            {
                var reader = new Basalt.Codec.BasaltReader(args);
                var threshold = reader.ReadUInt32();
                return new Basalt.Sdk.Contracts.Standards.BridgeETH(threshold);
            }
            return new Basalt.Sdk.Contracts.Standards.BridgeETH();
        });

        return registry;
    }

    private sealed record ContractRegistration(ushort TypeId, string Name, Func<byte[], IDispatchable> Factory);
}
