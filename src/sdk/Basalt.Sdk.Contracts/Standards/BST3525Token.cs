using Basalt.Core;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// BST-3525 Semi-Fungible Token — equivalent to ERC-3525.
/// Three-component model: (tokenId, slot, value). Tokens in the same slot
/// are fungible by value.
/// Type ID: 0x0005
/// </summary>
[BasaltContract]
public partial class BST3525Token : IBST3525
{
    private readonly string _name;
    private readonly string _symbol;
    private readonly byte _valueDecimals;
    private readonly StorageValue<ulong> _nextTokenId;
    private readonly StorageMap<string, UInt256> _tokenValues;
    private readonly StorageMap<string, ulong> _tokenSlots;
    private readonly StorageMap<string, string> _tokenOwners;
    private readonly StorageMap<string, ulong> _ownerBalances;
    private readonly StorageMap<string, string> _tokenApprovals;
    private readonly StorageMap<string, UInt256> _valueAllowances;
    private readonly StorageMap<string, string> _slotUris;
    private readonly StorageMap<string, string> _tokenUris;

    public BST3525Token(string name, string symbol, byte valueDecimals = 0)
    {
        _name = name;
        _symbol = symbol;
        _valueDecimals = valueDecimals;
        _nextTokenId = new StorageValue<ulong>("sft_next_id");
        _tokenValues = new StorageMap<string, UInt256>("sft_values");
        _tokenSlots = new StorageMap<string, ulong>("sft_slots");
        _tokenOwners = new StorageMap<string, string>("sft_owners");
        _ownerBalances = new StorageMap<string, ulong>("sft_obal");
        _tokenApprovals = new StorageMap<string, string>("sft_tappr");
        _valueAllowances = new StorageMap<string, UInt256>("sft_vallw");
        _slotUris = new StorageMap<string, string>("sft_suri");
        _tokenUris = new StorageMap<string, string>("sft_turi");
    }

    // --- Views ---

    [BasaltView]
    public string Name() => _name;

    [BasaltView]
    public string Symbol() => _symbol;

    [BasaltView]
    public byte ValueDecimals() => _valueDecimals;

    [BasaltView]
    public UInt256 BalanceOf(ulong tokenId)
        => _tokenValues.Get(TokenKey(tokenId));

    [BasaltView]
    public ulong TokenOwnerBalance(byte[] owner)
        => _ownerBalances.Get(AddrKey(owner));

    [BasaltView]
    public ulong SlotOf(ulong tokenId)
    {
        RequireTokenExists(tokenId);
        return _tokenSlots.Get(TokenKey(tokenId));
    }

    [BasaltView]
    public byte[] OwnerOf(ulong tokenId)
    {
        var ownerHex = _tokenOwners.Get(TokenKey(tokenId));
        Context.Require(!string.IsNullOrEmpty(ownerHex), "SFT: token does not exist");
        return Convert.FromHexString(ownerHex);
    }

    [BasaltView]
    public UInt256 ValueAllowance(ulong tokenId, byte[] operatorAddr)
        => _valueAllowances.Get(ValueAllowanceKey(tokenId, operatorAddr));

    [BasaltView]
    public byte[] GetApproved(ulong tokenId)
    {
        var hex = _tokenApprovals.Get(TokenKey(tokenId));
        return string.IsNullOrEmpty(hex) ? [] : Convert.FromHexString(hex);
    }

    [BasaltView]
    public string SlotUri(ulong slot)
        => _slotUris.Get(slot.ToString()) ?? "";

    [BasaltView]
    public string TokenUri(ulong tokenId)
        => _tokenUris.Get(TokenKey(tokenId)) ?? "";

    // --- Entrypoints ---

    [BasaltEntrypoint]
    public ulong Mint(byte[] to, ulong slot, UInt256 value)
    {
        Context.Require(to.Length > 0, "SFT: mint to zero address");
        return MintInternal(to, slot, value);
    }

    [BasaltEntrypoint]
    public void TransferValueToId(ulong fromId, ulong toId, UInt256 value)
    {
        Context.Require(value > 0, "SFT: zero value");
        RequireTokenExists(fromId);
        RequireTokenExists(toId);

        var fromSlot = _tokenSlots.Get(TokenKey(fromId));
        var toSlot = _tokenSlots.Get(TokenKey(toId));
        Context.Require(fromSlot == toSlot, "SFT: slot mismatch");

        RequireValueAuthorized(fromId, value);

        var fromVal = _tokenValues.Get(TokenKey(fromId));
        Context.Require(fromVal >= value, "SFT: insufficient value");
        _tokenValues.Set(TokenKey(fromId), fromVal - value);

        var toVal = _tokenValues.Get(TokenKey(toId));
        _tokenValues.Set(TokenKey(toId), toVal + value);

        Context.Emit(new TransferValueEvent
        {
            FromTokenId = fromId,
            ToTokenId = toId,
            Value = value,
        });
    }

    [BasaltEntrypoint]
    public ulong TransferValueToAddress(ulong fromId, byte[] to, UInt256 value)
    {
        Context.Require(value > 0, "SFT: zero value");
        Context.Require(to.Length > 0, "SFT: transfer to zero address");
        RequireTokenExists(fromId);
        RequireValueAuthorized(fromId, value);

        var fromVal = _tokenValues.Get(TokenKey(fromId));
        Context.Require(fromVal >= value, "SFT: insufficient value");

        var slot = _tokenSlots.Get(TokenKey(fromId));
        var newId = MintInternal(to, slot, UInt256.Zero);

        _tokenValues.Set(TokenKey(fromId), fromVal - value);
        _tokenValues.Set(TokenKey(newId), value);

        Context.Emit(new TransferValueEvent
        {
            FromTokenId = fromId,
            ToTokenId = newId,
            Value = value,
        });

        return newId;
    }

    [BasaltEntrypoint]
    public void ApproveValue(ulong tokenId, byte[] operatorAddr, UInt256 value)
    {
        RequireTokenOwner(tokenId);
        _valueAllowances.Set(ValueAllowanceKey(tokenId, operatorAddr), value);

        Context.Emit(new ApproveValueEvent
        {
            TokenId = tokenId,
            Operator = operatorAddr,
            Value = value,
        });
    }

    [BasaltEntrypoint]
    public void TransferToken(byte[] to, ulong tokenId)
    {
        Context.Require(to.Length > 0, "SFT: transfer to zero address");
        var ownerHex = _tokenOwners.Get(TokenKey(tokenId));
        Context.Require(!string.IsNullOrEmpty(ownerHex), "SFT: token does not exist");

        var callerHex = AddrKey(Context.Caller);
        var approvedHex = _tokenApprovals.Get(TokenKey(tokenId));
        Context.Require(
            callerHex == ownerHex || callerHex == approvedHex,
            "SFT: not owner or approved");

        var toHex = AddrKey(to);
        var from = Convert.FromHexString(ownerHex);

        // Update ownership
        _tokenOwners.Set(TokenKey(tokenId), toHex);

        // Clear approval
        _tokenApprovals.Delete(TokenKey(tokenId));

        // Update balances
        var fromBal = _ownerBalances.Get(ownerHex);
        if (fromBal > 0) _ownerBalances.Set(ownerHex, fromBal - 1);
        _ownerBalances.Set(toHex, _ownerBalances.Get(toHex) + 1);

        Context.Emit(new SftTransferEvent
        {
            From = from,
            To = to,
            TokenId = tokenId,
        });
    }

    [BasaltEntrypoint]
    public void ApproveToken(byte[] to, ulong tokenId)
    {
        RequireTokenOwner(tokenId);
        _tokenApprovals.Set(TokenKey(tokenId), AddrKey(to));

        Context.Emit(new SftApprovalEvent
        {
            Owner = Context.Caller,
            Approved = to,
            TokenId = tokenId,
        });
    }

    [BasaltEntrypoint]
    public void SetSlotUri(ulong slot, string uri)
    {
        _slotUris.Set(slot.ToString(), uri);
    }

    [BasaltEntrypoint]
    public void SetTokenUri(ulong tokenId, string uri)
    {
        RequireTokenOwner(tokenId);
        _tokenUris.Set(TokenKey(tokenId), uri);
    }

    // --- Internal helpers ---

    private ulong MintInternal(byte[] to, ulong slot, UInt256 value)
    {
        var id = _nextTokenId.Get() + 1;
        _nextTokenId.Set(id);

        var toHex = AddrKey(to);
        _tokenOwners.Set(TokenKey(id), toHex);
        _tokenSlots.Set(TokenKey(id), slot);
        _tokenValues.Set(TokenKey(id), value);
        _ownerBalances.Set(toHex, _ownerBalances.Get(toHex) + 1);

        Context.Emit(new SftMintEvent
        {
            To = to,
            TokenId = id,
            Slot = slot,
            Value = value,
        });

        return id;
    }

    private void RequireTokenExists(ulong tokenId)
    {
        var ownerHex = _tokenOwners.Get(TokenKey(tokenId));
        Context.Require(!string.IsNullOrEmpty(ownerHex), "SFT: token does not exist");
    }

    private void RequireTokenOwner(ulong tokenId)
    {
        var ownerHex = _tokenOwners.Get(TokenKey(tokenId));
        Context.Require(!string.IsNullOrEmpty(ownerHex), "SFT: token does not exist");
        Context.Require(AddrKey(Context.Caller) == ownerHex, "SFT: not owner");
    }

    private void RequireValueAuthorized(ulong tokenId, UInt256 value)
    {
        var ownerHex = _tokenOwners.Get(TokenKey(tokenId));
        var callerHex = AddrKey(Context.Caller);
        if (callerHex == ownerHex) return;

        var key = ValueAllowanceKey(tokenId, Context.Caller);
        var allowance = _valueAllowances.Get(key);
        Context.Require(allowance >= value, "SFT: insufficient allowance");
        _valueAllowances.Set(key, allowance - value);
    }

    private static string TokenKey(ulong id) => id.ToString();
    private static string AddrKey(byte[] addr) => Convert.ToHexString(addr);
    private static string ValueAllowanceKey(ulong tokenId, byte[] op)
        => tokenId.ToString() + ":" + Convert.ToHexString(op);
}
