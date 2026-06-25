using CSGOSkinAPI.Controllers;

namespace csgoSkinStatistics.Tests.Controllers;

// Exercises the real SkinController.ParseSteamInput (exposed via InternalsVisibleTo) rather than a
// reimplementation, so a regression in the parser fails the build.
public class ParseSteamInputTests
{
    [Theory]
    [InlineData("76561198123456789")]
    [InlineData("https://steamcommunity.com/profiles/76561198123456789")]
    [InlineData("steamcommunity.com/profiles/76561198123456789")]
    public void ParseSteamInput_KnownId_ReturnsId(string input)
    {
        var (steamId64, vanity) = SkinController.ParseSteamInput(input);
        Assert.Equal(76561198123456789UL, steamId64);
        Assert.Null(vanity);
    }

    [Theory]
    [InlineData("https://steamcommunity.com/id/mattrb", "mattrb")]
    [InlineData("steamcommunity.com/id/my-cool_name", "my-cool_name")]
    [InlineData("mattrb", "mattrb")]
    public void ParseSteamInput_ValidVanity_ReturnsVanity(string input, string expected)
    {
        var (steamId64, vanity) = SkinController.ParseSteamInput(input);
        Assert.Null(steamId64);
        Assert.Equal(expected, vanity);
    }

    [Theory]
    // Anything that could break out of the /id/<vanity> path segment must be rejected so it never
    // reaches the server-side steamcommunity.com fetch. The /id/ regex stops at the first '/', but
    // not at '#'/'@', so the charset check is what rejects those.
    [InlineData("steamcommunity.com/id/foo@evil.com")]
    [InlineData("steamcommunity.com/id/foo#frag")]
    [InlineData("foo/bar")]
    [InlineData("foo?x=1")]
    [InlineData("foo#frag")]
    [InlineData("foo bar")]
    [InlineData("a")]                       // too short
    [InlineData("evil.com")]                // contains a dot
    [InlineData("name%2f..%2f")]            // url-encoded slashes
    public void ParseSteamInput_MalformedVanity_ReturnsNeither(string input)
    {
        var (steamId64, vanity) = SkinController.ParseSteamInput(input);
        Assert.Null(steamId64);
        Assert.Null(vanity);
    }

    [Theory]
    [InlineData("mattrb", true)]
    [InlineData("a-b_c123", true)]
    [InlineData("a", false)]
    [InlineData("has space", false)]
    [InlineData("has/slash", false)]
    [InlineData("has.dot", false)]
    [InlineData("waaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaay-too-long", false)]
    public void IsValidVanity_ValidatesCharset(string vanity, bool expected)
    {
        Assert.Equal(expected, SkinController.IsValidVanity(vanity));
    }
}
