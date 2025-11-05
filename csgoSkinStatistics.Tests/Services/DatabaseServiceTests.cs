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
}