using CSGOSkinAPI.Models;
using System.Text.Json;
using Xunit;

namespace csgoSkinStatistics.Tests.Models;

public class ModelsTests
{
    [Fact]
    public void SteamAccount_ShouldSerializeCorrectly()
    {
        var account = new SteamAccount
        {
            Username = "testuser",
            Password = "testpass"
        };

        var json = JsonSerializer.Serialize(account);
        var deserialized = JsonSerializer.Deserialize<SteamAccount>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("testuser", deserialized.Username);
        Assert.Equal("testpass", deserialized.Password);
    }

    [Fact]
    public void SteamAccount_ShouldHaveDefaultValues()
    {
        var account = new SteamAccount();

        Assert.Equal(string.Empty, account.Username);
        Assert.Equal(string.Empty, account.Password);
    }

    [Fact]
    public void ItemInformation_ShouldInitializeCorrectly()
    {
        var itemInfo = new ItemInformation
        {
            Name = "Redline",
            Type = "AK-47",
            Special = "StatTrak™"
        };

        Assert.Equal("Redline", itemInfo.Name);
        Assert.Equal("AK-47", itemInfo.Type);
        Assert.Equal("StatTrak™", itemInfo.Special);
    }

    [Fact]
    public void ItemInformation_ShouldHaveDefaultValues()
    {
        var itemInfo = new ItemInformation();

        Assert.Equal(string.Empty, itemInfo.Name);
        Assert.Equal(string.Empty, itemInfo.Type);
        Assert.Equal(string.Empty, itemInfo.Special);
    }

    [Fact]
    public void ConstData_ShouldSerializeCorrectly()
    {
        var constData = new ConstData
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
            },
            Fades = new Dictionary<string, bool>
            {
                { "Karambit", false },
                { "Butterfly Knife", true }
            },
            FadeOrder = new[] { 0, 1, 2, 3, 4, 5 },
            Fireice = new[] { "Karambit", "M9 Bayonet" },
            FireiceOrder = new[] { 0, 1, 2, 3 },
            Doppler = new Dictionary<string, string>
            {
                { "415", "Phase 1" },
                { "416", "Phase 2" }
            },
            Kimonos = new Dictionary<string, string>
            {
                { "123", "BTA Red" },
                { "456", "BTA Blue" }
            }
        };

        var json = JsonSerializer.Serialize(constData);
        var deserialized = JsonSerializer.Deserialize<ConstData>(json);

        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized.Items.Count);
        Assert.Equal("AK-47", deserialized.Items["7"]);
        Assert.Equal(2, deserialized.Skins.Count);
        Assert.Equal("Redline", deserialized.Skins["179"]);
        Assert.Equal(2, deserialized.Fades.Count);
        Assert.False(deserialized.Fades["Karambit"]);
        Assert.Equal(6, deserialized.FadeOrder.Length);
        Assert.Equal(2, deserialized.Fireice.Length);
        Assert.Equal(4, deserialized.FireiceOrder.Length);
        Assert.Equal(2, deserialized.Doppler.Count);
        Assert.Equal("Phase 1", deserialized.Doppler["415"]);
        Assert.Equal(2, deserialized.Kimonos.Count);
        Assert.Equal("BTA Red", deserialized.Kimonos["123"]);
    }

    [Fact]
    public void ConstData_FireIceNames_ShouldHaveCorrectValues()
    {
        var fireIceNames = ConstData.FireIceNames;

        Assert.Equal(12, fireIceNames.Length);
        Assert.Equal("", fireIceNames[0]);
        Assert.Equal("1st Max", fireIceNames[1]);
        Assert.Equal("2nd Max", fireIceNames[2]);
        Assert.Equal("10th Max", fireIceNames[10]);
        Assert.Equal("FFI", fireIceNames[11]);
    }

    [Fact]
    public void SteamInventoryResponse_ShouldSerializeCorrectly()
    {
        var response = new SteamInventoryResponse
        {
            assets = new List<SteamAsset>
            {
                new()
                {
                    appid = 730,
                    contextid = "2",
                    assetid = "12345",
                    classid = "67890",
                    instanceid = "0",
                    amount = "1"
                }
            },
            descriptions = new List<SteamDescription>
            {
                new()
                {
                    appid = 730,
                    classid = "67890",
                    instanceid = "0",
                    name = "AK-47 | Redline",
                    market_name = "AK-47 | Redline (Field-Tested)",
                    type = "Rifle"
                }
            },
            total = 100,
            success = 1
        };

        var json = JsonSerializer.Serialize(response);
        var deserialized = JsonSerializer.Deserialize<SteamInventoryResponse>(json);

        Assert.NotNull(deserialized);
        Assert.Single(deserialized.assets);
        Assert.Single(deserialized.descriptions);
        Assert.Equal(730, deserialized.assets[0].appid);
        Assert.Equal("12345", deserialized.assets[0].assetid);
        Assert.Equal("AK-47 | Redline", deserialized.descriptions[0].name);
        Assert.Equal(100, deserialized.total);
        Assert.Equal(1, deserialized.success);
    }

    [Fact]
    public void SteamAsset_ShouldHaveCorrectProperties()
    {
        var asset = new SteamAsset
        {
            appid = 730,
            contextid = "2",
            assetid = "12345",
            classid = "67890",
            instanceid = "0",
            amount = "1"
        };

        Assert.Equal(730, asset.appid);
        Assert.Equal("2", asset.contextid);
        Assert.Equal("12345", asset.assetid);
        Assert.Equal("67890", asset.classid);
        Assert.Equal("0", asset.instanceid);
        Assert.Equal("1", asset.amount);
    }

    [Fact]
    public void SteamDescription_ShouldHaveCorrectProperties()
    {
        var description = new SteamDescription
        {
            appid = 730,
            classid = "67890",
            instanceid = "0",
            name = "AK-47 | Redline",
            market_name = "AK-47 | Redline (Field-Tested)",
            market_hash_name = "AK-47 | Redline (Field-Tested)",
            name_color = "D2D2D2",
            background_color = "3C352E",
            icon_url = "icon_url_here",
            icon_url_large = "icon_url_large_here",
            type = "Rifle",
            tradable = 1,
            marketable = 1,
            commodity = 0,
            market_tradable_restriction = 7
        };

        Assert.Equal(730, description.appid);
        Assert.Equal("67890", description.classid);
        Assert.Equal("0", description.instanceid);
        Assert.Equal("AK-47 | Redline", description.name);
        Assert.Equal("AK-47 | Redline (Field-Tested)", description.market_name);
        Assert.Equal("D2D2D2", description.name_color);
        Assert.Equal("Rifle", description.type);
        Assert.Equal(1, description.tradable);
        Assert.Equal(1, description.marketable);
    }

    [Fact]
    public void SteamAction_ShouldSerializeCorrectly()
    {
        var action = new SteamAction
        {
            link = "steam://rungame/730/76561202255233023/+csgo_econ_action_preview%20S%owner_steamid%A%assetid%D123",
            name = "Inspect in Game..."
        };

        var json = JsonSerializer.Serialize(action);
        var deserialized = JsonSerializer.Deserialize<SteamAction>(json);

        Assert.NotNull(deserialized);
        Assert.Contains("csgo_econ_action_preview", deserialized.link);
        Assert.Equal("Inspect in Game...", deserialized.name);
    }

    [Fact]
    public void SteamTag_ShouldSerializeCorrectly()
    {
        var tag = new SteamTag
        {
            category = "Exterior",
            internal_name = "CSGO_Type_WeaponCase",
            localized_category_name = "Exterior",
            localized_tag_name = "Field-Tested"
        };

        var json = JsonSerializer.Serialize(tag);
        var deserialized = JsonSerializer.Deserialize<SteamTag>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("Exterior", deserialized.category);
        Assert.Equal("CSGO_Type_WeaponCase", deserialized.internal_name);
        Assert.Equal("Exterior", deserialized.localized_category_name);
        Assert.Equal("Field-Tested", deserialized.localized_tag_name);
    }

    [Fact]
    public void SteamItemDescription_ShouldSerializeCorrectly()
    {
        var itemDesc = new SteamItemDescription
        {
            type = "html",
            value = "This is a weapon finish."
        };

        var json = JsonSerializer.Serialize(itemDesc);
        var deserialized = JsonSerializer.Deserialize<SteamItemDescription>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("html", deserialized.type);
        Assert.Equal("This is a weapon finish.", deserialized.value);
    }
}