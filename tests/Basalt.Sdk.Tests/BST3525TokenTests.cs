using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests;

public class BST3525TokenTests : IDisposable
{
    private readonly BasaltTestHost _host = new();
    private readonly BST3525Token _token;
    private readonly byte[] _alice;
    private readonly byte[] _bob;
    private readonly byte[] _charlie;

    public BST3525TokenTests()
    {
        _token = new BST3525Token("SemiFungible", "SFT", 18);
        _alice = BasaltTestHost.CreateAddress(1);
        _bob = BasaltTestHost.CreateAddress(2);
        _charlie = BasaltTestHost.CreateAddress(3);
    }

    // --- 1. Constructor: Name, Symbol, ValueDecimals ---

    [Fact]
    public void Constructor_ReturnsConfiguredName()
    {
        _host.Call(() => _token.Name()).Should().Be("SemiFungible");
    }

    [Fact]
    public void Constructor_ReturnsConfiguredSymbol()
    {
        _host.Call(() => _token.Symbol()).Should().Be("SFT");
    }

    [Fact]
    public void Constructor_ReturnsConfiguredValueDecimals()
    {
        _host.Call(() => _token.ValueDecimals()).Should().Be(18);
    }

    // --- 2. Mint: Creates token with correct slot, value, owner; auto-increments ID ---

    [Fact]
    public void Mint_CreatesTokenWithCorrectAttributes()
    {
        _host.SetCaller(_alice);
        var tokenId = _host.Call(() => _token.Mint(_bob, 10, 500));

        tokenId.Should().Be(1);
        _host.Call(() => _token.OwnerOf(1)).Should().BeEquivalentTo(_bob);
        _host.Call(() => _token.SlotOf(1)).Should().Be(10);
        _host.Call(() => _token.BalanceOf(1)).Should().Be(500);
    }

    [Fact]
    public void Mint_AutoIncrementsTokenId()
    {
        _host.SetCaller(_alice);
        var id1 = _host.Call(() => _token.Mint(_bob, 1, 100));
        var id2 = _host.Call(() => _token.Mint(_bob, 1, 200));
        var id3 = _host.Call(() => _token.Mint(_charlie, 2, 300));

        id1.Should().Be(1);
        id2.Should().Be(2);
        id3.Should().Be(3);
    }

    [Fact]
    public void Mint_ToZeroAddress_Reverts()
    {
        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _token.Mint([], 1, 100));
        msg.Should().Contain("mint to zero address");
    }

    // --- 3. BalanceOf: Returns value for existing token, 0 for nonexistent ---

    [Fact]
    public void BalanceOf_ReturnsValueForExistingToken()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Mint(_bob, 1, 750));

        _host.Call(() => _token.BalanceOf(1)).Should().Be(750);
    }

    [Fact]
    public void BalanceOf_ReturnsZeroForNonexistentToken()
    {
        _host.Call(() => _token.BalanceOf(999)).Should().Be(0);
    }

    // --- 4. SlotOf: Returns correct slot for existing token ---

    [Fact]
    public void SlotOf_ReturnsCorrectSlot()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Mint(_bob, 42, 100));

        _host.Call(() => _token.SlotOf(1)).Should().Be(42);
    }

    [Fact]
    public void SlotOf_NonexistentToken_Reverts()
    {
        var msg = _host.ExpectRevert(() => _token.SlotOf(999));
        msg.Should().Contain("token does not exist");
    }

    // --- 5. OwnerOf: Returns correct owner ---

    [Fact]
    public void OwnerOf_ReturnsCorrectOwner()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Mint(_bob, 1, 100));

        _host.Call(() => _token.OwnerOf(1)).Should().BeEquivalentTo(_bob);
    }

    [Fact]
    public void OwnerOf_NonexistentToken_Reverts()
    {
        var msg = _host.ExpectRevert(() => _token.OwnerOf(999));
        msg.Should().Contain("token does not exist");
    }

    // --- 6. TokenOwnerBalance: Returns count of tokens owned ---

    [Fact]
    public void TokenOwnerBalance_ReturnsCountOfTokensOwned()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Mint(_bob, 1, 100));
        _host.Call(() => _token.Mint(_bob, 2, 200));
        _host.Call(() => _token.Mint(_charlie, 1, 300));

        _host.Call(() => _token.TokenOwnerBalance(_bob)).Should().Be(2);
        _host.Call(() => _token.TokenOwnerBalance(_charlie)).Should().Be(1);
    }

    [Fact]
    public void TokenOwnerBalance_ReturnsZeroForUnknownOwner()
    {
        _host.Call(() => _token.TokenOwnerBalance(_alice)).Should().Be(0);
    }

    // --- 7. TransferValueToId: Transfers value between same-slot tokens ---

    [Fact]
    public void TransferValueToId_TransfersValueBetweenSameSlotTokens()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Mint(_alice, 5, 1000));
        _host.Call(() => _token.Mint(_bob, 5, 200));

        _host.SetCaller(_alice);
        _host.Call(() => _token.TransferValueToId(1, 2, 300));

        _host.Call(() => _token.BalanceOf(1)).Should().Be(700);
        _host.Call(() => _token.BalanceOf(2)).Should().Be(500);
    }

    // --- 8. TransferValueToId: Reverts for different slots ---

    [Fact]
    public void TransferValueToId_DifferentSlots_Reverts()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Mint(_alice, 1, 1000));
        _host.Call(() => _token.Mint(_alice, 2, 500));

        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _token.TransferValueToId(1, 2, 100));
        msg.Should().Contain("slot mismatch");
    }

    // --- 9. TransferValueToId: Reverts for insufficient value ---

    [Fact]
    public void TransferValueToId_InsufficientValue_Reverts()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Mint(_alice, 1, 100));
        _host.Call(() => _token.Mint(_alice, 1, 50));

        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _token.TransferValueToId(1, 2, 200));
        msg.Should().Contain("insufficient value");
    }

    [Fact]
    public void TransferValueToId_ZeroValue_Reverts()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Mint(_alice, 1, 100));
        _host.Call(() => _token.Mint(_alice, 1, 50));

        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _token.TransferValueToId(1, 2, 0));
        msg.Should().Contain("zero value");
    }

    [Fact]
    public void TransferValueToId_NonexistentFromToken_Reverts()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Mint(_alice, 1, 100));

        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _token.TransferValueToId(999, 1, 50));
        msg.Should().Contain("token does not exist");
    }

    // --- 10. TransferValueToId with value allowance ---

    [Fact]
    public void TransferValueToId_OperatorWithAllowance_Succeeds()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Mint(_alice, 1, 1000));
        _host.Call(() => _token.Mint(_bob, 1, 100));

        // Alice approves Bob for value transfers from token 1
        _host.SetCaller(_alice);
        _host.Call(() => _token.ApproveValue(1, _bob, 500));

        // Bob transfers value from Alice's token 1 to his token 2
        _host.SetCaller(_bob);
        _host.Call(() => _token.TransferValueToId(1, 2, 300));

        _host.Call(() => _token.BalanceOf(1)).Should().Be(700);
        _host.Call(() => _token.BalanceOf(2)).Should().Be(400);

        // Allowance should be decremented
        _host.Call(() => _token.ValueAllowance(1, _bob)).Should().Be(200);
    }

    [Fact]
    public void TransferValueToId_OperatorExceedsAllowance_Reverts()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Mint(_alice, 1, 1000));
        _host.Call(() => _token.Mint(_bob, 1, 100));

        _host.SetCaller(_alice);
        _host.Call(() => _token.ApproveValue(1, _bob, 200));

        _host.SetCaller(_bob);
        var msg = _host.ExpectRevert(() => _token.TransferValueToId(1, 2, 300));
        msg.Should().Contain("insufficient allowance");
    }

    // --- 11. TransferValueToAddress: Creates new token in same slot ---

    [Fact]
    public void TransferValueToAddress_CreatesNewTokenInSameSlot()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Mint(_alice, 7, 1000));

        _host.SetCaller(_alice);
        var newId = _host.Call(() => _token.TransferValueToAddress(1, _bob, 400));

        newId.Should().Be(2);
        _host.Call(() => _token.BalanceOf(1)).Should().Be(600);
        _host.Call(() => _token.BalanceOf(2)).Should().Be(400);
        _host.Call(() => _token.SlotOf(2)).Should().Be(7);
        _host.Call(() => _token.OwnerOf(2)).Should().BeEquivalentTo(_bob);
    }

    [Fact]
    public void TransferValueToAddress_ZeroValue_Reverts()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Mint(_alice, 1, 1000));

        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _token.TransferValueToAddress(1, _bob, 0));
        msg.Should().Contain("zero value");
    }

    [Fact]
    public void TransferValueToAddress_ToZeroAddress_Reverts()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Mint(_alice, 1, 1000));

        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _token.TransferValueToAddress(1, [], 100));
        msg.Should().Contain("transfer to zero address");
    }

    [Fact]
    public void TransferValueToAddress_InsufficientValue_Reverts()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Mint(_alice, 1, 100));

        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _token.TransferValueToAddress(1, _bob, 200));
        msg.Should().Contain("insufficient value");
    }

    // --- 12. TransferToken: Moves ownership, updates balances, clears approval ---

    [Fact]
    public void TransferToken_MovesOwnershipAndUpdatesBalances()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Mint(_alice, 1, 500));
        _host.Call(() => _token.Mint(_alice, 1, 300));

        _host.Call(() => _token.TokenOwnerBalance(_alice)).Should().Be(2);
        _host.Call(() => _token.TokenOwnerBalance(_bob)).Should().Be(0);

        _host.SetCaller(_alice);
        _host.Call(() => _token.TransferToken(_bob, 1));

        _host.Call(() => _token.OwnerOf(1)).Should().BeEquivalentTo(_bob);
        _host.Call(() => _token.TokenOwnerBalance(_alice)).Should().Be(1);
        _host.Call(() => _token.TokenOwnerBalance(_bob)).Should().Be(1);
    }

    [Fact]
    public void TransferToken_ClearsApproval()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Mint(_alice, 1, 500));

        // Approve Charlie
        _host.SetCaller(_alice);
        _host.Call(() => _token.ApproveToken(_charlie, 1));
        _host.Call(() => _token.GetApproved(1)).Should().BeEquivalentTo(_charlie);

        // Transfer to Bob
        _host.SetCaller(_alice);
        _host.Call(() => _token.TransferToken(_bob, 1));

        // Approval should be cleared
        _host.Call(() => _token.GetApproved(1)).Should().BeEmpty();
    }

    [Fact]
    public void TransferToken_ApprovedOperatorCanTransfer()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Mint(_alice, 1, 500));

        // Alice approves Bob
        _host.SetCaller(_alice);
        _host.Call(() => _token.ApproveToken(_bob, 1));

        // Bob transfers to Charlie
        _host.SetCaller(_bob);
        _host.Call(() => _token.TransferToken(_charlie, 1));

        _host.Call(() => _token.OwnerOf(1)).Should().BeEquivalentTo(_charlie);
    }

    [Fact]
    public void TransferToken_NotOwnerOrApproved_Reverts()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Mint(_alice, 1, 500));

        _host.SetCaller(_bob);
        var msg = _host.ExpectRevert(() => _token.TransferToken(_charlie, 1));
        msg.Should().Contain("not owner or approved");
    }

    [Fact]
    public void TransferToken_ToZeroAddress_Reverts()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Mint(_alice, 1, 500));

        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _token.TransferToken([], 1));
        msg.Should().Contain("transfer to zero address");
    }

    [Fact]
    public void TransferToken_NonexistentToken_Reverts()
    {
        _host.SetCaller(_alice);
        var msg = _host.ExpectRevert(() => _token.TransferToken(_bob, 999));
        msg.Should().Contain("token does not exist");
    }

    // --- 13. ApproveToken / GetApproved ---

    [Fact]
    public void ApproveToken_SetsAndReadsApproval()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Mint(_alice, 1, 100));

        _host.SetCaller(_alice);
        _host.Call(() => _token.ApproveToken(_bob, 1));

        _host.Call(() => _token.GetApproved(1)).Should().BeEquivalentTo(_bob);
    }

    [Fact]
    public void ApproveToken_NonOwner_Reverts()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Mint(_alice, 1, 100));

        _host.SetCaller(_bob);
        var msg = _host.ExpectRevert(() => _token.ApproveToken(_charlie, 1));
        msg.Should().Contain("not owner");
    }

    [Fact]
    public void GetApproved_NoApproval_ReturnsEmpty()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Mint(_alice, 1, 100));

        _host.Call(() => _token.GetApproved(1)).Should().BeEmpty();
    }

    // --- 14. ApproveValue / ValueAllowance ---

    [Fact]
    public void ApproveValue_SetsAndReadsAllowance()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Mint(_alice, 1, 1000));

        _host.SetCaller(_alice);
        _host.Call(() => _token.ApproveValue(1, _bob, 500));

        _host.Call(() => _token.ValueAllowance(1, _bob)).Should().Be(500);
    }

    [Fact]
    public void ApproveValue_NonOwner_Reverts()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Mint(_alice, 1, 1000));

        _host.SetCaller(_bob);
        var msg = _host.ExpectRevert(() => _token.ApproveValue(1, _charlie, 500));
        msg.Should().Contain("not owner");
    }

    [Fact]
    public void ApproveValue_OverwritesPreviousAllowance()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Mint(_alice, 1, 1000));

        _host.SetCaller(_alice);
        _host.Call(() => _token.ApproveValue(1, _bob, 500));
        _host.Call(() => _token.ApproveValue(1, _bob, 100));

        _host.Call(() => _token.ValueAllowance(1, _bob)).Should().Be(100);
    }

    [Fact]
    public void ValueAllowance_DefaultIsZero()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Mint(_alice, 1, 1000));

        _host.Call(() => _token.ValueAllowance(1, _bob)).Should().Be(0);
    }

    // --- 15. Events ---

    [Fact]
    public void Mint_EmitsSftMintEvent()
    {
        _host.SetCaller(_alice);
        _host.ClearEvents();
        _host.Call(() => _token.Mint(_bob, 42, 1000));

        var events = _host.GetEvents<SftMintEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].To.Should().BeEquivalentTo(_bob);
        events[0].TokenId.Should().Be(1);
        events[0].Slot.Should().Be(42);
        events[0].Value.Should().Be(1000);
    }

    [Fact]
    public void TransferValueToId_EmitsTransferValueEvent()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Mint(_alice, 1, 1000));
        _host.Call(() => _token.Mint(_alice, 1, 200));

        _host.ClearEvents();
        _host.SetCaller(_alice);
        _host.Call(() => _token.TransferValueToId(1, 2, 300));

        var events = _host.GetEvents<TransferValueEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].FromTokenId.Should().Be(1);
        events[0].ToTokenId.Should().Be(2);
        events[0].Value.Should().Be(300);
    }

    [Fact]
    public void TransferValueToAddress_EmitsTransferValueEvent()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Mint(_alice, 5, 1000));

        _host.ClearEvents();
        _host.SetCaller(_alice);
        var newId = _host.Call(() => _token.TransferValueToAddress(1, _bob, 400));

        var events = _host.GetEvents<TransferValueEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].FromTokenId.Should().Be(1);
        events[0].ToTokenId.Should().Be(newId);
        events[0].Value.Should().Be(400);
    }

    [Fact]
    public void TransferToken_EmitsSftTransferEvent()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Mint(_alice, 1, 500));

        _host.ClearEvents();
        _host.SetCaller(_alice);
        _host.Call(() => _token.TransferToken(_bob, 1));

        var events = _host.GetEvents<SftTransferEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].From.Should().BeEquivalentTo(_alice);
        events[0].To.Should().BeEquivalentTo(_bob);
        events[0].TokenId.Should().Be(1);
    }

    [Fact]
    public void ApproveToken_EmitsSftApprovalEvent()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Mint(_alice, 1, 100));

        _host.ClearEvents();
        _host.SetCaller(_alice);
        _host.Call(() => _token.ApproveToken(_bob, 1));

        var events = _host.GetEvents<SftApprovalEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].Owner.Should().BeEquivalentTo(_alice);
        events[0].Approved.Should().BeEquivalentTo(_bob);
        events[0].TokenId.Should().Be(1);
    }

    [Fact]
    public void ApproveValue_EmitsApproveValueEvent()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Mint(_alice, 1, 1000));

        _host.ClearEvents();
        _host.SetCaller(_alice);
        _host.Call(() => _token.ApproveValue(1, _bob, 500));

        var events = _host.GetEvents<ApproveValueEvent>().ToList();
        events.Should().HaveCount(1);
        events[0].TokenId.Should().Be(1);
        events[0].Operator.Should().BeEquivalentTo(_bob);
        events[0].Value.Should().Be(500);
    }

    // --- URI tests ---

    [Fact]
    public void SetSlotUri_StoresAndReturnsUri()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.SetSlotUri(1, "https://example.com/slot/1"));

        _host.Call(() => _token.SlotUri(1)).Should().Be("https://example.com/slot/1");
    }

    [Fact]
    public void SetTokenUri_StoresAndReturnsUri()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Mint(_alice, 1, 100));

        _host.SetCaller(_alice);
        _host.Call(() => _token.SetTokenUri(1, "https://example.com/token/1"));

        _host.Call(() => _token.TokenUri(1)).Should().Be("https://example.com/token/1");
    }

    [Fact]
    public void SetTokenUri_NonOwner_Reverts()
    {
        _host.SetCaller(_alice);
        _host.Call(() => _token.Mint(_alice, 1, 100));

        _host.SetCaller(_bob);
        var msg = _host.ExpectRevert(() => _token.SetTokenUri(1, "https://evil.com"));
        msg.Should().Contain("not owner");
    }

    [Fact]
    public void SlotUri_Default_ReturnsEmpty()
    {
        _host.Call(() => _token.SlotUri(999)).Should().BeEmpty();
    }

    [Fact]
    public void TokenUri_Default_ReturnsEmpty()
    {
        _host.Call(() => _token.TokenUri(999)).Should().BeEmpty();
    }

    public void Dispose() => _host.Dispose();
}
