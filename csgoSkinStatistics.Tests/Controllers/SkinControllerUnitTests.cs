using System.Text.Json;
using CSGOSkinAPI.Controllers;
using CSGOSkinAPI.Models;
using CSGOSkinAPI.Services;
using ProtoBuf;
using SteamKit2.GC.CSGO.Internal;
using Xunit;

namespace csgoSkinStatistics.Tests.Controllers;

// These tests resolve real decal names, so the fixture takes the shipped stickers.json. It is a few
// MB, so parse it once and share the service across every test in the class.
public sealed class ConstDataFixture : IDisposable
{
    private readonly CatalogDirectory _catalogs = CatalogDirectory.Create().WithShippedCatalog("stickers.json");

    public ConstDataService Service { get; }

    public ConstDataFixture() => Service = _catalogs.Build();

    public void Dispose() => _catalogs.Dispose();
}

// Exercises the real SkinController DTO builders and inspect-URL parser through InternalsVisibleTo.
// These assert on the serialized JSON rather than the anonymous types, because the JSON *is* the
// contract: the browser reads these field names straight off the response.
public class SkinControllerUnitTests(ConstDataFixture fixture) : IClassFixture<ConstDataFixture>
{
    private readonly ConstDataService _constData = fixture.Service;

    private static JsonElement ToJson(object dto) => JsonSerializer.SerializeToElement(dto);

    // Ids chosen from the shipped stickers.json: 1 exists in *both* catalogs under different names,
    // which is what lets the slab tests below prove which catalog a lookup went through.
    private const uint StickerAndKeychainId = 1;      // sticker "Shooter" / keychain "Lil' Ava"
    private const uint UnknownDecalId = 4294967295;   // in neither catalog

    // --- sticker DTO -------------------------------------------------------------------

    [Fact]
    public void MakeStickerDto_ExposesPlacementFieldsWhenPresent()
    {
        var sticker = new CEconItemPreviewDataBlock.Sticker
        {
            slot = 0,
            sticker_id = StickerAndKeychainId,
            wear = 0.25f,
            rotation = -14.5f,
            offset_x = 0.125f,
            offset_y = -0.5f,
        };

        var json = ToJson(SkinController.MakeStickerDto(sticker, _constData));

        Assert.Equal(StickerAndKeychainId, json.GetProperty("sticker_id").GetUInt32());
        Assert.Equal(0.25f, json.GetProperty("wear").GetSingle());
        Assert.Equal(-14.5f, json.GetProperty("rotation").GetSingle());
        Assert.Equal(0.125f, json.GetProperty("offset_x").GetSingle());
        Assert.Equal(-0.5f, json.GetProperty("offset_y").GetSingle());
    }

    [Fact]
    public void MakeStickerDto_PlacementFieldsAreNullWhenTheGcOmitsThem()
    {
        // Most applied stickers carry no placement data at all; those must serialize as null so the
        // client can tell "not positioned" from "positioned at the origin".
        var sticker = new CEconItemPreviewDataBlock.Sticker
        {
            slot = 0,
            sticker_id = StickerAndKeychainId,
            wear = 0.25f,
        };

        var json = ToJson(SkinController.MakeStickerDto(sticker, _constData));

        Assert.Equal(JsonValueKind.Null, json.GetProperty("rotation").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("offset_x").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("offset_y").ValueKind);
    }

    [Fact]
    public void MakeStickerDto_ZeroPlacementIsNotNull()
    {
        var sticker = new CEconItemPreviewDataBlock.Sticker
        {
            slot = 0,
            sticker_id = StickerAndKeychainId,
            wear = 0f,
            rotation = 0f,
            offset_x = 0f,
            offset_y = 0f,
        };

        var json = ToJson(SkinController.MakeStickerDto(sticker, _constData));

        Assert.Equal(0f, json.GetProperty("rotation").GetSingle());
        Assert.Equal(0f, json.GetProperty("offset_x").GetSingle());
        Assert.Equal(0f, json.GetProperty("offset_y").GetSingle());
    }

    [Fact]
    public void MakeStickerDto_ResolvesNameAndImageFromTheStickerCatalog()
    {
        var sticker = new CEconItemPreviewDataBlock.Sticker { slot = 0, sticker_id = StickerAndKeychainId, wear = 0f };

        var json = ToJson(SkinController.MakeStickerDto(sticker, _constData));

        var expected = _constData.ResolveSticker(StickerAndKeychainId);
        Assert.NotNull(expected);
        Assert.Equal(expected.Name, json.GetProperty("name").GetString());
        Assert.Equal(expected.Image, json.GetProperty("image").GetString());
    }

    [Fact]
    public void MakeStickerDto_UnknownDecalFallsBackToEmptyStrings()
    {
        // A sticker newer than the shipped catalog must still render - the client shows a labeled
        // placeholder rather than breaking on a null.
        var sticker = new CEconItemPreviewDataBlock.Sticker { slot = 0, sticker_id = UnknownDecalId, wear = 0f };

        var json = ToJson(SkinController.MakeStickerDto(sticker, _constData));

        Assert.Equal("", json.GetProperty("name").GetString());
        Assert.Equal("", json.GetProperty("image").GetString());
    }

    // --- keychain / slab DTO -----------------------------------------------------------

    [Fact]
    public void MakeKeychainDto_OrdinaryCharmResolvesAgainstTheKeychainCatalog()
    {
        var charm = new CEconItemPreviewDataBlock.Sticker
        {
            slot = 0,
            sticker_id = StickerAndKeychainId,
            wear = 0f,
            offset_x = 1.5f,
            offset_y = -2.5f,
            pattern = 88,
        };

        var json = ToJson(SkinController.MakeKeychainDto(charm, _constData));

        var expected = _constData.ResolveKeychain(StickerAndKeychainId);
        Assert.NotNull(expected);
        Assert.Equal(expected.Name, json.GetProperty("name").GetString());
        Assert.False(json.GetProperty("slab").GetBoolean());
        Assert.Equal(0u, json.GetProperty("wrapped_sticker").GetUInt32());
        Assert.Equal(1.5f, json.GetProperty("offset_x").GetSingle());
        Assert.Equal(-2.5f, json.GetProperty("offset_y").GetSingle());
        Assert.Equal(88u, json.GetProperty("pattern").GetUInt32());
    }

    [Fact]
    public void MakeKeychainDto_SlabResolvesTheSealedStickerAgainstTheStickerCatalog()
    {
        // The slab container itself isn't in the keychain catalog, so a slab must be displayed as
        // its sealed sticker. Id 1 names a different decal in each catalog, so this asserts the
        // lookup actually crossed over to the sticker catalog.
        var slab = new CEconItemPreviewDataBlock.Sticker { slot = 0, sticker_id = 37, wear = 0f };
        StickerSlab.SetWrappedStickerId(slab, StickerAndKeychainId);

        var json = ToJson(SkinController.MakeKeychainDto(slab, _constData));

        var sealedKit = _constData.ResolveSticker(StickerAndKeychainId);
        var charmKit = _constData.ResolveKeychain(StickerAndKeychainId);
        Assert.NotNull(sealedKit);
        Assert.NotNull(charmKit);
        Assert.NotEqual(sealedKit.Name, charmKit.Name); // guards the premise of this test

        Assert.Equal(sealedKit.Name, json.GetProperty("name").GetString());
        Assert.Equal(sealedKit.Image, json.GetProperty("image").GetString());
        Assert.True(json.GetProperty("slab").GetBoolean());
        Assert.Equal(StickerAndKeychainId, json.GetProperty("wrapped_sticker").GetUInt32());
        // The slab's own id still travels, so the client can tell which container it was.
        Assert.Equal(37u, json.GetProperty("sticker_id").GetUInt32());
    }

    [Fact]
    public void MakeKeychainDto_PlacementFieldsAreNullWhenAbsent()
    {
        var charm = new CEconItemPreviewDataBlock.Sticker { slot = 0, sticker_id = StickerAndKeychainId, wear = 0f };

        var json = ToJson(SkinController.MakeKeychainDto(charm, _constData));

        Assert.Equal(JsonValueKind.Null, json.GetProperty("offset_x").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("offset_y").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("pattern").ValueKind);
    }

    [Fact]
    public void MakeKeychainDto_SlabAndCharmShareOneShape()
    {
        // Both cases come back in the same `keychains` array, so the client must not have to branch
        // on which fields exist. (One builder now produces both; this pins that down.)
        var charm = new CEconItemPreviewDataBlock.Sticker { slot = 0, sticker_id = StickerAndKeychainId, wear = 0f };
        var slab = new CEconItemPreviewDataBlock.Sticker { slot = 0, sticker_id = 37, wear = 0f };
        StickerSlab.SetWrappedStickerId(slab, StickerAndKeychainId);

        static string[] Keys(JsonElement e) => [.. e.EnumerateObject().Select(p => p.Name)];

        Assert.Equal(
            Keys(ToJson(SkinController.MakeKeychainDto(charm, _constData))),
            Keys(ToJson(SkinController.MakeKeychainDto(slab, _constData))));
    }

    [Fact]
    public void MakeKeychainDto_SurvivesTheCacheRoundTrip()
    {
        // A cache-reloaded slab is rebuilt from DB columns with field 12 re-appended; it must render
        // identically to the freshly decoded one (see StickerSlab / ReadStickersAsync).
        var fresh = new CEconItemPreviewDataBlock.Sticker
        {
            slot = 0,
            sticker_id = 37,
            wear = 0.4f,
            offset_x = 0f,
            pattern = 12,
        };
        StickerSlab.SetWrappedStickerId(fresh, StickerAndKeychainId);

        using var ms = new MemoryStream();
        Serializer.Serialize(ms, fresh);
        ms.Position = 0;
        var reloaded = Serializer.Deserialize<CEconItemPreviewDataBlock.Sticker>(ms);

        Assert.Equal(
            JsonSerializer.Serialize(SkinController.MakeKeychainDto(fresh, _constData)),
            JsonSerializer.Serialize(SkinController.MakeKeychainDto(reloaded, _constData)));
    }

    // --- inspect URL parsing -----------------------------------------------------------

    [Fact]
    public void ParseInspectUrl_ClassicOwnerLink_ReturnsOwnerAssetAndD()
    {
        var url = "steam://rungame/730/76561202255233023/+csgo_econ_action_preview%20S76561198123456789A12345D67890";

        var parsed = InspectLink.ParseInspectUrl(url);

        Assert.NotNull(parsed);
        var (s, a, d, m, directItem) = parsed.Value;
        Assert.Equal(76561198123456789UL, s);
        Assert.Equal(12345UL, a);
        Assert.Equal(67890UL, d);
        Assert.Equal(0UL, m);
        Assert.Null(directItem); // an S-form link carries no item data; it needs the GC or the cache
    }

    [Fact]
    public void ParseInspectUrl_MarketLink_PopulatesMNotS()
    {
        var url = "steam://rungame/730/76561202255233023/+csgo_econ_action_preview%20M1A12345D67890";

        var parsed = InspectLink.ParseInspectUrl(url);

        Assert.NotNull(parsed);
        var (s, a, d, m, _) = parsed.Value;
        Assert.Equal(0UL, s);
        Assert.Equal(1UL, m);
        Assert.Equal(12345UL, a);
        Assert.Equal(67890UL, d);
    }

    [Fact]
    public void ParseInspectUrl_ShortPrefixForm_StillParses()
    {
        // The prefix changed from "rungame/730/<steamid>/" to "run/730//" in March 2026; the parser
        // matches on the command, not the prefix, so both forms have to work.
        var parsed = InspectLink.ParseInspectUrl(
            "steam://run/730//+csgo_econ_action_preview%20S76561198123456789A12345D67890");

        Assert.NotNull(parsed);
        Assert.Equal(12345UL, parsed.Value.a);
    }

    [Fact]
    public void ParseInspectUrl_NotAnInspectLink_ReturnsNull()
    {
        Assert.Null(InspectLink.ParseInspectUrl("https://example.com/not-a-link"));
    }

    // --- inventory inspect-link templating ---------------------------------------------

    [Fact]
    public void BuildInspectLink_SubstitutesOwnerAndAsset()
    {
        var link = InspectLink.BuildInspectLink(
            "steam://rungame/730/%owner_steamid%/+csgo_econ_action_preview S%owner_steamid%A%assetid%D123",
            assetProps: null,
            ownerSteamId: "76561198123456789",
            assetId: "519");

        Assert.Equal(
            "steam://rungame/730/76561198123456789/+csgo_econ_action_preview S76561198123456789A519D123",
            link);
    }

    [Fact]
    public void BuildInspectLink_SubstitutesTheItemCertificateProperty()
    {
        // propid 6 is the self-contained cert; once substituted the link decodes with no GC call.
        var props = new List<SteamAssetProperty>
        {
            new() { propertyid = 6, string_value = "00DEADBEEF" },
        };

        var link = InspectLink.BuildInspectLink(
            "steam://run/730//+csgo_econ_action_preview %propid:6%", props, "76561198123456789", "519");

        Assert.Equal("steam://run/730//+csgo_econ_action_preview 00DEADBEEF", link);
    }

    [Fact]
    public void BuildInspectLink_UnresolvedPlaceholderIsLeftIntact()
    {
        // An asset with no matching property must leave the placeholder alone rather than splice in
        // an empty string, so the link visibly fails to parse instead of silently pointing at junk.
        var link = InspectLink.BuildInspectLink(
            "steam://run/730//+csgo_econ_action_preview %propid:6%", [], "76561198123456789", "519");

        Assert.Equal("steam://run/730//+csgo_econ_action_preview %propid:6%", link);
    }

    // --- steam id parsing boundaries ---------------------------------------------------

    [Theory]
    [InlineData("7656119812345678")]      // 16 digits - too short for an id64
    [InlineData("765611981234567890")]    // 18 digits - too long
    [InlineData("86561198123456789")]     // right length, outside the individual-account block
    public void ParseSteamInput_OutOfRangeNumericId_IsNotTreatedAsAnId(string input)
    {
        var (steamId64, vanity) = SteamProfile.ParseSteamInput(input);

        Assert.Null(steamId64);
        // An all-digit input is never a vanity name either, so it resolves to nothing.
        Assert.Null(vanity);
    }
}
