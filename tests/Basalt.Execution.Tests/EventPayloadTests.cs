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
    [Fact]
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

    /// <summary>
    /// The log has to be decodable in declaration order, since that order is all an ABI gives a reader.
    /// Checking that the bytes merely contain the values would pass on a payload assembled in any order
    /// at all, which nobody could parse.
    /// </summary>
    [Fact]
    public void The_fields_decode_in_declaration_order()
    {
        byte[] code = DeployToken();

        var ctx = Context(TokenAddr, HolderAddr);
        new ManagedContractRuntime().Execute(code, TransferCall(RecipientAddr, SendAmount), ctx);

        var reader = new BasaltReader(ctx.EmittedLogs[0].Data!);
        reader.ReadBytes().ToArray().Should().Equal(HolderAddr.ToArray(), "From comes first");
        reader.ReadBytes().ToArray().Should().Equal(RecipientAddr.ToArray(), "To comes second");
        reader.ReadUInt256().Should().Be(SendAmount, "Amount comes last");
    }

    /// <summary>
    /// Indexed fields become topics so a reader can filter without decoding every log in a block. Each
    /// topic is the hash of the field's encoding, so a caller filtering on an address hashes it the same
    /// way and matches.
    /// </summary>
    [Fact]
    public void Indexed_fields_become_topics_a_reader_can_match_on()
    {
        byte[] code = DeployToken();

        var ctx = Context(TokenAddr, HolderAddr);
        new ManagedContractRuntime().Execute(code, TransferCall(RecipientAddr, SendAmount), ctx);

        Hash256[] topics = ctx.EmittedLogs[0].Topics;
        topics.Should().HaveCount(2, "From and To are the indexed fields of a transfer");

        var buffer = new byte[64];
        var writer = new BasaltWriter(buffer);
        writer.WriteBytes(RecipientAddr.ToArray());
        Hash256 expected = Basalt.Crypto.Blake3Hasher.Hash(buffer[..writer.Position]);

        topics[1].Should().Be(expected, "a reader hashes the address the same way to find its transfers");
    }

    /// <summary>
    /// Two transfers that differ only in amount must produce different data and the same topics, which
    /// is what makes topics an index rather than a copy of the record.
    /// </summary>
    [Fact]
    public void The_same_parties_share_topics_while_the_data_differs()
    {
        byte[] code = DeployToken();

        var first = Context(TokenAddr, HolderAddr);
        new ManagedContractRuntime().Execute(code, TransferCall(RecipientAddr, SendAmount), first);

        var second = Context(TokenAddr, HolderAddr);
        new ManagedContractRuntime().Execute(code, TransferCall(RecipientAddr, new UInt256(1)), second);

        second.EmittedLogs[0].Topics.Should().Equal(first.EmittedLogs[0].Topics);
        second.EmittedLogs[0].Data.Should().NotEqual(first.EmittedLogs[0].Data);
    }
}
