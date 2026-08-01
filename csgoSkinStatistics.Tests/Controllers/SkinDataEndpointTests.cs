using System.Net;
using System.Text.Json;
using SteamKit2.GC.CSGO.Internal;
using Xunit;

namespace csgoSkinStatistics.Tests.Controllers;

// End-to-end coverage of GET /api, the single-item lookup. It has three ways to answer - decode a
// self-contained certificate, read the item cache, or ask the Game Coordinator - and the order
// between them is the point.
public class SkinDataEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>, IDisposable
{
    private readonly ApiFactory _factory = factory;

    public void Dispose() => _factory.ResetPerTestState();

    private const ulong Owner = 76561198200000001UL;

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static CEconItemPreviewDataBlock Item(ulong itemId) => new()
    {
        itemid = itemId,
        defindex = 7,            // AK-47 in the test catalog
        paintindex = 44,         // Fire Serpent
        paintseed = 661,
        paintwear = BitConverter.SingleToUInt32Bits(0.25f), // Field-Tested
        rarity = 6,              // Covert
        quality = 4,             // Unique
        origin = 8,              // Found in Crate
    };

    // --- the certificate path ------------------------------------------------------------

    [Fact]
    public async Task CertificateLink_DecodesWithoutTouchingTheCacheOrTheGameCoordinator()
    {
        // A modern inspect link carries the whole item, so this answer owes nothing to Steam.
        var before = _factory.Steam.Calls;
        var item = Item(9001);
        item.stickers.Add(new CEconItemPreviewDataBlock.Sticker { slot = 0, sticker_id = 1, wear = 0.1f });
        item.keychains.Add(new CEconItemPreviewDataBlock.Sticker { slot = 0, sticker_id = 2, wear = 0f });
        var url = Uri.EscapeDataString(InspectCert.Link(item));

        var response = await _factory.CreateClient().GetAsync($"/api?url={url}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJson(response);
        Assert.Equal(9001UL, json.GetProperty("itemid").GetUInt64());
        Assert.Equal(661u, json.GetProperty("paintseed").GetUInt32());
        Assert.Equal("AK-47", json.GetProperty("weapon").GetString());
        Assert.Equal("Fire Serpent", json.GetProperty("skin").GetString());
        Assert.Equal("Field-Tested", json.GetProperty("wear_name").GetString());
        Assert.Equal("Covert", json.GetProperty("rarity_name").GetString());
        Assert.Equal("AK-47 | Fire Serpent (Field-Tested)", json.GetProperty("market_hash_name").GetString());
        // The market_hash_name the catalog derived is what the price is looked up by.
        Assert.Equal(125050, json.GetProperty("price").GetProperty("suggested").GetInt32());
        // Applied decals ride along resolved, so the client renders straight from the response
        // instead of downloading the catalog.
        Assert.Equal(1u, json.GetProperty("stickers").EnumerateArray().Single().GetProperty("sticker_id").GetUInt32());
        Assert.Equal(2u, json.GetProperty("keychains").EnumerateArray().Single().GetProperty("sticker_id").GetUInt32());
        // A certificate names no owner and no market listing, so those echo back as 0 and the
        // itemid stands in for `a`.
        Assert.Equal(0UL, json.GetProperty("s").GetUInt64());
        Assert.Equal(9001UL, json.GetProperty("a").GetUInt64());
        Assert.Equal(0UL, json.GetProperty("m").GetUInt64());
        Assert.Equal(before, _factory.Steam.Calls);
    }

    [Theory]
    // Not an inspect link at all.
    [InlineData("https://example.com/not-an-inspect-link")]
    // Hex that is too short to hold even the XOR key, one protobuf byte and the checksum. Malformed
    // certificates have to be 400s, never unhandled 500s.
    [InlineData("steam://run/730//+csgo_econ_action_preview 00AABB")]
    // Odd-length hex, which Convert.FromHexString rejects outright.
    [InlineData("steam://run/730//+csgo_econ_action_preview 00AABBC")]
    // Valid hex that is not a CEconItemPreviewDataBlock.
    [InlineData("steam://run/730//+csgo_econ_action_preview FFFFFFFFFFFFFFFFFF")]
    public async Task UnparseableUrl_Is400(string url)
    {
        var response = await _factory.CreateClient().GetAsync($"/api?url={Uri.EscapeDataString(url)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Invalid inspect URL format", (await ReadJson(response)).GetProperty("error").GetString());
    }

    // --- the item cache ------------------------------------------------------------------

    [Fact]
    public async Task CachedItemid_IsAnsweredFromTheDatabaseWithoutTheGameCoordinator()
    {
        // An itemid encodes an immutable config - any mutation mints a new one - so a cached row can
        // never disagree with the live item, and the cache is authoritative.
        await _factory.Database.SaveItemWithExtrasAsync(Item(9002));
        var before = _factory.Steam.Calls;

        var json = await ReadJson(await _factory.CreateClient().GetAsync($"/api?s={Owner}&a=9002&d=77"));

        Assert.Equal(9002UL, json.GetProperty("itemid").GetUInt64());
        Assert.Equal("Fire Serpent", json.GetProperty("skin").GetString());
        // The link's own parameters travel back so the client can rebuild it.
        Assert.Equal(Owner, json.GetProperty("s").GetUInt64());
        Assert.Equal(77UL, json.GetProperty("d").GetUInt64());
        Assert.Equal(before, _factory.Steam.Calls);
    }

    // --- the Game Coordinator ------------------------------------------------------------

    [Fact]
    public async Task CacheMiss_AsksTheGameCoordinatorAndPersistsTheAnswer()
    {
        _factory.Steam.Respond = (_, a, _, _) => a == 9003 ? Item(9003) : null;
        var before = _factory.Steam.Calls;
        var client = _factory.CreateClient();

        var first = await client.GetAsync($"/api?s={Owner}&a=9003&d=77");
        // The second lookup of the same item must not pay for another GC round-trip - the first one
        // is what puts the row in the cache.
        var second = await client.GetAsync($"/api?s={Owner}&a=9003&d=77");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(9003UL, (await ReadJson(first)).GetProperty("itemid").GetUInt64());
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        Assert.Equal(before + 1, _factory.Steam.Calls);
    }

    [Fact]
    public async Task ClassicLinkForAnUnknownItem_Is404()
    {
        // The GC returns nothing for a link whose item has been traded away or deleted.
        _factory.Steam.Respond = (_, _, _, _) => null;
        var url = Uri.EscapeDataString(
            $"steam://run/730//+csgo_econ_action_preview S{Owner}A9004D77");

        var response = await _factory.CreateClient().GetAsync($"/api?url={url}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Steam GC did not return an item", (await ReadJson(response)).GetProperty("error").GetString());
    }

    // --- parameter rejection -------------------------------------------------------------

    [Fact]
    public async Task ItemidZero_IsRejectedBeforeTheGameCoordinator()
    {
        // Beyond being unidentifiable, itemid 0 would pollute SteamService's pending-request map -
        // it is keyed by itemid, so any unrelated null response would resolve key 0.
        var before = _factory.Steam.Calls;

        var response = await _factory.CreateClient().GetAsync($"/api?s={Owner}&a=0&d=77");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Invalid inspect parameters", (await ReadJson(response)).GetProperty("error").GetString());
        Assert.Equal(before, _factory.Steam.Calls);
    }

    [Fact]
    public async Task NeitherAnOwnerNorAMarketListing_IsRejected()
    {
        // Without an owner (s) or a listing (m) there is nothing to point the GC at.
        var before = _factory.Steam.Calls;

        var response = await _factory.CreateClient().GetAsync("/api?a=9005&d=77");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Invalid inspect parameters", (await ReadJson(response)).GetProperty("error").GetString());
        Assert.Equal(before, _factory.Steam.Calls);
    }

    [Theory]
    // A value the ulong parameters cannot bind: model binding fails before the action runs.
    [InlineData("/api?a=abc", "a")]
    [InlineData("/api?s=notanumber&a=1", "s")]
    // Numerically valid, but wider than ulong - the same binding failure.
    [InlineData("/api?a=99999999999999999999999999", "a")]
    public async Task UnbindableParameter_Is400InTheSameErrorShapeAsEveryOtherFailure(string path, string parameter)
    {
        // [ApiController] answers invalid model state with an RFC-9110 ProblemDetails by default,
        // which would make `?a=abc` a different body shape (and content type) from `?a=0` on the
        // same endpoint. The controller's InvalidModelStateAsError filter runs first and returns
        // the API's one error shape instead.
        var response = await _factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var json = await ReadJson(response);
        Assert.Equal($"Invalid value for parameter '{parameter}'", json.GetProperty("error").GetString());
        Assert.False(json.TryGetProperty("errors", out _));
        // The rejected value itself is never echoed back into the response body.
        Assert.DoesNotContain("abc", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task MarketListingLink_ReachesTheGameCoordinatorWithNoOwner()
    {
        // An M-form link comes off a market listing and names no owner, so it has to pass the
        // "owner or listing" check on m alone.
        _factory.Steam.Respond = (_, a, _, _) => a == 9006 ? Item(9006) : null;
        var url = Uri.EscapeDataString("steam://run/730//+csgo_econ_action_preview M12345A9006D77");

        var json = await ReadJson(await _factory.CreateClient().GetAsync($"/api?url={url}"));

        Assert.Equal(9006UL, json.GetProperty("itemid").GetUInt64());
        Assert.Equal(0UL, json.GetProperty("s").GetUInt64());
        Assert.Equal(12345UL, json.GetProperty("m").GetUInt64());
    }
}
