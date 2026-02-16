using System.Diagnostics.CodeAnalysis;

namespace Basalt.Sdk.Wallet.HdWallet;

/// <summary>
/// Represents a BIP-32/BIP-44 HD key derivation path.
/// SLIP-0010 for Ed25519 requires all levels to be hardened.
/// Default Basalt path: m/44'/9000'/0'/0'/N
/// </summary>
public sealed class DerivationPath
{
    /// <summary>Basalt coin type for BIP-44 (9000).</summary>
    public const uint BasaltCoinType = 9000;

    /// <summary>BIP-44 hardened offset (0x80000000).</summary>
    public const uint HardenedOffset = 0x80000000;

    /// <summary>The parsed indices (with hardened offset applied where marked).</summary>
    public uint[] Indices { get; }

    /// <summary>The original path string.</summary>
    public string Path { get; }

    private DerivationPath(string path, uint[] indices)
    {
        Path = path;
        Indices = indices;
    }

    /// <summary>
    /// Get the default Basalt derivation path for a given account index.
    /// Returns m/44'/9000'/0'/0'/{index}'
    /// </summary>
    public static DerivationPath Basalt(uint index)
    {
        var path = $"m/44'/9000'/0'/0'/{index}'";
        var indices = new[]
        {
            44 + HardenedOffset,
            BasaltCoinType + HardenedOffset,
            0 + HardenedOffset,
            0 + HardenedOffset,
            index + HardenedOffset,
        };
        return new DerivationPath(path, indices);
    }

    /// <summary>
    /// Parse a derivation path string (e.g. "m/44'/9000'/0'/0'/0'").
    /// For SLIP-0010 Ed25519, all levels must be hardened (denoted by ').
    /// </summary>
    public static DerivationPath Parse(string path)
    {
        if (!TryParse(path, out var result, out var error))
            throw new FormatException(error);
        return result;
    }

    /// <summary>
    /// Try to parse a derivation path string.
    /// </summary>
    public static bool TryParse(string path, [NotNullWhen(true)] out DerivationPath? result, [NotNullWhen(false)] out string? error)
    {
        result = null;
        error = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Derivation path cannot be empty.";
            return false;
        }

        var trimmed = path.Trim();
        if (!trimmed.StartsWith("m/", StringComparison.Ordinal))
        {
            error = "Derivation path must start with 'm/'.";
            return false;
        }

        var segments = trimmed[2..].Split('/');
        if (segments.Length == 0)
        {
            error = "Derivation path must have at least one level.";
            return false;
        }

        var indices = new uint[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            var hardened = segment.EndsWith('\'') || segment.EndsWith('h');

            if (!hardened)
            {
                error = $"SLIP-0010 Ed25519 requires all derivation levels to be hardened. Level '{segment}' is not hardened.";
                return false;
            }

            var numberPart = segment[..^1];
            if (!uint.TryParse(numberPart, out var index))
            {
                error = $"Invalid index '{numberPart}' at level {i}.";
                return false;
            }

            indices[i] = index + HardenedOffset;
        }

        result = new DerivationPath(trimmed, indices);
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => Path;
}
