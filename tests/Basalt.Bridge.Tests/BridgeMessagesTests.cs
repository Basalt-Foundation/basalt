using Basalt.Bridge;
using FluentAssertions;
using Xunit;

namespace Basalt.Bridge.Tests;

public class BridgeMessagesTests
{
    private static byte[] Addr(byte seed) { var a = new byte[20]; a[19] = seed; return a; }

    // ── BridgeDirection enum ─────────────────────────────────────────────

    [Fact]
    public void BridgeDirection_Has_Both_Values()
    {
        Enum.GetValues<BridgeDirection>().Should().HaveCount(2);
        Enum.IsDefined(BridgeDirection.BasaltToEthereum).Should().BeTrue();
        Enum.IsDefined(BridgeDirection.EthereumToBasalt).Should().BeTrue();
    }

    [Fact]
    public void BridgeDirection_Values_Are_Distinct()
    {
        ((int)BridgeDirection.BasaltToEthereum).Should().NotBe((int)BridgeDirection.EthereumToBasalt);
    }

    // ── BridgeTransferStatus enum ────────────────────────────────────────

    [Fact]
    public void BridgeTransferStatus_Has_All_Expected_Values()
    {
        Enum.GetValues<BridgeTransferStatus>().Should().HaveCount(4);
        Enum.IsDefined(BridgeTransferStatus.Pending).Should().BeTrue();
        Enum.IsDefined(BridgeTransferStatus.Confirmed).Should().BeTrue();
        Enum.IsDefined(BridgeTransferStatus.Finalized).Should().BeTrue();
        Enum.IsDefined(BridgeTransferStatus.Failed).Should().BeTrue();
    }

    [Fact]
    public void BridgeTransferStatus_All_Values_Are_Distinct()
    {
        var values = Enum.GetValues<BridgeTransferStatus>().Select(v => (int)v).ToList();
        values.Distinct().Should().HaveCount(values.Count);
    }

    // ── BridgeDeposit ────────────────────────────────────────────────────

    [Fact]
    public void BridgeDeposit_Default_Values()
    {
        var deposit = new BridgeDeposit();

        deposit.Nonce.Should().Be(0);
        deposit.Sender.Should().BeEmpty();
        deposit.Recipient.Should().BeEmpty();
        deposit.Amount.Should().Be(0);
        deposit.TokenAddress.Should().BeEmpty();
        deposit.SourceChainId.Should().Be(0);
        deposit.DestinationChainId.Should().Be(0);
        deposit.BlockHeight.Should().Be(0);
        deposit.Timestamp.Should().Be(0);
        deposit.Direction.Should().Be(BridgeDirection.BasaltToEthereum); // default enum value
        deposit.Status.Should().Be(BridgeTransferStatus.Pending);
    }

    [Fact]
    public void BridgeDeposit_Init_Properties_Set_Correctly()
    {
        var sender = Addr(1);
        var recipient = Addr(2);
        var token = Addr(3);

        var deposit = new BridgeDeposit
        {
            Nonce = 42,
            Sender = sender,
            Recipient = recipient,
            Amount = 1_000_000,
            TokenAddress = token,
            SourceChainId = 1,
            DestinationChainId = 11155111,
            BlockHeight = 100,
            Timestamp = 1700000000,
            Direction = BridgeDirection.BasaltToEthereum,
            Status = BridgeTransferStatus.Confirmed,
        };

        deposit.Nonce.Should().Be(42);
        deposit.Sender.Should().BeEquivalentTo(sender);
        deposit.Recipient.Should().BeEquivalentTo(recipient);
        deposit.Amount.Should().Be(1_000_000);
        deposit.TokenAddress.Should().BeEquivalentTo(token);
        deposit.SourceChainId.Should().Be(1);
        deposit.DestinationChainId.Should().Be(11155111);
        deposit.BlockHeight.Should().Be(100);
        deposit.Timestamp.Should().Be(1700000000);
        deposit.Direction.Should().Be(BridgeDirection.BasaltToEthereum);
        deposit.Status.Should().Be(BridgeTransferStatus.Confirmed);
    }

    [Fact]
    public void BridgeDeposit_Status_Is_Mutable()
    {
        var deposit = new BridgeDeposit { Status = BridgeTransferStatus.Pending };
        deposit.Status.Should().Be(BridgeTransferStatus.Pending);

        deposit.Status = BridgeTransferStatus.Confirmed;
        deposit.Status.Should().Be(BridgeTransferStatus.Confirmed);

        deposit.Status = BridgeTransferStatus.Finalized;
        deposit.Status.Should().Be(BridgeTransferStatus.Finalized);

        deposit.Status = BridgeTransferStatus.Failed;
        deposit.Status.Should().Be(BridgeTransferStatus.Failed);
    }

    [Fact]
    public void BridgeDeposit_With_EthereumToBasalt_Direction()
    {
        var deposit = new BridgeDeposit
        {
            Direction = BridgeDirection.EthereumToBasalt,
            SourceChainId = 11155111,
            DestinationChainId = 1,
        };

        deposit.Direction.Should().Be(BridgeDirection.EthereumToBasalt);
        deposit.SourceChainId.Should().Be(11155111);
        deposit.DestinationChainId.Should().Be(1);
    }

    [Fact]
    public void BridgeDeposit_Large_Amount()
    {
        var deposit = new BridgeDeposit { Amount = ulong.MaxValue };
        deposit.Amount.Should().Be(ulong.MaxValue);
    }

    [Fact]
    public void BridgeDeposit_Large_Nonce()
    {
        var deposit = new BridgeDeposit { Nonce = ulong.MaxValue };
        deposit.Nonce.Should().Be(ulong.MaxValue);
    }

    // ── BridgeWithdrawal ─────────────────────────────────────────────────

    [Fact]
    public void BridgeWithdrawal_Default_Values()
    {
        var withdrawal = new BridgeWithdrawal();

        withdrawal.DepositNonce.Should().Be(0);
        withdrawal.Recipient.Should().BeEmpty();
        withdrawal.Amount.Should().Be(0);
        withdrawal.Proof.Should().BeEmpty();
        withdrawal.StateRoot.Should().BeEmpty();
        withdrawal.Signatures.Should().BeEmpty();
    }

    [Fact]
    public void BridgeWithdrawal_Init_Properties_Set_Correctly()
    {
        var recipient = Addr(5);
        var stateRoot = new byte[32]; stateRoot[0] = 0xAB;
        var proof = new byte[][] { new byte[32], new byte[32] };

        var withdrawal = new BridgeWithdrawal
        {
            DepositNonce = 99,
            Recipient = recipient,
            Amount = 500_000,
            Proof = proof,
            StateRoot = stateRoot,
        };

        withdrawal.DepositNonce.Should().Be(99);
        withdrawal.Recipient.Should().BeEquivalentTo(recipient);
        withdrawal.Amount.Should().Be(500_000);
        withdrawal.Proof.Should().HaveCount(2);
        withdrawal.StateRoot.Should().BeEquivalentTo(stateRoot);
    }

    [Fact]
    public void BridgeWithdrawal_Signatures_List_Is_Mutable()
    {
        var withdrawal = new BridgeWithdrawal
        {
            DepositNonce = 0,
            Recipient = Addr(1),
            Amount = 100,
            StateRoot = new byte[32],
        };

        withdrawal.Signatures.Should().BeEmpty();

        var sig = new RelayerSignature
        {
            PublicKey = new byte[32],
            Signature = new byte[64],
        };

        withdrawal.Signatures.Add(sig);
        withdrawal.Signatures.Should().HaveCount(1);

        withdrawal.Signatures.Add(sig);
        withdrawal.Signatures.Should().HaveCount(2);
    }

    [Fact]
    public void BridgeWithdrawal_Large_DepositNonce()
    {
        var withdrawal = new BridgeWithdrawal { DepositNonce = ulong.MaxValue };
        withdrawal.DepositNonce.Should().Be(ulong.MaxValue);
    }

    // ── RelayerSignature ─────────────────────────────────────────────────

    [Fact]
    public void RelayerSignature_Default_Values()
    {
        var sig = new RelayerSignature();
        sig.PublicKey.Should().BeEmpty();
        sig.Signature.Should().BeEmpty();
    }

    [Fact]
    public void RelayerSignature_Init_Properties_Set_Correctly()
    {
        var pubKey = new byte[32]; pubKey[0] = 0x01;
        var sigBytes = new byte[64]; sigBytes[0] = 0xFF;

        var sig = new RelayerSignature
        {
            PublicKey = pubKey,
            Signature = sigBytes,
        };

        sig.PublicKey.Should().BeEquivalentTo(pubKey);
        sig.Signature.Should().BeEquivalentTo(sigBytes);
        sig.PublicKey.Should().HaveCount(32);
        sig.Signature.Should().HaveCount(64);
    }

    [Fact]
    public void RelayerSignature_Arbitrary_Length_Byte_Arrays()
    {
        // The type does not enforce length constraints on its own
        var sig = new RelayerSignature
        {
            PublicKey = new byte[] { 1, 2, 3 },
            Signature = new byte[] { 4, 5 },
        };

        sig.PublicKey.Should().HaveCount(3);
        sig.Signature.Should().HaveCount(2);
    }

    // ── BridgeException ──────────────────────────────────────────────────

    [Fact]
    public void BridgeException_Has_Correct_Message()
    {
        var ex = new BridgeException("test error");
        ex.Message.Should().Be("test error");
    }

    [Fact]
    public void BridgeException_Is_Exception()
    {
        var ex = new BridgeException("something went wrong");
        ex.Should().BeAssignableTo<Exception>();
    }

    [Fact]
    public void BridgeException_Can_Be_Caught_As_Exception()
    {
        Action act = () => throw new BridgeException("bridge failure");
        act.Should().Throw<BridgeException>().WithMessage("bridge failure");
    }
}
