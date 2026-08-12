using DevStudio.Application.Common;

namespace DevStudio.Tests;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("1.4.2", "1.4.2")]
    [InlineData("v1.4.2", "1.4.2")]
    [InlineData("V1.4.2", "1.4.2")]
    [InlineData("1.4.2+abc1234", "1.4.2")]
    [InlineData("1.4.2.9", "1.4.2")]
    [InlineData("1.5", "1.5.0")]
    [InlineData("2", "2.0.0")]
    public void A_published_version_parses(string tag, string expected) =>
        Assert.Equal(expected, ReleaseVersion.Parse(tag)?.ToString());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("main")]
    [InlineData("sha-abc1234")]
    // The SDK stamps 1.0.0 on anything nobody versioned, and 0.0.0 is what the container build uses
    // for a non-tag build. Treating either as real tells every development build to update.
    [InlineData("1.0.0")]
    [InlineData("0.0.0")]
    public void An_unversioned_build_is_not_a_version(string? value) =>
        Assert.Null(ReleaseVersion.Parse(value));

    [Theory]
    [InlineData("1.4.2", "1.4.3")]
    [InlineData("1.4.2", "1.5.0")]
    [InlineData("1.4.2", "2.0.0")]
    [InlineData("1.4.2+localbuild", "v1.4.3")]
    public void A_newer_release_is_newer(string current, string latest) =>
        Assert.True(ReleaseVersion.IsNewer(current, latest));

    [Theory]
    [InlineData("1.4.2", "1.4.2")]
    [InlineData("1.4.2", "1.4.1")]
    [InlineData("2.0.0", "1.9.9")]
    public void The_same_or_older_release_is_not(string current, string latest) =>
        Assert.False(ReleaseVersion.IsNewer(current, latest));

    [Theory]
    [InlineData("1.0.0", "1.4.2")]
    [InlineData(null, "1.4.2")]
    [InlineData("1.4.2", "main")]
    [InlineData("1.4.2", null)]
    public void An_unknown_version_on_either_side_reports_nothing(string? current, string? latest) =>
        Assert.False(ReleaseVersion.IsNewer(current, latest));

    [Theory]
    [InlineData("1.5.0-rc.1", true)]
    [InlineData("v2.0.0-beta", true)]
    [InlineData("1.5.0", false)]
    public void A_pre_release_tag_is_recognised(string tag, bool expected) =>
        Assert.Equal(expected, ReleaseVersion.IsPreRelease(tag));

    [Fact]
    public void A_pre_release_never_looks_newer_than_the_version_it_precedes()
    {
        // 1.5.0-rc.1 parses as 1.5.0, so the guard against announcing it lives in IsPreRelease —
        // which is what the checker filters on before comparing at all.
        Assert.True(ReleaseVersion.IsPreRelease("1.5.0-rc.1"));
        Assert.Equal("1.5.0", ReleaseVersion.Parse("1.5.0-rc.1")?.ToString());
    }
}
