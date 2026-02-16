namespace Basalt.Explorer;

public static class FormatHelper
{
    private const int Decimals = 18;

    /// <summary>
    /// Convert a raw value string (base units) to human-readable BSLT.
    /// "100000000000000000000" => "100 BSLT"
    /// "1500000000000000000" => "1.50 BSLT"
    /// "0" => "0 BSLT"
    /// </summary>
    public static string FormatBslt(string? rawValue)
    {
        if (string.IsNullOrEmpty(rawValue) || rawValue == "0")
            return "0 BSLT";

        var padded = rawValue.PadLeft(Decimals + 1, '0');
        var wholePart = padded[..^Decimals].TrimStart('0');
        if (wholePart.Length == 0) wholePart = "0";
        var fracPart = padded[^Decimals..];

        var trimmed = fracPart.TrimEnd('0');
        if (trimmed.Length == 0)
            return wholePart + " BSLT";
        if (trimmed.Length < 2) trimmed = fracPart[..2];
        if (trimmed.Length > 6) trimmed = trimmed[..6];

        return wholePart + "." + trimmed + " BSLT";
    }

    /// <summary>
    /// Get CSS badge class for a transaction type.
    /// </summary>
    public static string GetTxTypeBadgeClass(string? type) => type switch
    {
        "Transfer" => "badge-transfer",
        "ContractDeploy" => "badge-deploy",
        "ContractCall" => "badge-call",
        "StakeDelegate" or "StakeUnbond" => "badge-stake",
        "ValidatorRegister" => "badge-validator",
        _ => "badge-default",
    };

    /// <summary>
    /// Get a human-friendly label for transaction type.
    /// </summary>
    public static string GetTxTypeLabel(string? type) => type switch
    {
        "Transfer" => "Transfer",
        "ContractDeploy" => "Deploy",
        "ContractCall" => "Call",
        "StakeDelegate" => "Stake",
        "StakeUnbond" => "Unstake",
        "ValidatorRegister" => "Register",
        _ => type ?? "Unknown",
    };

    // Hardcoded BLAKE3 selectors (first 4 bytes of BLAKE3(method_name) as uppercase hex).
    // Must match ManagedContractRuntime / AbiEncoder.ComputeSelector.
    private static readonly Dictionary<string, string> KnownSelectors = new()
    {
        ["3E927582"] = "storage_set",
        ["5DB08A5F"] = "storage_get",
        ["758EC466"] = "storage_del",
        ["81359626"] = "emit_event",
    };

    /// <summary>
    /// Decode the first 4 bytes of tx data into a human-readable method name.
    /// Returns null if data is too short or the selector is unknown.
    /// </summary>
    public static string? DecodeMethodName(string? txData)
    {
        if (string.IsNullOrEmpty(txData) || txData.Length < 8)
            return null;

        var selector = txData[..8].ToUpperInvariant();
        return KnownSelectors.GetValueOrDefault(selector);
    }

    /// <summary>
    /// Format a method badge label from tx data.
    /// Returns the method name or the raw selector if unknown.
    /// </summary>
    public static string FormatMethodBadge(string? txData)
    {
        if (string.IsNullOrEmpty(txData) || txData.Length < 8)
            return "\u2014";

        var selector = txData[..8].ToUpperInvariant();
        return KnownSelectors.TryGetValue(selector, out var name) ? name : "0x" + selector.ToLowerInvariant();
    }

    public static string TruncateHash(string? hash)
    {
        if (string.IsNullOrEmpty(hash) || hash.Length <= 16) return hash ?? "";
        return hash[..10] + "..." + hash[^6..];
    }

    /// <summary>
    /// Truncate hex data for display.
    /// </summary>
    public static string TruncateData(string? hex, int maxChars = 64)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length <= maxChars) return hex ?? "";
        return hex[..maxChars] + "...";
    }

    /// <summary>
    /// Format data size in human-readable bytes.
    /// </summary>
    public static string FormatBytes(int bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1048576 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / 1048576.0:F1} MB",
    };
}
