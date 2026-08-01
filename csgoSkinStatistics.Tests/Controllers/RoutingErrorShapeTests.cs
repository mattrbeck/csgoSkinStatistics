using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace csgoSkinStatistics.Tests.Controllers;

// Routing answers an unknown /api path and a wrong method on a known one before any endpoint - and
// therefore any controller filter - is selected, so those two used to be the only API errors with
// an empty body and no content type. Program.cs fills them in now. These tests pin both the shape
// and, just as importantly, the blast radius: rewriting response bodies from middleware is easy to
// over-apply, so the static-file and SPA-rewrite cases are pinned right alongside.
public class RoutingErrorShapeTests(ApiFactory factory) : IClassFixture<ApiFactory>, IDisposable
{
    private readonly ApiFactory _factory = factory;

    public void Dispose() => _factory.ResetPerTestState();

    private static async Task<string?> ReadError(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("error").GetString();
    }

    [Theory]
    [InlineData("/api/nope")]
    [InlineData("/api/inventory/extra")]
    [InlineData("/API/NOPE")] // the scoping check is case-insensitive, like route matching
    public async Task UnknownApiPath_Is404InTheHouseErrorShape(string path)
    {
        var response = await _factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Not found", await ReadError(response));
    }

    [Fact]
    public async Task WrongMethodOnAKnownApiPath_Is405InTheHouseErrorShape()
    {
        var response = await _factory.CreateClient().PostAsync("/api/inventory", JsonContent.Create(new { }));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Method not allowed", await ReadError(response));
        // The matcher's Allow header is what tells the caller which method to use; adding a body
        // must not cost it.
        Assert.Contains("GET", response.Content.Headers.Allow);
    }

    [Fact]
    public async Task AnActionsOwn404_KeepsItsOwnMessage()
    {
        // /api answers 404 from inside the action, with a message of its own. The middleware only
        // fills in responses that carry no body at all, so this one has to come through untouched -
        // if it did not, every "not found" in the API would collapse to the same generic string.
        var steamId = 76561198800000001UL;
        _factory.Steam.Reset();

        var response = await _factory.CreateClient().GetAsync($"/api?s={steamId}&a=987654321&d=1&m=0");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Steam GC did not return an item", await ReadError(response));
    }

    [Fact]
    public async Task MissingStaticFile_IsUnchanged()
    {
        // Outside /api, so the middleware must not touch it: still a bare 404 with no body and no
        // content type, exactly as before.
        var response = await _factory.CreateClient().GetAsync("/definitely-not-a-file.js");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(response.Content.Headers.ContentType);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnknownNonApiPath_IsUnchanged()
    {
        var response = await _factory.CreateClient().GetAsync("/nope");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(response.Content.Headers.ContentType);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/inventory")] // the rewrite
    [InlineData("/")]          // UseDefaultFiles
    public async Task SinglePageIsStillServed(string path)
    {
        var response = await _factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<title>Item Analyzer - Skin Stats</title>", await response.Content.ReadAsStringAsync());
    }
}
