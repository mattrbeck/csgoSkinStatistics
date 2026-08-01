using CSGOSkinAPI.Models;
using CSGOSkinAPI.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace csgoSkinStatistics.Tests.Services;

// Covers InspectLink end to end: ParseInspectUrl, which decodes an inspect link into its S/A/D/M
// parts (or, for a %20-encoded hex cert, straight into item data), and BuildInspectLink, which
// fills an inventory link template in before that parse ever happens. Both run against the real
// type via InternalsVisibleTo rather than a reimplementation, so a regression fails the build.
//
// ParseInspectUrl takes attacker-controlled text off a query string, so the malformed-input cases
// below are the point of the file, not an afterthought: every one of them must return null (-> 400)
// rather than throw (-> 500) or allocate its way through a multi-megabyte payload.
public class InspectLinkTests
{
    // --- inspect URL parsing -----------------------------------------------------------

    [Fact]
    public void ParseInspectUrl_ClassicOwnerLink_ReturnsOwnerAssetAndD()
    {
        var url = "steam://rungame/730/76561202255233023/+csgo_econ_action_preview%20S76561198123456789A12345D67890";

        var parsed = InspectLink.ParseInspectUrl(url);

        Assert.NotNull(parsed);
        var (s, a, d, m, directItem) = parsed.Value;
        Assert.Equal(76561198123456789UL, s);
        Assert.Equal(12345UL, a);
        Assert.Equal(67890UL, d);
        Assert.Equal(0UL, m);
        Assert.Null(directItem); // an S-form link carries no item data; it needs the GC or the cache
    }

    [Fact]
    public void ParseInspectUrl_MarketLink_PopulatesMNotS()
    {
        var url = "steam://rungame/730/76561202255233023/+csgo_econ_action_preview%20M1A12345D67890";

        var parsed = InspectLink.ParseInspectUrl(url);

        Assert.NotNull(parsed);
        var (s, a, d, m, _) = parsed.Value;
        Assert.Equal(0UL, s);
        Assert.Equal(1UL, m);
        Assert.Equal(12345UL, a);
        Assert.Equal(67890UL, d);
    }

    [Fact]
    public void ParseInspectUrl_ShortPrefixForm_StillParses()
    {
        // The prefix changed from "rungame/730/<steamid>/" to "run/730//" in March 2026; the parser
        // matches on the command, not the prefix, so both forms have to work.
        var parsed = InspectLink.ParseInspectUrl(
            "steam://run/730//+csgo_econ_action_preview%20S76561198123456789A12345D67890");

        Assert.NotNull(parsed);
        Assert.Equal(12345UL, parsed.Value.a);
    }

    [Fact]
    public void ParseInspectUrl_NotAnInspectLink_ReturnsNull()
    {
        Assert.Null(InspectLink.ParseInspectUrl("https://example.com/not-a-link"));
    }

    // --- malformed inspect URLs --------------------------------------------------------

    [Fact]
    public void ParseInspectUrl_OverlongHexPayload_ReturnsNull()
    {
        // A multi-megabyte hex payload must be rejected before it is hex-decoded and protobuf-parsed.
        var hugeHex = new string('A', 5000);
        var url = "steam://rungame/730/0/+csgo_econ_action_preview " + hugeHex;
        var log = new CapturingLogger();

        Assert.Null(InspectLink.ParseInspectUrl(url, log));

        // Asserting the *reason*, not just the null. This payload is also rejected further down,
        // by the protobuf deserialize catch - so `Assert.Null` alone passes with the length cap
        // raised to any value, and defends nothing. The cap is what stops the allocation and the
        // parse from happening at all; only the line it logs distinguishes it from the late catch.
        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("Hex payload too long: {InspectUrl}", entry["{OriginalFormat}"]);
    }

    [Fact]
    public void ParseInspectUrl_OddLengthHexPayload_ReturnsNull()
    {
        // The hex regex matches odd-length runs, which Convert.FromHexString would reject with a
        // FormatException. It must come back null (-> 400), not throw (-> 500).
        var url = "steam://rungame/730/0/+csgo_econ_action_preview ABC";
        Assert.Null(InspectLink.ParseInspectUrl(url));
    }

    [Fact]
    public void ParseInspectUrl_GarbageHexPayload_ReturnsNull()
    {
        // Valid, even-length hex that decodes to a malformed protobuf (here: xor key 0x00, then a
        // length-delimited field header claiming more bytes than remain) must not throw.
        var url = "steam://rungame/730/0/+csgo_econ_action_preview 000A0500000000";
        Assert.Null(InspectLink.ParseInspectUrl(url));
    }

    [Fact]
    public void ParseInspectUrl_OverlongNumericFields_ReturnsNull()
    {
        // A numeric field longer than ulong can hold would overflow ulong.Parse; TryParse maps it
        // to null (-> 400) instead of an unhandled OverflowException (-> 500).
        var url = "steam://rungame/730/0/+csgo_econ_action_preview S76561198123456789A123456789012345678901D67890";
        Assert.Null(InspectLink.ParseInspectUrl(url));
    }

    // --- inventory inspect-link templating ---------------------------------------------

    [Fact]
    public void BuildInspectLink_SubstitutesOwnerAndAsset()
    {
        var link = InspectLink.BuildInspectLink(
            "steam://rungame/730/%owner_steamid%/+csgo_econ_action_preview S%owner_steamid%A%assetid%D123",
            assetProps: null,
            ownerSteamId: "76561198123456789",
            assetId: "519");

        Assert.Equal(
            "steam://rungame/730/76561198123456789/+csgo_econ_action_preview S76561198123456789A519D123",
            link);
    }

    [Fact]
    public void BuildInspectLink_SubstitutesTheItemCertificateProperty()
    {
        // propid 6 is the self-contained cert; once substituted the link decodes with no GC call.
        var props = new List<SteamAssetProperty>
        {
            new() { propertyid = 6, string_value = "00DEADBEEF" },
        };

        var link = InspectLink.BuildInspectLink(
            "steam://run/730//+csgo_econ_action_preview %propid:6%", props, "76561198123456789", "519");

        Assert.Equal("steam://run/730//+csgo_econ_action_preview 00DEADBEEF", link);
    }

    [Fact]
    public void BuildInspectLink_UnresolvedPlaceholderIsLeftIntact()
    {
        // An asset with no matching property must leave the placeholder alone rather than splice in
        // an empty string, so the link visibly fails to parse instead of silently pointing at junk.
        var link = InspectLink.BuildInspectLink(
            "steam://run/730//+csgo_econ_action_preview %propid:6%", [], "76561198123456789", "519");

        Assert.Equal("steam://run/730//+csgo_econ_action_preview %propid:6%", link);
    }
}
