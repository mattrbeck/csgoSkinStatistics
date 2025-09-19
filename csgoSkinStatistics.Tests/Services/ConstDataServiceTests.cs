using CSGOSkinAPI.Services;
using CSGOSkinAPI.Models;
using SteamKit2.GC.CSGO.Internal;
using System.Text.Json;
using Xunit;

namespace csgoSkinStatistics.Tests.Services;

public class ConstDataServiceTests : IDisposable
{
    private readonly string _testConstFilePath = "test_const.json";

    public void Dispose()
    {
        if (File.Exists(_testConstFilePath))
        {
            File.Delete(_testConstFilePath);
        }
    }

    [Fact]
    public void Constructor_ShouldLoadConstDataFromFile()
    {
        var testData = new ConstData
        {
            Items = new Dictionary<string, string>
            {
                { "7", "AK-47" },
                { "1", "Desert Eagle" }
            },
            Skins = new Dictionary<string, string>
            {
                { "179", "Redline" },
                { "38", "Blaze" }
            }
        };

        var json = JsonSerializer.Serialize(testData);
        File.WriteAllText(_testConstFilePath, json);

        // Create a temporary copy for the service to use
        File.Copy(_testConstFilePath, "const.json", true);

        var service = new ConstDataService();

        var ak47Item = new CEconItemPreviewDataBlock { defindex = 7, paintindex = 179 };
        var itemInfo = service.GetItemInformation(ak47Item);

        Assert.Equal("AK-47", itemInfo.Type);
        Assert.Equal("Redline", itemInfo.Name);

        // Cleanup
        File.Delete("const.json");
    }

    [Fact]
    public void GetItemInformation_ShouldReturnCorrectItemInformation()
    {
        var testData = new ConstData
        {
            Items = new Dictionary<string, string>
            {
                { "7", "AK-47" }
            },
            Skins = new Dictionary<string, string>
            {
                { "179", "Redline" }
            }
        };

        var json = JsonSerializer.Serialize(testData);
        File.WriteAllText("const.json", json);

        var service = new ConstDataService();
        var item = new CEconItemPreviewDataBlock
        {
            defindex = 7,
            paintindex = 179,
            paintseed = 123
        };

        var result = service.GetItemInformation(item);

        Assert.Equal("AK-47", result.Type);
        Assert.Equal("Redline", result.Name);
        Assert.Equal("", result.Special); // No special pattern for regular items

        // Cleanup
        File.Delete("const.json");
    }

    [Fact]
    public void GetItemInformation_ShouldHandleMarbleFadeFireIce()
    {
        var testData = new ConstData
        {
            Items = new Dictionary<string, string>
            {
                { "42", "Karambit" }
            },
            Skins = new Dictionary<string, string>
            {
                { "413", "Marble Fade" }
            },
            Fireice = new[] { "Karambit" },
            FireiceOrder = new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }
        };

        var json = JsonSerializer.Serialize(testData);
        File.WriteAllText("const.json", json);

        var service = new ConstDataService();
        var item = new CEconItemPreviewDataBlock
        {
            defindex = 42,
            paintindex = 413,
            paintseed = 1 // Should correspond to "1st Max" in FireIceNames
        };

        var result = service.GetItemInformation(item);

        Assert.Equal("Karambit", result.Type);
        Assert.Equal("Marble Fade", result.Name);
        Assert.Equal("1st Max", result.Special);

        // Cleanup
        File.Delete("const.json");
    }

    [Fact]
    public void GetItemInformation_ShouldHandleFadePattern()
    {
        var testData = new ConstData
        {
            Items = new Dictionary<string, string>
            {
                { "42", "Karambit" }
            },
            Skins = new Dictionary<string, string>
            {
                { "38", "Fade" }
            },
            Fades = new Dictionary<string, bool>
            {
                { "Karambit", false }
            },
            FadeOrder = Enumerable.Range(0, 1001).ToArray()
        };

        var json = JsonSerializer.Serialize(testData);
        File.WriteAllText("const.json", json);

        var service = new ConstDataService();
        var item = new CEconItemPreviewDataBlock
        {
            defindex = 42,
            paintindex = 38,
            paintseed = 500 // Middle fade
        };

        var result = service.GetItemInformation(item);

        Assert.Equal("Karambit", result.Type);
        Assert.Equal("Fade", result.Name);
        Assert.Contains("%", result.Special); // Should contain percentage

        // Cleanup
        File.Delete("const.json");
    }

    [Fact]
    public void GetItemInformation_ShouldHandleDopplerPhase()
    {
        var testData = new ConstData
        {
            Items = new Dictionary<string, string>
            {
                { "42", "Karambit" }
            },
            Skins = new Dictionary<string, string>
            {
                { "415", "Doppler" }
            },
            Doppler = new Dictionary<string, string>
            {
                { "415", "Phase 1" }
            }
        };

        var json = JsonSerializer.Serialize(testData);
        File.WriteAllText("const.json", json);

        var service = new ConstDataService();
        var item = new CEconItemPreviewDataBlock
        {
            defindex = 42,
            paintindex = 415,
            paintseed = 123
        };

        var result = service.GetItemInformation(item);

        Assert.Equal("Karambit", result.Type);
        Assert.Equal("Doppler", result.Name);
        Assert.Equal("Phase 1", result.Special);

        // Cleanup
        File.Delete("const.json");
    }

    [Fact]
    public void GetItemInformation_ShouldHandleMissingItemInConstants()
    {
        var testData = new ConstData
        {
            Items = new Dictionary<string, string>(),
            Skins = new Dictionary<string, string>()
        };

        var json = JsonSerializer.Serialize(testData);
        File.WriteAllText("const.json", json);

        var service = new ConstDataService();
        var item = new CEconItemPreviewDataBlock
        {
            defindex = 999, // Non-existent item
            paintindex = 999, // Non-existent skin
            paintseed = 123
        };

        var result = service.GetItemInformation(item);

        Assert.Equal("", result.Type);
        Assert.Equal("", result.Name);
        Assert.Equal("", result.Special);

        // Cleanup
        File.Delete("const.json");
    }
}