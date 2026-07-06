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

    [Fact]
    public void ParseInspectUrl_OverlongHexPayload_ReturnsNull()
    {
        // A multi-megabyte hex payload must be rejected before it is hex-decoded and protobuf-parsed.
        var hugeHex = new string('A', 5000);
        var url = "steam://rungame/730/0/+csgo_econ_action_preview " + hugeHex;
        Assert.Null(SkinController.ParseInspectUrl(url));
    }

    [Fact]
    public void ParseInspectUrl_OddLengthHexPayload_ReturnsNull()
    {
        // The hex regex matches odd-length runs, which Convert.FromHexString would reject with a
        // FormatException. It must come back null (-> 400), not throw (-> 500).
        var url = "steam://rungame/730/0/+csgo_econ_action_preview ABC";
        Assert.Null(SkinController.ParseInspectUrl(url));
    }

    [Fact]
    public void ParseInspectUrl_GarbageHexPayload_ReturnsNull()
    {
        // Valid, even-length hex that decodes to a malformed protobuf (here: xor key 0x00, then a
        // length-delimited field header claiming more bytes than remain) must not throw.
        var url = "steam://rungame/730/0/+csgo_econ_action_preview 000A0500000000";
        Assert.Null(SkinController.ParseInspectUrl(url));
    }

    [Fact]
    public void ParseInspectUrl_OverlongNumericFields_ReturnsNull()
    {
        // A numeric field longer than ulong can hold would overflow ulong.Parse; TryParse maps it
        // to null (-> 400) instead of an unhandled OverflowException (-> 500).
        var url = "steam://rungame/730/0/+csgo_econ_action_preview S76561198123456789A123456789012345678901D67890";
        Assert.Null(SkinController.ParseInspectUrl(url));
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
