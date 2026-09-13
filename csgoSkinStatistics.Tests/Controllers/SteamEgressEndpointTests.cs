using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CSGOSkinAPI.Models;
using Xunit;

namespace csgoSkinStatistics.Tests.Controllers;

// The process-wide Steam egress gate as seen from the outside: what a viewer gets while Steam has
// paused us, while the queue is full, and what /health says about each. Split into two classes
// because a pause is host-wide state that outlives the test which armed it (one factory serves a
// class), so the tests that arm one live apart from the tests that need the gate open.
internal static class EgressFixtures
{
    private const string OwnerTemplate =
        "steam://rungame/730/%owner_steamid%/+csgo_econ_action_preview S%owner_steamid%A%assetid%D123";

    private static int _nextId = 700_000;
    public static ulong NextSteamId() => 76561198000000000UL + (ulong)Interlocked.Increment(ref _nextId);

    public static string InventoryUrl(ulong steamId) => $"steamcommunity.com/inventory/{steamId}/730/2";

    // The smallest inventory the endpoint accepts: one asset whose description carries an inspect
    // action, which the item cache has never seen, so it renders undecoded.
    public static string OneItemInventory() => JsonSerializer.Serialize(new SteamInventoryResponse
    {
        total = 1,
        success = 1,
        assets = [new() { appid = 730, contextid = "2", assetid = "5001", classid = "C1", instanceid = "I1", amount = "1" }],
        descriptions =
        [
            new()
            {
                classid = "C1",
                instanceid = "I1",
                name = "AWP | Asiimov",
                actions = [new() { name = "Inspect in Game...", link = OwnerTemplate }],
            },
        ],
    });

    public static HttpResponseMessage RateLimited(TimeSpan? retryAfter)
    {
        var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json"),
        };
        if (retryAfter is TimeSpan delay)
        {
            throttled.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        }
        return throttled;
    }

    public static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}

public class SteamEgressPauseTests : IClassFixture<ApiFactory>, IDisposable
{
    private readonly ApiFactory _factory;

    public SteamEgressPauseTests(ApiFactory factory)
    {
        _factory = factory;
        // A real pause (ApiFactory disables them by default), long enough to outlast the class.
        _factory.Settings["SteamEgress:MaxPauseSeconds"] = "120";
        _factory.Settings["SteamEgress:PauseOnRateLimitSeconds"] = "120";
    }

    public void Dispose() => _factory.ResetPerTestState();

    [Fact]
    public async Task A429_PausesFetchesForEveryone_AndHealthSaysSo()
    {
        var throttledId = EgressFixtures.NextSteamId();
        var bystanderId = EgressFixtures.NextSteamId();
        _factory.Http.Respond(EgressFixtures.InventoryUrl(throttledId), () => EgressFixtures.RateLimited(TimeSpan.FromSeconds(60)));
        _factory.Http.Respond(EgressFixtures.InventoryUrl(bystanderId), HttpStatusCode.OK, EgressFixtures.OneItemInventory());
        var client = _factory.CreateClient();

        var throttled = await client.GetAsync($"/api/inventory?steamid={throttledId}");
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);

        // A different inventory, never fetched before, with a perfectly good stub waiting: refused
        // without a single outbound request, because the pause is per process, not per inventory.
        var bystander = await client.GetAsync($"/api/inventory?steamid={bystanderId}");
        Assert.Equal(HttpStatusCode.TooManyRequests, bystander.StatusCode);
        Assert.Contains("rate limiting", (await EgressFixtures.ReadJson(bystander)).GetProperty("error").GetString());
        Assert.Equal(0, _factory.Http.RequestsMatching(EgressFixtures.InventoryUrl(bystanderId)));

        var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
        var body = await EgressFixtures.ReadJson(health);
        Assert.Equal("degraded", body.GetProperty("status").GetString());
        var steam = body.GetProperty("steam_inventory");
        Assert.False(steam.GetProperty("ok").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, steam.GetProperty("paused_until").ValueKind);
    }

    [Fact]
    public async Task WhileSteamRefuses_TheLastGoodCopyIsServedMarkedStale()
    {
        var steamId = EgressFixtures.NextSteamId();
        // The host (and with it the schema) exists only once a client has been created.
        var client = _factory.CreateClient();
        // What an earlier successful fetch would have stored: the exact bytes it returned.
        var earlier = JsonSerializer.SerializeToUtf8Bytes(new
        {
            total = 1,
            truncated = false,
            success = 1,
            steamid = steamId.ToString(),
            csgo_items = new[] { new { name = "AWP | Asiimov", asset_id = "5001" } },
        });
        await _factory.Database.SaveInventorySnapshotAsync(steamId, earlier, retentionDays: 7, maxRows: 2000);
        _factory.Http.Respond(EgressFixtures.InventoryUrl(steamId), () => EgressFixtures.RateLimited(null));

        var response = await client.GetAsync($"/api/inventory?steamid={steamId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await EgressFixtures.ReadJson(response);
        Assert.True(body.GetProperty("stale").GetBoolean());
        Assert.True(DateTime.TryParse(body.GetProperty("fetched_at").GetString(), out _));
        Assert.Equal("AWP | Asiimov", body.GetProperty("csgo_items")[0].GetProperty("name").GetString());
        Assert.Equal(steamId.ToString(), body.GetProperty("steamid").GetString());

        // And a reload inside the stale window is answered from memory, not by asking Steam again.
        var again = await client.GetAsync($"/api/inventory?steamid={steamId}");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.True(_factory.Http.RequestsMatching(EgressFixtures.InventoryUrl(steamId)) <= 1);
    }

    [Fact]
    public async Task WithoutASnapshot_ARefusalIsStillTheHonestError()
    {
        var steamId = EgressFixtures.NextSteamId();
        _factory.Http.Respond(EgressFixtures.InventoryUrl(steamId), () => EgressFixtures.RateLimited(null));
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/inventory?steamid={steamId}");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Contains("rate limiting", (await EgressFixtures.ReadJson(response)).GetProperty("error").GetString());
    }
}

public class SteamEgressQueueTests : IClassFixture<ApiFactory>, IDisposable
{
    private readonly ApiFactory _factory;

    public SteamEgressQueueTests(ApiFactory factory)
    {
        _factory = factory;
        // One caller may queue behind the fetch in flight; a second is refused at once. The wait
        // is short so a test that does queue never stalls the class.
        _factory.Settings["SteamEgress:MaxWaiters"] = "1";
        _factory.Settings["SteamEgress:MaxWaitSeconds"] = "5";
    }

    public void Dispose() => _factory.ResetPerTestState();

    [Fact]
    public async Task Health_IsOk_WhenSteamIsAnsweringAndNoAccountsAreConfigured()
    {
        var client = _factory.CreateClient();
        var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var body = await EgressFixtures.ReadJson(health);
        Assert.Equal("ok", body.GetProperty("status").GetString());
        // The test double has no bot accounts; "none configured" is not "none online".
        Assert.True(body.GetProperty("game_coordinator").GetProperty("ok").GetBoolean());
        Assert.Equal(0, body.GetProperty("game_coordinator").GetProperty("accounts").GetInt32());
    }

    [Fact]
    public async Task QueueFull_Answers503Busy_WithoutFetching_AndTheQueuedOnesStillComplete()
    {
        var inFlightId = EgressFixtures.NextSteamId();
        var queuedId = EgressFixtures.NextSteamId();
        var refusedId = EgressFixtures.NextSteamId();
        foreach (var id in new[] { inFlightId, queuedId, refusedId })
        {
            _factory.Http.Respond(EgressFixtures.InventoryUrl(id), HttpStatusCode.OK, EgressFixtures.OneItemInventory());
        }
        // Build the host before installing the hold: the hold applies to every stubbed request,
        // and the host's startup waits on the (stubbed) Skinport feed.
        var client = _factory.CreateClient();
        var release = new TaskCompletionSource();
        var held = new TaskCompletionSource();
        _factory.Http.Hold = async () =>
        {
            held.TrySetResult();
            await release.Task;
        };

        var inFlight = client.GetAsync($"/api/inventory?steamid={inFlightId}");
        await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = client.GetAsync($"/api/inventory?steamid={queuedId}");
        // Give the second request time to reach the gate and be counted as waiting.
        await Task.Delay(200);

        var refused = await client.GetAsync($"/api/inventory?steamid={refusedId}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Contains("busy", (await EgressFixtures.ReadJson(refused)).GetProperty("error").GetString());
        Assert.Equal(0, _factory.Http.RequestsMatching(EgressFixtures.InventoryUrl(refusedId)));

        _factory.Http.Hold = null;
        release.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await inFlight).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await queued).StatusCode);

        // The refusal was not negative-cached: with the queue drained the same inventory fetches.
        var retry = await client.GetAsync($"/api/inventory?steamid={refusedId}");
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(1, _factory.Http.RequestsMatching(EgressFixtures.InventoryUrl(refusedId)));

        var health = await EgressFixtures.ReadJson(await client.GetAsync("/health"));
        Assert.Equal(200, health.GetProperty("steam_inventory").GetProperty("last_fetch_status").GetInt32());
    }

    [Fact]
    public async Task ASuccessfulFetch_StoresASnapshotForLater()
    {
        var steamId = EgressFixtures.NextSteamId();
        _factory.Http.Respond(EgressFixtures.InventoryUrl(steamId), HttpStatusCode.OK, EgressFixtures.OneItemInventory());
        var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/inventory?steamid={steamId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var snapshot = await _factory.Database.GetInventorySnapshotAsync(steamId);
        Assert.NotNull(snapshot);
        var stored = JsonDocument.Parse(snapshot.Value.Payload).RootElement;
        Assert.Equal(steamId.ToString(), stored.GetProperty("steamid").GetString());
        Assert.False(stored.TryGetProperty("stale", out _), "the stored copy is the fresh response, unmarked");
    }
}
