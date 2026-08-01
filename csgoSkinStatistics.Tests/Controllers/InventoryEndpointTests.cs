using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CSGOSkinAPI.Models;
using SteamKit2.GC.CSGO.Internal;
using Xunit;

namespace csgoSkinStatistics.Tests.Controllers;

// End-to-end coverage of GET /api/inventory through the real pipeline: model binding, the resolve
// step, the response cache, the single-flight gate and the three-array stitching. Steam is stubbed
// at the HttpMessageHandler, so every case here is exact about what the endpoint asks Steam for and
// how often.
public class InventoryEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>, IDisposable
{
    private readonly ApiFactory _factory = factory;

    public void Dispose() => _factory.ResetPerTestState();

    // The response cache is keyed by resolved SteamId64 and outlives an individual test (one host
    // serves the whole class), so every test claims an id of its own.
    private static int _nextId;
    private static ulong NextSteamId() => 76561198000000000UL + (ulong)Interlocked.Increment(ref _nextId);

    private static string InventoryUrl(ulong steamId) => $"steamcommunity.com/inventory/{steamId}/730/2";

    // Steam templates the inspect link on the *description*, which every copy of a skin shares, so
    // the per-copy identity arrives as placeholders. These are the two shapes in the wild: a
    // self-contained certificate property, and the classic owner/asset form.
    private const string CertTemplate = "steam://run/730//+csgo_econ_action_preview %propid:6%";
    private const string OwnerTemplate =
        "steam://rungame/730/%owner_steamid%/+csgo_econ_action_preview S%owner_steamid%A%assetid%D999";

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static string Serialize(SteamInventoryResponse inventory) => JsonSerializer.Serialize(inventory);

    // --- the happy path ------------------------------------------------------------------

    // The item the certificate in the fixture inventory decodes to.
    private static CEconItemPreviewDataBlock CertItem() => new()
    {
        itemid = 111,
        defindex = 7,            // AK-47 in the test catalog
        paintindex = 44,         // Fire Serpent
        paintseed = 661,
        paintwear = BitConverter.SingleToUInt32Bits(0.25f), // Field-Tested
        rarity = 6,              // Covert
        quality = 4,             // Unique
        origin = 8,              // Found in Crate
    };

    // An inventory covering every branch of the stitching loop at once: an asset whose link decodes
    // locally from its certificate, an asset whose link needs the item cache, a second asset sharing
    // the first one's classid under a different instanceid, an asset whose description carries no
    // inspect action, and an asset with no description at all.
    private static SteamInventoryResponse FixtureInventory(int totalInventoryCount) => new()
    {
        total = totalInventoryCount,
        success = 1,
        assets =
        [
            new() { appid = 730, contextid = "2", assetid = "1001", classid = "C1", instanceid = "I1", amount = "1" },
            new() { appid = 730, contextid = "2", assetid = "222", classid = "C2", instanceid = "I2", amount = "1" },
            new() { appid = 730, contextid = "2", assetid = "1003", classid = "C3", instanceid = "I3", amount = "1" },
            new() { appid = 730, contextid = "2", assetid = "1004", classid = "CX", instanceid = "IX", amount = "1" },
            new() { appid = 730, contextid = "2", assetid = "1005", classid = "C1", instanceid = "I2", amount = "1" },
        ],
        descriptions =
        [
            new()
            {
                classid = "C1",
                instanceid = "I1",
                name = "StatTrak™ AK-47 | Fire Serpent",
                market_name = "StatTrak™ AK-47 | Fire Serpent (Field-Tested)",
                market_hash_name = "StatTrak™ AK-47 | Fire Serpent (Field-Tested)",
                type = "Covert Rifle",
                name_color = "EB4B4B",
                icon_url = "icon-small",
                icon_url_large = "icon-large",
                actions = [new() { name = "Inspect in Game...", link = CertTemplate }],
                descriptions = [new() { name = "stattrak_score", value = "StatTrak™ Confirmed Kills: 1,234" }],
                tags =
                [
                    new() { category = "Exterior", localized_tag_name = "Field-Tested" },
                    new() { category = "Rarity", localized_tag_name = "Covert" },
                    new() { category = "Quality", localized_tag_name = "StatTrak™" },
                    new() { category = "Type", localized_tag_name = "Rifle" },
                ],
            },
            new()
            {
                classid = "C2",
                instanceid = "I2",
                name = "AWP | Asiimov",
                market_hash_name = "AWP | Asiimov (Field-Tested)",
                actions = [new() { name = "Inspect in Game...", link = OwnerTemplate }],
            },
            // No actions at all (a case, a graffiti, ...) - never inspectable, so it is dropped.
            new() { classid = "C3", instanceid = "I3", name = "Chroma 3 Case" },
            // Same classid as the first description, different instanceid. Steam does this routinely
            // - a name tag, applied stickers or differing description text all mint a new instanceid
            // under the same class - so the description index has to be keyed on the pair.
            new()
            {
                classid = "C1",
                instanceid = "I2",
                name = "AK-47 | Fire Serpent",
                market_name = "AK-47 | Fire Serpent (Field-Tested)",
                market_hash_name = "AK-47 | Fire Serpent (Field-Tested)",
                type = "Covert Rifle",
                actions = [new() { name = "Inspect in Game...", link = OwnerTemplate }],
                tags =
                [
                    new() { category = "Exterior", localized_tag_name = "Field-Tested" },
                    new() { category = "Rarity", localized_tag_name = "Covert" },
                    new() { category = "Quality", localized_tag_name = "Unique" },
                    new() { category = "Type", localized_tag_name = "Rifle" },
                ],
            },
        ],
        asset_properties =
        [
            new()
            {
                assetid = "1001",
                asset_properties = [new() { propertyid = 6, string_value = InspectCert.Hex(CertItem()) }],
            },
        ],
    };

    [Fact]
    public async Task CacheMiss_FetchesFromSteamAndStitchesTheThreeArrays()
    {
        var steamId = NextSteamId();
        // Seeded so the second asset's classic S/A/D link - which carries no item data of its own -
        // resolves through the batched item-cache read rather than coming back bare.
        await _factory.Database.SaveItemWithExtrasAsync(new CEconItemPreviewDataBlock
        {
            itemid = 222,
            defindex = 7,
            paintindex = 44,
            paintseed = 5,
            paintwear = BitConverter.SingleToUInt32Bits(0.4f), // Well-Worn
            rarity = 6,
            quality = 4,
            origin = 8,
        });
        _factory.Http.Respond(InventoryUrl(steamId), HttpStatusCode.OK, Serialize(FixtureInventory(totalInventoryCount: 9)));

        var response = await _factory.CreateClient().GetAsync($"/api/inventory?steamid={steamId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var json = await ReadJson(response);

        Assert.Equal(9, json.GetProperty("total").GetInt32());
        Assert.Equal(1, json.GetProperty("success").GetInt32());
        Assert.Equal(steamId.ToString(), json.GetProperty("steamid").GetString());

        // Three of the five assets are inspectable; the description-less one and the action-less one
        // are dropped rather than shipped without a link.
        var items = json.GetProperty("csgo_items").EnumerateArray().ToArray();
        Assert.Equal(3, items.Length);
        Assert.DoesNotContain(items, i => i.GetProperty("assetid").GetString() is "1003" or "1004");

        var cert = items[0];
        Assert.Equal("1001", cert.GetProperty("assetid").GetString());
        Assert.Equal("C1", cert.GetProperty("classid").GetString());
        Assert.Equal("I1", cert.GetProperty("instanceid").GetString());
        Assert.Equal("StatTrak™ AK-47 | Fire Serpent", cert.GetProperty("name").GetString());
        Assert.Equal("StatTrak™ AK-47 | Fire Serpent (Field-Tested)", cert.GetProperty("market_name").GetString());
        Assert.Equal("Covert Rifle", cert.GetProperty("type").GetString());
        Assert.Equal("EB4B4B", cert.GetProperty("name_color").GetString());
        Assert.Equal("icon-small", cert.GetProperty("icon_url").GetString());
        Assert.Equal("icon-large", cert.GetProperty("icon_url_large").GetString());
        // Wear/rarity/quality/type come off four differently-categorised tags, not four fields.
        Assert.Equal("Field-Tested", cert.GetProperty("wear").GetString());
        Assert.Equal("Covert", cert.GetProperty("rarity").GetString());
        Assert.Equal("StatTrak™", cert.GetProperty("quality").GetString());
        Assert.Equal("Rifle", cert.GetProperty("item_type").GetString());
        // Parsed out of the free-text StatTrak score line, thousands separator and all.
        Assert.Equal(1234, cert.GetProperty("stattrak_kills").GetInt32());
        // Priced off Steam's own market_hash_name, so an item is priced even before anything about
        // it has been decoded. Cents, exactly as the feed gave them.
        var price = cert.GetProperty("price");
        Assert.Equal(200000, price.GetProperty("min").GetInt32());
        Assert.Equal(240000, price.GetProperty("suggested").GetInt32());
        Assert.Equal("USD", price.GetProperty("currency").GetString());
        Assert.Equal("skinport", price.GetProperty("source").GetString());
        Assert.False(price.GetProperty("approximate").GetBoolean());

        // %propid:6% was replaced with this asset's certificate, which is what lets the item decode
        // with no Game Coordinator round-trip.
        Assert.Equal($"steam://run/730//+csgo_econ_action_preview {InspectCert.Hex(CertItem())}",
            cert.GetProperty("inspect_link").GetString());
        var decoded = cert.GetProperty("existing_data");
        Assert.Equal(111UL, decoded.GetProperty("itemid").GetUInt64());
        Assert.Equal(661u, decoded.GetProperty("paintseed").GetUInt32());
        Assert.Equal("AK-47", decoded.GetProperty("weapon").GetString());
        Assert.Equal("Fire Serpent", decoded.GetProperty("skin").GetString());
        Assert.Equal("Field-Tested", decoded.GetProperty("wear_name").GetString());
        Assert.Equal("Covert", decoded.GetProperty("rarity_name").GetString());
        Assert.Equal("Found in Crate", decoded.GetProperty("origin_name").GetString());
        Assert.Equal("AK-47 | Fire Serpent (Field-Tested)", decoded.GetProperty("market_hash_name").GetString());

        var cached = items[1];
        Assert.Equal("222", cached.GetProperty("assetid").GetString());
        // %owner_steamid% and %assetid% both filled in from the resolved id and this copy's assetid.
        Assert.Equal(
            $"steam://rungame/730/{steamId}/+csgo_econ_action_preview S{steamId}A222D999",
            cached.GetProperty("inspect_link").GetString());
        Assert.Equal(222UL, cached.GetProperty("existing_data").GetProperty("itemid").GetUInt64());
        Assert.Equal("Well-Worn", cached.GetProperty("existing_data").GetProperty("wear_name").GetString());
        // Well-Worn was never listed on Skinport, so the decoded item is priced off the nearest
        // wear of the same skin and flagged approximate.
        var approximatePrice = cached.GetProperty("existing_data").GetProperty("price");
        Assert.Equal(125050, approximatePrice.GetProperty("suggested").GetInt32());
        Assert.True(approximatePrice.GetProperty("approximate").GetBoolean());
        // The classic link names the owner and the copy, and those travel back on the response.
        Assert.Equal(steamId, cached.GetProperty("existing_data").GetProperty("s").GetUInt64());
        Assert.Equal(999UL, cached.GetProperty("existing_data").GetProperty("d").GetUInt64());

        // Same classid as `cert`, different instanceid - a different copy of the same skin. Keyed on
        // classid alone this asset would inherit the C1/I1 description and come back renamed,
        // retagged, and priced as the StatTrak variant, for part of a real user's inventory.
        var sameClass = items[2];
        Assert.Equal("1005", sameClass.GetProperty("assetid").GetString());
        Assert.Equal("C1", sameClass.GetProperty("classid").GetString());
        Assert.Equal("I2", sameClass.GetProperty("instanceid").GetString());
        Assert.Equal("AK-47 | Fire Serpent", sameClass.GetProperty("name").GetString());
        Assert.Equal("Unique", sameClass.GetProperty("quality").GetString());
        Assert.Equal(JsonValueKind.Null, sameClass.GetProperty("stattrak_kills").ValueKind);
        // Priced off its own market_hash_name, not the StatTrak neighbour's 240000.
        Assert.Equal(125050, sameClass.GetProperty("price").GetProperty("suggested").GetInt32());

        Assert.Equal(1, _factory.Http.RequestsMatching(InventoryUrl(steamId)));
    }

    [Fact]
    public async Task TotalAboveTheReturnedAssetCount_FlagsTheResponseTruncated()
    {
        // We fetch a single count=2000 page, so a larger inventory comes back capped while `total`
        // still reports the full count. The UI must not present that as the whole inventory.
        var steamId = NextSteamId();
        _factory.Http.Respond(InventoryUrl(steamId), HttpStatusCode.OK, Serialize(FixtureInventory(totalInventoryCount: 5000)));

        var json = await ReadJson(await _factory.CreateClient().GetAsync($"/api/inventory?steamid={steamId}"));

        Assert.True(json.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task TotalMatchingTheReturnedAssetCount_IsNotTruncated()
    {
        var steamId = NextSteamId();
        // Five assets in the fixture, five reported by Steam - nothing was cut off.
        _factory.Http.Respond(InventoryUrl(steamId), HttpStatusCode.OK, Serialize(FixtureInventory(totalInventoryCount: 5)));

        var json = await ReadJson(await _factory.CreateClient().GetAsync($"/api/inventory?steamid={steamId}"));

        Assert.False(json.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task AssetsWhoseLinksCannotBeUsed_StillRenderWithoutDecodedData()
    {
        // Steam's own data is uneven: some descriptions carry a null action link, some carry an
        // action that isn't an inspect link, and a certificate placeholder goes unfilled when the
        // asset has no properties at all (this inventory has no asset_properties array). None of
        // those may drop the item silently or fail the whole request.
        var steamId = NextSteamId();
        var inventory = new SteamInventoryResponse
        {
            total = 3,
            success = 1,
            assets =
            [
                new() { assetid = "2001", classid = "D1", instanceid = "J1" },
                new() { assetid = "2002", classid = "D2", instanceid = "J2" },
                new() { assetid = "2003", classid = "D3", instanceid = "J3" },
            ],
            descriptions =
            [
                new() { classid = "D1", instanceid = "J1", name = "Null Link", actions = [new() { link = null }] },
                new()
                {
                    classid = "D2",
                    instanceid = "J2",
                    name = "Not An Inspect Link",
                    actions = [new() { name = "View in Workshop", link = "steam://openurl/https://example.com" }],
                },
                new()
                {
                    classid = "D3",
                    instanceid = "J3",
                    // No `name`; the response falls back to market_name.
                    market_name = "Unfilled Certificate",
                    actions = [new() { link = CertTemplate }],
                },
            ],
        };
        _factory.Http.Respond(InventoryUrl(steamId), HttpStatusCode.OK, Serialize(inventory));

        var json = await ReadJson(await _factory.CreateClient().GetAsync($"/api/inventory?steamid={steamId}"));

        // The first two have no inspect link to build, so they are dropped.
        var items = json.GetProperty("csgo_items").EnumerateArray().ToArray();
        var item = Assert.Single(items);
        Assert.Equal("2003", item.GetProperty("assetid").GetString());
        Assert.Equal("Unfilled Certificate", item.GetProperty("name").GetString());
        // The placeholder is left intact rather than spliced with an empty string, so the link
        // visibly fails to parse instead of pointing at junk - and the item ships undecoded.
        Assert.Equal(CertTemplate, item.GetProperty("inspect_link").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("existing_data").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("stattrak_kills").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("wear").ValueKind);
    }

    // --- caching -------------------------------------------------------------------------

    [Fact]
    public async Task SecondViewer_IsServedFromMemoryWithoutRefetching()
    {
        var steamId = NextSteamId();
        _factory.Http.Respond(InventoryUrl(steamId), HttpStatusCode.OK, Serialize(FixtureInventory(totalInventoryCount: 5)));
        var client = _factory.CreateClient();

        var first = await client.GetAsync($"/api/inventory?steamid={steamId}");
        var second = await client.GetAsync($"/api/inventory?steamid={steamId}");

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        // The cache stores the exact bytes that were returned, so a hit is byte-identical.
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        Assert.Equal(1, _factory.Http.RequestsMatching(InventoryUrl(steamId)));
    }

    [Fact]
    public async Task PrivateProfile_Is400AndTheFailureIsCached()
    {
        // Steam answers 403 for a private (or non-existent) inventory. A reload storm against one of
        // those must not keep re-hitting steamcommunity.com and extending the IP throttle.
        var steamId = NextSteamId();
        _factory.Http.Respond(InventoryUrl(steamId), HttpStatusCode.Forbidden, "{}");
        var client = _factory.CreateClient();

        var first = await client.GetAsync($"/api/inventory?steamid={steamId}");
        var second = await client.GetAsync($"/api/inventory?steamid={steamId}");

        Assert.Equal(HttpStatusCode.BadRequest, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Equal("Inventory is private or user does not exist",
            (await ReadJson(second)).GetProperty("error").GetString());
        Assert.Equal(1, _factory.Http.RequestsMatching(InventoryUrl(steamId)));
    }

    [Fact]
    public async Task SteamRateLimit_IsSurfacedAs429AndCached()
    {
        // A 429 means Steam is throttling this server's egress IP, so the one thing we must not do
        // is retry it on every reload.
        var steamId = NextSteamId();
        _factory.Http.Respond(InventoryUrl(steamId), () =>
        {
            var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("", System.Text.Encoding.UTF8, "application/json"),
            };
            throttled.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return throttled;
        });
        var client = _factory.CreateClient();

        var first = await client.GetAsync($"/api/inventory?steamid={steamId}");
        var second = await client.GetAsync($"/api/inventory?steamid={steamId}");

        Assert.Equal(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Contains("rate limiting", (await ReadJson(second)).GetProperty("error").GetString());
        Assert.Equal(1, _factory.Http.RequestsMatching(InventoryUrl(steamId)));
    }

    [Fact]
    public async Task OtherUpstreamFailure_Is400CarryingTheStatusAndIsCached()
    {
        var steamId = NextSteamId();
        _factory.Http.Respond(InventoryUrl(steamId), HttpStatusCode.InternalServerError, "");
        var client = _factory.CreateClient();

        var first = await client.GetAsync($"/api/inventory?steamid={steamId}");
        var second = await client.GetAsync($"/api/inventory?steamid={steamId}");

        Assert.Equal(HttpStatusCode.BadRequest, first.StatusCode);
        Assert.Equal("Failed to fetch inventory: InternalServerError",
            (await ReadJson(second)).GetProperty("error").GetString());
        Assert.Equal(1, _factory.Http.RequestsMatching(InventoryUrl(steamId)));
    }

    [Fact]
    public async Task EmptyBodyFromSteam_Is400()
    {
        var steamId = NextSteamId();
        _factory.Http.Respond(InventoryUrl(steamId), HttpStatusCode.OK, "");

        var response = await _factory.CreateClient().GetAsync($"/api/inventory?steamid={steamId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Empty response from Steam API", (await ReadJson(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task RateLimitWithoutARetryAfterHeader_IsStillSurfacedAs429()
    {
        // Steam does not always say how long the throttle lasts.
        var steamId = NextSteamId();
        _factory.Http.Respond(InventoryUrl(steamId), HttpStatusCode.TooManyRequests, "");

        var response = await _factory.CreateClient().GetAsync($"/api/inventory?steamid={steamId}");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    [Fact]
    public async Task InventoryWithNoAssets_Is400()
    {
        // Steam returns a well-formed body with no assets/descriptions for an empty inventory.
        var steamId = NextSteamId();
        _factory.Http.Respond(InventoryUrl(steamId), HttpStatusCode.OK, """{"success":1,"total_inventory_count":0}""");

        var response = await _factory.CreateClient().GetAsync($"/api/inventory?steamid={steamId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Invalid inventory data or inventory is empty",
            (await ReadJson(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task InventoryWithAssetsButNoDescriptions_Is400()
    {
        // Without descriptions there are no inspect links to build, so there is nothing to return.
        var steamId = NextSteamId();
        _factory.Http.Respond(InventoryUrl(steamId), HttpStatusCode.OK,
            """{"success":1,"total_inventory_count":1,"assets":[{"assetid":"1","classid":"C","instanceid":"I"}]}""");

        var response = await _factory.CreateClient().GetAsync($"/api/inventory?steamid={steamId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Invalid inventory data or inventory is empty",
            (await ReadJson(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task ConnectionFailure_Is400AndIsNotNegativeCached()
    {
        // A transient blip must let the next request retry rather than being pinned as an error for
        // the negative-cache window.
        var steamId = NextSteamId();
        _factory.Http.Throw(InventoryUrl(steamId), () => new HttpRequestException("connection reset"));
        var client = _factory.CreateClient();

        var first = await client.GetAsync($"/api/inventory?steamid={steamId}");
        var second = await client.GetAsync($"/api/inventory?steamid={steamId}");

        Assert.Equal(HttpStatusCode.BadRequest, first.StatusCode);
        Assert.Equal("Failed to connect to Steam API", (await ReadJson(first)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Equal(2, _factory.Http.RequestsMatching(InventoryUrl(steamId)));
    }

    [Fact]
    public async Task FetchTimeout_Is400()
    {
        var steamId = NextSteamId();
        _factory.Http.Throw(InventoryUrl(steamId), () => new TaskCanceledException("timed out"));

        var response = await _factory.CreateClient().GetAsync($"/api/inventory?steamid={steamId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Request timed out while fetching inventory",
            (await ReadJson(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task UnparseableBodyFromSteam_Is400NotA500()
    {
        // Steam sometimes answers 200 with an HTML error page. That must not escape as an unhandled
        // JsonException and become a 500.
        var steamId = NextSteamId();
        _factory.Http.Respond(InventoryUrl(steamId), HttpStatusCode.OK, "<html>nope</html>", "text/html");

        var response = await _factory.CreateClient().GetAsync($"/api/inventory?steamid={steamId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Invalid response from Steam API", (await ReadJson(response)).GetProperty("error").GetString());
    }

    // --- single flight -------------------------------------------------------------------

    [Fact]
    public async Task ConcurrentFirstViewers_CauseExactlyOneOutboundFetch()
    {
        // Without the gate, K simultaneous first-viewers of an uncached inventory would each fetch
        // and stampede steamcommunity.com. The leader fetches; the rest wait and then read the entry
        // it cached.
        var steamId = NextSteamId();
        var leaderIsFetching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _factory.Http.Respond(InventoryUrl(steamId), HttpStatusCode.OK, Serialize(FixtureInventory(totalInventoryCount: 5)));
        _factory.Http.Hold = async () =>
        {
            leaderIsFetching.TrySetResult();
            // Bounded too: a leader parked in here forever would hang every viewer waiting on the
            // gate behind it. Timing out fails the request instead, which fails the test.
            await release.Task.WaitAsync(TimeSpan.FromSeconds(20));
        };

        var client = _factory.CreateClient();
        var viewers = Enumerable.Range(0, 8)
            .Select(_ => client.GetAsync($"/api/inventory?steamid={steamId}"))
            .ToArray();

        try
        {
            // Every wait here is bounded. If nothing ever reaches the stub - a rate-limit rejection,
            // a routing change, a cache key that hands every viewer a hit - this test has to go red,
            // not park the whole run on a task no one will ever complete.
            await leaderIsFetching.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // The assertion below holds however these interleave - a viewer that arrives after the
            // leader finished simply reads the cache - but this pause is what actually parks the
            // other seven on the gate, which is the path worth exercising.
            await Task.Delay(250);
        }
        finally
        {
            // Released from the finally so a failure above can never leave the leader parked inside
            // the handler, which would hang the eight request tasks and host teardown with them.
            release.TrySetResult();
            _factory.Http.Hold = null;
        }

        var responses = await Task.WhenAll(viewers).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, _factory.Http.RequestsMatching(InventoryUrl(steamId)));
        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.All(bodies, body => Assert.Equal(bodies[0], body));
    }

    // --- steam id resolution -------------------------------------------------------------

    [Theory]
    [InlineData("garbage!!")]                        // neither an id, a profile URL, nor a legal vanity
    [InlineData("12345")]                            // all digits, but far outside the id64 block
    [InlineData("https://steamcommunity.com/id/x")]  // one-character vanity, below the 2-char minimum
    public async Task UnresolvableInput_Is400(string steamid)
    {
        var response = await _factory.CreateClient().GetAsync($"/api/inventory?steamid={Uri.EscapeDataString(steamid)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Unable to resolve Steam ID or inventory",
            (await ReadJson(response)).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("/api/inventory")]
    [InlineData("/api/inventory?steamid=")]
    public async Task MissingSteamId_Is400FromModelBinding(string path)
    {
        // `steamid` is a non-nullable string on an [ApiController], so MVC treats it as required and
        // rejects a missing or blank value with a ValidationProblem before the action runs. The
        // action's own IsNullOrEmpty guard is therefore never reached over HTTP; what callers
        // actually see is this, so this is what's pinned.
        var response = await _factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("steamid", (await ReadJson(response)).GetProperty("errors").ToString());
    }

    [Fact]
    public async Task VanityName_IsResolvedThroughTheProfileXmlFeed()
    {
        // The public XML feed hands back the SteamId64 without an API key; the inventory is then
        // fetched (and cached) under the resolved id, so a vanity and the raw id share one entry.
        var steamId = NextSteamId();
        const string vanity = "vanity-lookup";
        _factory.Http.RespondXml($"/id/{vanity}/", $"<profile><steamID64>{steamId}</steamID64></profile>");
        _factory.Http.Respond(InventoryUrl(steamId), HttpStatusCode.OK, Serialize(FixtureInventory(totalInventoryCount: 5)));
        var client = _factory.CreateClient();

        var byVanity = await client.GetAsync($"/api/inventory?steamid={vanity}");
        var byId = await client.GetAsync($"/api/inventory?steamid={steamId}");

        Assert.Equal(HttpStatusCode.OK, byVanity.StatusCode);
        Assert.Equal(steamId.ToString(), (await ReadJson(byVanity)).GetProperty("steamid").GetString());
        Assert.Equal(await byVanity.Content.ReadAsStringAsync(), await byId.Content.ReadAsStringAsync());
        Assert.Equal(1, _factory.Http.RequestsMatching(InventoryUrl(steamId)));
    }

    [Fact]
    public async Task VanityFeedWithoutASteamId64_Is400()
    {
        const string vanity = "vanity-no-id";
        _factory.Http.RespondXml($"/id/{vanity}/", "<response><error>The specified profile could not be found.</error></response>");

        var response = await _factory.CreateClient().GetAsync($"/api/inventory?steamid={vanity}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Unable to resolve Steam ID or inventory",
            (await ReadJson(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task VanityResolveThatThrows_Is400NotA500()
    {
        // The resolve step swallows transport failures itself, so a Steam outage during a vanity
        // lookup is a 400 like any other unresolvable input.
        const string vanity = "vanity-throws";
        _factory.Http.Throw($"/id/{vanity}/", () => new HttpRequestException("connection reset"));

        var response = await _factory.CreateClient().GetAsync($"/api/inventory?steamid={vanity}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Unable to resolve Steam ID or inventory",
            (await ReadJson(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task VanityFeedFailure_Is400()
    {
        const string vanity = "vanity-feed-down";
        _factory.Http.Respond($"/id/{vanity}/", HttpStatusCode.ServiceUnavailable, "");

        var response = await _factory.CreateClient().GetAsync($"/api/inventory?steamid={vanity}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Unable to resolve Steam ID or inventory",
            (await ReadJson(response)).GetProperty("error").GetString());
    }
}
