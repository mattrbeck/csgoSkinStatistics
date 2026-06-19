using System.IO;
using ProtoBuf;
using SteamKit2.GC.CSGO.Internal;
using Xunit;

namespace csgoSkinStatistics.Tests.Services;

// A Sticker Slab is a charm that seals a sticker inside it; the sealed sticker's id rides
// in the proto's `wrapped_sticker` field (12), which SteamKit's Sticker type does not model.
// The server reads/persists it purely through protobuf-net's extension-data preservation, so
// these tests pin down that behaviour - if a SteamKit/protobuf-net bump broke it, slabs would
// silently render as blank charms.
public class StickerSlabTests
{
    [Fact]
    public void WrappedSticker_SurvivesWireRoundTrip()
    {
        // Mirrors the live decode path: an unmodeled field 12 on the wire must come back out.
        var sticker = new CEconItemPreviewDataBlock.Sticker { slot = 0, sticker_id = 999, wear = 0f };
        Extensible.AppendValue<uint>(sticker, 12, 5032u);

        using var ms = new MemoryStream();
        Serializer.Serialize(ms, sticker);
        ms.Position = 0;
        var decoded = Serializer.Deserialize<CEconItemPreviewDataBlock.Sticker>(ms);

        Assert.True(Extensible.TryGetValue<uint>(decoded, 12, out var wrapped));
        Assert.Equal(5032u, wrapped);
        Assert.Equal(999u, decoded.sticker_id);
    }

    [Fact]
    public void WrappedSticker_AppendThenReadInMemory()
    {
        // Mirrors the cache-load path: the Sticker is rebuilt from DB columns and field 12 is
        // re-appended, then CreateResponse reads it back from the same in-memory object.
        var sticker = new CEconItemPreviewDataBlock.Sticker { slot = 0, sticker_id = 4521, wear = 0f };
        Extensible.AppendValue<uint>(sticker, 12, 7777u);

        Assert.True(Extensible.TryGetValue<uint>(sticker, 12, out var wrapped));
        Assert.Equal(7777u, wrapped);
    }

    [Fact]
    public void NoWrappedSticker_TryGetValueIsFalse()
    {
        // A normal charm (or a plain sticker) has no field 12; resolution must fall through.
        var sticker = new CEconItemPreviewDataBlock.Sticker { slot = 0, sticker_id = 1, wear = 0f };
        Assert.False(Extensible.TryGetValue<uint>(sticker, 12, out _));
    }
}
