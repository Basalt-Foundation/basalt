namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Reference implementation of the BST-721 Non-Fungible Token Standard.
/// </summary>
[BasaltContract]
public partial class BST721Token : IBST721
{
    private readonly StorageMap<string, ulong> _balances;      // owner -> count
    private readonly StorageMap<string, string> _owners;        // tokenId -> owner
    private readonly StorageMap<string, string> _approvals;     // tokenId -> approved
    private readonly StorageMap<string, string> _tokenURIs;     // tokenId -> uri
    private readonly StorageMap<string, bool> _operatorApprovals; // "owner:operator" -> approved
    private readonly StorageValue<ulong> _nextTokenId;
    private readonly StorageMap<string, string> _contractAdmin;
    private readonly string _name;
    private readonly string _symbol;

    public BST721Token(string name, string symbol)
    {
        _name = name;
        _symbol = symbol;
        _balances = new StorageMap<string, ulong>("nft_balances");
        _owners = new StorageMap<string, string>("nft_owners");
        _approvals = new StorageMap<string, string>("nft_approvals");
        _tokenURIs = new StorageMap<string, string>("nft_uris");
        _operatorApprovals = new StorageMap<string, bool>("nft_ops");
        _nextTokenId = new StorageValue<ulong>("nft_next_id");
        _contractAdmin = new StorageMap<string, string>("nft_admin");
        _contractAdmin.Set("owner", AddressKey(Context.Caller));
    }

    [BasaltView]
    public string Name() => _name;

    [BasaltView]
    public string Symbol() => _symbol;

    [BasaltView]
    public byte[] OwnerOf(ulong tokenId)
    {
        var ownerHex = _owners.Get(TokenKey(tokenId));
        Context.Require(!string.IsNullOrEmpty(ownerHex), "BST721: token does not exist");
        return Convert.FromHexString(ownerHex);
    }

    [BasaltView]
    public ulong BalanceOf(byte[] owner)
    {
        return _balances.Get(AddressKey(owner));
    }

    [BasaltEntrypoint]
    public void Transfer(byte[] to, ulong tokenId)
    {
        var caller = Context.Caller;
        var ownerHex = _owners.Get(TokenKey(tokenId));
        Context.Require(!string.IsNullOrEmpty(ownerHex), "BST721: token does not exist");

        var callerHex = AddressKey(caller);
        var approvedHex = _approvals.Get(TokenKey(tokenId));

        // H-8: Also check operator approval
        var isOperator = _operatorApprovals.Get($"{ownerHex}:{callerHex}");
        Context.Require(
            ownerHex == callerHex || approvedHex == callerHex || isOperator,
            "BST721: caller is not owner or approved");

        TransferInternal(Convert.FromHexString(ownerHex), to, tokenId);
    }

    [BasaltEntrypoint]
    public void Approve(byte[] approved, ulong tokenId)
    {
        var ownerHex = _owners.Get(TokenKey(tokenId));
        Context.Require(!string.IsNullOrEmpty(ownerHex), "BST721: token does not exist");
        Context.Require(ownerHex == AddressKey(Context.Caller), "BST721: caller is not owner");

        _approvals.Set(TokenKey(tokenId), AddressKey(approved));

        Context.Emit(new NftApprovalEvent
        {
            Owner = Convert.FromHexString(ownerHex),
            Approved = approved,
            TokenId = tokenId,
        });
    }

    [BasaltView]
    public byte[] GetApproved(ulong tokenId)
    {
        var hex = _approvals.Get(TokenKey(tokenId));
        return string.IsNullOrEmpty(hex) ? new byte[20] : Convert.FromHexString(hex);
    }

    [BasaltView]
    public string TokenURI(ulong tokenId)
    {
        return _tokenURIs.Get(TokenKey(tokenId)) ?? "";
    }

    /// <summary>
    /// Set or revoke operator approval for all tokens (H-8).
    /// </summary>
    [BasaltEntrypoint]
    public void SetApprovalForAll(byte[] operatorAddress, bool approved)
    {
        var ownerHex = AddressKey(Context.Caller);
        var opHex = AddressKey(operatorAddress);
        _operatorApprovals.Set($"{ownerHex}:{opHex}", approved);
    }

    /// <summary>
    /// Check if an operator is approved for all tokens of an owner.
    /// </summary>
    [BasaltView]
    public bool IsApprovedForAll(byte[] owner, byte[] operatorAddress)
    {
        return _operatorApprovals.Get($"{AddressKey(owner)}:{AddressKey(operatorAddress)}");
    }

    /// <summary>
    /// Mint a new token. Only callable by the contract owner (H-5).
    /// </summary>
    [BasaltEntrypoint]
    public ulong Mint(byte[] to, string tokenUri)
    {
        Context.Require(AddressKey(Context.Caller) == _contractAdmin.Get("owner"), "BST721: not owner");
        var tokenId = _nextTokenId.Get();
        _nextTokenId.Set(tokenId + 1);

        _owners.Set(TokenKey(tokenId), AddressKey(to));
        _tokenURIs.Set(TokenKey(tokenId), tokenUri);

        var balance = _balances.Get(AddressKey(to));
        _balances.Set(AddressKey(to), balance + 1);

        Context.Emit(new NftTransferEvent
        {
            From = new byte[20], // Zero = mint
            To = to,
            TokenId = tokenId,
        });

        return tokenId;
    }

    private void TransferInternal(byte[] from, byte[] to, ulong tokenId)
    {
        // Clear approval
        _approvals.Delete(TokenKey(tokenId));

        // Update balances
        var fromBalance = _balances.Get(AddressKey(from));
        _balances.Set(AddressKey(from), fromBalance - 1);

        var toBalance = _balances.Get(AddressKey(to));
        _balances.Set(AddressKey(to), toBalance + 1);

        // Update owner
        _owners.Set(TokenKey(tokenId), AddressKey(to));

        Context.Emit(new NftTransferEvent
        {
            From = from,
            To = to,
            TokenId = tokenId,
        });
    }

    private static string TokenKey(ulong tokenId) => tokenId.ToString();
    private static string AddressKey(byte[] address) => Convert.ToHexString(address);
}
