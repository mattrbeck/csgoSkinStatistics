using CSGOSkinAPI.Models;
using CSGOSkinAPI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace csgoSkinStatistics.Tests;

// Boots the real Program.cs pipeline - routing, model binding, the byte-bounded inventory
// MemoryCache, the rate limiter, the exception handler, the controller itself - with only the
// process boundaries stubbed: no Steam login, nothing that leaves the machine, and a throwaway
// database and catalog directory per factory so two test classes can never share state on disk.
//
// One factory serves a whole test class (xunit runs a class's tests sequentially).
//
// Steamids must be distinct across EVERY class in the assembly, not just within one, and for the
// whole life of the process. Two things are keyed by resolved SteamId64 and they have different
// lifetimes: IMemoryCache is per-host, so a repeated id inside a class serves a stale response, but
// SkinController.InventoryFetchGates is a `static` dictionary shared by every host in the process -
// so two classes running in parallel on the same id contend on one gate across hosts, and get
// either a double fetch or a stall. NextSteamId() in each test class hands out its own range.
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly CatalogDirectory _catalogs;
    private readonly string _databasePath;

    public ApiFactory()
    {
        _catalogs = CatalogDirectory.Create(Catalog);
        // The production default, "searches.db", resolves against the shared test working
        // directory, where concurrent test classes would trample each other's rows.
        _databasePath = Path.Combine(Path.GetTempPath(), $"csgoskin-tests-{Guid.NewGuid():N}.db");
        Database = new DatabaseService(_databasePath);
        Http.RespondJson("api.skinport.com", Prices);
    }

    // The Skinport feed PriceService loads at startup. Only the Field-Tested variants are listed,
    // which is true to the real feed - it only carries variants that have actually sold - and lets
    // the nearest-wear fallback show up in a response.
    public static SkinportItem[] Prices { get; } =
    [
        new() { market_hash_name = "AK-47 | Fire Serpent (Field-Tested)", min_price = 1000.00, suggested_price = 1250.50 },
        new() { market_hash_name = "StatTrak™ AK-47 | Fire Serpent (Field-Tested)", min_price = 2000.00, suggested_price = 2400.00 },
    ];

    // Every outbound call the app makes - inventory, profile XML, vanity resolve - lands here.
    public StubHttpMessageHandler Http { get; } = new();

    // Stands in for the Game Coordinator round-trip behind /api.
    public FakeSteamService Steam { get; } = new();

    // The same instance the app uses, so a test can seed the item cache the endpoints read.
    public DatabaseService Database { get; }

    // Host settings layered over appsettings.json, applied after this factory's own defaults so a
    // test can override them (the forwarded-headers suite tightens RateLimiting:TokenLimit to watch
    // partitioning, and names its own trusted proxies). Populate before the first CreateClient -
    // the host is built once, on demand, and never reconfigured after that.
    public Dictionary<string, string?> Settings { get; } = [];

    // Extra registrations for a test class that needs to reach into the host itself rather than
    // swap a service - specifically an IStartupFilter, which is the only way to run a middleware
    // *before* Program.cs's pipeline (TestServer leaves Connection.RemoteIpAddress null, so the
    // forwarded-headers trust check has no peer to judge unless a test stamps one).
    public Action<IServiceCollection>? ConfigureExtraServices { get; set; }

    // A small stand-in for const.json. Deliberately not the shipped catalog: these tests assert on
    // resolved names, and pinning them to the real data would make them fail whenever the catalog
    // is regenerated.
    public static ConstData Catalog { get; } = new()
    {
        Items = new Dictionary<string, string> { ["7"] = "AK-47" },
        Skins = new Dictionary<string, string> { ["0"] = "Vanilla", ["44"] = "Fire Serpent" },
        Rarities = ["Stock", "Consumer Grade", "Industrial Grade", "Mil-Spec", "Restricted", "Classified", "Covert"],
        Qualities = new Dictionary<string, string> { ["3"] = "★", ["4"] = "Unique", ["9"] = "StatTrak™", ["12"] = "Souvenir" },
        Origins = new Dictionary<string, string> { ["8"] = "Found in Crate" },
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // TestServer leaves RemoteIpAddress null, so every request from every test in a class lands
        // in the rate limiter's single "unknown" partition and draws on one shared token budget.
        // Production's 40 tokens is under 2 requests of headroom for this class and gets tighter on
        // faster hardware (less wall clock, so less replenishment). The limiter is infrastructure
        // these tests aren't exercising; take it out of the picture rather than budget around it.
        builder.UseSetting("RateLimiting:TokenLimit", "1000000");
        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureServices(services =>
        {
            ConfigureExtraServices?.Invoke(services);

            // SteamService's public constructor throws without credentials, and Program.cs kicks off
            // a real ConnectAsync at startup. An account-less stand-in boots and stays offline.
            services.RemoveAll<SteamService>();
            services.AddSingleton<SteamService>(Steam);

            services.RemoveAll<DatabaseService>();
            services.AddSingleton(Database);

            services.RemoveAll<ConstDataService>();
            services.AddSingleton(_catalogs.Build());

            // PriceService keeps its real loop: the feed it fetches is stubbed like everything else,
            // and CreateHost waits for the load, so `price` is populated and constant.
            //
            // The warm service does not. It fetches a whole inventory in the background off any
            // single-item cache miss, which would land in the middle of the outbound-request counts
            // these tests assert on. Idling only its loop leaves the rest of it - Enqueue, the
            // bounded drop-on-full queue - exactly as production has it.
            services.RemoveAll<InventoryWarmService>();
            services.AddSingleton<InventoryWarmService, IdleInventoryWarmService>();

            // Re-registering a named client appends configuration, and the last primary handler
            // wins, so this replaces the SocketsHttpHandler Program.cs installed.
            services.AddHttpClient("steam").ConfigurePrimaryHttpMessageHandler(() => Http);
            services.AddHttpClient("skinport").ConfigurePrimaryHttpMessageHandler(() => Http);
        });
    }

    // Clears the state a single test sets on these class-scoped doubles - the stub's response hold
    // and the fake GC's canned answer. Test classes call this from Dispose, which xunit runs after
    // every test, so one test's setup can never still be in force during the next one.
    public void ResetPerTestState()
    {
        Http.Hold = null;
        Steam.Reset();
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        // PriceService loads its (stubbed) feed on a background task as the host starts. Blocking
        // until it has means every test in the class sees the same prices, instead of the `price`
        // field flipping partway through the class depending on when the load landed.
        var prices = host.Services.GetRequiredService<PriceService>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (prices.UpdatedAtUtc == null)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("PriceService never loaded the stubbed Skinport feed.");
            }
            Thread.Sleep(10);
        }

        return host;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        _catalogs.Dispose();
        // WAL leaves -wal/-shm siblings next to the database file.
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                File.Delete(_databasePath + suffix);
            }
            catch (IOException)
            {
                // Still held open by a pooled connection; it's a temp file either way.
            }
        }
    }

    private sealed class IdleInventoryWarmService(IHttpClientFactory httpClientFactory,
        DatabaseService dbService, ILogger<InventoryWarmService> logger)
        : InventoryWarmService(httpClientFactory, dbService, logger)
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }
}
