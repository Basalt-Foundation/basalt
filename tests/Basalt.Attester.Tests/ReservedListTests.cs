using FluentAssertions;
using Xunit;

namespace Basalt.Attester.Tests;

/// <summary>
/// Reading the watch list.
///
/// A dropped line here is the quietest failure in the whole system: nothing errors, and one domain owner
/// publishes a record that no attester ever reads. So the parser reports what it drops, and these tests
/// pin down exactly what it drops.
/// </summary>
public class ReservedListTests
{
    [Fact]
    public void Bare_labels_and_com_domains_are_the_same_thing()
    {
        ReservedList.Parse(["google", "amazon.com", "Facebook.COM"])
            .Should().Equal("google", "amazon", "facebook");
    }

    [Fact]
    public void Blank_lines_and_comments_are_ignored_without_complaint()
    {
        var skipped = new List<string>();
        ReservedList.Parse(["# top 500", "", "   ", "google"], skipped.Add)
            .Should().Equal("google");
        skipped.Should().BeEmpty();
    }

    [Fact]
    public void A_duplicate_is_watched_once()
    {
        ReservedList.Parse(["google", "google.com", "GOOGLE"])
            .Should().Equal("google");
    }

    // Anything below the second level was never reserved, and trimming it down to something that was
    // would make the attester watch a name the operator did not put on the list.
    [Theory]
    [InlineData("mail.google.com")]
    [InlineData("google.co.uk")]
    [InlineData("google.net")]
    [InlineData(".com")]
    public void Anything_that_is_not_a_second_level_com_is_dropped_out_loud(string line)
    {
        var skipped = new List<string>();
        ReservedList.Parse([line], skipped.Add).Should().BeEmpty();
        skipped.Should().ContainSingle().Which.Should().Contain(line);
    }

    [Fact]
    public void Order_is_preserved_so_a_sweep_is_reproducible()
    {
        ReservedList.Parse(["zulu", "alpha", "mike"])
            .Should().Equal("zulu", "alpha", "mike");
    }
}
