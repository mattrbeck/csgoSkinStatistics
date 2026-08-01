using CSGOSkinAPI.Services;
using ProtoBuf;
using SteamKit2.GC.CSGO.Internal;
using Xunit;

namespace csgoSkinStatistics.Tests.Services;

// StickerFields is the single place that turns protobuf-net's "value + ShouldSerialize<field>()"
// pair into a nullable. The distinction it exists to preserve is absent-vs-zero: rotation 0 and
// offset 0 are perfectly ordinary values (a dead-centre, unrotated sticker), so collapsing an
// absent field to 0 - or a real 0 to null - would both be wrong on the wire and in the DB.
public class StickerFieldsTests
{
    [Fact]
    public void AbsentFields_AreNull()
    {
        // A sticker carrying nothing but the three fields the GC always sets.
        var s = new CEconItemPreviewDataBlock.Sticker { slot = 0, sticker_id = 1, wear = 0.1f };

        Assert.Null(s.Scale());
        Assert.Null(s.Rotation());
        Assert.Null(s.TintId());
        Assert.Null(s.OffsetX());
        Assert.Null(s.OffsetY());
        Assert.Null(s.OffsetZ());
        Assert.Null(s.Pattern());
        Assert.Null(s.HighlightReel());
    }

    [Fact]
    public void PresentFields_ReturnTheirValue()
    {
        var s = new CEconItemPreviewDataBlock.Sticker
        {
            slot = 0,
            sticker_id = 1,
            wear = 0.1f,
            scale = 1.5f,
            rotation = -12.5f,
            tint_id = 7,
            offset_x = 0.25f,
            offset_y = -0.75f,
            offset_z = 2f,
            pattern = 421,
            highlight_reel = 3,
        };

        Assert.Equal(1.5f, s.Scale());
        Assert.Equal(-12.5f, s.Rotation());
        Assert.Equal(7u, s.TintId());
        Assert.Equal(0.25f, s.OffsetX());
        Assert.Equal(-0.75f, s.OffsetY());
        Assert.Equal(2f, s.OffsetZ());
        Assert.Equal(421u, s.Pattern());
        Assert.Equal(3u, s.HighlightReel());
    }

    [Fact]
    public void ExplicitZero_IsZeroNotNull()
    {
        // The case that makes the helpers necessary: an unrotated, dead-centre sticker sets these
        // fields to 0. Reporting null here would tell the client "the GC didn't say", which is a
        // different statement from "it said zero".
        var s = new CEconItemPreviewDataBlock.Sticker
        {
            slot = 0,
            sticker_id = 1,
            wear = 0f,
            rotation = 0f,
            offset_x = 0f,
            offset_y = 0f,
            pattern = 0,
        };

        Assert.Equal(0f, s.Rotation());
        Assert.Equal(0f, s.OffsetX());
        Assert.Equal(0f, s.OffsetY());
        Assert.Equal(0u, s.Pattern());
    }

    [Fact]
    public void ExplicitZero_SurvivesTheWire()
    {
        // Presence has to survive serialization too, otherwise a real 0 from the GC would decode
        // as absent and the API would report null for it.
        var s = new CEconItemPreviewDataBlock.Sticker
        {
            slot = 0,
            sticker_id = 1,
            wear = 0f,
            rotation = 0f,
            offset_x = 0f,
        };

        using var ms = new MemoryStream();
        Serializer.Serialize(ms, s);
        ms.Position = 0;
        var decoded = Serializer.Deserialize<CEconItemPreviewDataBlock.Sticker>(ms);

        Assert.Equal(0f, decoded.Rotation());
        Assert.Equal(0f, decoded.OffsetX());
        // offset_y was never set, so it must stay absent across the same round trip.
        Assert.Null(decoded.OffsetY());
    }

    [Fact]
    public void StatTrakKills_TracksFieldPresence()
    {
        var plain = new CEconItemPreviewDataBlock { itemid = 1 };
        Assert.Null(plain.StatTrakKills());

        // A brand-new StatTrak weapon sits at 0 confirmed kills - present, not absent.
        var fresh = new CEconItemPreviewDataBlock { itemid = 2, killeatervalue = 0 };
        Assert.Equal(0u, fresh.StatTrakKills());

        var used = new CEconItemPreviewDataBlock { itemid = 3, killeatervalue = 1337 };
        Assert.Equal(1337u, used.StatTrakKills());
    }
}
