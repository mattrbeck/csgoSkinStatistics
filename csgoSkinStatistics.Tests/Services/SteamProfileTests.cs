using CSGOSkinAPI.Services;
using Xunit;

namespace csgoSkinStatistics.Tests.Services;

// Exercises the real SteamProfile identity parsers - ParseSteamInput and IsValidVanity - exposed
// via InternalsVisibleTo rather than a reimplementation, so a regression in the parser fails the
// build. ParseSteamInput is what turns whatever a user pasted into either an id64 or a vanity name,
// and a vanity name is then spliced into a server-side steamcommunity.com URL, so the rejection
// cases below are load-bearing rather than tidiness.
public class SteamProfileTests
{
    [Theory]
    [InlineData("76561198123456789")]
    [InlineData("https://steamcommunity.com/profiles/76561198123456789")]
    [InlineData("steamcommunity.com/profiles/76561198123456789")]
    public void ParseSteamInput_KnownId_ReturnsId(string input)
    {
        var (steamId64, vanity) = SteamProfile.ParseSteamInput(input);
        Assert.Equal(76561198123456789UL, steamId64);
        Assert.Null(vanity);
    }

    [Theory]
    [InlineData("https://steamcommunity.com/id/mattrb", "mattrb")]
    [InlineData("steamcommunity.com/id/my-cool_name", "my-cool_name")]
    [InlineData("mattrb", "mattrb")]
    public void ParseSteamInput_ValidVanity_ReturnsVanity(string input, string expected)
    {
        var (steamId64, vanity) = SteamProfile.ParseSteamInput(input);
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
        var (steamId64, vanity) = SteamProfile.ParseSteamInput(input);
        Assert.Null(steamId64);
        Assert.Null(vanity);
    }

    // --- steam id parsing boundaries ---------------------------------------------------

    [Theory]
    [InlineData("7656119812345678")]      // 16 digits - too short for an id64
    [InlineData("765611981234567890")]    // 18 digits - too long
    [InlineData("86561198123456789")]     // right length, outside the individual-account block
    public void ParseSteamInput_OutOfRangeNumericId_IsNotTreatedAsAnId(string input)
    {
        var (steamId64, vanity) = SteamProfile.ParseSteamInput(input);

        Assert.Null(steamId64);
        // An all-digit input is never a vanity name either, so it resolves to nothing.
        Assert.Null(vanity);
    }

    // --- vanity charset ----------------------------------------------------------------

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
        Assert.Equal(expected, SteamProfile.IsValidVanity(vanity));
    }
}
