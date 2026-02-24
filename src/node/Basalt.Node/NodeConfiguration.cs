namespace Basalt.Node;

/// <summary>
/// Node configuration populated from environment variables:
/// <list type="bullet">
/// <item><c>BASALT_VALIDATOR_INDEX</c> — Validator index (int, -1 = standalone)</item>
/// <item><c>BASALT_VALIDATOR_ADDRESS</c> — Validator address (hex)</item>
/// <item><c>BASALT_VALIDATOR_KEY</c> — Ed25519 private key (64 hex chars)</item>
/// <item><c>BASALT_PEERS</c> — Comma-separated peer list (host:port)</item>
/// <item><c>BASALT_NETWORK</c> — Network name (default: basalt-devnet)</item>
/// <item><c>BASALT_CHAIN_ID</c> — Chain ID (default: 31337)</item>
/// <item><c>HTTP_PORT</c> — REST API port (default: 5000)</item>
/// <item><c>P2P_PORT</c> — P2P port (default: 30303)</item>
/// <item><c>BASALT_DATA_DIR</c> — RocksDB data directory (null = in-memory)</item>
/// <item><c>BASALT_USE_PIPELINING</c> — Enable pipelined consensus (true/false)</item>
/// <item><c>BASALT_USE_SANDBOX</c> — Enable contract sandbox (true/false)</item>
/// <item><c>BASALT_FAUCET_KEY</c> — Faucet private key (hex, required for mainnet)</item>
/// <item><c>BASALT_DEBUG</c> — Enable debug endpoints (1 = enabled, blocked on mainnet)</item>
/// <item><c>BASALT_LOG_LEVEL</c> — Log level (Verbose/Debug/Information/Warning/Error/Fatal)</item>
/// </list>
/// </summary>
public sealed class NodeConfiguration
{
    // Validator identity
    public int ValidatorIndex { get; init; } = -1;
    public string ValidatorAddress { get; init; } = "";
    public string ValidatorKeyHex { get; init; } = "";

    // Network
    public string NetworkName { get; init; } = "basalt-devnet";
    public uint ChainId { get; init; } = 31337;
    public int HttpPort { get; init; } = 5000;
    public int P2PPort { get; init; } = 30303;

    // Storage
    public string? DataDir { get; init; }

    // Peer list (host:port format)
    public string[] Peers { get; init; } = [];

    // Consensus pipelining (opt-in)
    public bool UsePipelining { get; init; }

    // Contract sandboxing (opt-in)
    public bool UseSandbox { get; init; }

    // Mode detection
    public bool IsConsensusMode => Peers.Length > 0 && ValidatorIndex >= 0;

    public static NodeConfiguration FromEnvironment()
    {
        var peersRaw = Environment.GetEnvironmentVariable("BASALT_PEERS") ?? "";
        var peers = peersRaw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (!int.TryParse(Environment.GetEnvironmentVariable("BASALT_VALIDATOR_INDEX"), out var validatorIndex))
            validatorIndex = -1;

        if (!uint.TryParse(Environment.GetEnvironmentVariable("BASALT_CHAIN_ID"), out var chainId))
            chainId = 31337;

        if (!int.TryParse(Environment.GetEnvironmentVariable("HTTP_PORT"), out var httpPort))
            httpPort = 5000;

        if (!int.TryParse(Environment.GetEnvironmentVariable("P2P_PORT"), out var p2pPort))
            p2pPort = 30303;

        var dataDir = Environment.GetEnvironmentVariable("BASALT_DATA_DIR");

        var usePipelining = string.Equals(
            Environment.GetEnvironmentVariable("BASALT_USE_PIPELINING"), "true",
            StringComparison.OrdinalIgnoreCase);

        var useSandbox = string.Equals(
            Environment.GetEnvironmentVariable("BASALT_USE_SANDBOX"), "true",
            StringComparison.OrdinalIgnoreCase);

        // N-12: Validate DataDir to prevent path traversal
        var validatedDataDir = string.IsNullOrWhiteSpace(dataDir) ? null : ValidateDataDir(dataDir);

        return new NodeConfiguration
        {
            ValidatorIndex = validatorIndex,
            ValidatorAddress = Environment.GetEnvironmentVariable("BASALT_VALIDATOR_ADDRESS") ?? "",
            ValidatorKeyHex = Environment.GetEnvironmentVariable("BASALT_VALIDATOR_KEY") ?? "",
            NetworkName = Environment.GetEnvironmentVariable("BASALT_NETWORK") ?? "basalt-devnet",
            ChainId = chainId,
            HttpPort = httpPort,
            P2PPort = p2pPort,
            Peers = peers,
            DataDir = validatedDataDir,
            UsePipelining = usePipelining,
            UseSandbox = useSandbox,
        };
    }

    // N-12: Validate DataDir to prevent path traversal
    public static string ValidateDataDir(string dataDir)
    {
        // LOW-N03: Resolve symlinks before validation to prevent bypass via symlinked paths.
        // ResolveLinkTarget throws if the path doesn't exist yet, so fall back to GetFullPath.
        string resolved;
        try
        {
            resolved = new FileInfo(dataDir).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? Path.GetFullPath(dataDir);
        }
        catch (IOException)
        {
            resolved = Path.GetFullPath(dataDir);
        }

        // Use case-insensitive comparison on macOS (HFS+) and Windows
        var comparison = OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        // Reject paths that resolve to system directories
        string[] dangerous = ["/etc", "/usr", "/bin", "/sbin", "/var/run", "/proc", "/sys",
                              "/boot", "/dev", "/lib"];
        foreach (var d in dangerous)
        {
            if (resolved.StartsWith(d, comparison))
                throw new InvalidOperationException($"DataDir '{resolved}' resolves to a system directory");
        }

        // Windows system paths
        if (OperatingSystem.IsWindows())
        {
            string[] windowsDangerous = [@"C:\Windows", @"C:\Program Files", @"C:\Program Files (x86)"];
            foreach (var d in windowsDangerous)
            {
                if (resolved.StartsWith(d, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"DataDir '{resolved}' resolves to a system directory");
            }
        }

        return resolved;
    }
}
