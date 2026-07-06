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
        // Partition by client IP. A single key ("unknown") for IP-less requests is deliberate:
        // it caps that whole bucket rather than letting them each get their own allowance.
        var clientIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
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
        Console.WriteLine($"Rate limited {context.HttpContext.Connection.RemoteIpAddress} on {context.HttpContext.Request.Path}");
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

// Any unhandled exception from an endpoint becomes a generic 500 here, logged server-side. This
// keeps internal detail (paths, SQL, library internals) out of the response and means individual
// actions don't each need a copy-pasted catch-all.
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    if (error != null)
    {
        Console.WriteLine($"Unhandled exception on {context.Request.Path}: {error.Message}");
        Console.WriteLine(error.StackTrace);
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

app.UseRouting();
app.UseRateLimiter();
app.MapControllers();

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
        Console.WriteLine($"Initial Steam connection failed: {ex.Message}");
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
    Console.WriteLine("Application stopping, disconnecting from Steam...");
    steamService.Disconnect();
});

app.Run();
