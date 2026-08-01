namespace CSGOSkinAPI.Services
{
    // Building and decoding CS2 inspect links. Domain logic, not a controller concern: the
    // inventory document builds links here and the background warmer decodes them, so it sits
    // beside them rather than in the endpoint that also happens to use it.
    //
    // Static, and the logger arrives as an argument rather than through a field, because the
    // category is the caller's choice: the endpoint logs parse failures under its own
    // CSGOSkinAPI.InspectLinks knob (a bad link there came from the caller), while the warmer
    // logs them under its own (a bad link there came from Steam's feed).
    //
    // partial for the [GeneratedRegex] members below.
    internal static partial class InspectLink
    {
        // Match on the command itself rather than the prefix, which changed from
        // the legacy "rungame/730/<steamid>/" to "run/730//" in March 2026.
        [GeneratedRegex(@"csgo_econ_action_preview ([SM])(\d+)A(\d+)D(\d+)", RegexOptions.Compiled)]
        private static partial Regex InspectUrlRegex();
        [GeneratedRegex(@"csgo_econ_action_preview ([0-9A-F]+)", RegexOptions.Compiled)]
        private static partial Regex InspectUrlHexRegex();

        // Fill the placeholders Steam leaves in a description-level inspect link template:
        // %propid:N% with the value of this asset's property N (for skins, propid 6 is the
        // item certificate), and %owner_steamid%/%assetid% with the copy's identity.
        internal static string BuildInspectLink(string actionLink, List<SteamAssetProperty>? assetProps, string ownerSteamId, string assetId)
        {
            var link = Regex.Replace(actionLink, @"%propid:(\d+)%", m =>
            {
                // TryParse, not Parse: the regex caps nothing, so a digit run longer than int can
                // hold would throw OverflowException here and surface as a 500 for the whole
                // inventory. An unparseable id is left as-is, like one with no matching property.
                if (!int.TryParse(m.Groups[1].Value, out var pid))
                {
                    return m.Value;
                }
                var prop = assetProps?.FirstOrDefault(p => p.propertyid == pid);
                return prop?.string_value ?? prop?.int_value ?? prop?.float_value ?? m.Value;
            });
            return link
                .Replace("%owner_steamid%", ownerSteamId)
                .Replace("%assetid%", assetId);
        }

        // The logger is optional, and null-sunk when absent, so a test that is asserting on the
        // parse result need not supply one.
        internal static (ulong s, ulong a, ulong d, ulong m, CEconItemPreviewDataBlock? directItem)? ParseInspectUrl(
            string url, ILogger? logger = null)
        {
            logger ??= NullLogger.Instance;
            var decodedUrl = HttpUtility.UrlDecode(url);
            var match = InspectUrlRegex().Match(decodedUrl);
            if (!match.Success)
            {
                var hexMatch = InspectUrlHexRegex().Match(decodedUrl);
                if (!hexMatch.Success)
                {
                    logger.LogWarning("Failed to decode URL: {InspectUrl}", LogSanitizer.ForLog(url));
                    return null;
                }
                var hexValue = hexMatch.Groups[1].Value;
                // Real inspect certs are a few hundred hex chars; cap the length so a crafted
                // multi-megabyte payload can't force a huge allocation and protobuf parse on the
                // request thread.
                if (hexValue.Length > 2048)
                {
                    logger.LogWarning("Hex payload too long: {InspectUrl}", LogSanitizer.ForLog(url));
                    return null;
                }
                // The regex matches odd-length runs too, which Convert.FromHexString rejects with a
                // FormatException - guard it so a bad link is a 400, not an unhandled 500.
                if (hexValue.Length % 2 != 0)
                {
                    logger.LogWarning("Hex payload has odd length: {InspectUrl}", LogSanitizer.ForLog(url));
                    return null;
                }
                var rawBytes = Convert.FromHexString(hexValue);
                // Need at least the leading byte, one protobuf byte, and the 4-byte checksum.
                if (rawBytes.Length < 6)
                {
                    logger.LogWarning("Hex payload too short: {InspectUrl}", LogSanitizer.ForLog(url));
                    return null;
                }
                // As of March 2026 the payload is XOR-obfuscated with its first byte
                // as the key. Legacy masked links start with 0x00, so this is a no-op
                // for them and deobfuscates the new self-encoded links.
                var xorKey = rawBytes[0];
                for (var i = 0; i < rawBytes.Length; i++)
                {
                    rawBytes[i] ^= xorKey;
                }
                // Drop the leading xor byte and the trailing 4 checksum bytes
                var hexBytes = rawBytes[1..^4];
                CEconItemPreviewDataBlock itemInfoProto;
                try
                {
                    using var hexStream = new MemoryStream(hexBytes);
                    itemInfoProto = Serializer.Deserialize<CEconItemPreviewDataBlock>(hexStream);
                }
                catch (Exception ex)
                {
                    // Valid hex that isn't a valid CEconItemPreviewDataBlock (garbage, or a
                    // truncated/mis-typed protobuf) throws here - map it to a 400, not a 500.
                    logger.LogWarning(ex, "Failed to decode inspect cert payload from {InspectUrl}", LogSanitizer.ForLog(url));
                    return null;
                }
                return (0, itemInfoProto.itemid, 0, 0, itemInfoProto);
            }

            // TryParse, not Parse: the regex caps nothing, so a caller can supply a >20-digit run
            // that overflows ulong. Treat that as a malformed link (null -> 400) rather than letting
            // the OverflowException surface as a 500.
            ulong s = 0, m = 0;
            var firstParam = match.Groups[1].Value;
            if (!ulong.TryParse(match.Groups[2].Value, out var firstValue) ||
                !ulong.TryParse(match.Groups[3].Value, out var a) ||
                !ulong.TryParse(match.Groups[4].Value, out var d))
            {
                logger.LogWarning("Inspect URL has out-of-range numeric fields: {InspectUrl}", LogSanitizer.ForLog(url));
                return null;
            }
            if (firstParam == "S")
            {
                s = firstValue;
            }
            else if (firstParam == "M")
            {
                m = firstValue;
            }
            return (s, a, d, m, null);
        }
    }
}
