using Basalt.Core;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Reference implementation of the BST-20 Fungible Token Standard.
/// </summary>
[BasaltContract]
public partial class BST20Token : IBST20
{
    // Storage fields
    private readonly StorageValue<UInt256> _totalSupply;
    private readonly StorageMap<string, UInt256> _balances;
    private readonly StorageMap<string, UInt256> _allowances;
    private readonly string _name;
    private readonly string _symbol;
    private readonly byte _decimals;

    public BST20Token(string name, string symbol, byte decimals = 18)
    {
        _name = name;
        _symbol = symbol;
        _decimals = decimals;
        _totalSupply = new StorageValue<UInt256>("total_supply");
        _balances = new StorageMap<string, UInt256>("balances");
        _allowances = new StorageMap<string, UInt256>("allowances");
    }

    [BasaltView]
    public string Name() => _name;

    [BasaltView]
    public string Symbol() => _symbol;

    [BasaltView]
    public byte Decimals() => _decimals;

    [BasaltView]
    public UInt256 TotalSupply() => _totalSupply.Get();

    [BasaltView]
    public UInt256 BalanceOf(byte[] account)
    {
        return _balances.Get(ToKey(account));
    }

    [BasaltEntrypoint]
    public bool Transfer(byte[] to, UInt256 amount)
    {
        var sender = Context.Caller;
        return TransferInternal(sender, to, amount);
    }

    [BasaltView]
    public UInt256 Allowance(byte[] owner, byte[] spender)
    {
        return _allowances.Get(AllowanceKey(owner, spender));
    }

    [BasaltEntrypoint]
    public bool Approve(byte[] spender, UInt256 amount)
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

    /// <summary>
    /// Atomically increase the spender's allowance, preventing approve race conditions (H-4).
    /// </summary>
    [BasaltEntrypoint]
    public bool IncreaseAllowance(byte[] spender, UInt256 addedValue)
    {
        var owner = Context.Caller;
        var key = AllowanceKey(owner, spender);
        var current = _allowances.Get(key);
        var newAllowance = UInt256.CheckedAdd(current, addedValue);
        _allowances.Set(key, newAllowance);

        Context.Emit(new ApprovalEvent
        {
            Owner = owner,
            Spender = spender,
            Amount = newAllowance,
        });

        return true;
    }

    /// <summary>
    /// Atomically decrease the spender's allowance, preventing approve race conditions (H-4).
    /// </summary>
    [BasaltEntrypoint]
    public bool DecreaseAllowance(byte[] spender, UInt256 subtractedValue)
    {
        var owner = Context.Caller;
        var key = AllowanceKey(owner, spender);
        var current = _allowances.Get(key);
        Context.Require(current >= subtractedValue, "BST20: decreased allowance below zero");
        _allowances.Set(key, current - subtractedValue);

        Context.Emit(new ApprovalEvent
        {
            Owner = owner,
            Spender = spender,
            Amount = current - subtractedValue,
        });

        return true;
    }

    [BasaltEntrypoint]
    public bool TransferFrom(byte[] from, byte[] to, UInt256 amount)
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
    protected void Mint(byte[] to, UInt256 amount)
    {
        var supply = _totalSupply.Get();
        var newSupply = UInt256.CheckedAdd(supply, amount);
        _totalSupply.Set(newSupply);

        var balance = _balances.Get(ToKey(to));
        var newBalance = UInt256.CheckedAdd(balance, amount);
        _balances.Set(ToKey(to), newBalance);

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
    protected void Burn(byte[] from, UInt256 amount)
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

    private bool TransferInternal(byte[] from, byte[] to, UInt256 amount)
    {
        var fromBalance = _balances.Get(ToKey(from));
        Context.Require(fromBalance >= amount, "BST20: insufficient balance");

        _balances.Set(ToKey(from), fromBalance - amount);

        var toBalance = _balances.Get(ToKey(to));
        var newToBalance = UInt256.CheckedAdd(toBalance, amount);
        _balances.Set(ToKey(to), newToBalance);

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
