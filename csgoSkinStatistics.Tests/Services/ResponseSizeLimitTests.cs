using System.Net;
using System.Text.Json;
using CSGOSkinAPI.Models;
using CSGOSkinAPI.Services;
using Microsoft.Extensions.DependencyInjection;
using ProtoBuf;
using SteamKit2.GC.CSGO.Internal;
using Xunit;

namespace csgoSkinStatistics.Tests.Services;

// Every outbound call this app makes is buffered whole into memory before the caller sees it
// (HttpCompletionOption.ResponseContentRead, the default), so an upstream that answers with an
// unbounded body could exhaust the host. Program.cs caps that per named client:
// MaxResponseContentBufferSize of 32 MB on "steam" and 64 MB on "skinport". These tests pin both
// numbers *and* what happens when one is hit - which has to be an ordinary upstream failure on
// every path, never a 500 and never a stopped background loop.
//
// Everything here runs through the real host, so the caps under test are the production ones rather
// than something the test configured for itself. Remove either ConfigureHttpClient line in
// Program.cs and these fail.
public class ResponseSizeLimitTests(ApiFactory factory) : IClassFixture<ApiFactory>, IDisposable
{
    private readonly ApiFactory _factory = factory;

    public void Dispose() => _factory.ResetPerTestState();

    // Own range; see the note on ApiFactory about steamids being unique across the whole assembly.
    private static int _nextId;
    private static ulong NextSteamId() => 76561198700000000UL + (ulong)Interlocked.Increment(ref _nextId);

    // Comfortably over the 32 MB "steam" cap and the 64 MB "skinport" cap respectively. Declared,
    // not allocated - see StubHttpMessageHandler.RespondOversized.
    private const long OverSteamCap = 40L * 1024 * 1024;
    private const long OverSkinportCap = 80L * 1024 * 1024;

    // What each oversize stub would hand back if the cap were gone: a perfectly good response. That
    // is deliberate - it means a lost cap shows up as a *success* where these tests expect a
    // failure, rather than as some other error that might pass by accident.
    private static string ValidInventoryJson() => JsonSerializer.Serialize(
        new SteamInventoryResponse { assets = [], descriptions = [], total = 0, success = 1 });

    private static string ValidProfileXml(ulong steamId) =>
        $"<?xml version=\"1.0\"?><profile><steamID64>{steamId}</steamID64></profile>";

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static string InventoryUrl(ulong steamId) => $"/inventory/{steamId}/730/2";

    [Fact]
    public async Task OversizeInventory_Is400InTheHouseShapeNotA500()
    {
        var steamId = NextSteamId();
        _factory.Http.RespondOversized(InventoryUrl(steamId), OverSteamCap, ValidInventoryJson());

        var response = await _factory.CreateClient().GetAsync($"/api/inventory?steamid={steamId}");

        // The cap surfaces as an HttpRequestException, which the endpoint already treats as any
        // other failure to reach Steam - so no new error path, and no 500.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Failed to connect to Steam API", (await ReadJson(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task OversizeInventoryWithNoContentLength_IsStillStoppedMidStream()
    {
        // The hostile case rather than the merely broken one: no Content-Length to reject up front,
        // so the client has to stop itself while the body is still arriving. Real bytes here, which
        // is why this is the one test that pays for them.
        var steamId = NextSteamId();
        _factory.Http.RespondOversizedWithoutLength(InventoryUrl(steamId), OverSteamCap);

        var response = await _factory.CreateClient().GetAsync($"/api/inventory?steamid={steamId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Failed to connect to Steam API", (await ReadJson(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task OversizeProfileFeed_Is400InTheHouseShapeNotA500()
    {
        var steamId = NextSteamId();
        _factory.Http.RespondOversized($"/profiles/{steamId}/", OverSteamCap, ValidProfileXml(steamId), "text/xml");

        var response = await _factory.CreateClient().GetAsync($"/api/profile?steamid={steamId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Failed to connect to Steam API", (await ReadJson(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task OversizeVanityResolveFeed_Is400InTheHouseShapeNotA500()
    {
        // The resolve happens before the inventory fetch, so an oversize XML feed here fails the
        // request at the resolve step - the same answer a private or missing profile gets.
        var steamId = NextSteamId();
        const string vanity = "oversize-vanity";
        _factory.Http.RespondOversized($"/id/{vanity}/", OverSteamCap, ValidProfileXml(steamId), "text/xml");
        _factory.Http.Respond(InventoryUrl(steamId), HttpStatusCode.OK, ValidInventoryJson());

        var response = await _factory.CreateClient().GetAsync($"/api/inventory?steamid={vanity}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Unable to resolve Steam ID or inventory",
            (await ReadJson(response)).GetProperty("error").GetString());
        // The resolve never produced an id, so nothing went on to ask Steam for the inventory.
        Assert.Equal(0, _factory.Http.RequestsMatching(InventoryUrl(steamId)));
    }

    [Fact]
    public async Task OversizeSkinportFeed_KeepsTheLastKnownPrices()
    {
        // Losing the feed must never lose the prices: RefreshAsync swallows the failure and leaves
        // the in-memory map (and the DB snapshot behind it) exactly as it was.
        var prices = _factory.Services.GetRequiredService<PriceService>();
        var before = prices.UpdatedAtUtc;
        Assert.NotNull(before);

        _factory.Http.RespondOversized("api.skinport.com", OverSkinportCap, JsonSerializer.Serialize(ApiFactory.Prices));
        await prices.RefreshAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        var resolved = prices.Resolve("AK-47 | Fire Serpent (Field-Tested)");
        Assert.NotNull(resolved);
        Assert.Equal(125050, resolved.SuggestedCents);
        // Untouched: a refused feed is not a refresh, so the "last updated" stamp must not move.
        Assert.Equal(before, prices.UpdatedAtUtc);
    }

    [Fact]
    public async Task OversizeWarmFetch_CachesNothingAndDoesNotStopTheDrainLoop()
    {
        // The warmer is the one caller with no request behind it, so the only thing that can go
        // wrong is the loop dying silently. It is built here rather than resolved from the host
        // (the host's copy is idled so it stays out of other tests' request counts) but from the
        // host's IHttpClientFactory, so it is the production 32 MB cap being hit.
        var oversize = NextSteamId();
        var next = NextSteamId();
        // The body behind the oversize declaration is a genuinely warmable inventory - one asset
        // with a decodable certificate. Nothing may read it, so WarmableItemId must not reach the
        // database; that is what separates "the cap stopped it" from "there was nothing to cache".
        _factory.Http.RespondOversized(InventoryUrl(oversize), OverSteamCap, WarmableInventoryJson());
        _factory.Http.Respond(InventoryUrl(next), HttpStatusCode.OK, ValidInventoryJson());

        using var warmer = new InventoryWarmService(
            _factory.Services.GetRequiredService<IHttpClientFactory>(), _factory.Database,
            new CapturingLogger<InventoryWarmService>());
        await warmer.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        warmer.Enqueue(oversize);
        warmer.Enqueue(next);

        // The queue has a single consumer, so the second inventory being fetched at all proves the
        // first one's failure did not escape the loop.
        await WaitForAsync(() => _factory.Http.RequestsMatching(InventoryUrl(next)) > 0,
            "the next queued inventory after an oversize response");
        await warmer.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, _factory.Http.RequestsMatching(InventoryUrl(oversize)));
        Assert.Null(await _factory.Database.GetItemAsync(WarmableItemId)
            .WaitAsync(TimeSpan.FromSeconds(30)));
    }

    private const ulong WarmableItemId = 770001;

    // An inventory holding a single item the warmer would happily persist: a description whose
    // inspect action is the %propid:6% certificate template, and the per-asset certificate that
    // fills it in. Mirrors InventoryWarmServiceTests' cert construction - a leading XOR key byte
    // (0x00, the legacy no-op), the protobuf, then the four checksum bytes the client ignores.
    private static string WarmableInventoryJson()
    {
        var item = new CEconItemPreviewDataBlock
        {
            itemid = WarmableItemId, defindex = 7, paintindex = 282, rarity = 5, quality = 4,
            paintwear = 1065353216, paintseed = 661, inventory = 3, origin = 8,
        };
        using var buffer = new MemoryStream();
        Serializer.Serialize(buffer, item);
        var proto = buffer.ToArray();
        var raw = new byte[1 + proto.Length + 4];
        proto.CopyTo(raw, 1);

        return JsonSerializer.Serialize(new SteamInventoryResponse
        {
            assets = [new SteamAsset { assetid = "1", classid = "c1", instanceid = "i1" }],
            descriptions =
            [
                new SteamDescription
                {
                    classid = "c1",
                    instanceid = "i1",
                    actions = [new SteamAction { link = "steam://run/730//+csgo_econ_action_preview %propid:6%" }],
                },
            ],
            asset_properties =
            [
                new SteamAssetProperties
                {
                    assetid = "1",
                    asset_properties =
                    [
                        new SteamAssetProperty { propertyid = 6, string_value = Convert.ToHexString(raw) },
                    ],
                },
            ],
            total = 1,
            success = 1,
        });
    }

    private static async Task WaitForAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.Fail($"Timed out waiting for: {because}");
    }
}
