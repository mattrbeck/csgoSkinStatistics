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
builder.Services.AddSingleton<SteamService>();
builder.Services.AddSingleton<DatabaseService>();
builder.Services.AddSingleton<ConstDataService>();
// Registered once and exposed both as itself (controllers enqueue into it) and as the
// hosted service that drains the queue.
builder.Services.AddSingleton<InventoryWarmService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<InventoryWarmService>());

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
// Serve the inventory page at the clean /inventory URL.
app.UseRewriter(new RewriteOptions()
    .AddRewrite("^inventory$", "inventory.html", skipRemainingRules: true));
app.UseDefaultFiles(); // Must be before UseStaticFiles
app.UseStaticFiles();

app.UseRouting();
app.MapControllers();

// Initialize database on startup
var dbService = app.Services.GetRequiredService<DatabaseService>();
await dbService.InitializeDatabaseAsync();

// Initialize Steam connection
var steamService = app.Services.GetRequiredService<SteamService>();
_ = steamService.ConnectAsync();

// Initialize ConstDataService (loads const.json)
var constDataService = app.Services.GetRequiredService<ConstDataService>();

// Handle Ctrl-C gracefully
Console.CancelKeyPress += (sender, e) =>
{
    Console.WriteLine("\nReceived Ctrl-C, disconnecting from Steam...");
    steamService.Disconnect();
    e.Cancel = false;
};

app.Run();
