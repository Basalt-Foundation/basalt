using Basalt.Codec;
using Basalt.Core;
using Basalt.Execution.VM;
using Basalt.Sdk.Contracts;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests;

/// <summary>
/// What an event log actually carries once a validator has executed the contract.
///
/// Contracts declare events with fields and emit them with values, and the SDK test host records the
/// object so assertions on those fields pass. What reaches a receipt is produced separately by the
/// bridge, and these check that side, since it is the one an explorer, an indexer, or anyone auditing a
/// name claim ever reads.
/// </summary>
public class EventPayloadTests
{
    private readonly InMemoryStateDb _stateDb = new();

    private static readonly Address TokenAddr = new(Enumerable.Repeat((byte)0x44, 20).ToArray());
    private static readonly Address HolderAddr = new(Enumerable.Repeat((byte)0x55, 20).ToArray());
    private static readonly Address RecipientAddr = new(Enumerable.Repeat((byte)0x66, 20).ToArray());

    private static readonly UInt256 Supply = new(1_000_000);
    private static readonly UInt256 SendAmount = new(4_242);

    private VmExecutionContext Context(Address contract, Address caller) => new()
    {
        Caller = caller,
        ContractAddress = contract,
        Value = UInt256.Zero,
        BlockTimestamp = 1_700_000_000_000,
        BlockNumber = 42,
        BlockProposer = new Address(new byte[20]),
        ChainId = 4242,
        GasMeter = new GasMeter(50_000_000),
        StateDb = _stateDb,
        CallDepth = 0,
    };

    private static Hash256 CodeKey()
    {
        var key = new byte[32];
        key[0] = 0xFF;
        key[1] = 0x01;
        return new Hash256(key);
    }

    private byte[] DeployToken()
    {
        var buffer = new byte[256];
        var writer = new BasaltWriter(buffer);
        writer.WriteString("Token");
        writer.WriteString("TKN");
        writer.WriteByte(18);
        writer.WriteUInt256(Supply);

        var manifest = ContractRegistry.BuildManifest(0x0001, buffer[..writer.Position]);
        new ManagedContractRuntime().Deploy(manifest, [], Context(TokenAddr, HolderAddr));
        return _stateDb.GetStorage(TokenAddr, CodeKey())!;
    }

    private static byte[] TransferCall(Address to, UInt256 amount)
    {
        var buffer = new byte[256];
        var writer = new BasaltWriter(buffer);
        writer.WriteBytes(to.ToArray());
        writer.WriteUInt256(amount);

        var selector = SelectorHelper.ComputeSelectorBytes("Transfer");
        var callData = new byte[selector.Length + writer.Position];
        selector.CopyTo(callData, 0);
        buffer.AsSpan(0, writer.Position).CopyTo(callData.AsSpan(selector.Length));
        return callData;
    }

    /// <summary>
    /// A transfer log has to say who received what. Without the amount and the recipient a log records
    /// only that some transfer happened, which no explorer, indexer, or audit can work from.
    /// </summary>
    [Fact(Skip = "Known gap: the bridge discards the event object and logs its type name. " +
                "Fixing it changes the receipts root, so it is a consensus change awaiting a decision.")]
    public void A_transfer_log_carries_the_recipient_and_the_amount()
    {
        byte[] code = DeployToken();

        var ctx = Context(TokenAddr, HolderAddr);
        ContractCallResult result = new ManagedContractRuntime()
            .Execute(code, TransferCall(RecipientAddr, SendAmount), ctx);
        result.Success.Should().BeTrue(result.ErrorMessage);

        ctx.EmittedLogs.Should().ContainSingle();
        EventLog log = ctx.EmittedLogs[0];

        var payload = log.Data ?? [];
        var recipient = RecipientAddr.ToArray();

        var amountBuffer = new byte[32];
        var amountWriter = new BasaltWriter(amountBuffer);
        amountWriter.WriteUInt256(SendAmount);
        var amount = amountBuffer[..amountWriter.Position];

        payload.Should().NotBeEmpty();
        Contains(payload, recipient).Should().BeTrue("the log has to name who received the tokens");
        Contains(payload, amount).Should().BeTrue("the log has to state how much moved");
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
            return false;

        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return true;
        }

        return false;
    }
}
