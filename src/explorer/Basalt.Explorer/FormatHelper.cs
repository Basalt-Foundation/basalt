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
