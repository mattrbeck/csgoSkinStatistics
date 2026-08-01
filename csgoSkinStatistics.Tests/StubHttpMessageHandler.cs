using System.Net;
using System.Text;
using System.Text.Json;

namespace csgoSkinStatistics.Tests;

// Stands in for the primary handler of the app's named HttpClients ("steam", "skinport"), so no
// test ever reaches steamcommunity.com. Rules are matched on a URL substring, most-recently
// registered first; an unmatched URL answers 501 rather than throwing, so a forgotten stub shows up
// as a wrong response body instead of an opaque connection error.
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly object _sync = new();
    private readonly List<(string UrlContains, Func<HttpResponseMessage> Respond)> _rules = [];
    private readonly List<string> _requests = [];

    // Awaited after a request has been recorded but before its response is produced. The
    // single-flight test uses it to hold the one in-flight fetch open while the other viewers pile
    // up behind the controller's gate.
    public Func<Task>? Hold { get; set; }

    // A fresh HttpResponseMessage per call: a response body can only be read once, and the caller
    // disposes it.
    public StubHttpMessageHandler Respond(string urlContains, Func<HttpResponseMessage> respond)
    {
        lock (_sync)
        {
            _rules.Add((urlContains, respond));
        }
        return this;
    }

    public StubHttpMessageHandler Respond(string urlContains, HttpStatusCode status, string body = "",
        string contentType = "application/json")
        => Respond(urlContains, () => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        });

    public StubHttpMessageHandler RespondJson(string urlContains, object body)
        => Respond(urlContains, HttpStatusCode.OK, JsonSerializer.Serialize(body));

    public StubHttpMessageHandler RespondXml(string urlContains, string xml)
        => Respond(urlContains, HttpStatusCode.OK, xml, "text/xml");

    // Simulates a transport-level failure (connection reset, DNS, timeout) rather than an HTTP
    // status.
    public StubHttpMessageHandler Throw(string urlContains, Func<Exception> error)
        => Respond(urlContains, HttpResponseMessage () => throw error());

    public IReadOnlyList<string> Requests
    {
        get { lock (_sync) return [.. _requests]; }
    }

    public int RequestsMatching(string urlContains)
        => Requests.Count(url => url.Contains(urlContains, StringComparison.Ordinal));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        Func<HttpResponseMessage>? respond = null;
        lock (_sync)
        {
            _requests.Add(url);
            for (var i = _rules.Count - 1; i >= 0; i--)
            {
                if (url.Contains(_rules[i].UrlContains, StringComparison.Ordinal))
                {
                    respond = _rules[i].Respond;
                    break;
                }
            }
        }

        var hold = Hold;
        if (hold != null)
        {
            await hold();
        }

        return respond?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.NotImplemented)
        {
            Content = new StringContent($"no stub registered for {url}", Encoding.UTF8, "text/plain"),
        };
    }
}
