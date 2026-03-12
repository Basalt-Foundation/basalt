using Basalt.Core;
using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Policies;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests.PolicyTests;

/// <summary>
/// End-to-end test: deploy a BST-20 token with multiple policies
/// (sanctions + lockup), demonstrate the full compliance lifecycle.
/// </summary>
public class EndToEndPolicyTests : IDisposable
{
    private readonly BasaltTestHost _host;
    private readonly byte[] _admin = BasaltTestHost.CreateAddress(1);
    private readonly byte[] _alice = BasaltTestHost.CreateAddress(2);
    private readonly byte[] _bob = BasaltTestHost.CreateAddress(3);
    private readonly byte[] _charlie = BasaltTestHost.CreateAddress(4);
    private readonly byte[] _tokenAddr = BasaltTestHost.CreateAddress(0xA0);
    private readonly byte[] _sanctionsAddr = BasaltTestHost.CreateAddress(0xA1);
    private readonly byte[] _lockupAddr = BasaltTestHost.CreateAddress(0xA2);
    private readonly BST20Token _token;
    private readonly SanctionsPolicy _sanctions;
    private readonly LockupPolicy _lockup;

    public EndToEndPolicyTests()
    {
        _host = new BasaltTestHost();
        _host.SetBlockTimestamp(1_000_000);

        // Deploy token with 1M supply
        _host.SetCaller(_admin);
        Context.Self = _tokenAddr;
        _token = new BST20Token("ComplianceToken", "CMPL", 18, new UInt256(1_000_000));
        _host.Deploy(_tokenAddr, _token);

        // Deploy sanctions policy
        Context.Self = _sanctionsAddr;
        _sanctions = new SanctionsPolicy();
        _host.Deploy(_sanctionsAddr, _sanctions);

        // Deploy lockup policy
        Context.Self = _lockupAddr;
        _lockup = new LockupPolicy();
        _host.Deploy(_lockupAddr, _lockup);

        Context.IsDeploying = false;
    }

    [Fact]
    public void FullComplianceLifecycle()
    {
        // --- Step 1: Register both policies ---
        _host.SetCaller(_admin);
        Context.Self = _tokenAddr;
        _token.AddPolicy(_sanctionsAddr);
        _token.AddPolicy(_lockupAddr);
        _token.PolicyCount().Should().Be(2);

        // --- Step 2: Distribute tokens ---
        _host.SetCaller(_admin);
        Context.Self = _tokenAddr;
        _host.Call(() => _token.Transfer(_alice, new UInt256(10_000)));
        _host.Call(() => _token.Transfer(_bob, new UInt256(10_000)));
        _token.BalanceOf(_alice).Should().Be(new UInt256(10_000));

        // --- Step 3: Normal transfers work ---
        _host.SetCaller(_alice);
        Context.Self = _tokenAddr;
        _host.Call(() => _token.Transfer(_charlie, new UInt256(100)));
        _token.BalanceOf(_charlie).Should().Be(new UInt256(100));

        // --- Step 4: Sanction Charlie --- transfers to Charlie blocked ---
        _host.SetCaller(_admin);
        Context.Self = _sanctionsAddr;
        _sanctions.AddSanction(_charlie);

        _host.SetCaller(_alice);
        Context.Self = _tokenAddr;
        var msg = _host.ExpectRevert(() => _token.Transfer(_charlie, new UInt256(50)));
        msg.Should().Contain("transfer denied");

        // --- Step 5: Set lockup on Bob ---
        _host.SetCaller(_admin);
        Context.Self = _lockupAddr;
        _lockup.SetLockup(_tokenAddr, _bob, 2_000_000); // Unlocks at t=2M

        // Bob can receive tokens
        _host.SetCaller(_alice);
        Context.Self = _tokenAddr;
        _host.Call(() => _token.Transfer(_bob, new UInt256(200)));

        // Bob cannot send tokens (locked)
        _host.SetCaller(_bob);
        Context.Self = _tokenAddr;
        msg = _host.ExpectRevert(() => _token.Transfer(_alice, new UInt256(50)));
        msg.Should().Contain("transfer denied");

        // --- Step 6: Time passes, lockup expires ---
        _host.SetBlockTimestamp(2_000_001);

        _host.SetCaller(_bob);
        Context.Self = _tokenAddr;
        _host.Call(() => _token.Transfer(_alice, new UInt256(50)));
        _token.BalanceOf(_alice).Should().Be(new UInt256(9_750)); // 10000 - 100 - 200 + 50

        // --- Step 7: Remove sanctions, Charlie can receive again ---
        _host.SetCaller(_admin);
        Context.Self = _sanctionsAddr;
        _sanctions.RemoveSanction(_charlie);

        _host.SetCaller(_alice);
        Context.Self = _tokenAddr;
        _host.Call(() => _token.Transfer(_charlie, new UInt256(25)));
        _token.BalanceOf(_charlie).Should().Be(new UInt256(125));

        // --- Step 8: Remove all policies, no restrictions ---
        _host.SetCaller(_admin);
        Context.Self = _tokenAddr;
        _token.RemovePolicy(_sanctionsAddr);
        _token.RemovePolicy(_lockupAddr);
        _token.PolicyCount().Should().Be(0);
    }

    [Fact]
    public void PolicyEvents_EmittedCorrectly()
    {
        _host.ClearEvents();

        _host.SetCaller(_admin);
        Context.Self = _tokenAddr;
        _token.AddPolicy(_sanctionsAddr);

        var addEvents = _host.GetEvents<PolicyAddedEvent>().ToList();
        addEvents.Should().HaveCount(1);
        addEvents[0].Policy.Should().BeEquivalentTo(_sanctionsAddr);

        _host.ClearEvents();
        _token.RemovePolicy(_sanctionsAddr);

        var removeEvents = _host.GetEvents<PolicyRemovedEvent>().ToList();
        removeEvents.Should().HaveCount(1);
        removeEvents[0].Policy.Should().BeEquivalentTo(_sanctionsAddr);
    }

    [Fact]
    public void StaticCall_PolicyCallbackCanReadButNotWrite()
    {
        // Deploy a HoldingLimitPolicy that calls back token.BalanceOf during enforcement
        var holdingAddr = BasaltTestHost.CreateAddress(0xA5);
        _host.SetCaller(_admin);
        Context.Self = holdingAddr;
        Context.IsDeploying = true;
        var holding = new HoldingLimitPolicy();
        _host.Deploy(holdingAddr, holding);
        Context.IsDeploying = false;

        // Set a limit so CheckTransfer will call back token.BalanceOf (read)
        _host.SetCaller(_admin);
        Context.Self = holdingAddr;
        holding.SetDefaultLimit(_tokenAddr, new UInt256(50_000));

        // Register policy on token
        _host.SetCaller(_admin);
        Context.Self = _tokenAddr;
        _token.AddPolicy(holdingAddr);

        // Distribute tokens
        _host.Call(() => _token.Transfer(_alice, new UInt256(10_000)));

        // Transfer should succeed — HoldingLimitPolicy reads BalanceOf via static callback
        _host.SetCaller(_alice);
        Context.Self = _tokenAddr;
        _host.Call(() => _token.Transfer(_bob, new UInt256(1_000)));
        _token.BalanceOf(_bob).Should().Be(new UInt256(1_000));
    }

    [Fact]
    public void StaticCall_BlocksWritesDuringReentrantCallback()
    {
        // Use HoldingLimitPolicy which triggers the A→B→A pattern:
        // Token calls HoldingLimitPolicy.CheckTransfer, which calls back
        // Token.BalanceOf — this re-entry into the token forces static mode.
        var holdingAddr = BasaltTestHost.CreateAddress(0xA5);
        _host.SetCaller(_admin);
        Context.Self = holdingAddr;
        Context.IsDeploying = true;
        var holding = new HoldingLimitPolicy();
        _host.Deploy(holdingAddr, holding);
        Context.IsDeploying = false;

        // Set a holding limit so CheckTransfer will callback token.BalanceOf
        _host.SetCaller(_admin);
        Context.Self = holdingAddr;
        holding.SetDefaultLimit(_tokenAddr, new UInt256(50_000));

        // Register holding policy on token
        _host.SetCaller(_admin);
        Context.Self = _tokenAddr;
        _token.AddPolicy(holdingAddr);
        _host.Call(() => _token.Transfer(_alice, new UInt256(1_000)));

        // Now intercept the callback from holding policy → token.BalanceOf
        // to verify IsStaticCall is enforced and writes are blocked.
        var previousHandler = Context.CrossContractCallHandler;
        bool staticCallWasTrue = false;
        bool writeWasBlocked = false;

        Context.CrossContractCallHandler = (target, method, args) =>
        {
            // Intercept the BalanceOf callback (A→B→A: token→holding→token)
            if (method == "BalanceOf" && target.SequenceEqual(_tokenAddr))
            {
                staticCallWasTrue = Context.IsStaticCall;
                // Attempt to write during re-entrant callback — should be blocked
                try
                {
                    ContractStorage.Set("attack_key", "evil_value");
                }
                catch (ContractRevertException ex)
                {
                    writeWasBlocked = true;
                    ex.Message.Should().Contain("Static call");
                }
                // Return a balance so the policy check can complete
                return (UInt256)0;
            }
            return previousHandler?.Invoke(target, method, args);
        };

        // Trigger: Alice transfers to Bob → token calls holding.CheckTransfer
        // → holding calls back token.BalanceOf (re-entry, forced static)
        _host.SetCaller(_alice);
        Context.Self = _tokenAddr;
        _host.Call(() => _token.Transfer(_bob, new UInt256(100)));

        Context.CrossContractCallHandler = previousHandler;

        staticCallWasTrue.Should().BeTrue("re-entrant callback should execute in static mode");
        writeWasBlocked.Should().BeTrue("storage writes should be blocked during static callbacks");
        _token.BalanceOf(_bob).Should().Be(new UInt256(100));
    }

    [Fact]
    public void JurisdictionPolicy_WorksWithNftTransfers()
    {
        // Deploy BST-721 token
        var nftAddr = BasaltTestHost.CreateAddress(0xB0);
        var jurisdictionAddr = BasaltTestHost.CreateAddress(0xB1);

        _host.SetCaller(_admin);
        Context.IsDeploying = true;

        Context.Self = nftAddr;
        var nft = new BST721Token("TestNFT", "TNFT");
        _host.Deploy(nftAddr, nft);

        Context.Self = jurisdictionAddr;
        var jurisdiction = new JurisdictionPolicy();
        _host.Deploy(jurisdictionAddr, jurisdiction);

        Context.IsDeploying = false;

        // Set whitelist mode and whitelist US only
        _host.SetCaller(_admin);
        Context.Self = jurisdictionAddr;
        jurisdiction.SetMode(nftAddr, true); // whitelist
        jurisdiction.SetJurisdiction(nftAddr, 840, true); // US
        jurisdiction.SetAddressJurisdiction(_admin, 840); // Admin = US
        jurisdiction.SetAddressJurisdiction(_alice, 840); // Alice = US
        jurisdiction.SetAddressJurisdiction(_bob, 392); // Bob = Japan (not whitelisted)

        // Register on NFT
        _host.SetCaller(_admin);
        Context.Self = nftAddr;
        nft.AddPolicy(jurisdictionAddr);

        // Mint to admin (first token = ID 0)
        var tokenId = nft.Mint(_admin, "uri://1");

        // Transfer to Alice (US, whitelisted) — should work
        nft.Transfer(_alice, tokenId);
        nft.OwnerOf(tokenId).Should().BeEquivalentTo(_alice);

        // Alice → Bob (Japan, not whitelisted) — should revert
        _host.SetCaller(_alice);
        Context.Self = nftAddr;
        var msg = _host.ExpectRevert(() => nft.Transfer(_bob, tokenId));
        msg.Should().Contain("transfer denied");
    }

    public void Dispose() => _host.Dispose();
}
