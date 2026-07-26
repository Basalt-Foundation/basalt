using FluentAssertions;
using Xunit;

namespace Basalt.Attester.Tests;

/// <summary>
/// What counts as a claim.
///
/// Everything the attester puts on chain comes from a string a stranger controls, so the rule has to be
/// exact: a claim is a well formed 20-byte address or it is nothing at all. There is no repair step,
/// because a repaired record is an address the domain owner never wrote.
/// </summary>
public class DnsClaimParsingTests
{
    private const string Valid = "bslt-claim=0x0102030405060708090a0b0c0d0e0f1011121314";

    [Fact]
    public void A_well_formed_record_yields_its_address()
    {
        DnsClaimReader.TryParseClaim(Valid, out byte[] address).Should().BeTrue();
        Convert.ToHexStringLower(address).Should().Be("0102030405060708090a0b0c0d0e0f1011121314");
    }

    [Fact]
    public void The_hex_prefix_is_optional()
    {
        DnsClaimReader.TryParseClaim("bslt-claim=0102030405060708090a0b0c0d0e0f1011121314", out byte[] a)
            .Should().BeTrue();
        a.Should().HaveCount(20);
    }

    // DNS providers are relaxed about case and about the whitespace an operator pastes in, and being
    // strict about either would reject records that plainly say what they mean.
    [Theory]
    [InlineData("BSLT-CLAIM=0x0102030405060708090A0B0C0D0E0F1011121314")]
    [InlineData("  bslt-claim=0x0102030405060708090a0b0c0d0e0f1011121314  ")]
    [InlineData("bslt-claim= 0x0102030405060708090a0b0c0d0e0f1011121314")]
    public void Case_and_surrounding_whitespace_do_not_change_the_answer(string record)
    {
        DnsClaimReader.TryParseClaim(record, out byte[] a).Should().BeTrue();
        Convert.ToHexStringLower(a).Should().Be("0102030405060708090a0b0c0d0e0f1011121314");
    }

    [Theory]
    [InlineData("")]
    [InlineData("v=spf1 include:_spf.google.com ~all")]
    [InlineData("google-site-verification=abc123")]
    [InlineData("bslt-claim=")]
    [InlineData("bslt-claim=0x")]
    [InlineData("bslt-claim=0x0102030405060708090a0b0c0d0e0f10111213")]     // 19 bytes
    [InlineData("bslt-claim=0x0102030405060708090a0b0c0d0e0f101112131415")] // 21 bytes
    [InlineData("bslt-claim=0xzzzz030405060708090a0b0c0d0e0f1011121314")]
    [InlineData("bslt-claim=not-an-address")]
    [InlineData("xbslt-claim=0x0102030405060708090a0b0c0d0e0f1011121314")]
    public void Anything_else_is_not_a_claim(string record)
    {
        DnsClaimReader.TryParseClaim(record, out byte[] a).Should().BeFalse();
        a.Should().BeEmpty();
    }

    // The one well formed value that is still not a claim. A name assigned to the zero address is gone
    // for good, since assignment deletes the reservation and nobody holds the key, so an unedited
    // template must cost a domain owner a delay rather than the name itself.
    [Fact]
    public void The_zero_address_is_refused_even_though_it_parses()
    {
        DnsClaimReader.TryParseClaim("bslt-claim=0x" + new string('0', 40), out byte[] a)
            .Should().BeFalse();
        a.Should().BeEmpty();
    }
}
