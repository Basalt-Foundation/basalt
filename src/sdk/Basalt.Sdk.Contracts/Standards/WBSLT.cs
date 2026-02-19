using Basalt.Core;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Wrapped BSLT (WBSLT) — BST-20 token backed 1:1 by native BSLT.
/// Deposit native tokens to mint, burn to withdraw.
/// Type ID: 0x0100
/// </summary>
[BasaltContract]
public partial class WBSLT : BST20Token
{
    public WBSLT() : base("Wrapped BSLT", "WBSLT", 18) { }

    /// <summary>
    /// Deposit native BSLT to mint equal WBSLT.
    /// Send native tokens with the transaction.
    /// </summary>
    [BasaltEntrypoint]
    public void Deposit()
    {
        Context.Require(Context.TxValue > 0, "WBSLT: must send value");
        Mint(Context.Caller, Context.TxValue);
    }

    /// <summary>
    /// Burn WBSLT and withdraw native BSLT.
    /// </summary>
    [BasaltEntrypoint]
    public void Withdraw(UInt256 amount)
    {
        Context.Require(amount > 0, "WBSLT: amount must be > 0");
        Burn(Context.Caller, amount);
        Context.TransferNative(Context.Caller, amount);
    }
}
