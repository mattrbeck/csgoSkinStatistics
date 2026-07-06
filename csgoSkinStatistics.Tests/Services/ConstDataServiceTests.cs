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
            }
        };

        var json = JsonSerializer.Serialize(testData);
        File.WriteAllText("const.json", json);

        // Fade percentages now come from fade.json (scripts/update_fade.js) as a per-weapon
        // seed -> % table, not a shared rank table.
        var fadeTable = new double[1001];
        fadeTable[500] = 92.7;
        WriteFadeJson(new() { ["Fade"] = new() { ["Karambit"] = fadeTable } });

        var service = new ConstDataService();
        var item = new CEconItemPreviewDataBlock
        {
            defindex = 42,
            paintindex = 38,
            paintseed = 500
        };

        var result = service.GetItemInformation(item);

        Assert.Equal("Karambit", result.Type);
        Assert.Equal("Fade", result.Name);
        Assert.Equal("92.7%", result.Special);

        // Cleanup
        File.Delete("const.json");
        File.Delete("fade.json");
    }

    [Fact]
    public void GetItemInformation_FadeWithOutOfRangePaintseed_DoesNotThrow()
    {
        // A crafted item cert can carry a paintseed beyond the fade table; it must not throw, and it
        // falls through unlabelled rather than reporting a bogus percentage.
        var testData = new ConstData
        {
            Items = new Dictionary<string, string> { { "42", "Karambit" } },
            Skins = new Dictionary<string, string> { { "38", "Fade" } }
        };
        File.WriteAllText("const.json", JsonSerializer.Serialize(testData));
        WriteFadeJson(new() { ["Fade"] = new() { ["Karambit"] = new double[1001] } });

        var service = new ConstDataService();
        var item = new CEconItemPreviewDataBlock { defindex = 42, paintindex = 38, paintseed = 999999 };

        var result = service.GetItemInformation(item);

        Assert.Equal("Fade", result.Name);
        Assert.Equal("", result.Special);

        File.Delete("const.json");
        File.Delete("fade.json");
    }

    private static void WriteFadeJson(Dictionary<string, Dictionary<string, double[]>> fade)
        => File.WriteAllText("fade.json", JsonSerializer.Serialize(fade));

    [Fact]
    public void GetItemInformation_MarbleFadeWithOutOfRangePaintseed_DoesNotThrow()
    {
        var testData = new ConstData
        {
            Items = new Dictionary<string, string> { { "42", "Karambit" } },
            Skins = new Dictionary<string, string> { { "413", "Marble Fade" } },
            Fireice = new[] { "Karambit" },
            FireiceOrder = new[] { 0, 1, 2, 3 }
        };
        File.WriteAllText("const.json", JsonSerializer.Serialize(testData));

        var service = new ConstDataService();
        var item = new CEconItemPreviewDataBlock { defindex = 42, paintindex = 413, paintseed = 999999 };

        var result = service.GetItemInformation(item);

        Assert.Equal("Marble Fade", result.Name);
        Assert.Equal("", result.Special); // Out of range -> no special label, no throw

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