using Basalt.Core;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Reference implementation of the BST-1155 Multi-Token Standard.
/// </summary>
[BasaltContract]
public partial class BST1155Token : IBST1155
{
    private readonly StorageMap<string, UInt256> _balances;       // "account:tokenId" -> balance
    private readonly StorageMap<string, string> _approvals;      // "owner:operator" -> "1"/"0"
    private readonly StorageMap<string, string> _tokenURIs;      // tokenId -> uri
    private readonly StorageValue<ulong> _nextTokenId;
    private readonly StorageMap<string, string> _contractAdmin;
    private readonly string _baseUri;

    public BST1155Token(string baseUri)
    {
        _baseUri = baseUri;
        _balances = new StorageMap<string, UInt256>("m_balances");
        _approvals = new StorageMap<string, string>("m_approvals");
        _tokenURIs = new StorageMap<string, string>("m_uris");
        _nextTokenId = new StorageValue<ulong>("m_next_id");
        _contractAdmin = new StorageMap<string, string>("m_admin");
        _contractAdmin.Set("owner", Convert.ToHexString(Context.Caller));
    }

    [BasaltView]
    public UInt256 BalanceOf(byte[] account, ulong tokenId)
    {
        return _balances.Get(BalanceKey(account, tokenId));
    }

    [BasaltView]
    public ulong[] BalanceOfBatch(byte[][] accounts, ulong[] tokenIds)
    {
        Context.Require(accounts.Length == tokenIds.Length, "BST1155: length mismatch");
        var result = new ulong[accounts.Length];
        for (int i = 0; i < accounts.Length; i++)
            result[i] = (ulong)_balances.Get(BalanceKey(accounts[i], tokenIds[i]));
        return result;
    }

    [BasaltEntrypoint]
    public void SafeTransferFrom(byte[] from, byte[] to, ulong tokenId, UInt256 amount)
    {
        var caller = Context.Caller;
        Context.Require(
            IsCallerOrApproved(from, caller),
            "BST1155: caller is not owner or approved");

        var fromBalance = _balances.Get(BalanceKey(from, tokenId));
        Context.Require(fromBalance >= amount, "BST1155: insufficient balance");

        _balances.Set(BalanceKey(from, tokenId), fromBalance - amount);
        var toBalance = _balances.Get(BalanceKey(to, tokenId));
        // N-1: Use checked addition to prevent silent overflow
        _balances.Set(BalanceKey(to, tokenId), UInt256.CheckedAdd(toBalance, amount));

        Context.Emit(new TransferSingleEvent
        {
            Operator = caller,
            From = from,
            To = to,
            TokenId = tokenId,
            Amount = amount,
        });
    }

    [BasaltEntrypoint]
    public void SafeBatchTransferFrom(byte[] from, byte[] to, ulong[] tokenIds, ulong[] amounts)
    {
        var caller = Context.Caller;
        // L-4: Reject empty batch arrays to prevent gas waste
        Context.Require(tokenIds.Length > 0, "BST1155: empty batch");
        Context.Require(tokenIds.Length == amounts.Length, "BST1155: length mismatch");
        Context.Require(
            IsCallerOrApproved(from, caller),
            "BST1155: caller is not owner or approved");

        for (int i = 0; i < tokenIds.Length; i++)
        {
            UInt256 amount = amounts[i];
            var fromBal = _balances.Get(BalanceKey(from, tokenIds[i]));
            Context.Require(fromBal >= amount, $"BST1155: insufficient balance for token {tokenIds[i]}");
            _balances.Set(BalanceKey(from, tokenIds[i]), fromBal - amount);

            var toBal = _balances.Get(BalanceKey(to, tokenIds[i]));
            // N-1: Use checked addition to prevent silent overflow
            _balances.Set(BalanceKey(to, tokenIds[i]), UInt256.CheckedAdd(toBal, amount));
        }

        Context.Emit(new TransferBatchEvent
        {
            Operator = caller,
            From = from,
            To = to,
            TokenIds = tokenIds,
            Amounts = amounts,
        });
    }

    [BasaltEntrypoint]
    public void SetApprovalForAll(byte[] operatorAddress, bool approved)
    {
        var owner = Context.Caller;
        _approvals.Set(ApprovalKey(owner, operatorAddress), approved ? "1" : "0");

        Context.Emit(new ApprovalForAllEvent
        {
            Owner = owner,
            Operator = operatorAddress,
            Approved = approved,
        });
    }

    [BasaltView]
    public bool IsApprovedForAll(byte[] owner, byte[] operatorAddress)
    {
        return _approvals.Get(ApprovalKey(owner, operatorAddress)) == "1";
    }

    [BasaltView]
    public string Uri(ulong tokenId)
    {
        var custom = _tokenURIs.Get(tokenId.ToString());
        return !string.IsNullOrEmpty(custom) ? custom : $"{_baseUri}{tokenId}";
    }

    /// <summary>
    /// Mint tokens of a specific ID. Only callable by the contract owner (H-6).
    /// </summary>
    [BasaltEntrypoint]
    public void Mint(byte[] to, ulong tokenId, UInt256 amount, string uri)
    {
        Context.Require(Convert.ToHexString(Context.Caller) == _contractAdmin.Get("owner"), "BST1155: not owner");
        var balance = _balances.Get(BalanceKey(to, tokenId));
        _balances.Set(BalanceKey(to, tokenId), balance + amount);

        if (!string.IsNullOrEmpty(uri))
            _tokenURIs.Set(tokenId.ToString(), uri);

        Context.Emit(new TransferSingleEvent
        {
            Operator = Context.Caller,
            From = new byte[20],
            To = to,
            TokenId = tokenId,
            Amount = amount,
        });
    }

    /// <summary>
    /// Create a new token type and mint initial supply. Only callable by the contract owner (H-6).
    /// </summary>
    [BasaltEntrypoint]
    public ulong Create(byte[] to, UInt256 initialSupply, string uri)
    {
        Context.Require(Convert.ToHexString(Context.Caller) == _contractAdmin.Get("owner"), "BST1155: not owner");
        var tokenId = _nextTokenId.Get();
        _nextTokenId.Set(tokenId + 1);

        if (initialSupply > 0)
            Mint(to, tokenId, initialSupply, uri);
        else if (!string.IsNullOrEmpty(uri))
            _tokenURIs.Set(tokenId.ToString(), uri);

        return tokenId;
    }

    private bool IsCallerOrApproved(byte[] owner, byte[] caller)
    {
        if (owner.AsSpan().SequenceEqual(caller))
            return true;
        return _approvals.Get(ApprovalKey(owner, caller)) == "1";
    }

    private static string BalanceKey(byte[] account, ulong tokenId)
        => $"{Convert.ToHexString(account)}:{tokenId}";

    private static string ApprovalKey(byte[] owner, byte[] operatorAddr)
        => $"{Convert.ToHexString(owner)}:{Convert.ToHexString(operatorAddr)}";
}
