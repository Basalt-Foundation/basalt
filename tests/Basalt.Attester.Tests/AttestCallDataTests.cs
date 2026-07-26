using Basalt.Codec;
using Basalt.Sdk.Contracts;
using FluentAssertions;
using Xunit;

namespace Basalt.Attester.Tests;

/// <summary>
/// The bytes the attester actually sends.
///
/// This is the seam where a mistake is silent rather than loud. A wrong method name or two arguments in
/// the wrong order compiles, signs, and gets submitted, and the only symptom is that no attestation ever
/// counts. Reusing the SDK encoder rules out drift in the layout, so what is left to pin down is the
/// call itself.
/// </summary>
public class AttestCallDataTests
{
    private static readonly byte[] Owner =
        Convert.FromHexString("0102030405060708090a0b0c0d0e0f1011121314");

    private const string Label = "google";
    private const string Evidence = "dns:_bslt-claim.google.com TXT bslt-claim=0x01 @198.51.100.1";

    [Fact]
    public void The_selector_is_the_one_the_dispatcher_computes_for_AttestClaim()
    {
        byte[] data = AttesterChainClient.BuildCallData(Label, Owner, Evidence);

        data.AsSpan(0, 4).ToArray()
            .Should().Equal(SelectorHelper.ComputeSelectorBytes("AttestClaim"));
    }

    // The signature is AttestClaim(string name, byte[] owner, string evidence). Swapping the two strings
    // still encodes, still signs, and still submits, and the name would be attested for the wrong label.
    [Fact]
    public void The_arguments_decode_back_in_the_order_the_contract_declares_them()
    {
        byte[] data = AttesterChainClient.BuildCallData(Label, Owner, Evidence);

        var reader = new BasaltReader(data.AsSpan(4));
        reader.ReadString().Should().Be(Label);
        reader.ReadBytes().ToArray().Should().Equal(Owner);
        reader.ReadString().Should().Be(Evidence);
    }

    [Fact]
    public void Nothing_trails_the_last_argument()
    {
        byte[] data = AttesterChainClient.BuildCallData(Label, Owner, Evidence);

        var reader = new BasaltReader(data.AsSpan(4));
        reader.ReadString();
        reader.ReadBytes();
        reader.ReadString();

        // A trailing byte is padding to us and a decode failure to the dispatcher.
        (4 + reader.Position).Should().Be(data.Length);
    }

    // Evidence is capped at 256 characters on chain, so a long authoritative server name or a long
    // domain must not quietly push a real claim over the limit.
    [Fact]
    public void A_realistic_evidence_string_fits_the_contract_limit()
    {
        var longest = $"dns:_bslt-claim.{new string('a', 63)}.com TXT bslt-claim=0x"
                      + new string('0', 39) + "1 @255.255.255.255";

        longest.Length.Should().BeLessThanOrEqualTo(256);
    }
}
