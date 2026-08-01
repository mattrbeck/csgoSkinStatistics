using ProtoBuf;
using SteamKit2.GC.CSGO.Internal;

namespace csgoSkinStatistics.Tests;

// Builds the hex "item certificate" a modern inspect link carries, so tests can drive the
// direct-decode path from a known item instead of a captured live link.
//
// The wire format ParseInspectUrl expects is: one XOR key byte, the CEconItemPreviewDataBlock
// protobuf, then a four-byte checksum - with the whole run XOR'd by the key. Key 0x00 makes that
// XOR a no-op, which is exactly the legacy masked form still in circulation, and the checksum is
// never verified on read, so four zero bytes stand in for it.
internal static class InspectCert
{
    public static string Hex(CEconItemPreviewDataBlock item)
    {
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, item);
        byte[] payload = [0x00, .. stream.ToArray(), 0x00, 0x00, 0x00, 0x00];
        return Convert.ToHexString(payload);
    }

    public static string Link(CEconItemPreviewDataBlock item)
        => $"steam://run/730//+csgo_econ_action_preview {Hex(item)}";
}
