using System.Net;
using System.Text.Json;
using Xunit;

namespace csgoSkinStatistics.Tests.Controllers;

// End-to-end coverage of GET /api/profile, which the browser calls alongside /api/inventory so item
// rendering never waits on Steam's profile feed. The feed itself is stubbed.
public class ProfileEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory = factory;

    private static int _nextId;
    private static ulong NextSteamId() => 76561198100000000UL + (ulong)Interlocked.Increment(ref _nextId);

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static string ProfileXml(ulong steamId, string? customUrl) => $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <profile>
          <steamID64>{steamId}</steamID64>
          <steamID><![CDATA[Nine Lives]]></steamID>
          {(customUrl == null ? "" : $"<customURL><![CDATA[{customUrl}]]></customURL>")}
          <avatarFull><![CDATA[https://avatars.steamstatic.com/full.jpg]]></avatarFull>
          <memberSince>July 12, 2015</memberSince>
          <tradeBanState>Probation</tradeBanState>
          <isLimitedAccount>1</isLimitedAccount>
        </profile>
        """;

    [Fact]
    public async Task VanityInput_ReadsTheIdProfileFeedAndPrefersTheVanityForTheHash()
    {
        // /id/<vanity>/?xml=1 carries the SteamId64 *and* the profile info, so one request answers
        // both - no separate resolve call.
        var steamId = NextSteamId();
        const string vanity = "nine-lives";
        _factory.Http.RespondXml($"/id/{vanity}/", ProfileXml(steamId, vanity));

        var response = await _factory.CreateClient().GetAsync($"/api/profile?steamid={vanity}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJson(response);
        Assert.Equal(1, json.GetProperty("success").GetInt32());
        Assert.Equal(steamId.ToString(), json.GetProperty("steamid").GetString());
        Assert.Equal("Nine Lives", json.GetProperty("persona_name").GetString());
        Assert.Equal("https://avatars.steamstatic.com/full.jpg", json.GetProperty("avatar").GetString());
        Assert.Equal("Probation", json.GetProperty("trade_ban_state").GetString());
        Assert.True(json.GetProperty("limited_account").GetBoolean());
        // Only the year is surfaced from the human-readable memberSince date.
        Assert.Equal(2015, json.GetProperty("since_year").GetInt32());
        // The hash drives the location bar, so a profile with a vanity round-trips as the vanity.
        Assert.Equal(vanity, json.GetProperty("hash").GetString());
        Assert.Equal($"https://steamcommunity.com/id/{vanity}", json.GetProperty("profile_url").GetString());
        Assert.Contains($"https://steamcommunity.com/id/{vanity}/?xml=1", _factory.Http.Requests);
    }

    [Fact]
    public async Task Id64Input_ReadsTheProfilesFeedAndFallsBackToTheIdWhenThereIsNoVanity()
    {
        var steamId = NextSteamId();
        _factory.Http.RespondXml($"/profiles/{steamId}/", ProfileXml(steamId, customUrl: null));

        var json = await ReadJson(await _factory.CreateClient().GetAsync($"/api/profile?steamid={steamId}"));

        Assert.Equal(steamId.ToString(), json.GetProperty("steamid").GetString());
        // Steam omits customURL for profiles that never set one; both the hash and the link have to
        // fall back to the /profiles/<id64> form rather than emitting an empty vanity.
        Assert.Equal(steamId.ToString(), json.GetProperty("hash").GetString());
        Assert.Equal($"https://steamcommunity.com/profiles/{steamId}", json.GetProperty("profile_url").GetString());
        Assert.Contains($"https://steamcommunity.com/profiles/{steamId}/?xml=1", _factory.Http.Requests);
    }

    [Fact]
    public async Task ProfileUrlInput_IsAcceptedAsWellAsABareId()
    {
        // The search box takes whatever the user pastes, including a full profile URL.
        var steamId = NextSteamId();
        _factory.Http.RespondXml($"/profiles/{steamId}/", ProfileXml(steamId, customUrl: null));

        var input = Uri.EscapeDataString($"https://steamcommunity.com/profiles/{steamId}");
        var json = await ReadJson(await _factory.CreateClient().GetAsync($"/api/profile?steamid={input}"));

        Assert.Equal(steamId.ToString(), json.GetProperty("steamid").GetString());
    }

    [Fact]
    public async Task ProfileWithoutAMemberSinceElement_ReportsNoYearRatherThanGuessing()
    {
        var steamId = NextSteamId();
        _factory.Http.RespondXml($"/profiles/{steamId}/",
            $"<profile><steamID64>{steamId}</steamID64><steamID><![CDATA[No Date]]></steamID></profile>");

        var json = await ReadJson(await _factory.CreateClient().GetAsync($"/api/profile?steamid={steamId}"));

        Assert.Equal(JsonValueKind.Null, json.GetProperty("since_year").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("trade_ban_state").ValueKind);
        Assert.False(json.GetProperty("limited_account").GetBoolean());
    }

    [Theory]
    [InlineData("garbage!!")]
    [InlineData("12345")]
    public async Task UnresolvableInput_Is400WithoutContactingSteam(string steamid)
    {
        var client = _factory.CreateClient();
        var before = _factory.Http.RequestsMatching("steamcommunity.com");

        var response = await client.GetAsync($"/api/profile?steamid={Uri.EscapeDataString(steamid)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Unable to determine profile for the given Steam ID",
            (await ReadJson(response)).GetProperty("error").GetString());
        // An input we can't classify never reaches Steam - that check is also what keeps an
        // attacker-supplied string out of the fetch URL.
        Assert.Equal(before, _factory.Http.RequestsMatching("steamcommunity.com"));
    }

    [Theory]
    [InlineData("/api/profile")]
    [InlineData("/api/profile?steamid=")]
    public async Task MissingSteamId_Is400FromModelBinding(string path)
    {
        // Same as /api/inventory: the non-nullable parameter is implicitly required, so MVC rejects
        // this before the action's own IsNullOrEmpty guard can run.
        var response = await _factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("steamid", (await ReadJson(response)).GetProperty("errors").ToString());
    }

    [Fact]
    public async Task UpstreamFailure_Is400CarryingTheStatus()
    {
        var steamId = NextSteamId();
        _factory.Http.Respond($"/profiles/{steamId}/", HttpStatusCode.ServiceUnavailable, "");

        var response = await _factory.CreateClient().GetAsync($"/api/profile?steamid={steamId}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Failed to fetch profile: ServiceUnavailable",
            (await ReadJson(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task FeedWithoutASteamId64_Is400()
    {
        // Steam answers 200 with an error document for a vanity nobody owns.
        const string vanity = "no-such-user";
        _factory.Http.RespondXml($"/id/{vanity}/",
            "<response><error><![CDATA[The specified profile could not be found.]]></error></response>");

        var response = await _factory.CreateClient().GetAsync($"/api/profile?steamid={vanity}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Unable to resolve Steam profile",
            (await ReadJson(response)).GetProperty("error").GetString());
    }
}
