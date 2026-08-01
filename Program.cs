[assembly: InternalsVisibleTo("csgoSkinStatistics.Tests")]

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    options.MimeTypes = Microsoft.AspNetCore.ResponseCompression.ResponseCompressionDefaults.MimeTypes.Concat(
        ["application/javascript", "text/css", "text/html", "text/json", "text/plain"]);
});
builder.Services.AddHttpClient();
// Dedicated client for steamcommunity.com calls (inventory, profile, vanity resolve). Traffic is
// bursty/low, so we keep pooled connections alive far longer than the defaults to avoid paying a
// fresh TLS handshake (~100ms) on each cold request. PooledConnectionLifetime still rotates
// connections periodically for DNS hygiene, and an infinite handler lifetime stops IHttpClientFactory
// from recycling the handler (which would otherwise drop the warm connection pool every 2 minutes).
builder.Services.AddHttpClient("steam")
    // Cap how much of an upstream response we will buffer into memory. Every call on this client
    // (inventory fetch, profile XML, vanity resolve) uses the default HttpCompletionOption
    // .ResponseContentRead, so HttpClient buffers the whole body before the caller ever sees it -
    // without a cap a hostile, compromised or simply malfunctioning steamcommunity.com could stream
    // until the host runs out of memory. Set here rather than at each of the three call sites so
    // there is one number to reason about.
    //
    // Sizing: the biggest thing this client fetches is one count=2000 inventory page. Modelling
    // that response at its worst case - 2000 assets, 2000 *distinct* description blocks (real
    // inventories share descriptions across copies of the same skin, so this over-counts heavily),
    // each with the full descriptions/tags/actions/market_actions payload Steam sends, plus 2000
    // asset_properties entries carrying the propid-6 certificate hex - gives ~6.0 MB
    // (2684 B/description + 114 B/asset + 346 B/asset_properties). Our own serialization of that
    // same inventory is ~3 MB (see the MemoryCache SizeLimit comment below), which is consistent.
    // 32 MB is ~5x that worst case, so it has to be Steam changing shape by a wide margin - not an
    // unusually large inventory - before a real response is refused. The profile XML and vanity
    // resolve responses are ~2 KB and sit far under it.
    .ConfigureHttpClient(client => client.MaxResponseContentBufferSize = 32 * 1024 * 1024)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(30),
    })
    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
// Skinport's /v1/items feed is Brotli-only (a request without Accept-Encoding: br 406s), so this
// client auto-negotiates and decompresses it. AutomaticDecompression.All includes Brotli and adds
// the Accept-Encoding header itself.
builder.Services.AddHttpClient("skinport")
    // Same memory bound as the "steam" client above, but sized to a much larger feed. Measured
    // 2026-08-01: the live /v1/items response for app 730 is 21,998 items, 758 KB on the wire
    // (Brotli) and 9,099,357 bytes - 8.68 MB - once decompressed, at ~414 B/item. The cap applies
    // to the *decompressed* stream (AutomaticDecompression below unwraps the body before it is
    // buffered), so it is the 8.68 MB figure that matters, not the 758 KB one - a cap chosen off
    // the wire size would strangle the feed on day one.
    //
    // 64 MB is ~7x today's feed: the CS2 catalogue would have to reach roughly 160,000 items -
    // seven times its current size - before a legitimate response were refused. Losing this feed
    // is not fatal either way (RefreshAsync keeps serving the last-known prices), but it is the
    // response most likely to be strangled by a careless limit, so the headroom is deliberate.
    .ConfigureHttpClient(client => client.MaxResponseContentBufferSize = 64 * 1024 * 1024)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    });
// Inventory response cache: /api/inventory results are cached by resolved SteamId64 for a few
// minutes so reload storms (and repeat viewers of the same inventory) don't each re-hit
// steamcommunity.com's inventory endpoint, which rate-limits per server IP. Bounded by *bytes* -
// each entry's Size is its serialized length - so total memory can never exceed SizeLimit no
// matter how many inventories are viewed, which matters on a small-memory host. A maxed 2000-item
// inventory serializes to ~3 MB, so 8 MB holds a couple of large ones plus several smaller ones;
// lower SizeLimit to tighten the footprint, raise it to cache more.
builder.Services.AddMemoryCache(options => options.SizeLimit = 8 * 1024 * 1024);
// Per-client-IP rate limiting on the API. Every uncached /api, /api/inventory and /api/profile
// call can trigger an outbound steamcommunity.com request, so an unthrottled client could relay
// traffic through our egress IP until Steam 429-bans it. A token bucket per IP bounds that while
// staying comfortably above the ~10 req/s a single inventory analysis makes (the client paces its
// per-item lookups 100ms apart). Limits live in the RateLimiting config section so they can be
// tuned without a redeploy. Only the API carries the "api" policy; static files are never limited.
var rateLimitConfig = builder.Configuration.GetSection("RateLimiting");
var tokenLimit = rateLimitConfig.GetValue("TokenLimit", 40);
var tokensPerPeriod = rateLimitConfig.GetValue("TokensPerPeriod", 20);
var replenishmentSeconds = rateLimitConfig.GetValue("ReplenishmentPeriodSeconds", 1.0);
var queueLimit = rateLimitConfig.GetValue("QueueLimit", 10);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("api", httpContext =>
    {
        // Partition by client. Behind the reverse proxy this is the *client's* address rather than
        // the proxy's only because UseForwardedHeaders runs first (see Security/TransportSecurity.cs);
        // without it every caller on the internet would share one bucket. ClientPartitionKey also
        // decides what counts as one client - an IPv6 /64 rather than a single address, which a
        // client can rotate through freely - and folds IPv4-mapped addresses onto their IPv4 form.
        var clientIp = TransportSecurity.ClientPartitionKey(httpContext.Connection.RemoteIpAddress);
        return RateLimitPartition.GetTokenBucketLimiter(clientIp, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = tokenLimit,
            TokensPerPeriod = tokensPerPeriod,
            ReplenishmentPeriod = TimeSpan.FromSeconds(replenishmentSeconds),
            QueueLimit = queueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        });
    });
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        // This callback is configured before the container exists, so the logger comes off the
        // request rather than being captured here. The category is fixed so the line can be turned
        // down on its own (a scripted client can produce a lot of these) without touching the rest
        // of the app. The path is a parameter, not spliced text - though unlike ?url= it cannot
        // carry a CR/LF in the first place: Kestrel rejects a request target containing one long
        // before this runs.
        context.HttpContext.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("CSGOSkinAPI.RateLimiting")
            .LogWarning("Rate limited {ClientIp} on {RequestPath}",
                context.HttpContext.Connection.RemoteIpAddress,
                context.HttpContext.Request.Path.Value);
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { error = "Too many requests. Please slow down and try again shortly." }, cancellationToken);
    };
});
builder.Services.AddSingleton<SteamService>();
builder.Services.AddSingleton<DatabaseService>();
builder.Services.AddSingleton<ConstDataService>();
// Registered once and exposed both as itself (controllers enqueue into it) and as the
// hosted service that drains the queue.
builder.Services.AddSingleton<InventoryWarmService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<InventoryWarmService>());
// Skinport base prices: exposed as itself (controllers look prices up) and as the hosted service
// that refreshes them a few times a day.
builder.Services.AddSingleton<PriceService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PriceService>());

var app = builder.Build();

// FIRST in the pipeline, deliberately: the app runs behind a TLS-terminating Caddy, so the real
// client address and scheme only exist in X-Forwarded-For / X-Forwarded-Proto. Everything below -
// the security headers, the exception handler's logging, and above all the rate limiter's per-IP
// partition key - has to see the corrected connection, not the proxy's. Only headers arriving from
// a trusted proxy are honoured; see Security/TransportSecurity.cs for why that restriction is the
// whole fix, and for the config keys that override the trusted set.
var forwardedHeaderOptions = TransportSecurity.BuildForwardedHeadersOptions(app.Configuration);
// Say out loud what we ended up trusting. Every way this can be mis-set - a peer outside the
// default private ranges, a KnownNetworks value narrowed too far, an env var written in the scalar
// form - otherwise produces a working-looking app whose limiter has quietly collapsed back to one
// global bucket, with nothing in the log to explain it.
var transportLoggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
TransportSecurity.LogTrustedSources(
    transportLoggerFactory.CreateLogger(typeof(TransportSecurity)), forwardedHeaderOptions);
var forwardedTrustDiagnostics = new ForwardedTrustDiagnostics(
    forwardedHeaderOptions, transportLoggerFactory.CreateLogger(typeof(ForwardedTrustDiagnostics)));
app.Use(async (context, next) =>
{
    // Ahead of UseForwardedHeaders, which is about to overwrite the peer address this reads.
    forwardedTrustDiagnostics.Inspect(context);
    await next();
});
app.UseForwardedHeaders(forwardedHeaderOptions);

// Defense-in-depth security headers on every response (via OnStarting so they apply even to the
// error responses written by UseExceptionHandler below). The CSP is conservative but tuned to what
// the app actually loads: same-origin scripts/styles - plus 'unsafe-inline', which the page's
// bootstrap script and the stylesheet media-swap onload handlers still rely on - Steam CDN images,
// and Google Fonts. frame-ancestors / X-Frame-Options block clickjacking. (See L9: if this ever
// runs behind a proxy that already sets these, drop the middleware.)
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["Content-Security-Policy"] =
            "default-src 'self'; " +
            "script-src 'self' 'unsafe-inline'; " +
            "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
            "font-src 'self' https://fonts.gstatic.com; " +
            "img-src 'self' data: https://*.steamstatic.com; " +
            "connect-src 'self'; " +
            "frame-ancestors 'none'; " +
            "base-uri 'self'; " +
            "object-src 'none'";
        return Task.CompletedTask;
    });
    await next();
});

// Any unhandled exception from an endpoint becomes a generic 500 here, logged server-side. This
// keeps internal detail (paths, SQL, library internals) out of the response and means individual
// actions don't each need a copy-pasted catch-all.
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    if (error != null)
    {
        // The exception goes in as the exception argument rather than as its .Message, so the
        // stack trace travels with the record instead of on a second, unattached line.
        context.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("CSGOSkinAPI.UnhandledException")
            .LogError(error, "Unhandled exception on {RequestPath}", context.Request.Path.Value);
    }
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await context.Response.WriteAsJsonAsync(new { error = "Internal server error" });
}));

app.UseResponseCompression();
// /inventory now serves the unified single page (index.html); a #profile hash makes it open
// straight into the inventory view. Kept as a rewrite so old /inventory links still work.
app.UseRewriter(new RewriteOptions()
    .AddRewrite("^inventory$", "index.html", skipRemainingRules: true));
app.UseDefaultFiles(); // Must be before UseStaticFiles
app.UseStaticFiles();

// Every error the API returns is a status code plus a JSON `{ "error": "..." }` body - see
// InvalidModelStateAsErrorAttribute in SkinController.cs. Two escape that: routing answers an
// unknown /api path with a bare 404 and a wrong method on a known one with a bare 405, both before
// an endpoint (and therefore any controller-scoped filter) is ever selected. This fills those in so
// a caller has exactly one error body to parse.
//
// It has to wrap routing rather than sit after it: the 405 is produced by an endpoint the matcher
// synthesizes, which short-circuits, so a terminal middleware placed after MapControllers would
// never see it. Sitting here - after UseStaticFiles, before UseRouting - it wraps endpoint
// selection and nothing else.
//
// Deliberately narrow, because rewriting response bodies from middleware is easy to over-apply:
//   - only /api paths, so the /inventory rewrite, wwwroot files and a missing static file are all
//     untouched and still answer exactly as before;
//   - only 404 and 405, so no successful response is ever rewritten;
//   - only when nothing has been written yet (no content type, no body, response not started), so
//     an action's own NotFound(new { error = ... }) - which already carries the house shape - is
//     left alone rather than overwritten.
app.Use(async (context, next) =>
{
    await next();

    var response = context.Response;
    if (!context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        || response.HasStarted
        || response.ContentType != null
        || response.ContentLength is not (null or 0))
    {
        return;
    }

    var error = response.StatusCode switch
    {
        StatusCodes.Status404NotFound => "Not found",
        StatusCodes.Status405MethodNotAllowed => "Method not allowed",
        _ => null,
    };
    if (error != null)
    {
        // The 405's Allow header is set by the matcher and survives this - only the body is added.
        await response.WriteAsJsonAsync(new { error });
    }
});

app.UseRouting();
app.UseRateLimiter();
app.MapControllers();

// The boot and shutdown lines below. A fixed category rather than app.Logger, whose category is
// the application name: everything this app logs is then under CSGOSkinAPI.*, which is what makes
// that one key in appsettings.json a complete knob for the app's own output.
var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("CSGOSkinAPI.Startup");

// Initialize database on startup
var dbService = app.Services.GetRequiredService<DatabaseService>();
await dbService.InitializeDatabaseAsync();

// Initialize Steam connection. Supervised: a boot-time failure (bad credentials, Steam outage)
// is logged rather than left as an unobserved exception, and ConnectAsync resets its running flag
// on failure so the on-demand reconnect in GetItemInfoAsync retries on the next lookup.
var steamService = app.Services.GetRequiredService<SteamService>();
_ = Task.Run(async () =>
{
    try
    {
        await steamService.ConnectAsync();
    }
    catch (Exception ex)
    {
        startupLogger.LogError(ex, "Initial Steam connection failed");
    }
});

// Initialize ConstDataService (loads const.json)
var constDataService = app.Services.GetRequiredService<ConstDataService>();

// Disconnect from Steam as part of the host's graceful shutdown (which Ctrl-C / SIGTERM already
// trigger) rather than from a Console.CancelKeyPress handler. The old handler tore Steam down and
// let the process die *around* the host, skipping request draining and hosted-service stop - and
// could dispose an account's RateLimitSemaphore out from under an in-flight GC request. Running on
// ApplicationStopping means the server has stopped accepting requests and in-flight ones have
// drained first.
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    startupLogger.LogInformation("Application stopping, disconnecting from Steam...");
    steamService.Disconnect();
});

app.Run();
