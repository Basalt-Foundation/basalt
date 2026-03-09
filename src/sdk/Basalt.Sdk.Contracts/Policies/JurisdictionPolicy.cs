using Basalt.Core;

namespace Basalt.Sdk.Contracts.Policies;

/// <summary>
/// Policy contract that restricts transfers based on jurisdiction (country code).
/// Maintains a whitelist or blacklist of country codes per token.
/// Queries an on-chain registry for address→jurisdiction mapping.
/// Type ID: 0x000A
/// </summary>
[BasaltContract]
public partial class JurisdictionPolicy : ITransferPolicy
{
    private readonly StorageMap<string, string> _admin;
    private readonly StorageMap<string, bool> _allowedJurisdictions; // "token:countryCode" -> allowed
    private readonly StorageMap<string, ushort> _addressJurisdictions; // address -> country code
    private readonly StorageMap<string, bool> _useWhitelist; // token -> true=whitelist, false=blacklist

    public JurisdictionPolicy()
    {
        _admin = new StorageMap<string, string>("jur_admin");
        _allowedJurisdictions = new StorageMap<string, bool>("jur_allowed");
        _addressJurisdictions = new StorageMap<string, ushort>("jur_addr");
        _useWhitelist = new StorageMap<string, bool>("jur_mode");
        if (Context.IsDeploying)
            _admin.Set("owner", Convert.ToHexString(Context.Caller));
    }

    /// <summary>
    /// Set whether a token uses whitelist mode (true) or blacklist mode (false).
    /// Whitelist: only listed jurisdictions are allowed.
    /// Blacklist: listed jurisdictions are blocked, all others allowed.
    /// </summary>
    [BasaltEntrypoint]
    public void SetMode(byte[] token, bool whitelist)
    {
        RequireAdmin();
        _useWhitelist.Set(Convert.ToHexString(token), whitelist);
    }

    /// <summary>
    /// Add or remove a jurisdiction for a token.
    /// </summary>
    [BasaltEntrypoint]
    public void SetJurisdiction(byte[] token, ushort countryCode, bool allowed)
    {
        RequireAdmin();
        _allowedJurisdictions.Set(JurKey(token, countryCode), allowed);
    }

    /// <summary>
    /// Register an address's jurisdiction. Can be called by admin or by a KYC provider.
    /// </summary>
    [BasaltEntrypoint]
    public void SetAddressJurisdiction(byte[] account, ushort countryCode)
    {
        RequireAdmin();
        _addressJurisdictions.Set(Convert.ToHexString(account), countryCode);
    }

    /// <summary>
    /// Query the jurisdiction of an address.
    /// </summary>
    [BasaltView]
    public ushort GetAddressJurisdiction(byte[] account)
    {
        return _addressJurisdictions.Get(Convert.ToHexString(account));
    }

    [BasaltView]
    public bool CheckTransfer(byte[] token, byte[] from, byte[] to, UInt256 amount)
    {
        var tokenHex = Convert.ToHexString(token);

        // Check sender jurisdiction
        if (!CheckAddress(tokenHex, from)) return false;

        // Check recipient jurisdiction
        if (!CheckAddress(tokenHex, to)) return false;

        return true;
    }

    private bool CheckAddress(string tokenHex, byte[] account)
    {
        var country = _addressJurisdictions.Get(Convert.ToHexString(account));
        if (country == 0) return true; // No jurisdiction registered — allow by default

        var isWhitelist = _useWhitelist.Get(tokenHex);
        var isListed = _allowedJurisdictions.Get(JurKey(tokenHex, country));

        // Whitelist: must be listed. Blacklist: must NOT be listed.
        return isWhitelist ? isListed : !isListed;
    }

    private void RequireAdmin()
    {
        Context.Require(
            Convert.ToHexString(Context.Caller) == _admin.Get("owner"),
            "Jurisdiction: not admin");
    }

    private static string JurKey(byte[] token, ushort countryCode) =>
        Convert.ToHexString(token) + ":" + countryCode;

    private static string JurKey(string tokenHex, ushort countryCode) =>
        tokenHex + ":" + countryCode;
}
