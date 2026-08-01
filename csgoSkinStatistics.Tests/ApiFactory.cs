using CSGOSkinAPI.Models;
using CSGOSkinAPI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace csgoSkinStatistics.Tests;

// Boots the real Program.cs pipeline - routing, model binding, the byte-bounded inventory
// MemoryCache, the rate limiter, the exception handler, the controller itself - with only the
// process boundaries stubbed: no Steam login, no outbound HTTP, and a throwaway database and
// catalog directory per factory so two test classes can never share state on disk.
//
// One factory serves a whole test class (xunit runs a class's tests sequentially), so tests must
// pick distinct steamids: the inventory cache lives for the life of the host.
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
    }

    // Every outbound call the app makes - inventory, profile XML, vanity resolve - lands here.
    public StubHttpMessageHandler Http { get; } = new();

    // The same instance the app uses, so a test can seed the item cache the endpoints read.
    public DatabaseService Database { get; }

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
        builder.ConfigureServices(services =>
        {
            // SteamService's public constructor throws without credentials, and Program.cs kicks off
            // a real ConnectAsync at startup. An account-less instance boots and stays offline.
            services.RemoveAll<SteamService>();
            services.AddSingleton(SteamService.CreateWithoutAccounts());

            services.RemoveAll<DatabaseService>();
            services.AddSingleton(Database);

            services.RemoveAll<ConstDataService>();
            services.AddSingleton(_catalogs.Build());

            // Both BackgroundServices start with the host. PriceService would immediately fetch the
            // Skinport feed (and make every asserted `price` field depend on when that landed), and
            // InventoryWarmService would fetch a whole inventory in the background off any cache
            // miss, polluting the outbound-request counts these tests assert on. Idling their loops
            // leaves the rest of each service - Resolve, Enqueue - exactly as production has it.
            services.RemoveAll<PriceService>();
            services.AddSingleton<PriceService, IdlePriceService>();
            services.RemoveAll<InventoryWarmService>();
            services.AddSingleton<InventoryWarmService, IdleInventoryWarmService>();

            // Re-registering a named client appends configuration, and the last primary handler
            // wins, so this replaces the SocketsHttpHandler Program.cs installed.
            services.AddHttpClient("steam").ConfigurePrimaryHttpMessageHandler(() => Http);
            services.AddHttpClient("skinport").ConfigurePrimaryHttpMessageHandler(() => Http);
        });
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

    private sealed class IdlePriceService(IHttpClientFactory httpClientFactory, DatabaseService dbService)
        : PriceService(httpClientFactory, dbService)
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }

    private sealed class IdleInventoryWarmService(IHttpClientFactory httpClientFactory, DatabaseService dbService)
        : InventoryWarmService(httpClientFactory, dbService)
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }
}
