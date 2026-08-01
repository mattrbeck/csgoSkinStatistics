using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CSGOSkinAPI.Services;
using Xunit;

namespace csgoSkinStatistics.Tests.Services;

// PriceService is the only thing between the Skinport feed and every price shown in the UI, and
// most of what it does is inference: a variant that never listed borrows its neighbour's price, a
// variant that dropped out of the feed keeps its last one. Those two paths are invisible in the
// response apart from the Approximate flag, so they are pinned here.
//
// No test touches the network: the "skinport" client is always built from a stubbed handler, and
// every database lives in its own temp file (never the test working directory - a shared file there
// has flaked this suite before).
public class PriceServiceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"pricesvc_{Guid.NewGuid():N}.db");
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        // WAL leaves -wal/-shm siblings next to the database.
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // Returns a canned response per request, and records what was asked for so a test can prove a
    // path made no network call at all.
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly Lock _gate = new();
        private readonly List<string> _requests = [];

        public IReadOnlyList<string> Requests
        {
            get { lock (_gate) return _requests.ToList(); }
        }

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

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Feed(params (string Name, double? Min, double? Suggested)[] items)
        => "[" + string.Join(",", items.Select(i =>
            $"{{\"market_hash_name\":\"{i.Name}\"," +
            $"\"min_price\":{(i.Min is double m ? m.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null")}," +
            $"\"suggested_price\":{(i.Suggested is double s ? s.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null")}}}")) + "]";

    private async Task<DatabaseService> NewDbAsync()
    {
        var db = new DatabaseService(_dbPath);
        await db.InitializeDatabaseAsync();
        return db;
    }

    private (PriceService Service, StubHandler Handler, StubClientFactory Factory) NewService(
        DatabaseService db, Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StubHandler(responder);
        var factory = new StubClientFactory(handler);
        var service = new PriceService(factory, db);
        _disposables.Add(handler);
        _disposables.Add(service);
        return (service, handler, factory);
    }

    // Load a feed into a service the way production does, so the tests below exercise the real
    // parse/merge path rather than a hand-populated map.
    private async Task<PriceService> ServiceWithFeedAsync(params (string Name, double? Min, double? Suggested)[] items)
    {
        var db = await NewDbAsync();
        var (service, _, _) = NewService(db, _ => Json(Feed(items)));
        await service.RefreshAsync(CancellationToken.None);
        return service;
    }

    // Seed prices with a chosen age. Timestamps come from the DB snapshot, which is the only way
    // (short of waiting a week) to observe the staleness rule - and it exercises the real load path.
    private async Task<PriceService> ServiceWithAgedPricesAsync(
        params (string Name, int? Min, int? Suggested, TimeSpan Age)[] items)
    {
        var db = await NewDbAsync();
        foreach (var group in items.GroupBy(i => i.Age))
        {
            await db.SavePricesAsync(
                group.ToDictionary(i => i.Name, i => (i.Min, i.Suggested)),
                DateTime.UtcNow - group.Key);
        }
        var (service, _, _) = NewService(db, _ => throw new InvalidOperationException("no feed expected"));
        await service.LoadPersistedPricesAsync();
        return service;
    }

    private static async Task WaitForAsync(Func<bool> condition, string because)
    {
        // Polls instead of sleeping a fixed amount: the background loop's first pass is fast, and a
        // fixed sleep would be both slower and flakier.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.Fail($"Timed out waiting for: {because}");
    }

    [Fact]
    public async Task Resolve_ExactName_ReturnsFeedPrice()
    {
        var service = await ServiceWithFeedAsync(("AK-47 | Redline (Field-Tested)", 12.34, 15.50));

        var result = service.Resolve("AK-47 | Redline (Field-Tested)");

        Assert.NotNull(result);
        Assert.Equal(1234, result.MinCents);
        Assert.Equal(1550, result.SuggestedCents);
        // Just fetched, so nothing about it is a guess.
        Assert.False(result.Approximate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Resolve_NullOrEmptyName_ReturnsNull(string? name)
    {
        var service = await ServiceWithFeedAsync(("AK-47 | Redline (Field-Tested)", 12.34, 15.50));

        Assert.Null(service.Resolve(name));
    }

    [Fact]
    public async Task Resolve_UnknownName_ReturnsNull()
    {
        var service = await ServiceWithFeedAsync(("AK-47 | Redline (Field-Tested)", 12.34, 15.50));

        // No entry and no wear suffix to fall back from: the caller must be told "no price", not
        // handed some other item's.
        Assert.Null(service.Resolve("Sticker | Katowice 2014"));
    }

    [Fact]
    public async Task Resolve_ExactHit_IsApproximateOnlyOnceOverAWeekOld()
    {
        var service = await ServiceWithAgedPricesAsync(
            ("AWP | Asiimov (Field-Tested)", 4000, 4500, TimeSpan.FromDays(8)),
            ("AK-47 | Redline (Field-Tested)", 1200, 1500, TimeSpan.FromDays(6)));

        var stale = service.Resolve("AWP | Asiimov (Field-Tested)");
        var fresh = service.Resolve("AK-47 | Redline (Field-Tested)");

        Assert.NotNull(stale);
        Assert.NotNull(fresh);
        // Both still carry their last-known value; only the week-old one is flagged.
        Assert.Equal(4500, stale.SuggestedCents);
        Assert.True(stale.Approximate);
        Assert.Equal(1500, fresh.SuggestedCents);
        Assert.False(fresh.Approximate);
    }

    [Fact]
    public async Task Resolve_VariantNeverListed_FallsBackToNearestWearOfSameSkin()
    {
        // Battle-Scarred was never listed; Well-Worn is its immediate neighbour.
        var service = await ServiceWithFeedAsync(
            ("AK-47 | Redline (Field-Tested)", 12.00, 15.00),
            ("AK-47 | Redline (Well-Worn)", 10.00, 11.00));

        var result = service.Resolve("AK-47 | Redline (Battle-Scarred)");

        Assert.NotNull(result);
        Assert.Equal(1100, result.SuggestedCents);
        Assert.Equal(1000, result.MinCents);
        // A neighbour's price is always a guess, however fresh the feed is.
        Assert.True(result.Approximate);
    }

    [Fact]
    public async Task NearestWear_PicksTheClosestWear_NotTheFirstPriced()
    {
        // Factory New is missing. Battle-Scarred (distance 4) is listed first in the feed, but
        // Field-Tested (distance 2) is the closer float and must win.
        var service = await ServiceWithFeedAsync(
            ("AK-47 | Redline (Battle-Scarred)", 5.00, 6.00),
            ("AK-47 | Redline (Field-Tested)", 12.00, 15.00));

        var result = service.Resolve("AK-47 | Redline (Factory New)");

        Assert.NotNull(result);
        Assert.Equal(1500, result.SuggestedCents);
        Assert.True(result.Approximate);
    }

    [Fact]
    public async Task NearestWear_Tie_ResolvesTowardTheBetterWear()
    {
        // The documented tie-break, and the easiest thing here to regress: Minimal Wear and
        // Well-Worn are both one step from Field-Tested. WearOrder is scanned front to back with a
        // strict <, so the lower-float side wins - guessing high on a missing variant is the
        // deliberate choice. Feed order deliberately lists the worse wear first so a scan that
        // took the last equal candidate would fail here.
        var service = await ServiceWithFeedAsync(
            ("AK-47 | Redline (Well-Worn)", 8.00, 9.00),
            ("AK-47 | Redline (Minimal Wear)", 20.00, 22.00));

        var result = service.Resolve("AK-47 | Redline (Field-Tested)");

        Assert.NotNull(result);
        Assert.Equal(2200, result.SuggestedCents);
        Assert.Equal(2000, result.MinCents);
        Assert.True(result.Approximate);
    }

    [Fact]
    public async Task Resolve_NameWithoutWearSuffix_HasNoNearestWearFallback()
    {
        // A vanilla knife has no wear in its name, so there is no sibling to borrow from even
        // though the same base name is priced with wears.
        var service = await ServiceWithFeedAsync(
            ("★ Karambit | Doppler (Factory New)", 900.00, 1000.00));

        Assert.Null(service.Resolve("★ Karambit"));
        Assert.Null(service.Resolve("★ Karambit | Doppler"));
    }

    [Fact]
    public async Task NearestWear_StaysWithinTheStatTrakVariant()
    {
        // The fallback keys on the full base name, so a StatTrak lookup must never quietly borrow
        // the (much cheaper) plain variant's price.
        var service = await ServiceWithFeedAsync(
            ("AK-47 | Redline (Field-Tested)", 12.00, 15.00),
            ("AK-47 | Redline (Minimal Wear)", 20.00, 22.00));

        Assert.Null(service.Resolve("StatTrak™ AK-47 | Redline (Field-Tested)"));
    }

    [Fact]
    public async Task Resolve_StaleExactValue_IsPreferredOverANearestWearGuess()
    {
        // The class comment claims a kept value beats a neighbour's; verify it holds even when the
        // kept value is old enough to be flagged and the neighbour is fresh.
        var service = await ServiceWithAgedPricesAsync(
            ("AK-47 | Redline (Field-Tested)", 1200, 1500, TimeSpan.FromDays(30)),
            ("AK-47 | Redline (Minimal Wear)", 2000, 2200, TimeSpan.FromMinutes(1)));

        var result = service.Resolve("AK-47 | Redline (Field-Tested)");

        Assert.NotNull(result);
        Assert.Equal(1500, result.SuggestedCents);
        Assert.True(result.Approximate);
    }

    [Fact]
    public async Task Resolve_ExactRowWithNoSuggestedPrice_FallsBackToNearestWear()
    {
        // Skinport can carry a row with no suggested price at all. That is not a usable display
        // price, so the neighbour is a better answer than showing nothing.
        var service = await ServiceWithFeedAsync(
            ("AK-47 | Redline (Field-Tested)", 12.00, null),
            ("AK-47 | Redline (Minimal Wear)", 20.00, 22.00));

        var result = service.Resolve("AK-47 | Redline (Field-Tested)");

        Assert.NotNull(result);
        Assert.Equal(2200, result.SuggestedCents);
        Assert.True(result.Approximate);
    }

    [Fact]
    public async Task Resolve_MinPriceNull_IsCarriedThroughAsNull()
    {
        // min_price is null when nothing is listed. That has to stay null rather than becoming 0,
        // which would render as a free item.
        var service = await ServiceWithFeedAsync(("AK-47 | Redline (Field-Tested)", null, 15.00));

        var result = service.Resolve("AK-47 | Redline (Field-Tested)");

        Assert.NotNull(result);
        Assert.Null(result.MinCents);
        Assert.Equal(1500, result.SuggestedCents);
    }

    [Fact]
    public async Task RefreshAsync_UsesTheSkinportNamedClient()
    {
        // The named client is what carries AutomaticDecompression; the feed is Brotli-only and a
        // plain client 406s, so the name is load-bearing.
        var db = await NewDbAsync();
        var (service, handler, factory) = NewService(db, _ => Json(Feed(("AK-47 | Redline (Field-Tested)", 1.00, 2.00))));

        await service.RefreshAsync(CancellationToken.None);

        Assert.Equal("skinport", Assert.Single(factory.NamesRequested));
        Assert.Contains("api.skinport.com", Assert.Single(handler.Requests));
    }

    [Fact]
    public async Task RefreshAsync_PersistsTheFeedAndStampsUpdatedAt()
    {
        var db = await NewDbAsync();
        var (service, _, _) = NewService(db, _ => Json(Feed(
            ("AK-47 | Redline (Field-Tested)", 12.34, 15.50),
            ("", 1.00, 2.00))));

        Assert.Null(service.UpdatedAtUtc); // nothing loaded yet
        var before = DateTime.UtcNow;
        await service.RefreshAsync(CancellationToken.None);

        Assert.NotNull(service.UpdatedAtUtc);
        Assert.InRange(service.UpdatedAtUtc.Value, before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));

        // A restart has to serve these immediately, so they must be on disk - and the nameless row
        // must not be, since an empty key would collide across items.
        var persisted = await db.LoadPricesAsync();
        Assert.Equal(1550, persisted["AK-47 | Redline (Field-Tested)"].SuggestedCents);
        Assert.DoesNotContain("", persisted.Keys);
    }

    [Fact]
    public async Task LoadPersistedPricesAsync_UsesTheNewestRowAsUpdatedAt()
    {
        // Rows age individually (a delisted item keeps its old stamp), so the service's "last
        // refreshed" time is the newest row, not the oldest.
        var service = await ServiceWithAgedPricesAsync(
            ("AK-47 | Redline (Field-Tested)", 1200, 1500, TimeSpan.FromDays(30)),
            ("AWP | Asiimov (Field-Tested)", 4000, 4500, TimeSpan.FromHours(2)));

        Assert.NotNull(service.UpdatedAtUtc);
        Assert.InRange(service.UpdatedAtUtc.Value,
            DateTime.UtcNow.AddHours(-3), DateTime.UtcNow.AddHours(-1));
    }

    [Fact]
    public async Task LoadPersistedPricesAsync_EmptyDatabase_LeavesServiceUnloaded()
    {
        var db = await NewDbAsync();
        var (service, _, _) = NewService(db, _ => Json("[]"));

        await service.LoadPersistedPricesAsync();

        // Nothing persisted yet must read as "never loaded" so the loop treats it as a cold start.
        Assert.Null(service.UpdatedAtUtc);
        Assert.Null(service.Resolve("AK-47 | Redline (Field-Tested)"));
    }

    [Fact]
    public async Task LoadPersistedPricesAsync_UnreadableDatabase_IsSurvivable()
    {
        // The snapshot is an optimisation, not a dependency: if the DB is missing or locked, the
        // service must still come up and refresh from the feed rather than fault its host loop.
        var unopenable = new DatabaseService(Path.GetTempPath()); // a directory, not a file
        var (service, _, _) = NewService(unopenable, _ => Json(Feed(("AK-47 | Redline (Field-Tested)", 12.00, 15.00))));

        await service.LoadPersistedPricesAsync();

        Assert.Null(service.UpdatedAtUtc);
        // The same is true of the write back: the in-memory map is swapped before the save, so a
        // failing persist still leaves this process fully priced.
        await service.RefreshAsync(CancellationToken.None);
        Assert.Equal(1500, service.Resolve("AK-47 | Redline (Field-Tested)")!.SuggestedCents);
    }

    [Fact]
    public async Task RefreshAsync_DelistedItem_KeepsItsLastKnownValue()
    {
        var db = await NewDbAsync();
        var responses = new Queue<string>([
            Feed(("AK-47 | Redline (Field-Tested)", 12.00, 15.00), ("AWP | Asiimov (Field-Tested)", 40.00, 45.00)),
            Feed(("AK-47 | Redline (Field-Tested)", 13.00, 16.00)),
        ]);
        var (service, _, _) = NewService(db, _ => Json(responses.Dequeue()));

        await service.RefreshAsync(CancellationToken.None);
        await service.RefreshAsync(CancellationToken.None);

        // The Asiimov left the feed; merging (rather than replacing) is what keeps it priced.
        var delisted = service.Resolve("AWP | Asiimov (Field-Tested)");
        Assert.NotNull(delisted);
        Assert.Equal(4500, delisted.SuggestedCents);
        Assert.False(delisted.Approximate); // aged out of the feed, but not yet a week old
        Assert.Equal(1600, service.Resolve("AK-47 | Redline (Field-Tested)")!.SuggestedCents);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]  // Skinport does not always send Retry-After; the handler must not need it
    public async Task RefreshAsync_RateLimited_KeepsCurrentPrices(bool withRetryAfter)
    {
        var db = await NewDbAsync();
        var first = true;
        var (service, _, _) = NewService(db, _ =>
        {
            if (first)
            {
                first = false;
                return Json(Feed(("AK-47 | Redline (Field-Tested)", 12.00, 15.00)));
            }
            var rateLimited = new HttpResponseMessage((HttpStatusCode)429);
            if (withRetryAfter)
            {
                rateLimited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            }
            return rateLimited;
        });

        await service.RefreshAsync(CancellationToken.None);
        var loadedAt = service.UpdatedAtUtc;
        await service.RefreshAsync(CancellationToken.None);

        // A 429 must never be mistaken for "no items"; the previous map has to survive intact.
        Assert.Equal(1500, service.Resolve("AK-47 | Redline (Field-Tested)")!.SuggestedCents);
        Assert.Equal(loadedAt, service.UpdatedAtUtc);
    }

    public static TheoryData<string, HttpStatusCode> BadFeeds() => new()
    {
        // An empty array is what Skinport returns during its own outages; replacing the map with it
        // would blank every price on the site.
        { "[]", HttpStatusCode.OK },
        { "null", HttpStatusCode.OK },
        // Truncated / non-JSON body: a proxy error page, or a cut-off response.
        { "<html>502 Bad Gateway</html>", HttpStatusCode.OK },
        { "[{\"market_hash_name\":", HttpStatusCode.OK },
        // A server error never reaches the parser, but must be just as harmless.
        { "", HttpStatusCode.InternalServerError },
        { "", HttpStatusCode.Forbidden },
    };

    [Theory]
    [MemberData(nameof(BadFeeds))]
    public async Task RefreshAsync_BadFeed_KeepsKnownPricesAndDoesNotThrow(string body, HttpStatusCode status)
    {
        var db = await NewDbAsync();
        var first = true;
        var (service, _, _) = NewService(db, _ =>
        {
            if (first)
            {
                first = false;
                return Json(Feed(("AK-47 | Redline (Field-Tested)", 12.00, 15.00)));
            }
            return Json(body, status);
        });

        await service.RefreshAsync(CancellationToken.None);
        var loadedAt = service.UpdatedAtUtc;
        await service.RefreshAsync(CancellationToken.None); // must not throw

        Assert.Equal(1500, service.Resolve("AK-47 | Redline (Field-Tested)")!.SuggestedCents);
        // A failed refresh must not advance the timestamp either, or a permanently broken feed
        // would keep reporting itself as fresh.
        Assert.Equal(loadedAt, service.UpdatedAtUtc);
    }

    [Fact]
    public async Task RefreshAsync_TransportFailure_IsSwallowed()
    {
        // DNS/TLS failures surface as an exception from GetAsync rather than a status code, and
        // this runs on a background loop with nobody to catch it.
        var db = await NewDbAsync();
        var (service, _, _) = NewService(db, _ => throw new HttpRequestException("no such host"));

        await service.RefreshAsync(CancellationToken.None);

        Assert.Null(service.UpdatedAtUtc);
        Assert.Null(service.Resolve("AK-47 | Redline (Field-Tested)"));
    }

    [Fact]
    public async Task ExecuteAsync_ColdStart_FetchesTheFeedAndServesIt()
    {
        var db = await NewDbAsync();
        var (service, handler, _) = NewService(db, _ => Json(Feed(("AK-47 | Redline (Field-Tested)", 12.00, 15.00))));

        await service.StartAsync(CancellationToken.None);
        await WaitForAsync(() => service.UpdatedAtUtc != null, "the background loop's first refresh");
        await service.StopAsync(CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Equal(1500, service.Resolve("AK-47 | Redline (Field-Tested)")!.SuggestedCents);
    }

    [Fact]
    public async Task ExecuteAsync_FreshPersistedSnapshot_SkipsTheStartupFetch()
    {
        // Restarts are the common case (deploys), and the feed is rate limited to 8 requests per 5
        // minutes - so a snapshot younger than the refresh interval must not trigger a fetch.
        var db = await NewDbAsync();
        await db.SavePricesAsync(
            new Dictionary<string, (int?, int?)> { ["AK-47 | Redline (Field-Tested)"] = (1200, 1500) },
            DateTime.UtcNow.AddMinutes(-5));
        var (service, handler, _) = NewService(db, _ => Json(Feed(("AK-47 | Redline (Field-Tested)", 99.00, 99.00))));

        await service.StartAsync(CancellationToken.None);
        await WaitForAsync(() => service.UpdatedAtUtc != null, "the persisted snapshot to load");
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(handler.Requests);
        Assert.Equal(1500, service.Resolve("AK-47 | Redline (Field-Tested)")!.SuggestedCents);
    }

    [Fact]
    public async Task ExecuteAsync_StalePersistedSnapshot_IsServedWhileTheRefreshRuns()
    {
        var db = await NewDbAsync();
        await db.SavePricesAsync(
            new Dictionary<string, (int?, int?)> { ["AK-47 | Redline (Field-Tested)"] = (1200, 1500) },
            DateTime.UtcNow.AddDays(-2));
        var (service, handler, _) = NewService(db, _ => Json(Feed(("AK-47 | Redline (Field-Tested)", 20.00, 25.00))));

        await service.StartAsync(CancellationToken.None);
        await WaitForAsync(() => handler.Requests.Count == 1, "the startup refresh of a stale snapshot");
        await WaitForAsync(() => service.Resolve("AK-47 | Redline (Field-Tested)")!.SuggestedCents == 2500,
            "the feed to replace the stale snapshot");
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_FailingFeedOnColdStart_LeavesTheServiceUnloadedWithoutCrashing()
    {
        // The site has no prices at all in this state; what matters is that the hosted service
        // survives to retry rather than faulting the loop.
        var db = await NewDbAsync();
        var (service, handler, _) = NewService(db, _ => Json("", HttpStatusCode.ServiceUnavailable));

        await service.StartAsync(CancellationToken.None);
        await WaitForAsync(() => handler.Requests.Count >= 1, "the first failed refresh attempt");
        await service.StopAsync(CancellationToken.None);

        Assert.Null(service.UpdatedAtUtc);
        Assert.Null(service.Resolve("AK-47 | Redline (Field-Tested)"));
    }
}
