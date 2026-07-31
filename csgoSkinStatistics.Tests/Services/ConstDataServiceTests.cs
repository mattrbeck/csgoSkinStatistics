using CSGOSkinAPI.Models;
using SteamKit2.GC.CSGO.Internal;
using Xunit;

namespace csgoSkinStatistics.Tests.Services;

// Each test builds its catalogs in its own directory (see CatalogDirectory) rather than writing
// them into the shared test working directory, so these can't race - or leak stub catalogs into -
// any other test that constructs a ConstDataService.
public class ConstDataServiceTests
{
    private static ConstData AkRedline() => new()
    {
        Items = new Dictionary<string, string> { { "7", "AK-47" }, { "1", "Desert Eagle" } },
        Skins = new Dictionary<string, string> { { "179", "Redline" }, { "38", "Blaze" } }
    };

    [Fact]
    public void Constructor_ShouldLoadConstDataFromFile()
    {
        using var catalogs = CatalogDirectory.Create(AkRedline());
        var service = catalogs.Build();

        var itemInfo = service.GetItemInformation(new CEconItemPreviewDataBlock { defindex = 7, paintindex = 179 });

        Assert.Equal("AK-47", itemInfo.Type);
        Assert.Equal("Redline", itemInfo.Name);
    }

    [Fact]
    public void GetItemInformation_ShouldReturnCorrectItemInformation()
    {
        using var catalogs = CatalogDirectory.Create(new ConstData
        {
            Items = new Dictionary<string, string> { { "7", "AK-47" } },
            Skins = new Dictionary<string, string> { { "179", "Redline" } }
        });
        var service = catalogs.Build();

        var result = service.GetItemInformation(new CEconItemPreviewDataBlock
        {
            defindex = 7,
            paintindex = 179,
            paintseed = 123
        });

        Assert.Equal("AK-47", result.Type);
        Assert.Equal("Redline", result.Name);
    }

    [Fact]
    public void GetItemInformation_ShouldHandleFireIcePattern()
    {
        using var catalogs = CatalogDirectory.Create(new ConstData
        {
            Items = new Dictionary<string, string> { { "42", "Karambit" } },
            Skins = new Dictionary<string, string> { { "413", "Marble Fade" } },
            Fireice = new[] { "Karambit" },
            FireiceOrder = new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 }
        });
        var service = catalogs.Build();

        var result = service.GetItemInformation(new CEconItemPreviewDataBlock
        {
            defindex = 42,
            paintindex = 413,
            paintseed = 1 // Should correspond to "1st Max" in FireIceNames
        });

        Assert.Equal("Karambit", result.Type);
        Assert.Equal("Marble Fade", result.Name);
        Assert.Equal("1st Max", result.Special);
    }

    [Fact]
    public void GetItemInformation_ShouldHandleFadePattern()
    {
        // Fade percentages come from fade.json (scripts/update_fade.js) as a per-weapon
        // seed -> % table, not a shared rank table.
        var fadeTable = new double[1001];
        fadeTable[500] = 92.7;

        using var catalogs = CatalogDirectory.Create(new ConstData
        {
            Items = new Dictionary<string, string> { { "42", "Karambit" } },
            Skins = new Dictionary<string, string> { { "38", "Fade" } }
        }).WithFade(new() { ["Fade"] = new() { ["Karambit"] = fadeTable } });
        var service = catalogs.Build();

        var result = service.GetItemInformation(new CEconItemPreviewDataBlock
        {
            defindex = 42,
            paintindex = 38,
            paintseed = 500
        });

        Assert.Equal("Karambit", result.Type);
        Assert.Equal("Fade", result.Name);
        Assert.Equal("92.7%", result.Special);
    }

    [Fact]
    public void GetItemInformation_FadeWithOutOfRangePaintseed_DoesNotThrow()
    {
        // A crafted item cert can carry a paintseed beyond the fade table; it must not throw, and it
        // falls through unlabelled rather than reporting a bogus percentage.
        using var catalogs = CatalogDirectory.Create(new ConstData
        {
            Items = new Dictionary<string, string> { { "42", "Karambit" } },
            Skins = new Dictionary<string, string> { { "38", "Fade" } }
        }).WithFade(new() { ["Fade"] = new() { ["Karambit"] = new double[1001] } });
        var service = catalogs.Build();

        var result = service.GetItemInformation(new CEconItemPreviewDataBlock
        {
            defindex = 42,
            paintindex = 38,
            paintseed = 999999
        });

        Assert.Equal("Fade", result.Name);
        Assert.Equal("", result.Special);
    }

    [Fact]
    public void GetItemInformation_MarbleFadeWithOutOfRangePaintseed_DoesNotThrow()
    {
        using var catalogs = CatalogDirectory.Create(new ConstData
        {
            Items = new Dictionary<string, string> { { "42", "Karambit" } },
            Skins = new Dictionary<string, string> { { "413", "Marble Fade" } },
            Fireice = new[] { "Karambit" },
            FireiceOrder = new[] { 0, 1, 2, 3 }
        });
        var service = catalogs.Build();

        var result = service.GetItemInformation(new CEconItemPreviewDataBlock
        {
            defindex = 42,
            paintindex = 413,
            paintseed = 999999
        });

        Assert.Equal("Marble Fade", result.Name);
        Assert.Equal("", result.Special); // Out of range -> no special label, no throw
    }

    [Fact]
    public void GetItemInformation_ShouldHandleDopplerPhase()
    {
        using var catalogs = CatalogDirectory.Create(new ConstData
        {
            Items = new Dictionary<string, string> { { "42", "Karambit" } },
            Skins = new Dictionary<string, string> { { "415", "Doppler" } },
            Doppler = new Dictionary<string, string> { { "415", "Phase 1" } }
        });
        var service = catalogs.Build();

        var result = service.GetItemInformation(new CEconItemPreviewDataBlock
        {
            defindex = 42,
            paintindex = 415,
            paintseed = 123
        });

        Assert.Equal("Karambit", result.Type);
        Assert.Equal("Doppler", result.Name);
        Assert.Equal("Phase 1", result.Special);
    }

    [Fact]
    public void GetItemInformation_ShouldHandleMissingItemInConstants()
    {
        using var catalogs = CatalogDirectory.Create(new ConstData
        {
            Items = new Dictionary<string, string>(),
            Skins = new Dictionary<string, string>()
        });
        var service = catalogs.Build();

        var result = service.GetItemInformation(new CEconItemPreviewDataBlock
        {
            defindex = 999,  // Non-existent item
            paintindex = 999, // Non-existent skin
            paintseed = 123
        });

        Assert.Equal("", result.Type);
        Assert.Equal("", result.Name);
        Assert.Equal("", result.Special);
    }
}
