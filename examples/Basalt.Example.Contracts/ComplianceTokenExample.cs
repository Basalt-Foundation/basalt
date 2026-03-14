using Basalt.Core;
using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Policies;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;

namespace Basalt.Example.Contracts;

/// <summary>
/// Example: A regulated security token using BST-20 with policy hooks.
///
/// This shows how to:
///   1. Deploy a BST-20 token
///   2. Deploy compliance policies (sanctions, lockup, jurisdiction)
///   3. Register policies on the token
///   4. All transfers are automatically enforced
///
/// Run this as a standalone program to see the compliance workflow.
/// </summary>
public static class ComplianceTokenExample
{
    public static void Run()
    {
        using var host = new BasaltTestHost();

        // --- Addresses ---
        var issuer = BasaltTestHost.CreateAddress(1);
        var alice = BasaltTestHost.CreateAddress(2);
        var bob = BasaltTestHost.CreateAddress(3);
        var charlie = BasaltTestHost.CreateAddress(4); // Will be sanctioned
        var tokenAddr = BasaltTestHost.CreateAddress(0xA0);
        var sanctionsAddr = BasaltTestHost.CreateAddress(0xA1);
        var lockupAddr = BasaltTestHost.CreateAddress(0xA2);
        var jurisdictionAddr = BasaltTestHost.CreateAddress(0xA3);

        // === Step 1: Deploy the security token ===
        host.SetCaller(issuer);
        Context.Self = tokenAddr;
        var token = new BST20Token("RegulatedToken", "RSEC", 18, new UInt256(1_000_000));
        host.Deploy(tokenAddr, token);

        // === Step 2: Deploy compliance policies ===

        // Sanctions: blocks transfers involving sanctioned addresses
        Context.Self = sanctionsAddr;
        var sanctions = new SanctionsPolicy();
        host.Deploy(sanctionsAddr, sanctions);

        // Lockup: time-based transfer restrictions for insiders
        Context.Self = lockupAddr;
        var lockup = new LockupPolicy();
        host.Deploy(lockupAddr, lockup);

        // Jurisdiction: country-based whitelist/blacklist
        Context.Self = jurisdictionAddr;
        var jurisdiction = new JurisdictionPolicy();
        host.Deploy(jurisdictionAddr, jurisdiction);

        Context.IsDeploying = false;

        // === Step 3: Configure policies ===

        // Sanction Charlie
        host.SetCaller(issuer);
        Context.Self = sanctionsAddr;
        sanctions.AddSanction(charlie);

        // Set 6-month lockup on Alice (insider)
        host.SetBlockTimestamp(1_000_000);
        Context.Self = lockupAddr;
        lockup.SetLockup(tokenAddr, alice, 16_000_000); // ~6 months

        // Whitelist US + EU jurisdictions
        Context.Self = jurisdictionAddr;
        jurisdiction.SetMode(tokenAddr, true); // whitelist mode
        jurisdiction.SetJurisdiction(tokenAddr, 840, true); // US
        jurisdiction.SetJurisdiction(tokenAddr, 276, true); // Germany
        jurisdiction.SetAddressJurisdiction(alice, 840);
        jurisdiction.SetAddressJurisdiction(bob, 276);

        // === Step 4: Register all policies on the token ===
        host.SetCaller(issuer);
        Context.Self = tokenAddr;
        token.AddPolicy(sanctionsAddr);
        token.AddPolicy(lockupAddr);
        token.AddPolicy(jurisdictionAddr);

        // Token now has 3 active policies
        Console.WriteLine($"Policies registered: {token.PolicyCount()}");

        // === Step 5: Distribute tokens ===
        token.Transfer(alice, new UInt256(50_000));
        token.Transfer(bob, new UInt256(50_000));
        Console.WriteLine($"Alice balance: {token.BalanceOf(alice)}");
        Console.WriteLine($"Bob balance: {token.BalanceOf(bob)}");

        // === Step 6: Demonstrate enforcement ===

        // Bob -> Alice: works (both in whitelisted jurisdictions, no lockup on Bob)
        host.SetCaller(bob);
        Context.Self = tokenAddr;
        token.Transfer(alice, new UInt256(1_000));
        Console.WriteLine("Bob -> Alice: OK");

        // Alice -> Bob: BLOCKED (Alice is locked up)
        host.SetCaller(alice);
        Context.Self = tokenAddr;
        try { token.Transfer(bob, new UInt256(100)); }
        catch (ContractRevertException e) { Console.WriteLine($"Alice -> Bob: DENIED ({e.Message})"); }

        // Bob -> Charlie: BLOCKED (Charlie is sanctioned)
        host.SetCaller(bob);
        Context.Self = tokenAddr;
        try { token.Transfer(charlie, new UInt256(100)); }
        catch (ContractRevertException e) { Console.WriteLine($"Bob -> Charlie: DENIED ({e.Message})"); }

        // === Step 7: Time passes, lockup expires ===
        host.SetBlockTimestamp(16_000_001);
        host.SetCaller(alice);
        Context.Self = tokenAddr;
        token.Transfer(bob, new UInt256(5_000));
        Console.WriteLine("Alice -> Bob after lockup expiry: OK");

        Console.WriteLine($"Final Alice balance: {token.BalanceOf(alice)}");
        Console.WriteLine($"Final Bob balance: {token.BalanceOf(bob)}");
    }
}
