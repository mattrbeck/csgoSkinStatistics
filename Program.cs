[assembly: InternalsVisibleTo("csgoSkinStatistics.Tests")]

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    options.MimeTypes = Microsoft.AspNetCore.ResponseCompression.ResponseCompressionDefaults.MimeTypes.Concat(
        ["application/javascript", "text/css", "text/html", "text/json", "text/plain"]);
});
builder.Services.AddHttpClient();
// Dedicated client for steamcommunity.com calls (inventory, profile, vanity resolve). Traffic is
// bursty/low, so we keep pooled connections alive far longer than the defaults to avoid paying a
// fresh TLS handshake (~100ms) on each cold request. PooledConnectionLifetime still rotates
// connections periodically for DNS hygiene, and an infinite handler lifetime stops IHttpClientFactory
// from recycling the handler (which would otherwise drop the warm connection pool every 2 minutes).
builder.Services.AddHttpClient("steam")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(30),
    })
    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
builder.Services.AddSingleton<SteamService>();
builder.Services.AddSingleton<DatabaseService>();
builder.Services.AddSingleton<ConstDataService>();
// Registered once and exposed both as itself (controllers enqueue into it) and as the
// hosted service that drains the queue.
builder.Services.AddSingleton<InventoryWarmService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<InventoryWarmService>());

var app = builder.Build();

app.UseResponseCompression();
// Serve the inventory page at the clean /inventory URL.
app.UseRewriter(new RewriteOptions()
    .AddRewrite("^inventory$", "inventory.html", skipRemainingRules: true));
app.UseDefaultFiles(); // Must be before UseStaticFiles
app.UseStaticFiles();

app.UseRouting();
app.MapControllers();

// Initialize database on startup
var dbService = app.Services.GetRequiredService<DatabaseService>();
await dbService.InitializeDatabaseAsync();

// Initialize Steam connection
var steamService = app.Services.GetRequiredService<SteamService>();
_ = steamService.ConnectAsync();

// Initialize ConstDataService (loads const.json)
var constDataService = app.Services.GetRequiredService<ConstDataService>();

// Handle Ctrl-C gracefully
Console.CancelKeyPress += (sender, e) =>
{
    Console.WriteLine("\nReceived Ctrl-C, disconnecting from Steam...");
    steamService.Disconnect();
    e.Cancel = false;
};

app.Run();

namespace CSGOSkinAPI.Controllers
{
    [ApiController]
    [Route("api")]
    public partial class SkinController(SteamService steamService, DatabaseService dbService, ConstDataService constDataService, IHttpClientFactory httpClientFactory, InventoryWarmService warmService) : ControllerBase
    {
        // SteamID64 of the first individual account; anything below is not a profile id.
        private const ulong MinSteamId64 = 76561197960265729;

        // Match on the command itself rather than the prefix, which changed from
        // the legacy "rungame/730/<steamid>/" to "run/730//" in March 2026.
        [GeneratedRegex(@"csgo_econ_action_preview ([SM])(\d+)A(\d+)D(\d+)", RegexOptions.Compiled)]
        private static partial Regex InspectUrlRegex();
        [GeneratedRegex(@"csgo_econ_action_preview ([0-9A-F]+)", RegexOptions.Compiled)]
        private static partial Regex InspectUrlHexRegex();

        [HttpGet]
        public async Task<IActionResult> GetSkinData([FromQuery] string? url,
            [FromQuery] ulong s = 0, [FromQuery] ulong a = 0,
            [FromQuery] ulong d = 0, [FromQuery] ulong m = 0)
        {
            try
            {
                if (!string.IsNullOrEmpty(url))
                {
                    var parsed = ParseInspectUrl(url);
                    if (parsed == null)
                    {
                        Console.WriteLine("Failed to parse inspect URL");
                        return BadRequest(new { error = "Invalid inspect URL format" });
                    }

                    (s, a, d, m, var directItem) = parsed.Value;

                    if (directItem != null)
                    {
                        return Ok(CreateResponse(directItem, constDataService, s, a, d, m));
                    }
                }

                // Cache hit is authoritative for the item's config: an itemid encodes an
                // immutable config. Any mutation (sticker/keychain applied or removed, name tag,
                // etc.) mints a brand-new itemid in the GC, so the row we stored for this id can
                // never disagree with the live item's config. (The StatTrak kill count and
                // inventory slot do drift under a fixed itemid; we persist the kill count, so a
                // cache hit may report a count slightly behind the live one - acceptable, and far
                // better than none. A hex cert link decodes fresh and is always current. See
                // docs/inventory-endpoint-cert.md, "applying a sticker mints a new itemid".)
                var existingItem = await dbService.GetItemAsync(a);
                if (existingItem != null)
                {
                    return Ok(CreateResponse(existingItem, constDataService, s, a, d, m));
                }

                // A classic S-form link that missed the cache still goes through the GC below,
                // but it also tells us whose inventory the wild link points into. Queue a
                // background warm of that whole inventory (cert decode, no GC traffic) so
                // follow-up lookups of the owner's other items become DB hits. M-form market
                // links carry no owner id, so they can't be warmed.
                if (s >= MinSteamId64)
                {
                    Console.WriteLine($"Cache miss for item {a}; queueing inventory warm for owner {s}");
                    warmService.Enqueue(s);
                }

                var itemInfo = await steamService.GetItemInfoAsync(s, a, d, m);
                if (itemInfo == null)
                {
                    Console.WriteLine("Item not found in Steam GC");
                    return NotFound(new { error = "Steam GC did not return an item" });
                }

                await dbService.SaveItemWithExtrasAsync(itemInfo);
                return Ok(CreateResponse(itemInfo, constDataService, s, a, d, m));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in GetSkinData: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        [HttpGet("inventory")]
        public async Task<IActionResult> GetInventoryData([FromQuery] string steamid)
        {
            try
            {
                if (string.IsNullOrEmpty(steamid))
                {
                    return BadRequest(new { error = "Steam ID is required" });
                }

                var resolvedSteamId = await ResolveSteamIdAsync(steamid);
                if (resolvedSteamId == null)
                {
                    return BadRequest(new { error = "Unable to resolve Steam ID or inventory" });
                }

                var steamId = resolvedSteamId.Value;
                steamid = steamId.ToString(); // Use resolved SteamId64 for inventory URL

                using var httpClient = httpClientFactory.CreateClient("steam");
                httpClient.Timeout = TimeSpan.FromSeconds(10);
                
                var inventoryUrl = $"https://steamcommunity.com/inventory/{steamid}/730/2?l=english&count=2000";
                Console.WriteLine($"Fetching inventory from: {inventoryUrl}");
                
                var response = await httpClient.GetAsync(inventoryUrl);
                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        return BadRequest(new { error = "Inventory is private or user does not exist" });
                    }
                    return BadRequest(new { error = $"Failed to fetch inventory: {response.StatusCode}" });
                }

                var jsonContent = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrEmpty(jsonContent))
                {
                    return BadRequest(new { error = "Empty response from Steam API" });
                }

                var inventoryData = JsonSerializer.Deserialize<SteamInventoryResponse>(jsonContent);
                if (inventoryData?.assets == null || inventoryData.descriptions == null)
                {
                    return BadRequest(new { error = "Invalid inventory data or inventory is empty" });
                }

                // The Steam Community inventory response is split across three parallel arrays
                // that we have to stitch together to build a usable inspect link per item:
                //
                //   {
                //     "assets":          [ { "assetid": "519...", "classid": "799...", "instanceid": "302..." }, ... ],
                //     "descriptions":    [ { "classid": "799...", "instanceid": "302...",   // shared by all assets of this kind
                //                            "actions": [ { "link": "steam://run/730//+csgo_econ_action_preview%20%propid:6%" } ],
                //                            "tags": [...] }, ... ],
                //     "asset_properties":[ { "assetid": "519...",                            // per-asset, only for items that have them
                //                            "asset_properties": [ { "propertyid": 6, "string_value": "352581D6EDF7..." }, ... ] }, ... ]
                //   }
                //
                // The inspect link lives on the *description* (shared by every copy of that skin), so it can't
                // embed per-asset data directly. Instead Steam templates it with placeholders we must fill in:
                //   - %owner_steamid% / %assetid% -> identify which copy in whose inventory (the classic S..A..D.. form)
                //   - %propid:N%                  -> the value of property N in *this asset's* asset_properties entry.
                // For skins, propid 6 ("Item Certificate") is a self-contained, XOR-obfuscated hex payload; once
                // substituted in, the link becomes the hex form that ParseInspectUrl decodes directly into full
                // item data with no Game Coordinator round-trip. (Fixed items like music kits skip the templating
                // and ship the hex inline, so they need no substitution at all.)
                //
                // Build assetid -> properties up front so the per-item loop can resolve %propid:N% in O(1).
                var propsByAsset = inventoryData.asset_properties?
                    .ToDictionary(ap => ap.assetid, ap => ap.asset_properties ?? [])
                    ?? [];

                var csgoItems = new List<object>();
                foreach (var asset in inventoryData.assets)
                {
                    var description = inventoryData.descriptions.FirstOrDefault(d =>
                        d.classid == asset.classid && d.instanceid == asset.instanceid);

                    if (description?.actions != null)
                    {
                        var inspectAction = description.actions.FirstOrDefault(a =>
                            a.link?.Contains("csgo_econ_action_preview") == true);

                        if (inspectAction?.link != null)
                        {
                            // Fill the template placeholders described above.
                            propsByAsset.TryGetValue(asset.assetid, out var assetProps);
                            var inspectLink = BuildInspectLink(inspectAction.link, assetProps, steamid, asset.assetid);

                            // Extract wear, rarity, and item type from tags
                            var wearTag = description.tags?.FirstOrDefault(t => t.category == "Exterior");
                            var rarityTag = description.tags?.FirstOrDefault(t => t.category == "Rarity");
                            var qualityTag = description.tags?.FirstOrDefault(t => t.category == "Quality");
                            var typeTag = description.tags?.FirstOrDefault(t => t.category == "Type");

                            // StatTrak kill count, when Steam exposes it on the StatTrak score line
                            // (e.g. "StatTrak™ Confirmed Kills: 1234"). Some copies only carry the
                            // generic "This item tracks Confirmed Kills." line, which has no number.
                            int? stattrakKills = null;
                            var scoreLine = description.descriptions?
                                .FirstOrDefault(l => l.name == "stattrak_score")?.value;
                            if (scoreLine != null)
                            {
                                var killMatch = Regex.Match(scoreLine, @"Confirmed Kills:\s*([\d,]+)");
                                if (killMatch.Success &&
                                    int.TryParse(killMatch.Groups[1].Value.Replace(",", ""), out var kills))
                                {
                                    stattrakKills = kills;
                                }
                            }
                            
                            // Try to extract itemid from inspect link and check if we have this item in database
                            var parsed = ParseInspectUrl(inspectLink);
                            object? existingItemData = null;
                            
                            if (parsed.HasValue)
                            {
                                var (s, a, d, m, directItem) = parsed.Value;
                                if (directItem != null)
                                {
                                    existingItemData = CreateResponse(directItem, constDataService, s, a, d, m);
                                }
                                else
                                {
                                    var existingItem = await dbService.GetItemAsync(a);
                                    if (existingItem != null)
                                    {
                                        existingItemData = CreateResponse(existingItem, constDataService, s, a, d, m);
                                    }
                                }
                            }
                            
                            csgoItems.Add(new
                            {
                                name = description.name ?? description.market_name ?? "Unknown Item",
                                market_name = description.market_name,
                                type = description.type,
                                inspect_link = inspectLink,
                                wear = wearTag?.localized_tag_name,
                                rarity = rarityTag?.localized_tag_name,
                                quality = qualityTag?.localized_tag_name,
                                item_type = typeTag?.localized_tag_name,
                                stattrak_kills = stattrakKills,
                                name_color = description.name_color,
                                icon_url = description.icon_url,
                                icon_url_large = description.icon_url_large,
                                assetid = asset.assetid,
                                classid = asset.classid,
                                instanceid = asset.instanceid,
                                existing_data = existingItemData
                            });
                        }
                    }
                }

                // Profile info (avatar, persona, trade-ban) is fetched separately by the browser
                // via /api/profile so item rendering never waits on Steam's profile feed.
                var result = new
                {
                    total = inventoryData.total,
                    success = 1,
                    steamid = steamId.ToString(),
                    csgo_items = csgoItems
                };

                Console.WriteLine($"Successfully parsed {csgoItems.Count} CS2 items from {inventoryData.total} total items");
                return Ok(result);
            }
            catch (TaskCanceledException)
            {
                return BadRequest(new { error = "Request timed out while fetching inventory" });
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"HTTP error fetching inventory: {ex.Message}");
                return BadRequest(new { error = "Failed to connect to Steam API" });
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"JSON parsing error: {ex.Message}");
                return BadRequest(new { error = "Invalid response from Steam API" });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in GetInventoryData: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        [HttpGet("profile")]
        public async Task<IActionResult> GetProfile([FromQuery] string steamid)
        {
            if (string.IsNullOrEmpty(steamid))
            {
                return BadRequest(new { error = "Steam ID is required" });
            }

            var xmlUrl = GetProfileXmlUrl(steamid);
            if (xmlUrl == null)
            {
                return BadRequest(new { error = "Unable to determine profile for the given Steam ID" });
            }

            try
            {
                using var httpClient = httpClientFactory.CreateClient("steam");
                httpClient.Timeout = TimeSpan.FromSeconds(5);

                var response = await httpClient.GetAsync(xmlUrl);
                if (!response.IsSuccessStatusCode)
                {
                    return BadRequest(new { error = $"Failed to fetch profile: {response.StatusCode}" });
                }

                var profile = ParseProfileXml(await response.Content.ReadAsStringAsync());
                if (profile.SteamId == null)
                {
                    return BadRequest(new { error = "Unable to resolve Steam profile" });
                }

                return Ok(new
                {
                    success = 1,
                    steamid = profile.SteamId.ToString(),
                    // The canonical value for the location hash: prefer the vanity name when the
                    // profile has one (friendlier, round-trips back to /id/<vanity>), else the id64.
                    hash = string.IsNullOrEmpty(profile.CustomUrl) ? profile.SteamId.ToString() : profile.CustomUrl,
                    persona_name = profile.Persona,
                    avatar = profile.Avatar,
                    trade_ban_state = profile.TradeBanState,
                    limited_account = profile.LimitedAccount,
                    // Prefer the vanity URL (/id/<vanity>) when the profile exposes one; Steam omits
                    // customURL for some profiles, so fall back to the /profiles/<id64> form.
                    profile_url = string.IsNullOrEmpty(profile.CustomUrl)
                        ? $"https://steamcommunity.com/profiles/{profile.SteamId}"
                        : $"https://steamcommunity.com/id/{profile.CustomUrl}"
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error fetching profile for '{steamid}': {ex.Message}");
                return StatusCode(500, new { error = "Failed to fetch profile" });
            }
        }

        // A 17-digit id64 in the "76561…" individual-account block. Checked numerically rather
        // than by formatting the value to a string twice.
        private static bool IsValidSteamId64(ulong steamId) =>
            steamId is >= 76561000000000000UL and <= 76561999999999999UL;

        // Steam vanity names are letters, digits, underscores and hyphens. Validating before the
        // name is interpolated into a steamcommunity.com URL keeps an attacker from injecting path
        // segments, a different host, or query parameters into our server-side fetch (SSRF).
        internal static bool IsValidVanity(string vanity) =>
            Regex.IsMatch(vanity, @"^[A-Za-z0-9_-]{2,32}$");

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

        private async Task<ulong?> ResolveSteamIdAsync(string input)
        {
            var (steamId64, vanity) = ParseSteamInput(input);
            if (steamId64 != null) return steamId64;
            if (vanity != null) return await ResolveCustomUrlToSteamId64Async(vanity);
            return null;
        }

        private async Task<ulong?> ResolveCustomUrlToSteamId64Async(string customUrl)
        {
            try
            {
                using var httpClient = httpClientFactory.CreateClient("steam");
                httpClient.Timeout = TimeSpan.FromSeconds(5);

                // The public profile XML feed exposes the SteamId64 without an API key.
                var xmlUrl = $"https://steamcommunity.com/id/{customUrl}/?xml=1";

                var response = await httpClient.GetAsync(xmlUrl);
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Steam profile request failed: {response.StatusCode}");
                    return null;
                }

                var xmlContent = await response.Content.ReadAsStringAsync();
                var match = Regex.Match(xmlContent, @"<steamID64>(\d+)</steamID64>");
                if (match.Success && ulong.TryParse(match.Groups[1].Value, out var steamId))
                {
                    return steamId;
                }

                Console.WriteLine($"Failed to resolve custom URL '{customUrl}' to SteamId64");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error resolving custom URL '{customUrl}': {ex.Message}");
                return null;
            }
        }

        private sealed class ProfileInfo
        {
            public ulong? SteamId { get; init; }
            public string? CustomUrl { get; init; }
            public string? Persona { get; init; }
            public string? Avatar { get; init; }
            public string? TradeBanState { get; init; }
            public bool LimitedAccount { get; init; }
        }

        // Parses the public Steam profile XML feed. Both /id/<vanity>/?xml=1 and
        // /profiles/<id64>/?xml=1 return the same shape, so the vanity feed yields the SteamId64
        // *and* the profile info in one request - no separate resolve call needed.
        private static ProfileInfo ParseProfileXml(string xml)
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

            return new ProfileInfo
            {
                SteamId = idMatch.Success && ulong.TryParse(idMatch.Groups[1].Value, out var id) ? id : null,
                CustomUrl = customUrlMatch.Success ? customUrlMatch.Groups[1].Value : null,
                Persona = nameMatch.Success ? nameMatch.Groups[1].Value : null,
                Avatar = avatarMatch.Success ? avatarMatch.Groups[1].Value : null,
                TradeBanState = tradeBanMatch.Success ? tradeBanMatch.Groups[1].Value : null,
                LimitedAccount = limitedMatch.Success && limitedMatch.Groups[1].Value == "1"
            };
        }

        // Picks the profile XML feed URL for a user input. Vanity inputs use /id/<vanity> (which
        // also carries the SteamId64); known ids use /profiles/<id64>.
        private static string? GetProfileXmlUrl(string input)
        {
            var (steamId64, vanity) = ParseSteamInput(input);
            if (steamId64 != null) return $"https://steamcommunity.com/profiles/{steamId64}/?xml=1";
            if (vanity != null) return $"https://steamcommunity.com/id/{vanity}/?xml=1";
            return null;
        }

        // Fill the placeholders Steam leaves in a description-level inspect link template:
        // %propid:N% with the value of this asset's property N (for skins, propid 6 is the
        // item certificate), and %owner_steamid%/%assetid% with the copy's identity.
        internal static string BuildInspectLink(string actionLink, List<SteamAssetProperty>? assetProps, string ownerSteamId, string assetId)
        {
            var link = Regex.Replace(actionLink, @"%propid:(\d+)%", m =>
            {
                var pid = int.Parse(m.Groups[1].Value);
                var prop = assetProps?.FirstOrDefault(p => p.propertyid == pid);
                return prop?.string_value ?? prop?.int_value ?? prop?.float_value ?? m.Value;
            });
            return link
                .Replace("%owner_steamid%", ownerSteamId)
                .Replace("%assetid%", assetId);
        }

        internal static (ulong s, ulong a, ulong d, ulong m, CEconItemPreviewDataBlock? directItem)? ParseInspectUrl(string url)
        {
            var decodedUrl = HttpUtility.UrlDecode(url);
            var match = InspectUrlRegex().Match(decodedUrl);
            if (!match.Success)
            {
                var hexMatch = InspectUrlHexRegex().Match(decodedUrl);
                if (!hexMatch.Success)
                {
                    Console.WriteLine($"Failed to decode URL: {url}");
                    return null;
                }
                var hexValue = hexMatch.Groups[1].Value;
                // Real inspect certs are a few hundred hex chars; cap the length so a crafted
                // multi-megabyte payload can't force a huge allocation and protobuf parse on the
                // request thread.
                if (hexValue.Length > 2048)
                {
                    Console.WriteLine($"Hex payload too long: {url}");
                    return null;
                }
                var rawBytes = Convert.FromHexString(hexValue);
                // Need at least the leading byte, one protobuf byte, and the 4-byte checksum.
                if (rawBytes.Length < 6)
                {
                    Console.WriteLine($"Hex payload too short: {url}");
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
                using var hexStream = new MemoryStream(hexBytes);
                var itemInfoProto = Serializer.Deserialize<CEconItemPreviewDataBlock>(hexStream);
                return (0, itemInfoProto.itemid, 0, 0, itemInfoProto);
            }

            ulong s = 0, a, d, m = 0;
            var firstParam = match.Groups[1].Value;
            var firstValue = ulong.Parse(match.Groups[2].Value);
            if (firstParam == "S")
            {
                s = firstValue;
            }
            else if (firstParam == "M")
            {
                m = firstValue;
            }
            a = ulong.Parse(match.Groups[3].Value);
            d = ulong.Parse(match.Groups[4].Value);
            return (s, a, d, m, null);
        }

        private static object CreateResponse(CEconItemPreviewDataBlock item, ConstDataService constDataService, ulong s, ulong a, ulong d, ulong m)
        {
            var itemInfo = constDataService.GetItemInformation(item);
            
            return new
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
                // The decoded cert/GC item carries the live kill count for free (proto field
                // 10); null for non-StatTrak items. Cached items keep it via the killeatervalue
                // column (see below) - older cached rows that predate that column report null.
                stattrak_kills = item.ShouldSerializekilleatervalue() ? item.killeatervalue : (uint?)null,
                souvenir = itemInfo.IsSouvenir,
                market_hash_name = itemInfo.MarketHashName,
                special = itemInfo.Special,
                weapon = itemInfo.Type,
                skin = itemInfo.Name,
                wear_name = itemInfo.WearName,
                rarity_name = itemInfo.RarityName,
                quality_name = itemInfo.QualityName,
                origin_name = itemInfo.OriginName,
                paintwear_float = itemInfo.PaintWear,
                is_knife_or_glove = itemInfo.IsKnifeOrGlove,
                image = constDataService.ResolveSkinImage(item.defindex, item.paintindex),
                // Ordered arrays; `slot` is NOT unique — CS2 stacks multiple stickers in one
                // slot (verified live), so these stay positional. Each decal is resolved to its
                // name + image here so the client renders straight from the response and never
                // downloads the full catalog. Only `wear` (scrape level) travels alongside.
                stickers = item.stickers.Select(s => MakeStickerDto(s, constDataService)).ToArray(),
                keychains = item.keychains.Select(k => MakeKeychainDto(k, constDataService)).ToArray(),
                s,
                a,
                d,
                m
            };
        }

        private static object MakeStickerDto(CEconItemPreviewDataBlock.Sticker s, ConstDataService constData)
        {
            var kit = constData.ResolveSticker(s.sticker_id);
            return new
            {
                s.sticker_id,
                s.wear,
                name = kit?.Name ?? "",
                image = kit?.Image ?? "",
            };
        }

        // A charm, or a Sticker Slab. A slab is a single-use charm that seals a sticker inside
        // it; the sealed sticker's id rides in proto field 12 (see StickerSlab). When present we
        // display the sealed sticker (the slab container itself isn't in our keychain catalog)
        // and flag it, so the client can mark it as a slab.
        private static object MakeKeychainDto(CEconItemPreviewDataBlock.Sticker k, ConstDataService constData)
        {
            var wrapped = StickerSlab.GetWrappedStickerId(k);
            if (wrapped != 0)
            {
                var sealedKit = constData.ResolveSticker(wrapped);
                return new
                {
                    k.sticker_id,
                    k.wear,
                    name = sealedKit?.Name ?? "",
                    image = sealedKit?.Image ?? "",
                    slab = true,
                    wrapped_sticker = wrapped,
                };
            }

            var kit = constData.ResolveKeychain(k.sticker_id);
            return new
            {
                k.sticker_id,
                k.wear,
                name = kit?.Name ?? "",
                image = kit?.Image ?? "",
                slab = false,
                wrapped_sticker = 0u,
            };
        }
    }
}

namespace CSGOSkinAPI.Services
{
    // A Sticker Slab is a charm that seals a sticker inside it. The sealed sticker's id
    // rides in the item proto's `wrapped_sticker` field (tag 12). SteamKit2 3.3.1 does not
    // model that field, so protobuf-net keeps it as extension data - verified against a real
    // applied slab (sticker_id=37 slab container, wrapped_sticker=4352 sealed sticker). The
    // tag and its read/write live here so decode, persistence and cache-reload stay in lockstep.
    //
    // TODO: when a SteamKit2 bump models `wrapped_sticker` as a generated property, switch to
    // it and delete the extension plumbing. Once the field is "known", Extensible.TryGetValue
    // returns false for it, which would silently blank every slab - so this is a breaking bump
    // to watch for (the StickerSlabTests pin the current extension behaviour).
    public static class StickerSlab
    {
        private const int WrappedStickerTag = 12;

        // Sealed sticker id for a slab, or 0 for an ordinary charm/sticker.
        public static uint GetWrappedStickerId(CEconItemPreviewDataBlock.Sticker sticker)
            => Extensible.TryGetValue<uint>(sticker, WrappedStickerTag, out var id) ? id : 0u;

        // Re-attach a persisted slab id so a cache-reloaded keychain matches a fresh decode.
        public static void SetWrappedStickerId(CEconItemPreviewDataBlock.Sticker sticker, uint id)
            => Extensible.AppendValue<uint>(sticker, WrappedStickerTag, id);
    }

    public class SteamAccountManager
    {
        public SteamClient Client { get; }
        public SteamUser User { get; }
        public SteamGameCoordinator GC { get; }
        public CallbackManager Manager { get; }
        public SteamAccount Account { get; }
        public bool IsConnected { get; set; }
        public bool IsLoggedIn { get; set; }
        // Whether the in-flight logon used a cached refresh token, so a rejection can fall back
        // to a fresh credential auth (see SteamService.OnLoggedOn).
        public bool UsedCachedToken { get; set; }
        public DateTime LastRequestTime { get; set; } = DateTime.MinValue;
        public SemaphoreSlim RateLimitSemaphore { get; } = new(1, 1);

        public SteamAccountManager(SteamAccount account)
        {
            Account = account;
            Client = new SteamClient();
            User = Client.GetHandler<SteamUser>()!;
            GC = Client.GetHandler<SteamGameCoordinator>()!;
            Manager = new CallbackManager(Client);
        }

        public void Dispose()
        {
            RateLimitSemaphore?.Dispose();
            Client?.Disconnect();
        }
    }

    // Persists the long-lived refresh token Steam hands back after a credential login, keyed by
    // configured username, so restarts can log on with the token instead of re-sending the
    // password (and re-prompting for any Steam Guard). A plain JSON file, gitignored like
    // steam-accounts.json - a refresh token is itself a credential. All access is locked since
    // each account logs on from its own thread.
    public class SteamTokenStore
    {
        private readonly string _path;
        private readonly object _lock = new();

        public SteamTokenStore(string path) => _path = path;

        public string? Get(string username)
        {
            lock (_lock)
            {
                return Read().GetValueOrDefault(username);
            }
        }

        public void Set(string username, string token)
        {
            lock (_lock)
            {
                var tokens = Read();
                tokens[username] = token;
                Write(tokens);
            }
        }

        public void Remove(string username)
        {
            lock (_lock)
            {
                var tokens = Read();
                if (tokens.Remove(username))
                {
                    Write(tokens);
                }
            }
        }

        private Dictionary<string, string> Read()
        {
            try
            {
                if (File.Exists(_path))
                {
                    return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? [];
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Missing/corrupt/unreadable: start empty. The next successful login rewrites it.
                Console.WriteLine($"Could not read {_path}: {ex.Message}");
            }
            return [];
        }

        private void Write(Dictionary<string, string> tokens)
        {
            try
            {
                File.WriteAllText(_path, JsonSerializer.Serialize(tokens));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A non-writable token store just means we re-auth with credentials next time.
                Console.WriteLine($"Could not write {_path}: {ex.Message}");
            }
        }
    }

    public class SteamService
    {
        private readonly List<SteamAccountManager> _accountManagers = [];
        private readonly SteamTokenStore _tokenStore = new("steam-tokens.json");
        // Written on the HTTP thread (ConnectAsync/Disconnect), read on the per-account callback
        // loop threads, so it must be volatile for those loops to observe a shutdown promptly.
        private volatile bool _isRunning = false;
        private readonly ConcurrentDictionary<ulong, List<TaskCompletionSource<CEconItemPreviewDataBlock?>>> _pendingRequests = new();
        private int _currentAccountIndex = 0;
        private readonly object _accountSelectionLock = new();


        public SteamService()
        {
            LoadAndInitializeAccounts();
        }

        private void LoadAndInitializeAccounts()
        {
            List<SteamAccount> accounts = [];

            if (File.Exists("steam-accounts.json"))
            {
                try
                {
                    var json = File.ReadAllText("steam-accounts.json");
                    var loadedAccounts = JsonSerializer.Deserialize<List<SteamAccount>>(json);
                    if (loadedAccounts != null && loadedAccounts.Count > 0)
                    {
                        accounts.AddRange(loadedAccounts);
                        Console.WriteLine($"Loaded {accounts.Count} Steam accounts from steam-accounts.json");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error loading steam-accounts.json: {ex.Message}");
                }
            }

            // Fallback to environment variables if no accounts loaded from JSON
            if (accounts.Count == 0)
            {
                var steamUsername = Environment.GetEnvironmentVariable("STEAM_USERNAME");
                var steamPassword = Environment.GetEnvironmentVariable("STEAM_PASSWORD");
                if (!string.IsNullOrEmpty(steamUsername) && !string.IsNullOrEmpty(steamPassword))
                {
                    accounts.Add(new SteamAccount { Username = steamUsername, Password = steamPassword });
                    Console.WriteLine("Using Steam account from environment variables");
                }
            }

            if (accounts.Count == 0)
            {
                throw new InvalidOperationException("No Steam accounts configured. Please provide steam-accounts.json or set STEAM_USERNAME/STEAM_PASSWORD environment variables.");
            }

            // Create account managers
            foreach (var account in accounts)
            {
                var manager = new SteamAccountManager(account);
                _accountManagers.Add(manager);

                // Subscribe to callbacks for each account
                manager.Manager.Subscribe<SteamClient.ConnectedCallback>((callback) => OnConnected(callback, manager));
                manager.Manager.Subscribe<SteamClient.DisconnectedCallback>((callback) => OnDisconnected(callback, manager));
                manager.Manager.Subscribe<SteamUser.LoggedOnCallback>((callback) => OnLoggedOn(callback, manager));
                manager.Manager.Subscribe<SteamUser.LoggedOffCallback>((callback) => OnLoggedOff(callback, manager));
                manager.Manager.Subscribe<SteamGameCoordinator.MessageCallback>((callback) => OnGCMessage(callback, manager));
            }
        }

        public async Task<CEconItemPreviewDataBlock?> GetItemInfoAsync(ulong s, ulong a, ulong d, ulong m)
        {
            if (_accountManagers.Count == 0)
            {
                throw new InvalidOperationException("No Steam accounts configured");
            }

            if (!_isRunning)
            {
                Console.WriteLine("Steam service not running, connecting...");
                await ConnectAsync();
            }

            var jobId = a; // Use A (itemid) parameter as Job ID

            // Check if request is already pending
            if (_pendingRequests.ContainsKey(jobId))
            {
                Console.WriteLine($"Request for itemid {jobId} already pending, waiting for existing request...");
                var tcs = new TaskCompletionSource<CEconItemPreviewDataBlock?>();
                _pendingRequests.AddOrUpdate(jobId,
                    [tcs],
                    (key, existingList) =>
                    {
                        lock (existingList)
                        {
                            existingList.Add(tcs);
                        }
                        return existingList;
                    });

                // Bound the wait: if the leader request never completes - or a race left this
                // waiter on a list nobody is driving - return null instead of hanging forever.
                // The window covers the leader's own 2s-per-account retry budget plus slack.
                var coalescedTimeout = Task.Delay(TimeSpan.FromSeconds(10));
                if (await Task.WhenAny(tcs.Task, coalescedTimeout) == coalescedTimeout)
                {
                    RemovePendingRequest(jobId, tcs);
                    Console.WriteLine($"Coalesced wait for itemid {jobId} timed out");
                    return null;
                }
                return await tcs.Task;
            }

            // Try up to 3 accounts or all available accounts
            var maxRetries = Math.Min(3, _accountManagers.Count);
            var attemptedAccounts = new HashSet<int>();

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                var accountManager = GetNextAvailableAccount(attemptedAccounts);
                if (accountManager == null)
                {
                    Console.WriteLine("No available accounts for request");
                    return null;
                }

                attemptedAccounts.Add(_accountManagers.IndexOf(accountManager));

                if (!accountManager.IsConnected || !accountManager.IsLoggedIn)
                {
                    Console.WriteLine($"[{accountManager.Account.Username}] Account not ready, trying next account...");
                    continue;
                }

                var result = await TryGCRequestWithAccount(accountManager, s, a, d, m, jobId);
                if (result != null)
                {
                    return result; // Success
                }

                Console.WriteLine($"[{accountManager.Account.Username}] Request timed out for job {jobId}, trying next account...");
            }

            Console.WriteLine($"All {maxRetries} account attempts failed for itemid {jobId}");
            return null;
        }

        // Removes one waiter from a job's pending list, dropping the job entry when it was the last.
        private void RemovePendingRequest(ulong jobId, TaskCompletionSource<CEconItemPreviewDataBlock?> tcs)
        {
            if (_pendingRequests.TryGetValue(jobId, out var list))
            {
                lock (list)
                {
                    list.Remove(tcs);
                    if (list.Count == 0)
                    {
                        _pendingRequests.TryRemove(jobId, out _);
                    }
                }
            }
        }

        private async Task<CEconItemPreviewDataBlock?> TryGCRequestWithAccount(SteamAccountManager accountManager, ulong s, ulong a, ulong d, ulong m, ulong jobId)
        {
            var tcs = new TaskCompletionSource<CEconItemPreviewDataBlock?>();

            _pendingRequests.AddOrUpdate(jobId,
                [tcs],
                (key, existingList) =>
                {
                    lock (existingList)
                    {
                        existingList.Add(tcs);
                    }
                    return existingList;
                });

            try
            {
                await SendGCRequest(accountManager, s, a, d, m, jobId);

                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(2));
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    RemovePendingRequest(jobId, tcs);
                    return null; // Timeout - will try next account
                }

                return await tcs.Task; // Success or GC returned null
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{accountManager.Account.Username}] Failed to send GC request: {ex.Message}");
                RemovePendingRequest(jobId, tcs);
                return null; // Exception - will try next account
            }
        }

        private SteamAccountManager? GetNextAvailableAccount(HashSet<int> attemptedAccounts)
        {
            // Round-robin selection, but skip already attempted accounts. Concurrent callers read
            // and advance _currentAccountIndex, so the read-modify-write is locked - otherwise two
            // requests can pick the same account and defeat the rotation and per-account throttle.
            lock (_accountSelectionLock)
            {
                for (int i = 0; i < _accountManagers.Count; i++)
                {
                    var index = (_currentAccountIndex + i) % _accountManagers.Count;
                    if (!attemptedAccounts.Contains(index))
                    {
                        _currentAccountIndex = (index + 1) % _accountManagers.Count;
                        return _accountManagers[index];
                    }
                }
                return null;
            }
        }

        private static async Task SendGCRequest(SteamAccountManager accountManager, ulong s, ulong a, ulong d, ulong m, ulong jobId)
        {
            await accountManager.RateLimitSemaphore.WaitAsync();
            try
            {
                var timeSinceLastRequest = DateTime.UtcNow - accountManager.LastRequestTime;
                var minimumInterval = TimeSpan.FromSeconds(1);

                if (timeSinceLastRequest < minimumInterval)
                {
                    var waitTime = minimumInterval - timeSinceLastRequest;
                    Console.WriteLine($"[{accountManager.Account.Username}] Rate limiting: waiting {waitTime.TotalMilliseconds:F0}ms");
                    await Task.Delay(waitTime);
                }

                accountManager.LastRequestTime = DateTime.UtcNow;

                var request = new ClientGCMsgProtobuf<CMsgGCCStrike15_v2_Client2GCEconPreviewDataBlockRequest>(
                    (uint)ECsgoGCMsg.k_EMsgGCCStrike15_v2_Client2GCEconPreviewDataBlockRequest);
                request.Body.param_s = s;
                request.Body.param_a = a;
                request.Body.param_d = d;
                request.Body.param_m = m;

                accountManager.GC.Send(request, 730);
                Console.WriteLine($"[{accountManager.Account.Username}] Sent GC request for itemid {jobId}");
            }
            finally
            {
                accountManager.RateLimitSemaphore.Release();
            }
        }

        public async Task ConnectAsync()
        {
            Console.WriteLine($"ConnectAsync called - connecting {_accountManagers.Count} Steam accounts");
            _isRunning = true;

            // Connect all accounts
            var connectionTasks = _accountManagers.Select(ConnectAccount).ToArray();

            // Start callback managers for all accounts
            foreach (var accountManager in _accountManagers)
            {
                _ = Task.Run(() =>
                {
                    Console.WriteLine($"[{accountManager.Account.Username}] Starting callback manager loop");
                    while (_isRunning)
                    {
                        accountManager.Manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
                    }
                    Console.WriteLine($"[{accountManager.Account.Username}] Callback manager loop ended");
                });
            }

            // Wait for at least one account to be ready
            var timeout = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < timeout)
            {
                if (_accountManagers.Any(am => am.IsConnected && am.IsLoggedIn))
                {
                    Console.WriteLine("At least one Steam account connected successfully");
                    return;
                }
                Console.WriteLine("Waiting for account connections...");
                await Task.Delay(1000);
            }

            var connectedCount = _accountManagers.Count(am => am.IsConnected && am.IsLoggedIn);
            if (connectedCount == 0)
            {
                throw new Exception("Failed to connect any Steam accounts");
            }

            Console.WriteLine($"Steam service started with {connectedCount}/{_accountManagers.Count} accounts connected");
        }

        private async Task ConnectAccount(SteamAccountManager accountManager)
        {
            try
            {
                Console.WriteLine($"[{accountManager.Account.Username}] Connecting account");
                accountManager.Client.Connect();
                await Task.Delay(2000); // Give some time for connection
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{accountManager.Account.Username}] Failed to connect account: {ex.Message}");
            }
        }

        public void Disconnect()
        {
            Console.WriteLine("Disconnecting from Steam...");
            _isRunning = false;
            foreach (var accountManager in _accountManagers)
            {
                accountManager.Dispose();
            }
        }

        private void OnConnected(SteamClient.ConnectedCallback callback, SteamAccountManager accountManager)
        {
            Console.WriteLine($"[{accountManager.Account.Username}] Steam client connected");
            accountManager.IsConnected = true;
            _ = LogOnAsync(accountManager); // async auth; don't block the callback thread
        }

        // Steam no longer accepts the legacy username+password logon (it returns InvalidPassword).
        // A cached refresh token logs on directly; otherwise we exchange the credentials for a new
        // token through the authentication service, cache it, and log on with that. No
        // authenticator is supplied, so this covers accounts without Steam Guard; an account that
        // requires a Guard code will throw during the credential exchange.
        private async Task LogOnAsync(SteamAccountManager accountManager)
        {
            var username = accountManager.Account.Username;

            // A token Steam later rejects is dropped in OnLoggedOn, which calls back here; with no
            // cached token this then falls through to a fresh credential auth.
            var cachedToken = _tokenStore.Get(username);
            if (cachedToken != null)
            {
                Console.WriteLine($"[{username}] Logging on with cached token");
                accountManager.UsedCachedToken = true;
                accountManager.User.LogOn(new SteamUser.LogOnDetails
                {
                    Username = username,
                    AccessToken = cachedToken,
                });
                return;
            }

            try
            {
                Console.WriteLine($"[{username}] Authenticating");
                accountManager.UsedCachedToken = false;
                var session = await accountManager.Client.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
                {
                    Username = username,
                    Password = accountManager.Account.Password,
                    IsPersistentSession = true, // long-lived refresh token we can reuse across restarts
                });

                var poll = await session.PollingWaitForResultAsync();

                // Key by the configured username so the next lookup (also by it) hits.
                _tokenStore.Set(username, poll.RefreshToken);

                Console.WriteLine($"[{username}] Logging on");
                accountManager.User.LogOn(new SteamUser.LogOnDetails
                {
                    Username = poll.AccountName,
                    AccessToken = poll.RefreshToken,
                });
            }
            catch (Exception ex)
            {
                // Bad credentials, or an account that needs a Steam Guard code we don't supply.
                Console.WriteLine($"[{username}] Authentication failed: {ex.Message}");
            }
        }

        private void OnDisconnected(SteamClient.DisconnectedCallback callback, SteamAccountManager accountManager)
        {
            Console.WriteLine($"[{accountManager.Account.Username}] Steam client disconnected. User initiated: {callback.UserInitiated}");
            accountManager.IsConnected = false;
            accountManager.IsLoggedIn = false;

            if (!callback.UserInitiated && _isRunning)
            {
                Console.WriteLine($"[{accountManager.Account.Username}] Disconnection was not user-initiated, attempting to reconnect...");
                _ = Task.Run(async () =>
                {
                    await Task.Delay(5000); // Wait 5 seconds before reconnecting
                    if (_isRunning && !accountManager.IsConnected)
                    {
                        Console.WriteLine($"[{accountManager.Account.Username}] Reconnecting Steam account");
                        accountManager.Client.Connect();
                    }
                });
            }
        }

        private void OnLoggedOn(SteamUser.LoggedOnCallback callback, SteamAccountManager accountManager)
        {
            var username = accountManager.Account.Username;
            Console.WriteLine($"[{username}] Steam logon result: {callback.Result}");
            if (callback.Result != EResult.OK)
            {
                Console.WriteLine($"[{username}] Failed to log on to Steam: {callback.Result}");

                // A cached token Steam rejected (expired, revoked, password changed, ...): drop it
                // and re-authenticate with credentials. The credential path sets UsedCachedToken
                // false, so a failure there just logs and never loops back here.
                if (accountManager.UsedCachedToken)
                {
                    Console.WriteLine($"[{username}] Cached token rejected; re-authenticating with credentials");
                    accountManager.UsedCachedToken = false;
                    _tokenStore.Remove(username);
                    _ = LogOnAsync(accountManager);
                }
                return;
            }

            accountManager.IsLoggedIn = true;

            // Launch CS:GO to connect to game coordinator
            var playGame = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);
            playGame.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed
            {
                game_id = new GameID(730)
            });
            accountManager.Client.Send(playGame);

            _ = Task.Run(async () =>
            {
                // Wait for CS:GO connection to stabilize before sending Hello
                Console.WriteLine($"[{accountManager.Account.Username}] Waiting 5 seconds for connection to stabilize...");
                await Task.Delay(5000);

                // Send Hello message to GC to establish session
                Console.WriteLine($"[{accountManager.Account.Username}] Sending Hello message to Game Coordinator...");
                var helloMsg = new ClientGCMsgProtobuf<SteamKit2.GC.CSGO.Internal.CMsgClientHello>((uint)EGCBaseClientMsg.k_EMsgGCClientHello);
                helloMsg.Body.version = 2000202; // Protocol version
                accountManager.GC.Send(helloMsg, 730);
            });
        }

        private static void OnLoggedOff(SteamUser.LoggedOffCallback callback, SteamAccountManager accountManager)
        {
            Console.WriteLine($"[{accountManager.Account.Username}] Steam user logged off. Result: {callback.Result}");
            accountManager.IsLoggedIn = false;
        }

        private void OnGCMessage(SteamGameCoordinator.MessageCallback callback, SteamAccountManager accountManager)
        {
            if (callback.AppID != 730) return;

            if (callback.EMsg == (uint)ECsgoGCMsg.k_EMsgGCCStrike15_v2_Client2GCEconPreviewDataBlockResponse)
            {
                var response = new ClientGCMsgProtobuf<CMsgGCCStrike15_v2_Client2GCEconPreviewDataBlockResponse>(callback.Message);
                var responseItemId = response.Body.iteminfo?.itemid ?? 0;

                if (_pendingRequests.TryGetValue(responseItemId, out var pendingList))
                {
                    CEconItemPreviewDataBlock? item = null;
                    if (response.Body.iteminfo != null)
                    {
                        item = response.Body.iteminfo;
                    }
                    else
                    {
                        Console.WriteLine($"[{accountManager.Account.Username}] No item info in response");
                    }

                    // Resolve all pending requests for this itemid
                    lock (pendingList)
                    {
                        Console.WriteLine($"[{accountManager.Account.Username}] Resolving {pendingList.Count} pending requests for itemid {responseItemId}");
                        foreach (var tcs in pendingList)
                        {
                            tcs.SetResult(item);
                        }
                    }
                    _pendingRequests.TryRemove(responseItemId, out _);
                }
                else
                {
                    Console.WriteLine($"[{accountManager.Account.Username}] No pending request found for ItemID: {responseItemId}");
                }
            }
            else if (callback.EMsg == (uint)EGCBaseClientMsg.k_EMsgGCClientConnectionStatus)
            {
                var response = new ClientGCMsgProtobuf<CMsgConnectionStatus>(callback.Message);
                Console.WriteLine($"[{accountManager.Account.Username}] GC Connection Status:{response.Body.status}, WaitSeconds:{response.Body.wait_seconds}");

                if (response.Body.status != GCConnectionStatus.GCConnectionStatus_HAVE_SESSION)
                {
                    Console.WriteLine($"[{accountManager.Account.Username}] WARNING: Not properly connected to Game Coordinator!");
                }
            }
            else if (callback.EMsg == (uint)EGCBaseClientMsg.k_EMsgGCClientWelcome)
            {
                var response = new ClientGCMsgProtobuf<CMsgClientWelcome>(callback.Message);
                Console.WriteLine($"[{accountManager.Account.Username}] GC Welcome Received, version: {response.Body.version}");
            }
            else if (callback.EMsg == (uint)ECsgoGCMsg.k_EMsgGCCStrike15_v2_ClientLogonFatalError)
            {
                var response = new ClientGCMsgProtobuf<CMsgGCCStrike15_v2_ClientLogonFatalError>(callback.Message);
                Console.WriteLine($"[{accountManager.Account.Username}] ERROR: GC Fatal Logon Error: Code:{response.Body.errorcode}, Message: {response.Body.message}");
            }
            else if (callback.EMsg == (uint)ECsgoGCMsg.k_EMsgGCCStrike15_v2_GC2ClientGlobalStats)
            {
                Console.WriteLine($"[{accountManager.Account.Username}] GC Global Stats Received");
            }
            else if (callback.EMsg == (uint)ECsgoGCMsg.k_EMsgGCCStrike15_v2_MatchmakingGC2ClientHello)
            {
                Console.WriteLine($"[{accountManager.Account.Username}] GC Hello Received");
            }
            else if (callback.EMsg == (uint)ECsgoGCMsg.k_EMsgGCCStrike15_v2_ClientGCRankUpdate)
            {
                Console.WriteLine($"[{accountManager.Account.Username}] GC Rank Update Received");
            }
            else
            {
                Console.WriteLine($"[{accountManager.Account.Username}] Unhandled GC Message Received: {callback.EMsg}");
            }
        }
    }
}
