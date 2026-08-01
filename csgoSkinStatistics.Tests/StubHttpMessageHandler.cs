using System.Net;
using System.Net.Http.Headers;
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

    // An upstream answering with a body far larger than the app is willing to buffer - the case the
    // named clients' MaxResponseContentBufferSize (Program.cs) exists for.
    //
    // The response *declares* `declaredBytes` in Content-Length while actually holding `bodyIfRead`.
    // HttpClient compares Content-Length against the cap before reading a single byte, so a test can
    // present a 40 MB response without allocating one - and if the cap is ever removed, the small
    // body is read normally and the endpoint succeeds, so the assertion flips rather than passing
    // for the wrong reason. Steam and Skinport both send Content-Length, so this is the realistic
    // shape; RespondOversizedWithoutLength below covers the one that doesn't.
    public StubHttpMessageHandler RespondOversized(string urlContains, long declaredBytes, string bodyIfRead,
        string contentType = "application/json")
        => Respond(urlContains, () =>
        {
            var content = new StringContent(bodyIfRead, Encoding.UTF8, contentType);
            content.Headers.ContentLength = declaredBytes;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

    // An upstream that declares no length and just keeps sending, so there is no Content-Length to
    // reject up front and the client has to stop itself mid-stream. Bytes are produced as they are
    // read, so nothing this size is ever held in the test; the total is finite so that a *missing*
    // cap fails the test instead of hanging it.
    public StubHttpMessageHandler RespondOversizedWithoutLength(string urlContains, long bytes,
        string contentType = "application/json")
        => Respond(urlContains, () =>
        {
            var content = new StreamContent(new EndlessStream(bytes));
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            content.Headers.ContentLength = null;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

    // Yields `length` bytes of filler and then ends. Non-seekable so StreamContent cannot compute a
    // length from it, which is the whole point.
    private sealed class EndlessStream(long length) : Stream
    {
        private long _produced;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var produce = (int)Math.Min(count, length - _produced);
            if (produce <= 0)
            {
                return 0;
            }
            Array.Fill(buffer, (byte)'x', offset, produce);
            _produced += produce;
            return produce;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

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
