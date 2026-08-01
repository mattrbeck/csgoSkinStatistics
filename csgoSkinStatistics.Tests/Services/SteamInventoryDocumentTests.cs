using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CSGOSkinAPI.Models;
using CSGOSkinAPI.Services;
using SteamKit2.GC.CSGO.Internal;
using Xunit;
using Xunit.Abstractions;

namespace csgoSkinStatistics.Tests.Services;

// SteamInventoryDocument is the one place that turns steamcommunity.com's inventory response into
// inspectable items. Both callers - the /api/inventory endpoint and the background warmer - go
// through it, so a defect here is a defect in both at once; that is the point of it existing, and
// the reason it is worth testing directly rather than only through the two of them.
//
// The endpoint and warmer suites still own their own behaviour (response shapes, negative caching,
// the drain loop, what gets persisted). These tests own the stitching: which asset resolves to
// which description, which assets are skipped, what the link comes out as, and what the walk costs.
public class SteamInventoryDocumentTests(ITestOutputHelper output)
{
    private const ulong Owner = 76561198123456789;
    private static string OwnerId => Owner.ToString();

    // The two link shapes in the wild: a self-contained certificate property, and the classic
    // owner/asset form that only the game coordinator can resolve.
    private const string CertTemplate = "steam://run/730//+csgo_econ_action_preview %propid:6%";
    private const string OwnerTemplate =
        "steam://rungame/730/%owner_steamid%/+csgo_econ_action_preview S%owner_steamid%A%assetid%D999";

    // Steam sends actions other than the inspect one, and not necessarily last - taking actions[0],
    // or the first action with a non-null link, would pick this instead.
    private const string DecoyActionLink = "https://steamcommunity.com/market/listings/730/Whatever";

    private static SteamAsset Asset(string assetid, string classid, string instanceid) =>
        new() { appid = 730, contextid = "2", assetid = assetid, classid = classid, instanceid = instanceid, amount = "1" };

    private static SteamDescription Description(string classid, string instanceid, params string?[] actionLinks) =>
        new()
        {
            classid = classid,
            instanceid = instanceid,
            actions = [.. actionLinks.Select(l => new SteamAction { link = l })],
        };

    private static SteamAssetProperties CertProperty(string assetid, string hex) =>
        new() { assetid = assetid, asset_properties = [new SteamAssetProperty { propertyid = 6, string_value = hex }] };

    private static SteamInventoryDocument Parse(SteamInventoryResponse inventory)
    {
        // Round-tripped through JSON rather than handed the object graph, so these tests exercise
        // the same entry point production does.
        var document = SteamInventoryDocument.TryParse(JsonSerializer.Serialize(inventory));
        Assert.NotNull(document);
        return document;
    }

    // --- the URL both callers now share -------------------------------------------------

    [Fact]
    public void BuildUrl_AsksForOneEnglishPageOfTheCs2Inventory()
    {
        // Every part of this is load-bearing and used to be written out twice: 730/2 is CS2's
        // app and its "backpack" context, l=english is what the tag/description parsing assumes,
        // and count=2000 is the page size both callers report truncation against.
        Assert.Equal(
            $"https://steamcommunity.com/inventory/{Owner}/730/2?l=english&count=2000",
            SteamInventoryDocument.BuildUrl(OwnerId));
    }

    // --- what counts as an inventory ----------------------------------------------------

    public static TheoryData<string> UnusableBodies() => new()
    {
        "null",                                   // Steam's answer for a private inventory
        "{\"success\":0}",                        // neither array
        "{\"assets\":[],\"descriptions\":null}",  // half an inventory
        "{\"descriptions\":[]}",                  // the other half
    };

    [Theory]
    [MemberData(nameof(UnusableBodies))]
    public void TryParse_BodyWithoutBothArrays_IsNotAnInventory(string body)
    {
        Assert.Null(SteamInventoryDocument.TryParse(body));
    }

    [Fact]
    public void TryParse_MalformedJson_ThrowsRatherThanReturningNull()
    {
        // Deliberate, and relied upon by both callers: "Steam sent something that is not JSON" is a
        // different event from "Steam sent an inventory we cannot use", and they handle it
        // differently (the endpoint answers 400 from its JsonException catch, the warmer lets its
        // drain loop log it). Swallowing it here would silently merge the two.
        Assert.Throws<JsonException>(() => SteamInventoryDocument.TryParse("<html>502 Bad Gateway</html>"));
    }

    [Fact]
    public void TryParse_EmptyButValidInventory_IsADocumentWithNothingInIt()
    {
        var document = Parse(new SteamInventoryResponse { assets = [], descriptions = [], total = 0, success = 1 });

        Assert.Empty(document.Assets);
        Assert.Equal(0, document.Total);
        Assert.False(document.Truncated);
        Assert.Empty(document.InspectableAssets(OwnerId));
    }

    // --- the composite description key --------------------------------------------------

    // The defect this type exists to make impossible in one place instead of two. Steam mints a new
    // instanceid under the same classid whenever a copy's description differs - a name tag, applied
    // stickers, a StatTrak score line - so two assets can share a classid and legitimately have
    // completely different descriptions.
    private static SteamInventoryResponse SharedClassidInventory() => new()
    {
        total = 2,
        success = 1,
        assets = [Asset("10", "shared", "plain"), Asset("20", "shared", "named")],
        descriptions =
        [
            new()
            {
                classid = "shared", instanceid = "plain", name = "AK-47 | Redline",
                actions = [new() { link = OwnerTemplate }],
            },
            new()
            {
                classid = "shared", instanceid = "named", name = "'Bertha' (AK-47 | Redline)",
                actions = [new() { link = CertTemplate }],
            },
        ],
        asset_properties = [CertProperty("20", "00DEADBEEF")],
    };

    [Fact]
    public void FindDescription_IsKeyedOnClassidAndInstanceid_NotClassidAlone()
    {
        var document = Parse(SharedClassidInventory());

        // Keyed on classid alone, both of these would return whichever description was indexed
        // first, and one of the two assertions would fail whichever one that was.
        Assert.Equal("AK-47 | Redline", document.FindDescription("shared", "plain")?.name);
        Assert.Equal("'Bertha' (AK-47 | Redline)", document.FindDescription("shared", "named")?.name);
        // A classid that exists but under no such instanceid is a miss, not a near-enough hit.
        Assert.Null(document.FindDescription("shared", "nosuch"));
        Assert.Null(document.FindDescription("nosuch", "plain"));
    }

    [Fact]
    public void InspectableAssets_PairEachAssetWithItsOwnInstancesDescriptionAndLink()
    {
        var document = Parse(SharedClassidInventory());

        var walked = document.InspectableAssets(OwnerId).ToList();
        Assert.Equal(2, walked.Count);

        // Asset 10 is the plain copy: its description offers only the masked owner/asset template.
        Assert.Equal("10", walked[0].Asset.assetid);
        Assert.Equal("AK-47 | Redline", walked[0].Description.name);
        Assert.Equal(
            $"steam://rungame/730/{Owner}/+csgo_econ_action_preview S{Owner}A10D999",
            walked[0].InspectLink);

        // Asset 20 is the named copy: its own description offers a cert template, filled from its
        // own asset_properties. Borrowing either from the other copy would show up here.
        Assert.Equal("20", walked[1].Asset.assetid);
        Assert.Equal("'Bertha' (AK-47 | Redline)", walked[1].Description.name);
        Assert.Equal("steam://run/730//+csgo_econ_action_preview 00DEADBEEF", walked[1].InspectLink);
    }

    [Fact]
    public void InspectableAssets_ResolveThePropidPlaceholderFromTheAssetsOwnProperties()
    {
        // Two copies of one class/instance - the ordinary case, and the one where a properties
        // lookup keyed on anything but assetid would hand both copies the same certificate and so
        // report two copies of one item.
        var document = Parse(new SteamInventoryResponse
        {
            total = 2,
            success = 1,
            assets = [Asset("101", "c", "i"), Asset("102", "c", "i")],
            descriptions = [Description("c", "i", CertTemplate)],
            asset_properties = [CertProperty("101", "00AAAA"), CertProperty("102", "00BBBB")],
        });

        var links = document.InspectableAssets(OwnerId).Select(x => x.InspectLink).ToList();
        Assert.Equal(
            [
                "steam://run/730//+csgo_econ_action_preview 00AAAA",
                "steam://run/730//+csgo_econ_action_preview 00BBBB",
            ],
            links);
    }

    // --- which assets are walked and which are skipped ----------------------------------

    [Fact]
    public void InspectableAssets_SkipEverythingThatCannotYieldALink()
    {
        var document = Parse(new SteamInventoryResponse
        {
            total = 6,
            success = 1,
            assets =
            [
                Asset("1", "keep", "i"),      // a real inspect action, behind a decoy
                Asset("2", "noactions", "i"), // description exists but carries no actions at all
                Asset("3", "emptyactions", "i"),
                Asset("4", "decoyonly", "i"), // actions, none of them an inspect action
                Asset("5", "nulllink", "i"),  // an action whose link is null must not throw
                Asset("6", "gone", "i"),      // no description on this page at all
            ],
            descriptions =
            [
                Description("keep", "i", DecoyActionLink, CertTemplate),
                new() { classid = "noactions", instanceid = "i", actions = null },
                new() { classid = "emptyactions", instanceid = "i", actions = [] },
                Description("decoyonly", "i", DecoyActionLink),
                Description("nulllink", "i", [null]),
            ],
            asset_properties = [CertProperty("1", "00CAFE")],
        });

        var walked = document.InspectableAssets(OwnerId).ToList();
        var kept = Assert.Single(walked);
        Assert.Equal("1", kept.Asset.assetid);
        // The decoy sits ahead of the inspect action, so this also pins that the walk picks the
        // inspect action rather than the first one.
        Assert.Equal("steam://run/730//+csgo_econ_action_preview 00CAFE", kept.InspectLink);
    }

    [Fact]
    public void InspectableAssets_AssetWithNoPropertiesEntry_LeavesThePlaceholderIntact()
    {
        // Nothing to substitute must leave %propid:6% alone rather than splice in an empty string:
        // the link then visibly fails to parse instead of pointing at junk.
        var document = Parse(new SteamInventoryResponse
        {
            total = 1,
            success = 1,
            assets = [Asset("1", "c", "i")],
            descriptions = [Description("c", "i", CertTemplate)],
            asset_properties = [],
        });

        var kept = Assert.Single(document.InspectableAssets(OwnerId));
        Assert.Equal("steam://run/730//+csgo_econ_action_preview %propid:6%", kept.InspectLink);
    }

    [Fact]
    public void InspectableAssets_DuplicateClassInstancePair_FirstDescriptionWins()
    {
        // First-wins is what a scan for the first match used to do, so indexing must not quietly
        // change which of a repeated pair an asset resolves to.
        var document = Parse(new SteamInventoryResponse
        {
            total = 1,
            success = 1,
            assets = [Asset("1", "c", "i")],
            descriptions =
            [
                new() { classid = "c", instanceid = "i", name = "first", actions = [new() { link = CertTemplate }] },
                new() { classid = "c", instanceid = "i", name = "second", actions = [new() { link = OwnerTemplate }] },
            ],
            asset_properties = [CertProperty("1", "00FEED")],
        });

        Assert.Equal("first", Assert.Single(document.InspectableAssets(OwnerId)).Description.name);
    }

    [Fact]
    public void InspectableAssets_WalkAssetsInSteamsOrder()
    {
        // The endpoint ships csgo_items in this order and the UI renders it as given, so the walk
        // must follow the assets array rather than the descriptions array or a dictionary's
        // enumeration order.
        var document = Parse(new SteamInventoryResponse
        {
            total = 3,
            success = 1,
            assets = [Asset("30", "c3", "i"), Asset("10", "c1", "i"), Asset("20", "c2", "i")],
            descriptions =
            [
                Description("c1", "i", CertTemplate),
                Description("c2", "i", CertTemplate),
                Description("c3", "i", CertTemplate),
            ],
            asset_properties = [],
        });

        Assert.Equal(["30", "10", "20"], document.InspectableAssets(OwnerId).Select(x => x.Asset.assetid));
    }

    // --- truncation ---------------------------------------------------------------------

    [Theory]
    [InlineData(1, false)]  // the page holds the whole inventory
    [InlineData(2, false)]  // total equal to what came back
    [InlineData(3, true)]   // more items than this page carries
    public void Truncated_ComparesTheReportedTotalAgainstThisPage(int total, bool expected)
    {
        var document = Parse(new SteamInventoryResponse
        {
            total = total,
            success = 1,
            assets = [Asset("1", "c", "i"), Asset("2", "c", "i")],
            descriptions = [Description("c", "i", CertTemplate)],
        });

        Assert.Equal(total, document.Total);
        Assert.Equal(2, document.Assets.Count);
        Assert.Equal(expected, document.Truncated);
    }

    // --- the fetch ----------------------------------------------------------------------

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public string? RequestedUrl { get; private set; }
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUrl = request.RequestUri?.ToString();
            Calls++;
            return Task.FromResult(response);
        }
    }

    private sealed class RecordingFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public List<string> NamesRequested { get; } = [];
        public List<HttpClient> Created { get; } = [];

        public HttpClient CreateClient(string name)
        {
            NamesRequested.Add(name);
            var client = new HttpClient(handler, disposeHandler: false);
            Created.Add(client);
            return client;
        }
    }

    [Fact]
    public async Task FetchAsync_UsesThePooledSteamClientAndTheSharedUrl()
    {
        var body = JsonSerializer.Serialize(new SteamInventoryResponse { assets = [], descriptions = [], success = 1 });
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
        var factory = new RecordingFactory(handler);

        var response = await SteamInventoryDocument.FetchAsync(factory, OwnerId);

        // "steam" and not the default client: Program.cs hangs the connection pooling and the
        // MaxResponseContentBufferSize cap off that name, and an unnamed client would have neither.
        Assert.Equal("steam", Assert.Single(factory.NamesRequested));
        Assert.Equal(SteamInventoryDocument.BuildUrl(OwnerId), handler.RequestedUrl);
        Assert.Equal(1, handler.Calls);
        // Both callers set this before it was shared; a Steam that never answers must not pin a
        // request thread (the endpoint) or the single drain loop (the warmer) indefinitely.
        Assert.Equal(TimeSpan.FromSeconds(10), Assert.Single(factory.Created).Timeout);

        // The client wrapper is disposed inside FetchAsync while the response is handed back. Both
        // callers read status, headers and body from it afterwards, so pin that this works: the
        // body is already buffered by the time GetAsync returns.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task FetchAsync_HandsBackFailuresRatherThanThrowing()
    {
        // The two callers do completely different things with a non-success status - the endpoint
        // splits 429/403/other into three cached failures, the warmer logs and gives up - so the
        // shared fetch must not decide for them.
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("", Encoding.UTF8, "text/plain"),
            Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60)) },
        });

        var response = await SteamInventoryDocument.FetchAsync(new RecordingFactory(handler), OwnerId);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(60), response.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task FetchAsync_PassesTheCancellationTokenThrough()
    {
        // The warmer's drain loop cancels on shutdown and relies on the fetch honouring it.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await SteamInventoryDocument.FetchAsync(new RecordingFactory(handler), OwnerId, cts.Token)
                .WaitAsync(TimeSpan.FromSeconds(10)));
    }

    // --- cost ---------------------------------------------------------------------------

    // Builds an inventory of `n` assets where every asset has a description of its own, with the
    // descriptions array in reverse order - so a scan finds asset k's description at index n-1-k.
    private static string ScalingInventory(int n) => JsonSerializer.Serialize(new SteamInventoryResponse
    {
        total = n,
        success = 1,
        assets = [.. Enumerable.Range(0, n).Select(i => Asset(i.ToString(), $"c{i}", $"i{i}"))],
        descriptions = [.. Enumerable.Range(0, n).Reverse().Select(i => Description($"c{i}", $"i{i}", CertTemplate))],
        asset_properties = [],
    });

    // The reason this type exists at all, asserted rather than assumed.
    //
    // The warmer used to find each asset's description with a FirstOrDefault over the descriptions
    // array, which is O(assets x descriptions): 2000x2000 = 4,000,000 comparisons on a maxed
    // inventory, on the single background thread that serves every warm. The endpoint had already
    // been fixed to index the descriptions; the warmer never was, because it was a second copy.
    // Sharing this walk is what removed it, so the property worth pinning is the shape of the
    // curve, not a wall-clock number that would only pin the machine it was measured on.
    //
    // Quadruple the input: a linear walk takes about 4x as long, a quadratic one about 16x.
    // Measured on the extracted type, 1000 -> 4000 assets is ~2.4x; measured on the scan it
    // replaced, ~13.7x. The 8x bar sits between those with room on both sides, and each timing is
    // the best of several runs so a scheduling spike can only ever make a run look slower, never
    // faster - and the numbers go to the test output either way.
    [Fact]
    public void InspectableAssets_CostGrowsWithTheAssetCount_NotWithAssetsTimesDescriptions()
    {
        const int small = 1000;
        const int large = 4000;
        var smallDocument = SteamInventoryDocument.TryParse(ScalingInventory(small));
        var largeDocument = SteamInventoryDocument.TryParse(ScalingInventory(large));
        Assert.NotNull(smallDocument);
        Assert.NotNull(largeDocument);

        // Warm the JIT so the first measured run is not paying for compilation.
        Assert.Equal(small, smallDocument.InspectableAssets(OwnerId).Count());
        Assert.Equal(large, largeDocument.InspectableAssets(OwnerId).Count());

        var smallMs = BestOf(() => smallDocument.InspectableAssets(OwnerId).Count(), small);
        var largeMs = BestOf(() => largeDocument.InspectableAssets(OwnerId).Count(), large);
        var growth = largeMs / smallMs;
        output.WriteLine($"{small} assets: {smallMs:F3} ms; {large} assets: {largeMs:F3} ms; growth {growth:F1}x for 4x the input");

        Assert.True(growth < 8.0,
            $"Walking {large} assets took {growth:F1}x as long as {small} ({largeMs:F3} ms vs {smallMs:F3} ms). "
            + "4x the input should cost about 4x, not about 16x - something reintroduced a per-asset "
            + "scan over the descriptions array.");
    }

    private static double BestOf(Func<int> walk, int expected, int runs = 7)
    {
        var best = double.MaxValue;
        for (var i = 0; i < runs; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            var walked = walk();
            stopwatch.Stop();
            Assert.Equal(expected, walked);
            best = Math.Min(best, stopwatch.Elapsed.TotalMilliseconds);
        }
        return best;
    }
}
