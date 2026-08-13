using CSGOSkinAPI.Services;
using Xunit;

namespace csgoSkinStatistics.Tests.Services;

// The sale index has to survive restarts AND survive items falling out of Skinport's feed, which
// are different things. Skinport's sales windows only reach back 90 days, so a rarely-traded item
// stops being reported entirely - and those are exactly the items with no live listing to fall back
// on, so losing them would leave them priced only by a neighbouring wear, or not at all. The table
// is therefore an accumulating index, not a mirror of the last fetch: writes upsert what was
// observed and never delete what wasn't.
[Collection("Database Tests")]
public class SalePriceIndexDbTests : IDisposable
{
    private readonly DatabaseService _db;
    private readonly string _dbPath;

    public SalePriceIndexDbTests()
    {
        _dbPath = $"test_saleprices_{Guid.NewGuid():N}.db";
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        _db = new DatabaseService(_dbPath);
    }

    public void Dispose()
    {
        // WAL leaves -wal/-shm siblings next to the database.
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            if (File.Exists(_dbPath + suffix)) File.Delete(_dbPath + suffix);
        }
    }

    private static Dictionary<string, (int, int, string, bool)> Batch(
        params (string Name, int Cents, int Volume, string Window, bool Pooled)[] rows)
    {
        var map = new Dictionary<string, (int, int, string, bool)>(StringComparer.Ordinal);
        foreach (var r in rows) map[r.Name] = (r.Cents, r.Volume, r.Window, r.Pooled);
        return map;
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsEveryField()
    {
        await _db.InitializeDatabaseAsync();
        var stamp = new DateTime(2026, 8, 13, 9, 30, 0, DateTimeKind.Utc);

        await _db.SaveSalePricesAsync(
            Batch(("★ Bayonet | Doppler (Factory New)", 95000, 4, "30d", true)), stamp);

        var loaded = await _db.LoadSalePricesAsync();
        var row = loaded["★ Bayonet | Doppler (Factory New)"];
        Assert.Equal(95000, row.MedianCents);
        Assert.Equal(4, row.Volume);
        Assert.Equal("30d", row.Window);
        Assert.True(row.Pooled);
        Assert.Equal(stamp, row.UpdatedAt);
    }

    [Fact]
    public async Task ItemsMissingFromALaterFetchKeepTheirLastObservedSale()
    {
        await _db.InitializeDatabaseAsync();
        var first = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var second = new DateTime(2026, 8, 13, 0, 0, 0, DateTimeKind.Utc);

        // A rare knife sells in May, alongside an everyday rifle.
        await _db.SaveSalePricesAsync(Batch(
            ("Souvenir AWP | Dragon Lore (Factory New)", 2500000, 2, "90d", false),
            ("AK-47 | Redline (Field-Tested)", 1000, 400, "24h", false)), first);

        // By August the knife has gone three months without a sale, so it is absent from the feed
        // and therefore absent from this batch.
        await _db.SaveSalePricesAsync(Batch(
            ("AK-47 | Redline (Field-Tested)", 1100, 380, "24h", false)), second);

        var loaded = await _db.LoadSalePricesAsync();

        // The rifle moved on...
        Assert.Equal(1100, loaded["AK-47 | Redline (Field-Tested)"].MedianCents);
        Assert.Equal(second, loaded["AK-47 | Redline (Field-Tested)"].UpdatedAt);

        // ...and the knife is still here, at its May price with its May timestamp, which is what
        // lets PriceService age it into an approximate value instead of losing it.
        var knife = loaded["Souvenir AWP | Dragon Lore (Factory New)"];
        Assert.Equal(2500000, knife.MedianCents);
        Assert.Equal(first, knife.UpdatedAt);
    }

    [Fact]
    public async Task ReopeningTheDatabaseKeepsTheIndex()
    {
        await _db.InitializeDatabaseAsync();
        var stamp = new DateTime(2026, 8, 13, 0, 0, 0, DateTimeKind.Utc);
        await _db.SaveSalePricesAsync(Batch(("AWP | Asiimov (Field-Tested)", 6000, 30, "7d", false)), stamp);

        // A fresh service over the same file is what a process restart looks like.
        var reopened = new DatabaseService(_dbPath);
        await reopened.InitializeDatabaseAsync();

        var loaded = await reopened.LoadSalePricesAsync();
        Assert.Equal(6000, loaded["AWP | Asiimov (Field-Tested)"].MedianCents);
    }

    [Fact]
    public async Task LoadIsEmptyBeforeAnythingIsWritten()
    {
        await _db.InitializeDatabaseAsync();
        Assert.Empty(await _db.LoadSalePricesAsync());
    }
}
