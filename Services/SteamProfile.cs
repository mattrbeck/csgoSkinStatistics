namespace CSGOSkinAPI.Services
{
    // Reading a Steam identity: classifying whatever a user typed into an id or a vanity name,
    // picking the profile XML feed that answers for it, and parsing that feed. Domain logic, not a
    // controller concern - these are pure functions over strings with no HTTP of their own, and the
    // /api/inventory and /api/profile endpoints both go through them, so they sit together here
    // rather than in the endpoint that happens to call them.
    //
    // partial for the [GeneratedRegex] member below.
    internal static partial class SteamProfile
    {
        // A 17-digit id64 in the "76561…" individual-account block. Checked numerically rather
        // than by formatting the value to a string twice.
        private static bool IsValidSteamId64(ulong steamId) =>
            steamId is >= 76561000000000000UL and <= 76561999999999999UL;

        // Steam vanity names are letters, digits, underscores and hyphens. Validating before the
        // name is interpolated into a steamcommunity.com URL keeps an attacker from injecting path
        // segments, a different host, or query parameters into our server-side fetch (SSRF).
        //
        // \z, not $: in .NET `$` also matches immediately *before* a trailing newline, so the old
        // pattern accepted "name\n" - which then travelled on into the fetch URL and the logs.
        [GeneratedRegex(@"\A[A-Za-z0-9_-]{2,32}\z")]
        private static partial Regex VanityRegex();

        internal static bool IsValidVanity(string vanity) => VanityRegex().IsMatch(vanity);

        // Classifies a user input - a raw SteamId64, a profiles/<id64> URL, an id/<vanity> URL,
        // or a bare vanity name - into either a known SteamId64 or a vanity that still needs a
        // lookup. Centralizes the parsing so every caller (resolve + profile XML) stays in sync.
        internal static (ulong? steamId64, string? vanity) ParseSteamInput(string input)
        {
            // Already a valid SteamId64
            if (ulong.TryParse(input, out var id) && IsValidSteamId64(id))
                return (id, null);

            // profiles/<id64> URL
            var profileMatch = Regex.Match(input, @"steamcommunity\.com/profiles/(\d+)");
            if (profileMatch.Success && ulong.TryParse(profileMatch.Groups[1].Value, out var pid) && IsValidSteamId64(pid))
                return (pid, null);

            // id/<vanity> URL
            var customUrlMatch = Regex.Match(input, @"steamcommunity\.com/id/([^/?]+)");
            if (customUrlMatch.Success && IsValidVanity(customUrlMatch.Groups[1].Value))
                return (null, customUrlMatch.Groups[1].Value);

            // Bare vanity name (not a steamcommunity URL, not an all-digit id)
            if (!input.Contains("steamcommunity.com") && !input.All(char.IsDigit) && IsValidVanity(input))
                return (null, input);

            return (null, null);
        }

        // Picks the profile XML feed URL for a user input. Vanity inputs use /id/<vanity> (which
        // also carries the SteamId64); known ids use /profiles/<id64>.
        internal static string? GetProfileXmlUrl(string input)
        {
            var (steamId64, vanity) = ParseSteamInput(input);
            if (steamId64 != null) return $"https://steamcommunity.com/profiles/{steamId64}/?xml=1";
            if (vanity != null) return $"https://steamcommunity.com/id/{vanity}/?xml=1";
            return null;
        }

        internal sealed class ProfileInfo
        {
            public ulong? SteamId { get; init; }
            public string? CustomUrl { get; init; }
            public string? Persona { get; init; }
            public string? Avatar { get; init; }
            public string? TradeBanState { get; init; }
            public bool LimitedAccount { get; init; }
            // Year the account was created, parsed from <memberSince> (e.g. "July 12, 2015" -> 2015).
            // Null when the profile feed omits the element or it can't be parsed.
            public int? SinceYear { get; init; }
        }

        // Parses the public Steam profile XML feed. Both /id/<vanity>/?xml=1 and
        // /profiles/<id64>/?xml=1 return the same shape, so the vanity feed yields the SteamId64
        // *and* the profile info in one request - no separate resolve call needed.
        internal static ProfileInfo ParseProfileXml(string xml)
        {
            var idMatch = Regex.Match(xml, @"<steamID64>(\d+)</steamID64>");
            // customURL is the vanity name (e.g. "mattrb"); it's omitted when the user hasn't set one.
            var customUrlMatch = Regex.Match(xml, @"<customURL><!\[CDATA\[(.*?)\]\]></customURL>", RegexOptions.Singleline);
            var nameMatch = Regex.Match(xml, @"<steamID><!\[CDATA\[(.*?)\]\]></steamID>", RegexOptions.Singleline);
            var avatarMatch = Regex.Match(xml, @"<avatarFull><!\[CDATA\[(.*?)\]\]></avatarFull>", RegexOptions.Singleline);
            // tradeBanState is "None"/"Probation"/"Banned"; isLimitedAccount is 0/1. Either one
            // means the user is restricted from trading or using the market.
            var tradeBanMatch = Regex.Match(xml, @"<tradeBanState>(.*?)</tradeBanState>", RegexOptions.Singleline);
            var limitedMatch = Regex.Match(xml, @"<isLimitedAccount>(\d+)</isLimitedAccount>", RegexOptions.Singleline);
            // memberSince is a human date string like "July 12, 2015" (occasionally wrapped in
            // CDATA). We only surface the 4-digit year; anything else stays null so we never invent.
            var memberSinceMatch = Regex.Match(xml, @"<memberSince>(?:<!\[CDATA\[)?(.*?)(?:\]\]>)?</memberSince>", RegexOptions.Singleline);
            int? sinceYear = null;
            if (memberSinceMatch.Success)
            {
                var yearMatch = Regex.Match(memberSinceMatch.Groups[1].Value, @"\b(19|20)\d{2}\b");
                if (yearMatch.Success && int.TryParse(yearMatch.Value, out var y))
                {
                    sinceYear = y;
                }
            }

            return new ProfileInfo
            {
                SteamId = idMatch.Success && ulong.TryParse(idMatch.Groups[1].Value, out var id) ? id : null,
                CustomUrl = customUrlMatch.Success ? customUrlMatch.Groups[1].Value : null,
                Persona = nameMatch.Success ? nameMatch.Groups[1].Value : null,
                Avatar = avatarMatch.Success ? avatarMatch.Groups[1].Value : null,
                TradeBanState = tradeBanMatch.Success ? tradeBanMatch.Groups[1].Value : null,
                LimitedAccount = limitedMatch.Success && limitedMatch.Groups[1].Value == "1",
                SinceYear = sinceYear
            };
        }
    }
}
