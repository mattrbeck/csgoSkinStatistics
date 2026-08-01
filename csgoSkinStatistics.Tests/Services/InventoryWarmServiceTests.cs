using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CSGOSkinAPI.Models;
using CSGOSkinAPI.Services;
using Microsoft.Data.Sqlite;
using ProtoBuf;
using SteamKit2.GC.CSGO.Internal;
using Xunit;

namespace csgoSkinStatistics.Tests.Services;

// The warm service is a background cache filler: nothing waits on it and nothing surfaces its
// failures, so the properties that keep it from becoming a problem (one fetch per owner per day, a
// bounded queue, a loop that survives Steam saying no) are only observable from tests like these.
//
// Everything runs against a stubbed steamcommunity.com handler and a temp-file database - no test
// touches the network, and none writes into the test working directory.
public class InventoryWarmServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"warmsvc_{Guid.NewGuid():N}.db");
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly Lock _gate = new();
        private readonly List<string> _requests = [];

        public IReadOnlyList<string> Requests
        {
            get { lock (_gate) return _requests.ToList(); }
        }

        public bool Requested(ulong steamid) => Requests.Any(r => r.Contains($"/inventory/{steamid}/"));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_gate) _requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(responder(request));
        }
    }

    private sealed class StubClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        private readonly Lock _gate = new();
        private readonly List<string> _names = [];

        public IReadOnlyList<string> NamesRequested
        {
            get { lock (_gate) return _names.ToList(); }
        }

        public HttpClient CreateClient(string name)
        {
            lock (_gate) _names.Add(name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    private const ulong SteamA = 76561198000000001;
    private const ulong SteamB = 76561198000000002;

    private async Task<DatabaseService> NewDbAsync()
    {
        var db = new DatabaseService(_dbPath);
        await db.InitializeDatabaseAsync();
        return db;
    }

    private (InventoryWarmService Service, StubHandler Handler, StubClientFactory Factory) NewService(
        DatabaseService db, Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StubHandler(responder);
        var factory = new StubClientFactory(handler);
        var service = new InventoryWarmService(factory, db);
        _disposables.Add(handler);
        _disposables.Add(service);
        return (service, handler, factory);
    }

    private static HttpResponseMessage Ok(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Status(HttpStatusCode status)
        => new(status) { Content = new StringContent("", Encoding.UTF8, "text/plain") };

    // An inventory Steam would accept but that holds nothing warmable.
    private static readonly string EmptyInventory =
        JsonSerializer.Serialize(new SteamInventoryResponse { assets = [], descriptions = [] });

    private static async Task WaitForAsync(Func<bool> condition, string because, int timeoutSeconds = 15)
    {
        // Polled rather than slept: the queue drains as fast as the stub responds, and a fixed
        // sleep would be both slower and flakier.
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.Fail($"Timed out waiting for: {because}");
    }

    // Builds the hex inspect-cert form of a link: a leading XOR key byte (0x00, the legacy no-op
    // key), the protobuf, then the 4-byte checksum the client ignores. This is what makes an
    // inventory item decodable locally without ever asking the game coordinator.
    private static string CertHex(CEconItemPreviewDataBlock item)
    {
        using var ms = new MemoryStream();
        Serializer.Serialize(ms, item);
        var proto = ms.ToArray();
        var raw = new byte[1 + proto.Length + 4];
        proto.CopyTo(raw, 1);
        return Convert.ToHexString(raw);
    }

    // The inspect action is not the only one Steam sends, and not necessarily the first, so the
    // description carries a decoy ahead of it: picking actions[0] - or any non-null link - yields a
    // URL that decodes to nothing.
    private const string DecoyActionLink = "https://steamcommunity.com/market/listings/730/Whatever";
    private const string CertActionLink = "steam://run/730//+csgo_econ_action_preview %propid:6%";
    // A masked link: it parses, but only the game coordinator can resolve it, so the warmer has
    // nothing to persist even when the asset carries a perfectly good certificate.
    private const string LegacyActionLink =
        "steam://rungame/730/%owner_steamid%/+csgo_econ_action_preview S%owner_steamid%A%assetid%D123";

    private static SteamDescription Description(string classid, string instanceid, params string?[] actionLinks) => new()
    {
        classid = classid,
        instanceid = instanceid,
        actions = [.. actionLinks.Select(l => new SteamAction { link = l })],
    };

    // Steam leaves the cert as a %propid:6% placeholder in the description-level template; the
    // per-asset value is what makes each copy's link unique.
    private static SteamDescription CertDescription(string classid, string instanceid)
        => Description(classid, instanceid, DecoyActionLink, CertActionLink);

    private static SteamAssetProperties CertProperty(string assetid, CEconItemPreviewDataBlock item) => new()
    {
        assetid = assetid,
        asset_properties = [new SteamAssetProperty { propertyid = 6, string_value = CertHex(item) }],
    };

    private async Task<int> ReadItemsCachedAsync(ulong steamid)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath};foreign keys=true;");
        await connection.OpenAsync();
        using var command = new SqliteCommand("SELECT items_cached FROM inventory_warms WHERE steamid = @steamid", connection);
        command.Parameters.AddWithValue("@steamid", (long)steamid);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    [Theory]
    // A warm from seconds ago: a burst of misses for this owner must cost nothing. 23h pins the
    // other end of the 24h window - shortening the cooldown to an hour would multiply the traffic
    // this service sends Steam, and nothing else in the suite would notice.
    [InlineData(0)]
    [InlineData(23)]
    public async Task WarmWithinCooldown_IsSkippedWithoutFetching(int hoursAgo)
    {
        var db = await NewDbAsync();
        await RecordWarmAtAsync(SteamA, DateTime.UtcNow.AddHours(-hoursAgo), itemsCached: 7);
        var (service, handler, factory) = NewService(db, _ => Ok(EmptyInventory));

        await service.StartAsync(CancellationToken.None);
        service.Enqueue(SteamA);
        service.Enqueue(SteamB);
        // The queue is drained serially in order, so B being done proves A was already considered.
        await WaitForAsync(() => handler.Requested(SteamB), "the second inventory to be warmed");
        await service.StopAsync(CancellationToken.None);

        Assert.False(handler.Requested(SteamA));
        Assert.Single(handler.Requests);
        Assert.Equal("steam", Assert.Single(factory.NamesRequested));
        // The skipped owner keeps its original count - the skip must not overwrite the record.
        Assert.Equal(7, await ReadItemsCachedAsync(SteamA));
    }

    [Fact]
    public async Task WarmOlderThanTheCooldown_IsFetchedAgain()
    {
        // The other half of the cooldown rule: inventories change, so once a day has passed the
        // owner is warmable again. 25h vs the 23h case above brackets the 24h constant from both
        // sides with an hour of margin, so neither test depends on how long the run takes.
        var db = await NewDbAsync();
        await RecordWarmAtAsync(SteamA, DateTime.UtcNow.AddHours(-25), itemsCached: 7);
        var (service, handler, _) = NewService(db, _ => Ok(EmptyInventory));

        await service.StartAsync(CancellationToken.None);
        service.Enqueue(SteamA);
        await WaitForAsync(() => handler.Requested(SteamA), "the expired cooldown to allow a refetch");
        await service.StopAsync(CancellationToken.None);
    }

    // RecordWarmAsync always stamps UtcNow, so an aged record has to be written directly.
    private async Task RecordWarmAtAsync(ulong steamid, DateTime lastWarmed, int itemsCached)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath};foreign keys=true;");
        await connection.OpenAsync();
        using var command = new SqliteCommand(
            @"INSERT OR REPLACE INTO inventory_warms (steamid, last_warmed, items_cached)
              VALUES (@steamid, @last_warmed, @items_cached)", connection);
        command.Parameters.AddWithValue("@steamid", (long)steamid);
        command.Parameters.AddWithValue("@last_warmed", lastWarmed.ToString("o"));
        command.Parameters.AddWithValue("@items_cached", itemsCached);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task RepeatEnqueueOfTheSameOwner_FetchesOnce()
    {
        var db = await NewDbAsync();
        var (service, handler, _) = NewService(db, _ => Ok(EmptyInventory));

        await service.StartAsync(CancellationToken.None);
        // The real trigger: several items from one inventory miss the cache at once.
        service.Enqueue(SteamA);
        service.Enqueue(SteamA);
        service.Enqueue(SteamA);
        service.Enqueue(SteamB);
        await WaitForAsync(() => handler.Requested(SteamB), "the second inventory to be warmed");
        await service.StopAsync(CancellationToken.None);

        // The first warm records itself before the next dequeue checks the cooldown, so the repeats
        // collapse into one fetch.
        Assert.Equal(2, handler.Requests.Count);
        Assert.Single(handler.Requests, r => r.Contains($"/inventory/{SteamA}/"));
    }

    [Fact]
    public async Task CooldownIsRecordedBeforeFetching_SoFailuresAreThrottledToo()
    {
        var db = await NewDbAsync();
        // A private inventory never becomes warmable; retrying it on every miss would be pure waste.
        var (service, handler, _) = NewService(db, _ => Status(HttpStatusCode.Forbidden));

        await service.StartAsync(CancellationToken.None);
        service.Enqueue(SteamA);
        await WaitForAsync(() => handler.Requested(SteamA), "the failing warm attempt");
        await WaitForAsync(async () => await db.GetLastWarmAsync(SteamA) != null, "the attempt to be recorded");
        await service.StopAsync(CancellationToken.None);

        Assert.NotNull(await db.GetLastWarmAsync(SteamA));
        Assert.Equal(0, await ReadItemsCachedAsync(SteamA));
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(10);
        }
        Assert.Fail($"Timed out waiting for: {because}");
    }

    public static TheoryData<HttpStatusCode, bool> UnhappyStatuses() => new()
    {
        // Steam rate limiting the server's IP is the one to expect, with or without Retry-After.
        { HttpStatusCode.TooManyRequests, true },
        { HttpStatusCode.TooManyRequests, false },
        { HttpStatusCode.Forbidden, false },       // private / friends-only inventory
        { HttpStatusCode.InternalServerError, false },
        { HttpStatusCode.BadGateway, false },
    };

    [Theory]
    [MemberData(nameof(UnhappyStatuses))]
    public async Task NonSuccessResponse_DoesNotStopTheDrainLoop(HttpStatusCode status, bool withRetryAfter)
    {
        var db = await NewDbAsync();
        var (service, handler, _) = NewService(db, request =>
        {
            if (request.RequestUri!.ToString().Contains($"/inventory/{SteamA}/"))
            {
                var failure = Status(status);
                if (withRetryAfter)
                {
                    failure.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
                }
                return failure;
            }
            return Ok(EmptyInventory);
        });

        await service.StartAsync(CancellationToken.None);
        service.Enqueue(SteamA);
        service.Enqueue(SteamB);
        // The loop is a single consumer: if the failure escaped, B would never be fetched.
        await WaitForAsync(() => handler.Requested(SteamB), "the next queued inventory after a failure");
        await service.StopAsync(CancellationToken.None);

        Assert.True(handler.Requested(SteamA));
    }

    public static TheoryData<string> UnusableBodies() => new()
    {
        "null",                                  // Steam's answer for a private inventory
        "{\"success\":0}",                       // no assets, no descriptions
        "{\"assets\":[],\"descriptions\":null}", // half an inventory
        "<html>502</html>",                      // proxy error page served with a 200
    };

    [Theory]
    [MemberData(nameof(UnusableBodies))]
    public async Task UnusableBody_DoesNotStopTheDrainLoop(string body)
    {
        var db = await NewDbAsync();
        var (service, handler, _) = NewService(db, request =>
            request.RequestUri!.ToString().Contains($"/inventory/{SteamA}/") ? Ok(body) : Ok(EmptyInventory));

        await service.StartAsync(CancellationToken.None);
        service.Enqueue(SteamA);
        service.Enqueue(SteamB);
        await WaitForAsync(() => handler.Requested(SteamB), "the next queued inventory after a bad body");
        await service.StopAsync(CancellationToken.None);

        Assert.True(handler.Requested(SteamA));
    }

    // Warms one owner and waits for a second to be fetched behind it. The queue has a single
    // consumer, so the fence proves the warm under test ran to completion - including one that
    // cached nothing, which polling the database could never tell apart from one that never
    // started, and which is exactly the outcome the selection tests below need to observe.
    private async Task WarmBehindFenceAsync(InventoryWarmService service, StubHandler handler, ulong steamid)
    {
        await service.StartAsync(CancellationToken.None);
        service.Enqueue(steamid);
        service.Enqueue(SteamB);
        await WaitForAsync(() => handler.Requested(SteamB), "the fence inventory queued behind the warm under test");
        await service.StopAsync(CancellationToken.None);
    }

    // Serves `inventory` to SteamA and an empty inventory to the fence owner.
    private Func<HttpRequestMessage, HttpResponseMessage> ServeToA(SteamInventoryResponse inventory)
    {
        var body = JsonSerializer.Serialize(inventory);
        return request => request.RequestUri!.ToString().Contains($"/inventory/{SteamA}/")
            ? Ok(body)
            : Ok(EmptyInventory);
    }

    [Fact]
    public async Task OnlyCertificateBearingItemsWithARealItemidArePersisted()
    {
        var db = await NewDbAsync();
        var skin = new CEconItemPreviewDataBlock
        {
            itemid = 41001, defindex = 7, paintindex = 282, rarity = 5, quality = 4,
            paintwear = 1065353216, paintseed = 661, inventory = 3, origin = 8,
        };
        // Every excluded asset carries a real, decodable certificate of its own, so the only thing
        // that can keep it out of the database is the rule its comment names. (Handing certs only
        // to the assets that were meant to survive would have made this test pass no matter what
        // the description/link filtering did.)
        var legacyLinked = new CEconItemPreviewDataBlock { itemid = 41002, defindex = 7, paintindex = 282, paintseed = 2 };
        var notInspectable = new CEconItemPreviewDataBlock { itemid = 41003, defindex = 7, paintindex = 282, paintseed = 3 };
        var undescribed = new CEconItemPreviewDataBlock { itemid = 41005, defindex = 7, paintindex = 282, paintseed = 5 };
        // Music kits, graffiti and the like decode with itemid 0 and would all collide on the
        // searches primary key, so they must be dropped even though the cert parses fine.
        var nonPaint = new CEconItemPreviewDataBlock { itemid = 0, defindex = 1314, rarity = 3, quality = 4 };

        var inventory = new SteamInventoryResponse
        {
            assets =
            [
                new SteamAsset { assetid = "1", classid = "c1", instanceid = "i1" },  // cert-bearing skin
                new SteamAsset { assetid = "2", classid = "c2", instanceid = "i2" },  // legacy S/A/D link
                new SteamAsset { assetid = "3", classid = "c3", instanceid = "i3" },  // no inspect action
                new SteamAsset { assetid = "4", classid = "c4", instanceid = "i4" },  // itemid 0 cert
                new SteamAsset { assetid = "5", classid = "gone", instanceid = "i5" },// no description at all
            ],
            descriptions =
            [
                CertDescription("c1", "i1"),
                Description("c2", "i2", LegacyActionLink),
                // A linkless action and a non-inspect one: neither yields an inspect URL, and the
                // null must not throw while being ruled out.
                Description("c3", "i3", null, DecoyActionLink),
                CertDescription("c4", "i4"),
            ],
            asset_properties =
            [
                CertProperty("1", skin),
                CertProperty("2", legacyLinked),
                CertProperty("3", notInspectable),
                CertProperty("4", nonPaint),
                CertProperty("5", undescribed),
            ],
            total = 5,
        };

        var (service, handler, _) = NewService(db, ServeToA(inventory));
        await WarmBehindFenceAsync(service, handler, SteamA);

        Assert.Equal(1, await ReadItemsCachedAsync(SteamA));
        var cached = await db.GetItemAsync(41001);
        Assert.NotNull(cached);
        Assert.Equal(282u, cached.paintindex);
        Assert.Equal(661u, cached.paintseed);
        // The masked link needs the game coordinator, the third has no inspect action to build a
        // link from, and the fifth's description is missing - their certs are never even reached.
        Assert.Null(await db.GetItemAsync(41002));
        Assert.Null(await db.GetItemAsync(41003));
        Assert.Null(await db.GetItemAsync(41005));
        // The zero-itemid item would have taken the key 0 row and been served for every other
        // non-paint lookup.
        Assert.Null(await db.GetItemAsync(0));
    }

    [Fact]
    public async Task DescriptionsAreMatchedOnClassidAndInstanceid()
    {
        // Two copies of one class differing only by instanceid is the normal shape for an item
        // whose description varies (name tag, applied stickers). Their descriptions differ in the
        // only way that matters here - one offers a locally decodable cert template, the other only
        // a masked link - so a lookup that ignored instanceid (or matched anything at all) would
        // pair both assets with the first description and cache nothing.
        //
        // NOTE: this pins the (classid, instanceid) pairing, not how it is looked up. The per-asset
        // FirstOrDefault scan is O(assets x descriptions) and is owned by a later wave; a
        // dictionary keyed on the same pair must keep this test green.
        var db = await NewDbAsync();
        var masked = new CEconItemPreviewDataBlock { itemid = 42001, defindex = 7, paintindex = 282, paintseed = 1 };
        var certified = new CEconItemPreviewDataBlock { itemid = 42002, defindex = 7, paintindex = 282, paintseed = 2 };

        var inventory = new SteamInventoryResponse
        {
            assets =
            [
                new SteamAsset { assetid = "10", classid = "shared", instanceid = "masked" },
                new SteamAsset { assetid = "20", classid = "shared", instanceid = "certified" },
            ],
            descriptions =
            [
                Description("shared", "masked", LegacyActionLink),
                CertDescription("shared", "certified"),
            ],
            asset_properties = [CertProperty("10", masked), CertProperty("20", certified)],
            total = 2,
        };

        var (service, handler, _) = NewService(db, ServeToA(inventory));
        await WarmBehindFenceAsync(service, handler, SteamA);

        Assert.Equal(1, await ReadItemsCachedAsync(SteamA));
        // Asset 20 got its own description's cert template, and asset 10 got its own masked link -
        // neither borrowed the other's.
        Assert.Equal(2u, (await db.GetItemAsync(42002))!.paintseed);
        Assert.Null(await db.GetItemAsync(42001));
    }

    [Fact]
    public async Task TruncatedInventory_StillWarmsTheFirstPage()
    {
        // The warmer fetches a single count=2000 page, so a maxed inventory is only partly warmed.
        // That is accepted (it is best-effort), but it must warm what it did get rather than
        // treating a short page as a failure.
        var db = await NewDbAsync();
        var skin = new CEconItemPreviewDataBlock { itemid = 43001, defindex = 7, paintindex = 44, paintseed = 9 };
        var inventory = new SteamInventoryResponse
        {
            assets = [new SteamAsset { assetid = "1", classid = "c1", instanceid = "i1" }],
            descriptions = [CertDescription("c1", "i1")],
            asset_properties = [CertProperty("1", skin)],
            total = 2500, // more than this page carries
        };
        var (service, handler, _) = NewService(db, ServeToA(inventory));
        await WarmBehindFenceAsync(service, handler, SteamA);

        Assert.Equal(1, await ReadItemsCachedAsync(SteamA));
        Assert.NotNull(await db.GetItemAsync(43001));
    }

    [Fact]
    public async Task Queue_DropsBeyondCapacity_RatherThanGrowingUnbounded()
    {
        // Capacity 256, DropWrite. A flood of misses (a crawler, or one popular trade thread) must
        // cost a bounded amount of work; the dropped ids re-enqueue on their next miss anyway.
        const int capacity = 256;
        const int flood = 320;
        var db = await NewDbAsync();
        // 403 keeps each warm to a single request and a single row, so this stays fast.
        var (service, handler, _) = NewService(db, _ => Status(HttpStatusCode.Forbidden));

        // Nothing is draining yet, so all 320 writes race the bound rather than the reader.
        for (var i = 0; i < flood; i++)
        {
            service.Enqueue(SteamA + (ulong)i);
        }

        await service.StartAsync(CancellationToken.None);
        await WaitForAsync(() => handler.Requests.Count >= capacity, "the queued backlog to drain", timeoutSeconds: 60);

        // By now the reader has taken everything it accepted, so this write has room - and it acts
        // as a fence: once the sentinel is fetched, anything still queued would already have been.
        var sentinel = SteamA + 100000;
        service.Enqueue(sentinel);
        await WaitForAsync(() => handler.Requested(sentinel), "the sentinel enqueued after the backlog drained");
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(capacity + 1, handler.Requests.Count);
        // DropWrite drops the newest writes, so the tail of the flood was never queued.
        Assert.False(handler.Requested(SteamA + flood - 1));
        Assert.True(handler.Requested(SteamA));
    }
}
