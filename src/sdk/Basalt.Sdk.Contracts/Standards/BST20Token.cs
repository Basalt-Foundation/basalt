namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Reference implementation of the BST-20 Fungible Token Standard.
/// </summary>
[BasaltContract]
public partial class BST20Token : IBST20
{
    // Storage fields
    private readonly StorageValue<ulong> _totalSupply;
    private readonly StorageMap<string, ulong> _balances;
    private readonly StorageMap<string, ulong> _allowances;
    private readonly string _name;
    private readonly string _symbol;
    private readonly byte _decimals;

    public BST20Token(string name, string symbol, byte decimals = 18)
    {
        _name = name;
        _symbol = symbol;
        _decimals = decimals;
        _totalSupply = new StorageValue<ulong>("total_supply");
        _balances = new StorageMap<string, ulong>("balances");
        _allowances = new StorageMap<string, ulong>("allowances");
    }

    [BasaltView]
    public string Name() => _name;

    [BasaltView]
    public string Symbol() => _symbol;

    [BasaltView]
    public byte Decimals() => _decimals;

    [BasaltView]
    public ulong TotalSupply() => _totalSupply.Get();

    [BasaltView]
    public ulong BalanceOf(byte[] account)
    {
        return _balances.Get(ToKey(account));
    }

    [BasaltEntrypoint]
    public bool Transfer(byte[] to, ulong amount)
    {
        var sender = Context.Caller;
        return TransferInternal(sender, to, amount);
    }

    [BasaltView]
    public ulong Allowance(byte[] owner, byte[] spender)
    {
        return _allowances.Get(AllowanceKey(owner, spender));
    }

    [BasaltEntrypoint]
    public bool Approve(byte[] spender, ulong amount)
    {
        var owner = Context.Caller;
        _allowances.Set(AllowanceKey(owner, spender), amount);

        Context.Emit(new ApprovalEvent
        {
            Owner = owner,
            Spender = spender,
            Amount = amount,
        });

        return true;
    }

    [BasaltEntrypoint]
    public bool TransferFrom(byte[] from, byte[] to, ulong amount)
    {
        var spender = Context.Caller;
        var allowanceKey = AllowanceKey(from, spender);
        var currentAllowance = _allowances.Get(allowanceKey);

        Context.Require(currentAllowance >= amount, "BST20: insufficient allowance");

        _allowances.Set(allowanceKey, currentAllowance - amount);
        return TransferInternal(from, to, amount);
    }

    /// <summary>
    /// Mint new tokens to an address. Only callable internally.
    /// </summary>
    protected void Mint(byte[] to, ulong amount)
    {
        var supply = _totalSupply.Get();
        _totalSupply.Set(supply + amount);

        var balance = _balances.Get(ToKey(to));
        _balances.Set(ToKey(to), balance + amount);

        Context.Emit(new TransferEvent
        {
            From = new byte[20], // Zero address = mint
            To = to,
            Amount = amount,
        });
    }

    /// <summary>
    /// Burn tokens from an address. Only callable internally.
    /// </summary>
    protected void Burn(byte[] from, ulong amount)
    {
        var balance = _balances.Get(ToKey(from));
        Context.Require(balance >= amount, "BST20: burn exceeds balance");

        _balances.Set(ToKey(from), balance - amount);
        var supply = _totalSupply.Get();
        _totalSupply.Set(supply - amount);

        Context.Emit(new TransferEvent
        {
            From = from,
            To = new byte[20], // Zero address = burn
            Amount = amount,
        });
    }

    private bool TransferInternal(byte[] from, byte[] to, ulong amount)
    {
        var fromBalance = _balances.Get(ToKey(from));
        Context.Require(fromBalance >= amount, "BST20: insufficient balance");

        _balances.Set(ToKey(from), fromBalance - amount);

        var toBalance = _balances.Get(ToKey(to));
        _balances.Set(ToKey(to), toBalance + amount);

        Context.Emit(new TransferEvent
        {
            From = from,
            To = to,
            Amount = amount,
        });

        return true;
    }

    private static string ToKey(byte[] address) => Convert.ToHexString(address);

    private static string AllowanceKey(byte[] owner, byte[] spender) =>
        $"{Convert.ToHexString(owner)}:{Convert.ToHexString(spender)}";
}
