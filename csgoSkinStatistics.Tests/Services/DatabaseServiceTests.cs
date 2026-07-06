using Microsoft.Data.Sqlite;
using CSGOSkinAPI.Services;
using CSGOSkinAPI.Models;
using SteamKit2.GC.CSGO.Internal;
using Xunit;

namespace csgoSkinStatistics.Tests.Services;

[Collection("Database Tests")]
public class DatabaseServiceTests : IDisposable
{
    private readonly DatabaseService _databaseService;
    private readonly string _testDbPath;

    public DatabaseServiceTests()
    {
        // Create a unique database file for this test instance
        _testDbPath = $"test_searches_{Guid.NewGuid():N}.db";

        // Clean up any existing test database
        if (File.Exists(_testDbPath))
        {
            File.Delete(_testDbPath);
        }

        _databaseService = new DatabaseService(_testDbPath);
    }

    public void Dispose()
    {
        // Clean up test database
        if (File.Exists(_testDbPath))
        {
            File.Delete(_testDbPath);
        }
    }

    [Fact]
    public async Task InitializeDatabaseAsync_ShouldCreateTables()
    {
        await _databaseService.InitializeDatabaseAsync();

        using var connection = new SqliteConnection($"Data Source={_testDbPath};foreign keys=true;");
        await connection.OpenAsync();

        var command = new SqliteCommand("SELECT name FROM sqlite_master WHERE type='table'", connection);
        using var reader = await command.ExecuteReaderAsync();

        var tables = new List<string>();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        Assert.Contains("searches", tables);
        Assert.Contains("stickers", tables);
        Assert.Contains("keychains", tables);
    }

    [Fact]
    public async Task SaveItemAsync_ShouldSaveItemToDatabase()
    {
        await _databaseService.InitializeDatabaseAsync();

        var item = new CEconItemPreviewDataBlock
        {
            itemid = 12345,
            defindex = 7,
            paintindex = 10,
            rarity = 3,
            quality = 4,
            paintwear = 1065353216, // 1.0 as uint32
            paintseed = 661,
            inventory = 2147483649,
            origin = 8
        };

        await _databaseService.SaveItemAsync(item);

        var retrievedItem = await _databaseService.GetItemAsync(12345);

        Assert.NotNull(retrievedItem);
        Assert.Equal(item.itemid, retrievedItem.itemid);
        Assert.Equal(item.defindex, retrievedItem.defindex);
        Assert.Equal(item.paintindex, retrievedItem.paintindex);
        Assert.Equal(item.rarity, retrievedItem.rarity);
        Assert.Equal(item.quality, retrievedItem.quality);
        Assert.Equal(item.paintwear, retrievedItem.paintwear);
        Assert.Equal(item.paintseed, retrievedItem.paintseed);
        Assert.Equal(item.inventory, retrievedItem.inventory);
        Assert.Equal(item.origin, retrievedItem.origin);
    }

    [Fact]
    public async Task SaveItemAsync_ShouldPreserveStatTrakKillCount()
    {
        await _databaseService.InitializeDatabaseAsync();

        // killeatervalue is the live StatTrak kill count; a cache hit must keep the exact
        // count, not just the StatTrak flag.
        var item = new CEconItemPreviewDataBlock
        {
            itemid = 22222, defindex = 7, paintindex = 282, rarity = 5, quality = 9,
            paintwear = 1065353216, paintseed = 1, inventory = 1, origin = 8,
            killeatervalue = 1373
        };

        await _databaseService.SaveItemAsync(item);
        var retrieved = await _databaseService.GetItemAsync(22222);

        Assert.NotNull(retrieved);
        Assert.True(retrieved.ShouldSerializekilleatervalue());
        Assert.Equal(1373u, retrieved.killeatervalue);
    }

    [Fact]
    public async Task SaveItemAsync_NonStatTrak_HasNoKillCount()
    {
        await _databaseService.InitializeDatabaseAsync();

        // No killeatervalue set: a non-StatTrak item must come back without one (not 0 kills).
        var item = new CEconItemPreviewDataBlock
        {
            itemid = 33333, defindex = 7, paintindex = 282, rarity = 5, quality = 4,
            paintwear = 1065353216, paintseed = 1, inventory = 1, origin = 8
        };

        await _databaseService.SaveItemAsync(item);
        var retrieved = await _databaseService.GetItemAsync(33333);

        Assert.NotNull(retrieved);
        Assert.False(retrieved.ShouldSerializekilleatervalue());
    }

    [Fact]
    public async Task SaveItemWithExtrasAsync_ShouldSaveItemWithStickersAndKeychains()
    {
        await _databaseService.InitializeDatabaseAsync();

        var item = new CEconItemPreviewDataBlock
        {
            itemid = 12345,
            defindex = 7,
            paintindex = 10,
            rarity = 3,
            quality = 4,
            paintwear = 1065353216,
            paintseed = 661,
            inventory = 2147483649,
            origin = 8
        };

        item.stickers.Add(new CEconItemPreviewDataBlock.Sticker
        {
            slot = 0,
            sticker_id = 1,
            wear = 0.5f,
            scale = 1.0f,
            rotation = 0.0f
        });

        item.keychains.Add(new CEconItemPreviewDataBlock.Sticker
        {
            slot = 0,
            sticker_id = 100,
            wear = 0.2f
        });

        await _databaseService.SaveItemWithExtrasAsync(item);

        var retrievedItem = await _databaseService.GetItemAsync(12345);

        Assert.NotNull(retrievedItem);
        Assert.Single(retrievedItem.stickers);
        Assert.Single(retrievedItem.keychains);

        var sticker = retrievedItem.stickers[0];
        Assert.Equal(0u, sticker.slot);
        Assert.Equal(1u, sticker.sticker_id);
        Assert.Equal(0.5f, sticker.wear);

        var keychain = retrievedItem.keychains[0];
        Assert.Equal(0u, keychain.slot);
        Assert.Equal(100u, keychain.sticker_id);
        Assert.Equal(0.2f, keychain.wear);
    }

    [Fact]
    public async Task SaveItemWithExtrasAsync_ShouldSkipZeroItemId()
    {
        await _databaseService.InitializeDatabaseAsync();

        // Music kits, graffiti, passes, etc. decode with itemid == 0. They must not
        // be persisted, or they would all collide on the itemid PRIMARY KEY.
        var item = new CEconItemPreviewDataBlock
        {
            itemid = 0,
            defindex = 1314,
            paintindex = 0,
            rarity = 3,
            quality = 4,
            paintwear = 0,
            paintseed = 0,
            inventory = 2147483649,
            origin = 8
        };

        item.stickers.Add(new CEconItemPreviewDataBlock.Sticker { slot = 0, sticker_id = 1, wear = 0.5f });

        await _databaseService.SaveItemWithExtrasAsync(item);

        Assert.Null(await _databaseService.GetItemAsync(0));
        Assert.Empty(await _databaseService.GetStickersAsync(0, true));
    }

    [Fact]
    public async Task SaveItemWithExtrasAsync_ReSavingSameItem_DoesNotDuplicateExtras()
    {
        await _databaseService.InitializeDatabaseAsync();

        CEconItemPreviewDataBlock MakeItem()
        {
            var item = new CEconItemPreviewDataBlock
            {
                itemid = 777,
                defindex = 7,
                paintindex = 10,
                rarity = 3,
                quality = 4,
                paintwear = 1065353216,
                paintseed = 5,
                inventory = 2147483649,
                origin = 8
            };
            item.stickers.Add(new CEconItemPreviewDataBlock.Sticker { slot = 0, sticker_id = 1, wear = 0.1f });
            item.stickers.Add(new CEconItemPreviewDataBlock.Sticker { slot = 1, sticker_id = 2, wear = 0.2f });
            item.keychains.Add(new CEconItemPreviewDataBlock.Sticker { slot = 0, sticker_id = 100, wear = 0.3f });
            return item;
        }

        // Saving the same itemid twice must clear-and-rewrite, not append.
        await _databaseService.SaveItemWithExtrasAsync(MakeItem());
        await _databaseService.SaveItemWithExtrasAsync(MakeItem());

        Assert.Equal(2, (await _databaseService.GetStickersAsync(777, true)).Count);
        Assert.Single(await _databaseService.GetStickersAsync(777, false));
    }

    [Fact]
    public async Task SaveItemWithExtrasAsync_SupportsMultipleStickersInSameSlot()
    {
        await _databaseService.InitializeDatabaseAsync();

        var item = new CEconItemPreviewDataBlock
        {
            itemid = 888,
            defindex = 24,
            paintindex = 688,
            rarity = 4,
            quality = 4,
            paintwear = 1043574843,
            paintseed = 185,
            inventory = 2147483649,
            origin = 8
        };
        // Stacked craft: two stickers share slot 2 (mirrors the live UMP-45 test).
        item.stickers.Add(new CEconItemPreviewDataBlock.Sticker { slot = 2, sticker_id = 4515, wear = 0f });
        item.stickers.Add(new CEconItemPreviewDataBlock.Sticker { slot = 2, sticker_id = 4516, wear = 0f });

        await _databaseService.SaveItemWithExtrasAsync(item);

        var stickers = await _databaseService.GetStickersAsync(888, true);
        Assert.Equal(2, stickers.Count);
        Assert.All(stickers, s => Assert.Equal(2u, s.slot));
        Assert.Equal(new[] { 4515u, 4516u }, stickers.Select(s => s.sticker_id).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task GetItemAsync_ShouldReturnNullForNonExistentItem()
    {
        await _databaseService.InitializeDatabaseAsync();

        var result = await _databaseService.GetItemAsync(99999);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetStickersAsync_ShouldReturnCorrectStickers()
    {
        await _databaseService.InitializeDatabaseAsync();

        var item = new CEconItemPreviewDataBlock
        {
            itemid = 12345,
            defindex = 7,
            paintindex = 10,
            rarity = 3,
            quality = 4,
            paintwear = 1065353216,
            paintseed = 661,
            inventory = 2147483649,
            origin = 8
        };

        item.stickers.Add(new CEconItemPreviewDataBlock.Sticker
        {
            slot = 0,
            sticker_id = 1,
            wear = 0.5f
        });

        item.stickers.Add(new CEconItemPreviewDataBlock.Sticker
        {
            slot = 1,
            sticker_id = 2,
            wear = 0.7f
        });

        await _databaseService.SaveItemWithExtrasAsync(item);

        var stickers = await _databaseService.GetStickersAsync(12345, true);

        Assert.Equal(2, stickers.Count);
        Assert.Equal(0u, stickers[0].slot);
        Assert.Equal(1u, stickers[1].slot);
    }

    [Fact]
    public async Task GetItemsAsync_BatchFetchesItemsWithExtras()
    {
        await _databaseService.InitializeDatabaseAsync();

        var a = new CEconItemPreviewDataBlock
        {
            itemid = 1001, defindex = 7, paintindex = 10, rarity = 3, quality = 4,
            paintwear = 1065353216, paintseed = 1, inventory = 1, origin = 8, killeatervalue = 55
        };
        a.stickers.Add(new CEconItemPreviewDataBlock.Sticker { slot = 0, sticker_id = 5, wear = 0.1f });
        var b = new CEconItemPreviewDataBlock
        {
            itemid = 1002, defindex = 9, paintindex = 20, rarity = 5, quality = 9,
            paintwear = 1043574843, paintseed = 2, inventory = 1, origin = 8
        };
        b.keychains.Add(new CEconItemPreviewDataBlock.Sticker { slot = 0, sticker_id = 200, wear = 0.2f });

        await _databaseService.SaveItemWithExtrasAsync(a);
        await _databaseService.SaveItemWithExtrasAsync(b);

        // Both saved ids, plus a duplicate and one that was never cached.
        var map = await _databaseService.GetItemsAsync(new ulong[] { 1001, 1002, 1002, 9999 });

        Assert.Equal(2, map.Count);
        Assert.False(map.ContainsKey(9999));

        var got = map[1001];
        Assert.Equal(7u, got.defindex);
        Assert.True(got.ShouldSerializekilleatervalue());
        Assert.Equal(55u, got.killeatervalue);
        Assert.Single(got.stickers);
        Assert.Equal(5u, got.stickers[0].sticker_id);

        Assert.Single(map[1002].keychains);
        Assert.Equal(200u, map[1002].keychains[0].sticker_id);
        Assert.False(map[1002].ShouldSerializekilleatervalue());
    }

    [Fact]
    public async Task GetItemsAsync_MatchesGetItemAsync_ForOneItem()
    {
        await _databaseService.InitializeDatabaseAsync();

        var item = new CEconItemPreviewDataBlock
        {
            itemid = 555, defindex = 24, paintindex = 688, rarity = 4, quality = 4,
            paintwear = 1043574843, paintseed = 185, inventory = 2147483649, origin = 8
        };
        item.stickers.Add(new CEconItemPreviewDataBlock.Sticker { slot = 2, sticker_id = 4515, wear = 0f });
        item.stickers.Add(new CEconItemPreviewDataBlock.Sticker { slot = 2, sticker_id = 4516, wear = 0f });
        await _databaseService.SaveItemWithExtrasAsync(item);

        var single = await _databaseService.GetItemAsync(555);
        var batched = (await _databaseService.GetItemsAsync(new ulong[] { 555 }))[555];

        Assert.NotNull(single);
        Assert.Equal(single.defindex, batched.defindex);
        Assert.Equal(single.paintseed, batched.paintseed);
        Assert.Equal(single.stickers.Count, batched.stickers.Count);
        Assert.Equal(
            single.stickers.Select(s => s.sticker_id).OrderBy(x => x),
            batched.stickers.Select(s => s.sticker_id).OrderBy(x => x));
    }

    [Fact]
    public async Task GetItemsAsync_EmptyInput_ReturnsEmpty()
    {
        await _databaseService.InitializeDatabaseAsync();
        Assert.Empty(await _databaseService.GetItemsAsync(Array.Empty<ulong>()));
    }

    [Fact]
    public async Task GetLastWarmAsync_UnknownSteamId_ReturnsNull()
    {
        await _databaseService.InitializeDatabaseAsync();

        var lastWarmed = await _databaseService.GetLastWarmAsync(76561198000000001);

        Assert.Null(lastWarmed);
    }

    [Fact]
    public async Task RecordWarmAsync_RoundTripsUtcTimestamp()
    {
        await _databaseService.InitializeDatabaseAsync();
        const ulong steamid = 76561198000000001;

        var before = DateTime.UtcNow.AddSeconds(-1);
        await _databaseService.RecordWarmAsync(steamid, 42);
        var lastWarmed = await _databaseService.GetLastWarmAsync(steamid);
        var after = DateTime.UtcNow.AddSeconds(1);

        Assert.NotNull(lastWarmed);
        Assert.Equal(DateTimeKind.Utc, lastWarmed.Value.Kind);
        Assert.InRange(lastWarmed.Value, before, after);
    }

    [Fact]
    public async Task RecordWarmAsync_SecondWarm_ReplacesRow()
    {
        await _databaseService.InitializeDatabaseAsync();
        const ulong steamid = 76561198000000001;

        await _databaseService.RecordWarmAsync(steamid, 0);
        var firstWarm = await _databaseService.GetLastWarmAsync(steamid);
        await Task.Delay(20);
        await _databaseService.RecordWarmAsync(steamid, 196);
        var secondWarm = await _databaseService.GetLastWarmAsync(steamid);

        Assert.NotNull(firstWarm);
        Assert.NotNull(secondWarm);
        Assert.True(secondWarm > firstWarm);

        // One row per steamid, holding the latest count
        using var connection = new SqliteConnection($"Data Source={_testDbPath};foreign keys=true;");
        await connection.OpenAsync();
        var command = new SqliteCommand("SELECT COUNT(*), MAX(items_cached) FROM inventory_warms", connection);
        using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(196, reader.GetInt32(1));
    }
}