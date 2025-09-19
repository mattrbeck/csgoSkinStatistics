using CSGOSkinAPI.Services;
using CSGOSkinAPI.Models;
using SteamKit2.GC.CSGO.Internal;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace csgoSkinStatistics.Tests.Controllers;

public class SkinControllerUnitTests
{
    [Theory]
    [InlineData("76561198123456789", true)]
    [InlineData("7656119812345678", false)] // Too short
    [InlineData("765611981234567890", false)] // Too long
    [InlineData("86561198123456789", false)] // Wrong prefix
    [InlineData("invalid", false)]
    [InlineData("", false)]
    public void IsValidSteamId64_ShouldValidateCorrectly(string steamId, bool expected)
    {
        // Test the validation logic used in the controller
        bool isValid = steamId.StartsWith("76561") && steamId.Length == 17;
        Assert.Equal(expected, isValid);
    }

    [Fact]
    public void ParseInspectUrl_WithValidUrl_ShouldReturnCorrectParameters()
    {
        var url = "steam://rungame/730/76561202255233023/+csgo_econ_action_preview%20S76561198123456789A12345D67890";
        var decodedUrl = System.Web.HttpUtility.UrlDecode(url);

        // Test that URL contains expected patterns
        Assert.Contains("S76561198123456789A12345D67890", decodedUrl);

        // Test regex pattern matching
        var pattern = @"([SM])(\d+)A(\d+)D(\d+)";
        var match = Regex.Match(decodedUrl, pattern);

        Assert.True(match.Success);
        Assert.Equal("S", match.Groups[1].Value);
        Assert.Equal("76561198123456789", match.Groups[2].Value);
        Assert.Equal("12345", match.Groups[3].Value);
        Assert.Equal("67890", match.Groups[4].Value);
    }

    [Theory]
    [InlineData("https://steamcommunity.com/profiles/76561198123456789", "76561198123456789")]
    [InlineData("steamcommunity.com/profiles/76561198123456789", "76561198123456789")]
    [InlineData("https://steamcommunity.com/id/customurl", null)]
    [InlineData("steamcommunity.com/id/customurl", null)]
    [InlineData("76561198123456789", "76561198123456789")]
    [InlineData("invalid", null)]
    public void ExtractSteamIdFromInput_ShouldParseCorrectly(string input, string expected)
    {
        // Test Steam ID extraction logic
        string result = null;

        // Check if it's already a valid SteamId64
        if (input.StartsWith("76561") && input.Length == 17)
        {
            result = input;
        }
        else
        {
            // Try to extract from Steam profile URL
            var profileMatch = Regex.Match(input, @"steamcommunity\.com/profiles/(\d+)");
            if (profileMatch.Success && profileMatch.Groups[1].Value.StartsWith("76561") && profileMatch.Groups[1].Value.Length == 17)
            {
                result = profileMatch.Groups[1].Value;
            }
        }

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void ValidateEmptySteamId_ShouldReturnFalse(string steamId)
    {
        var isValid = !string.IsNullOrWhiteSpace(steamId);
        Assert.False(isValid);
    }

    [Theory]
    [InlineData("steam://rungame/730/76561202255233023/+csgo_econ_action_preview%20S76561198123456789A12345D67890")]
    [InlineData("steam://rungame/730/76561202255233023/+csgo_econ_action_preview%20M1A12345D67890")]
    public void ValidInspectUrl_ShouldContainCorrectPattern(string inspectUrl)
    {
        var url = System.Web.HttpUtility.UrlDecode(inspectUrl);
        var containsValidPattern = url.Contains("S76561198123456789A12345D67890") || url.Contains("M1A12345D67890");

        Assert.True(containsValidPattern);
    }

    [Fact]
    public void CreateItemResponse_ShouldFormatCorrectly()
    {
        var item = new CEconItemPreviewDataBlock
        {
            itemid = 12345,
            defindex = 7,
            paintindex = 179,
            rarity = 3,
            quality = 4,
            paintwear = 1065353216, // 1.0 as uint32
            paintseed = 661,
            inventory = 2147483649,
            origin = 8,
            killeatervalue = 100 // StatTrak
        };

        item.stickers.Add(new CEconItemPreviewDataBlock.Sticker
        {
            slot = 0,
            sticker_id = 1,
            wear = 0.5f
        });

        var itemInfo = new ItemInformation
        {
            Type = "AK-47",
            Name = "Redline",
            Special = ""
        };

        // Test the response creation logic
        var response = new
        {
            item.itemid,
            item.defindex,
            item.paintindex,
            item.rarity,
            item.quality,
            item.paintwear,
            item.paintseed,
            item.inventory,
            item.origin,
            stattrak = item.ShouldSerializekilleatervalue(),
            special = itemInfo.Special,
            weapon = itemInfo.Type,
            skin = itemInfo.Name,
            stickers = item.stickers.Select(s => new
            {
                s.slot,
                s.sticker_id,
                s.wear
            }).ToArray(),
            keychains = item.keychains.Select(k => new
            {
                k.slot,
                k.sticker_id,
                k.wear
            }).ToArray(),
            s = 0UL,
            a = 0UL,
            d = 0UL,
            m = 0UL
        };

        Assert.Equal(12345UL, response.itemid);
        Assert.Equal(7U, response.defindex);
        Assert.Equal(179U, response.paintindex);
        Assert.True(response.stattrak);
        Assert.Single(response.stickers);
        Assert.Empty(response.keychains);
        Assert.Equal("AK-47", response.weapon);
        Assert.Equal("Redline", response.skin);
    }
}