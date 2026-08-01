using System.Text.Json;
using CSGOSkinAPI.Services;
using ProtoBuf;
using SteamKit2.GC.CSGO.Internal;
using Xunit;

namespace csgoSkinStatistics.Tests.Services;

// These tests resolve real decal names, so the fixture takes the shipped stickers.json. It is a few
// MB, so parse it once and share the service across every test in the class.
public sealed class ConstDataFixture : IDisposable
{
    private readonly CatalogDirectory _catalogs = CatalogDirectory.Create().WithShippedCatalog("stickers.json");

    public ConstDataService Service { get; }

    public ConstDataFixture() => Service = _catalogs.Build();

    public void Dispose() => _catalogs.Dispose();
}

// Exercises the real ItemResponse DTO builders - MakeStickerDto and MakeKeychainDto - through
// InternalsVisibleTo. These assert on the serialized JSON rather than the anonymous types, because
// the JSON *is* the contract: the browser reads these field names straight off the response.
public class ItemResponseTests(ConstDataFixture fixture) : IClassFixture<ConstDataFixture>
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

        var json = ToJson(ItemResponse.MakeStickerDto(sticker, _constData));

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

        var json = ToJson(ItemResponse.MakeStickerDto(sticker, _constData));

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

        var json = ToJson(ItemResponse.MakeStickerDto(sticker, _constData));

        Assert.Equal(0f, json.GetProperty("rotation").GetSingle());
        Assert.Equal(0f, json.GetProperty("offset_x").GetSingle());
        Assert.Equal(0f, json.GetProperty("offset_y").GetSingle());
    }

    [Fact]
    public void MakeStickerDto_ResolvesNameAndImageFromTheStickerCatalog()
    {
        var sticker = new CEconItemPreviewDataBlock.Sticker { slot = 0, sticker_id = StickerAndKeychainId, wear = 0f };

        var json = ToJson(ItemResponse.MakeStickerDto(sticker, _constData));

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

        var json = ToJson(ItemResponse.MakeStickerDto(sticker, _constData));

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

        var json = ToJson(ItemResponse.MakeKeychainDto(charm, _constData));

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

        var json = ToJson(ItemResponse.MakeKeychainDto(slab, _constData));

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

        var json = ToJson(ItemResponse.MakeKeychainDto(charm, _constData));

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
            Keys(ToJson(ItemResponse.MakeKeychainDto(charm, _constData))),
            Keys(ToJson(ItemResponse.MakeKeychainDto(slab, _constData))));
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
            JsonSerializer.Serialize(ItemResponse.MakeKeychainDto(fresh, _constData)),
            JsonSerializer.Serialize(ItemResponse.MakeKeychainDto(reloaded, _constData)));
    }
}
