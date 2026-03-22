using Basalt.Core;
using Basalt.Crypto;

namespace Basalt.Node;

/// <summary>
/// Deterministic developer accounts for DevNet mode.
/// Private keys are derived from simple seeds — NEVER use these on mainnet.
/// Printed at startup like Hardhat/Anvil for easy copy-paste.
/// </summary>
public static class DevAccounts
{
    /// <summary>Default balance per dev account: 10,000 BSLT (18 decimals).</summary>
    public static readonly UInt256 DefaultBalance = UInt256.Parse("10000000000000000000000");

    /// <summary>
    /// Generate deterministic dev accounts from seed bytes.
    /// Account i uses private key [0x00...00, i+1] (32 bytes, last byte = i+1).
    /// </summary>
    public static (byte[] PrivateKey, PublicKey PublicKey, Address Address)[] Generate(int count)
    {
        var accounts = new (byte[], PublicKey, Address)[count];
        for (int i = 0; i < count; i++)
        {
            var privateKey = new byte[32];
            privateKey[31] = (byte)(i + 1);
            var publicKey = Ed25519Signer.GetPublicKey(privateKey);
            var address = Ed25519Signer.DeriveAddress(publicKey);
            accounts[i] = (privateKey, publicKey, address);
        }
        return accounts;
    }

    /// <summary>
    /// Build genesis balances for dev accounts.
    /// </summary>
    public static Dictionary<Address, UInt256> GetGenesisBalances(int count, UInt256? balance = null)
    {
        var bal = balance ?? DefaultBalance;
        var accounts = Generate(count);
        var balances = new Dictionary<Address, UInt256>(count);
        foreach (var (_, _, address) in accounts)
            balances[address] = bal;
        return balances;
    }

    /// <summary>
    /// Print the Hardhat/Anvil-style account table to the console.
    /// </summary>
    public static void PrintAccountTable(int count, UInt256? balance = null)
    {
        var bal = balance ?? DefaultBalance;
        var accounts = Generate(count);

        Console.WriteLine();
        Console.WriteLine("Available Accounts");
        Console.WriteLine("==================");
        for (int i = 0; i < accounts.Length; i++)
        {
            var (_, _, address) = accounts[i];
            Console.WriteLine($"  ({i}) {address} ({FormatBslt(bal)} BSLT)");
        }

        Console.WriteLine();
        Console.WriteLine("Private Keys");
        Console.WriteLine("==================");
        for (int i = 0; i < accounts.Length; i++)
        {
            var (privateKey, _, _) = accounts[i];
            Console.WriteLine($"  ({i}) 0x{Convert.ToHexString(privateKey).ToLowerInvariant()}");
        }
        Console.WriteLine();
    }

    private static string FormatBslt(UInt256 value)
    {
        // Convert from 18-decimal representation to human-readable
        var str = value.ToString();
        if (str.Length <= 18)
            return "0." + str.PadLeft(18, '0').TrimEnd('0');
        var whole = str[..^18];
        var frac = str[^18..].TrimEnd('0');
        return frac.Length > 0 ? $"{whole}.{frac}" : whole;
    }
}
