using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Rewrite;
using Microsoft.Data.Sqlite;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.GC;
using SteamKit2.GC.CSGO.Internal;
using SteamKit2.Internal;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Web;
using CSGOSkinAPI.Services;
using CSGOSkinAPI.Models;
using ProtoBuf;
using System.Runtime.CompilerServices;

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

    public class DatabaseService
    {
        private readonly string _connectionString;

        public DatabaseService(string? databasePath = null)
        {
            var dbPath = databasePath ?? "searches.db";
            _connectionString = $"Data Source={dbPath};foreign keys=true;";
        }

        // Opens a connection with a busy timeout so a read that lands during a write waits for the
        // lock instead of failing immediately with SQLITE_BUSY. The warm service writes
        // concurrently with cache-hit reads, so this matters under load.
        private async Task<SqliteConnection> OpenConnectionAsync()
        {
            var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            await using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA busy_timeout=5000;";
            await pragma.ExecuteNonQueryAsync();
            return connection;
        }

        public async Task InitializeDatabaseAsync()
        {
            using var connection = await OpenConnectionAsync();

            // WAL lets readers and a writer proceed concurrently (the default rollback journal
            // blocks readers during a write). It is a persistent property of the database file,
            // so setting it once at startup is enough.
            await using (var walCommand = connection.CreateCommand())
            {
                walCommand.CommandText = "PRAGMA journal_mode=WAL;";
                await walCommand.ExecuteNonQueryAsync();
            }

            // `itemid` is a sound PRIMARY KEY because it is immutable identity: the GC mints
            // a new itemid whenever an item's config changes (stickers, name tag, ...), so a
            // row here never goes stale. Caveat: non-paint types (music kits, graffiti,
            // passes, ...) decode with itemid == 0 and would all collide on key 0 — they are
            // filtered out before persistence in SaveItemWithExtrasAsync.
            var createTableCommand = @"
                CREATE TABLE IF NOT EXISTS searches (
                    itemid INTEGER PRIMARY KEY NOT NULL,
                    defindex INTEGER NOT NULL,
                    paintindex INTEGER NOT NULL,
                    rarity INTEGER NOT NULL,
                    quality INTEGER NOT NULL,
                    paintwear INTEGER NOT NULL,
                    paintseed INTEGER NOT NULL,
                    inventory INTEGER NOT NULL,
                    origin INTEGER NOT NULL,
                    stattrak INTEGER NOT NULL,
                    killeatervalue INTEGER
                )";

            using var command = new SqliteCommand(createTableCommand, connection);
            await command.ExecuteNonQueryAsync();

            // killeatervalue (the live StatTrak kill count) was added after this table shipped;
            // back-fill the column on pre-existing databases. SQLite has no "ADD COLUMN IF NOT
            // EXISTS", so a duplicate-column error just means the migration already ran.
            try
            {
                using var alterCommand = new SqliteCommand(
                    "ALTER TABLE searches ADD COLUMN killeatervalue INTEGER", connection);
                await alterCommand.ExecuteNonQueryAsync();
            }
            catch (SqliteException)
            {
                // Column already exists - nothing to migrate.
            }


            foreach (var tableName in new[] { "stickers", "keychains" })
            {
                var createStickerTableCommand = @$"
                    CREATE TABLE IF NOT EXISTS {tableName} (
                        itemid INTEGER NOT NULL,
                        slot INTEGER NOT NULL,
                        sticker_id INTEGER NOT NULL,
                        wear REAL NOT NULL,
                        scale REAL,
                        rotation REAL,
                        tint_id INTEGER,
                        offset_x REAL,
                        offset_y REAL,
                        offset_z REAL,
                        pattern INTEGER,
                        highlight_reel INTEGER,
                        wrapped_sticker INTEGER,
                        FOREIGN KEY (itemid) REFERENCES searches(itemid) ON DELETE CASCADE
                )";

                using var stickersCommand = new SqliteCommand(createStickerTableCommand, connection);
                await stickersCommand.ExecuteNonQueryAsync();

                // wrapped_sticker (the Sticker Slab's sealed sticker id) was added after these
                // tables shipped, so back-fill the column on pre-existing cache databases.
                // SQLite has no "ADD COLUMN IF NOT EXISTS"; a duplicate column just no-ops.
                try
                {
                    using var alterCommand = new SqliteCommand(
                        $"ALTER TABLE {tableName} ADD COLUMN wrapped_sticker INTEGER", connection);
                    await alterCommand.ExecuteNonQueryAsync();
                }
                catch (SqliteException)
                {
                    // Column already exists - nothing to migrate.
                }

                var createIndexCommand = @$"CREATE INDEX IF NOT EXISTS itemid on {tableName} (itemid)";
                using var indexCommand = new SqliteCommand(createIndexCommand, connection);
                await indexCommand.ExecuteNonQueryAsync();
            }

            // Log of background inventory warms (one row per owner steamid, refreshed on
            // each warm). Doubles as the throttle that keeps a burst of cache misses for
            // one owner from re-fetching their inventory over and over.
            var createWarmsTableCommand = @"
                CREATE TABLE IF NOT EXISTS inventory_warms (
                    steamid INTEGER PRIMARY KEY NOT NULL,
                    last_warmed TEXT NOT NULL,
                    items_cached INTEGER NOT NULL
                )";
            using var warmsCommand = new SqliteCommand(createWarmsTableCommand, connection);
            await warmsCommand.ExecuteNonQueryAsync();
        }

        public async Task<List<CEconItemPreviewDataBlock.Sticker>> GetStickersAsync(ulong itemId, bool stickersTable)
        {
            using var connection = await OpenConnectionAsync();

            const string stickersQuery = "SELECT * FROM stickers WHERE itemid = @itemid ORDER BY slot";
            const string keychainsQuery = "SELECT * FROM keychains WHERE itemid = @itemid ORDER BY slot";
            var query = stickersTable ? stickersQuery : keychainsQuery;
            using var command = new SqliteCommand(query, connection);
            command.Parameters.AddWithValue("@itemid", (long)itemId);

            var stickers = new List<CEconItemPreviewDataBlock.Sticker>();
            using var reader = await command.ExecuteReaderAsync();

            var slotOrd = reader.GetOrdinal("slot");
            var stickerIdOrd = reader.GetOrdinal("sticker_id");
            var wearOrd = reader.GetOrdinal("wear");
            var scaleOrd = reader.GetOrdinal("scale");
            var rotationOrd = reader.GetOrdinal("rotation");
            var tintIdOrd = reader.GetOrdinal("tint_id");
            var offsetXOrd = reader.GetOrdinal("offset_x");
            var offsetYOrd = reader.GetOrdinal("offset_y");
            var offsetZOrd = reader.GetOrdinal("offset_z");
            var patternOrd = reader.GetOrdinal("pattern");
            var highlightReelOrd = reader.GetOrdinal("highlight_reel");
            var wrappedStickerOrd = reader.GetOrdinal("wrapped_sticker");

            while (await reader.ReadAsync())
            {
                var sticker = new CEconItemPreviewDataBlock.Sticker
                {
                    slot = (uint)reader.GetInt32(slotOrd),
                    sticker_id = (uint)reader.GetInt32(stickerIdOrd),
                    wear = reader.GetFloat(wearOrd)
                };
                if (!reader.IsDBNull(scaleOrd)) sticker.scale = reader.GetFloat(scaleOrd);
                if (!reader.IsDBNull(rotationOrd)) sticker.rotation = reader.GetFloat(rotationOrd);
                if (!reader.IsDBNull(tintIdOrd)) sticker.tint_id = (uint)reader.GetInt32(tintIdOrd);
                if (!reader.IsDBNull(offsetXOrd)) sticker.offset_x = reader.GetFloat(offsetXOrd);
                if (!reader.IsDBNull(offsetYOrd)) sticker.offset_y = reader.GetFloat(offsetYOrd);
                if (!reader.IsDBNull(offsetZOrd)) sticker.offset_z = reader.GetFloat(offsetZOrd);
                if (!reader.IsDBNull(patternOrd)) sticker.pattern = (uint)reader.GetInt32(patternOrd);
                if (!reader.IsDBNull(highlightReelOrd)) sticker.highlight_reel = (uint)reader.GetInt32(highlightReelOrd);
                // Re-attach the Sticker Slab's sealed sticker id as proto field 12, so a cached
                // slab looks identical to a freshly-decoded one and resolves the same way.
                if (!reader.IsDBNull(wrappedStickerOrd))
                {
                    StickerSlab.SetWrappedStickerId(sticker, (uint)reader.GetInt32(wrappedStickerOrd));
                }
                stickers.Add(sticker);
            }

            return stickers;
        }

        public async Task<CEconItemPreviewDataBlock?> GetItemAsync(ulong itemId)
        {
            using var connection = await OpenConnectionAsync();

            var query = "SELECT * FROM searches WHERE itemid = @itemid";
            using var command = new SqliteCommand(query, connection);
            command.Parameters.AddWithValue("@itemid", itemId);

            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var itemIdOrd = reader.GetOrdinal("itemid");
                var defIndexOrd = reader.GetOrdinal("defindex");
                var paintIndexOrd = reader.GetOrdinal("paintindex");
                var rarityOrd = reader.GetOrdinal("rarity");
                var qualityOrd = reader.GetOrdinal("quality");
                var paintWearOrd = reader.GetOrdinal("paintwear");
                var paintSeedOrd = reader.GetOrdinal("paintseed");
                var inventoryOrd = reader.GetOrdinal("inventory");
                var originOrd = reader.GetOrdinal("origin");
                var statTrakOrd = reader.GetOrdinal("stattrak");
                var killEaterOrd = reader.GetOrdinal("killeatervalue");

                var item = new CEconItemPreviewDataBlock
                {
                    itemid = (ulong)reader.GetInt64(itemIdOrd),
                    defindex = (uint)reader.GetInt32(defIndexOrd),
                    paintindex = (uint)reader.GetInt32(paintIndexOrd),
                    rarity = (uint)reader.GetInt32(rarityOrd),
                    quality = (uint)reader.GetInt32(qualityOrd),
                    paintwear = (uint)reader.GetInt32(paintWearOrd),
                    paintseed = (uint)reader.GetInt32(paintSeedOrd),
                    inventory = (uint)reader.GetInt64(inventoryOrd),
                    origin = (uint)reader.GetInt32(originOrd)
                };
                // killeatervalue non-null is both the StatTrak flag and the live kill count.
                // Prefer the stored count; fall back to 0 for legacy rows cached before the
                // column existed (StatTrak presence stays correct, count shows 0 until re-cached).
                if (!reader.IsDBNull(killEaterOrd)) item.killeatervalue = (uint)reader.GetInt64(killEaterOrd);
                else if (reader.GetInt32(statTrakOrd) == 1) item.killeatervalue = 0;
                item.stickers.AddRange(await GetStickersAsync(itemId, true));
                item.keychains.AddRange(await GetStickersAsync(itemId, false));

                return item;
            }

            return null;
        }

        public async Task SaveItemAsync(CEconItemPreviewDataBlock item)
        {
            using var connection = await OpenConnectionAsync();
            await InsertSearchRowAsync(item, connection, null);
        }

        private static async Task InsertSearchRowAsync(CEconItemPreviewDataBlock item, SqliteConnection connection, SqliteTransaction? transaction)
        {
            var insert = @"
                INSERT OR REPLACE INTO searches
                (itemid, defindex, paintindex, rarity, quality, paintwear, paintseed, inventory, origin, stattrak, killeatervalue)
                VALUES (@itemid, @defindex, @paintindex, @rarity, @quality, @paintwear, @paintseed, @inventory, @origin, @stattrak, @killeatervalue)";

            using var command = new SqliteCommand(insert, connection, transaction);
            command.Parameters.AddWithValue("@itemid", (long)item.itemid);
            command.Parameters.AddWithValue("@defindex", item.defindex);
            command.Parameters.AddWithValue("@paintindex", item.paintindex);
            command.Parameters.AddWithValue("@rarity", item.rarity);
            command.Parameters.AddWithValue("@quality", item.quality);
            command.Parameters.AddWithValue("@paintwear", item.paintwear);
            command.Parameters.AddWithValue("@paintseed", item.paintseed);
            command.Parameters.AddWithValue("@inventory", item.inventory);
            command.Parameters.AddWithValue("@origin", item.origin);
            command.Parameters.AddWithValue("@stattrak", item.ShouldSerializekilleatervalue() ? 1 : 0);
            // The live kill count when present, so cache hits keep it (not just the flag).
            command.Parameters.AddWithValue("@killeatervalue",
                item.ShouldSerializekilleatervalue() ? item.killeatervalue : DBNull.Value);

            await command.ExecuteNonQueryAsync();
        }

        public async Task SaveItemWithExtrasAsync(CEconItemPreviewDataBlock itemInfo)
        {
            // Music kits, graffiti, passes, standalone stickers, tools, etc. decode
            // with itemid == 0 (it is intrinsic to those defindex types, not a missing
            // value). Since `searches.itemid` is the PRIMARY KEY, persisting any of them
            // would collapse every zero-itemid item onto key 0 and, with INSERT OR
            // REPLACE, silently overwrite each other. These items carry no expensive
            // float/seed worth caching, so skip them. (See docs/inventory-endpoint-cert.md.)
            if (itemInfo.itemid == 0)
            {
                return;
            }

            // Persist the row and its extras atomically, and idempotently: re-saving the
            // same itemid must not duplicate sticker/keychain rows. The searches row uses
            // INSERT OR REPLACE, but the extras are plain INSERTs into tables with no
            // unique constraint on (itemid, slot) — slots are deliberately non-unique so
            // stacked stickers can share a slot — so we clear and rewrite the whole set
            // rather than relying on an upsert. (See docs/inventory-endpoint-cert.md 3b.)
            using var connection = await OpenConnectionAsync();
            using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

            await InsertSearchRowAsync(itemInfo, connection, transaction);
            await ClearExtrasAsync(itemInfo.itemid, connection, transaction);

            if (itemInfo.stickers?.Count > 0)
            {
                await SaveStickersAsync(itemInfo.itemid, itemInfo.stickers, true, connection, transaction);
            }

            if (itemInfo.keychains?.Count > 0)
            {
                await SaveStickersAsync(itemInfo.itemid, itemInfo.keychains, false, connection, transaction);
            }

            await transaction.CommitAsync();
        }

        private static async Task ClearExtrasAsync(ulong itemId, SqliteConnection connection, SqliteTransaction transaction)
        {
            foreach (var table in new[] { "stickers", "keychains" })
            {
                using var command = new SqliteCommand($"DELETE FROM {table} WHERE itemid = @itemid", connection, transaction);
                command.Parameters.AddWithValue("@itemid", (long)itemId);
                await command.ExecuteNonQueryAsync();
            }
        }

        private static async Task SaveStickersAsync(ulong itemId, List<CEconItemPreviewDataBlock.Sticker> items, bool stickerTable, SqliteConnection connection, SqliteTransaction transaction)
        {
            const string insertSchema = @"
                (itemid, slot, sticker_id, wear, scale, rotation, tint_id, offset_x, offset_y, offset_z, pattern, highlight_reel, wrapped_sticker) VALUES
                (@itemid, @slot, @sticker_id, @wear, @scale, @rotation, @tint_id, @offset_x, @offset_y, @offset_z, @pattern, @highlight_reel, @wrapped_sticker)";
            const string insertStickersQuery = @"INSERT INTO stickers " + insertSchema;
            const string insertKeychainsQuery = @"INSERT INTO keychains " + insertSchema;

            var insertQuery = stickerTable ? insertStickersQuery : insertKeychainsQuery;

            foreach (var item in items)
            {
                using var insertCommand = new SqliteCommand(insertQuery, connection, transaction);
                insertCommand.Parameters.AddWithValue("@itemid", (long)itemId);
                insertCommand.Parameters.AddWithValue("@slot", item.slot);
                insertCommand.Parameters.AddWithValue("@sticker_id", item.sticker_id);
                insertCommand.Parameters.AddWithValue("@wear", item.wear);
                insertCommand.Parameters.AddWithValue("@scale", item.ShouldSerializescale() ? item.scale : DBNull.Value);
                insertCommand.Parameters.AddWithValue("@rotation", item.ShouldSerializerotation() ? item.rotation : DBNull.Value);
                insertCommand.Parameters.AddWithValue("@tint_id", item.ShouldSerializetint_id() ? item.tint_id : DBNull.Value);
                insertCommand.Parameters.AddWithValue("@offset_x", item.ShouldSerializeoffset_x() ? item.offset_x : DBNull.Value);
                insertCommand.Parameters.AddWithValue("@offset_y", item.ShouldSerializeoffset_y() ? item.offset_y : DBNull.Value);
                insertCommand.Parameters.AddWithValue("@offset_z", item.ShouldSerializeoffset_z() ? item.offset_z : DBNull.Value);
                insertCommand.Parameters.AddWithValue("@pattern", item.ShouldSerializepattern() ? item.pattern : DBNull.Value);
                insertCommand.Parameters.AddWithValue("@highlight_reel", item.ShouldSerializehighlight_reel() ? item.highlight_reel : DBNull.Value);
                // Sticker Slab's sealed sticker id, carried in the unmodeled proto field 12.
                var wrapped = StickerSlab.GetWrappedStickerId(item);
                insertCommand.Parameters.AddWithValue("@wrapped_sticker", wrapped != 0 ? wrapped : DBNull.Value);
                await insertCommand.ExecuteNonQueryAsync();
            }
        }

        public async Task<DateTime?> GetLastWarmAsync(ulong steamid)
        {
            using var connection = await OpenConnectionAsync();

            using var command = new SqliteCommand("SELECT last_warmed FROM inventory_warms WHERE steamid = @steamid", connection);
            command.Parameters.AddWithValue("@steamid", (long)steamid);

            var value = await command.ExecuteScalarAsync();
            return value is string text
                ? DateTime.Parse(text, null, System.Globalization.DateTimeStyles.RoundtripKind)
                : null;
        }

        public async Task RecordWarmAsync(ulong steamid, int itemsCached)
        {
            using var connection = await OpenConnectionAsync();

            const string upsertQuery = @"INSERT OR REPLACE INTO inventory_warms
                (steamid, last_warmed, items_cached)
                VALUES (@steamid, @last_warmed, @items_cached)";
            using var command = new SqliteCommand(upsertQuery, connection);
            command.Parameters.AddWithValue("@steamid", (long)steamid);
            command.Parameters.AddWithValue("@last_warmed", DateTime.UtcNow.ToString("o"));
            command.Parameters.AddWithValue("@items_cached", itemsCached);
            await command.ExecuteNonQueryAsync();
        }
    }

    // Background cache warmer: when a single-item lookup misses the DB, the owner's whole
    // inventory becomes interesting - wild inspect links tend to come in clusters from one
    // inventory (trade threads, showcases). This fetches that inventory once, decodes each
    // item's embedded certificate locally (see docs/inventory-endpoint-cert.md), and
    // persists the results, so follow-up lookups become DB hits with zero GC traffic.
    public class InventoryWarmService(IHttpClientFactory httpClientFactory, DatabaseService dbService) : BackgroundService
    {
        // One warm per owner per cooldown: a burst of misses for the same inventory should
        // cost a single fetch, and a stale link whose item left the inventory will never
        // become warmable no matter how often we retry.
        private static readonly TimeSpan WarmCooldown = TimeSpan.FromHours(24);

        // Drop-on-full keeps a flood of misses from queueing unbounded work; a dropped id
        // re-enqueues naturally the next time one of its items misses the cache.
        private readonly Channel<ulong> _queue = Channel.CreateBounded<ulong>(
            new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropWrite });

        public void Enqueue(ulong steamid) => _queue.Writer.TryWrite(steamid);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Serial on purpose: one steamcommunity.com fetch at a time stays well inside
            // its rate limits, and guarantees a burst of misses for one owner resolves as
            // one fetch (the first warm is recorded before the next dequeue checks the
            // cooldown).
            await foreach (var steamid in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await WarmInventoryAsync(steamid, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.WriteLine($"Inventory warm for {steamid} failed: {ex.Message}");
                }
            }
        }

        private async Task WarmInventoryAsync(ulong steamid, CancellationToken cancellationToken)
        {
            var lastWarmed = await dbService.GetLastWarmAsync(steamid);
            if (lastWarmed != null && DateTime.UtcNow - lastWarmed < WarmCooldown)
            {
                return;
            }

            // Log the attempt before fetching so failures (private inventory, rate limit)
            // are throttled too instead of being retried on every subsequent miss.
            await dbService.RecordWarmAsync(steamid, 0);

            using var httpClient = httpClientFactory.CreateClient("steam");
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            var response = await httpClient.GetAsync(
                $"https://steamcommunity.com/inventory/{steamid}/730/2?l=english&count=2000", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Inventory warm for {steamid}: fetch failed with {response.StatusCode}");
                return;
            }

            var inventoryData = JsonSerializer.Deserialize<SteamInventoryResponse>(
                await response.Content.ReadAsStringAsync(cancellationToken));
            if (inventoryData?.assets == null || inventoryData.descriptions == null)
            {
                Console.WriteLine($"Inventory warm for {steamid}: empty or invalid inventory");
                return;
            }

            var propsByAsset = inventoryData.asset_properties?
                .ToDictionary(ap => ap.assetid, ap => ap.asset_properties ?? [])
                ?? [];

            var cached = 0;
            foreach (var asset in inventoryData.assets)
            {
                var description = inventoryData.descriptions.FirstOrDefault(d =>
                    d.classid == asset.classid && d.instanceid == asset.instanceid);
                var actionLink = description?.actions?.FirstOrDefault(a =>
                    a.link?.Contains("csgo_econ_action_preview") == true)?.link;
                if (actionLink == null)
                {
                    continue;
                }

                propsByAsset.TryGetValue(asset.assetid, out var assetProps);
                var inspectLink = Controllers.SkinController.BuildInspectLink(
                    actionLink, assetProps, steamid.ToString(), asset.assetid);

                // Only certificate-bearing items decode locally (directItem != null);
                // legacy S/A/D links parse but would need the GC, so they are skipped.
                // SaveItemWithExtrasAsync additionally guards the itemid==0 non-paint
                // types that cannot be keyed.
                var directItem = Controllers.SkinController.ParseInspectUrl(inspectLink)?.directItem;
                if (directItem != null && directItem.itemid != 0)
                {
                    await dbService.SaveItemWithExtrasAsync(directItem);
                    cached++;
                }
            }

            await dbService.RecordWarmAsync(steamid, cached);
            Console.WriteLine($"Inventory warm for {steamid}: cached {cached} of {inventoryData.assets.Count} items");
        }
    }

    public class ConstDataService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly ConstData _constData;
        private readonly StickerCatalog _stickers;
        private readonly Dictionary<string, string> _skinImages;

        public ConstDataService()
        {
            var jsonString = File.ReadAllText("const.json");
            _constData = JsonSerializer.Deserialize<ConstData>(jsonString, JsonOptions) ?? new ConstData();

            // sticker_id/keychain_id -> {name, image}, generated by scripts/update_skin_data.py.
            // Missing is fine: a brand-new decal the catalog predates resolves to null and the
            // frontend shows a labeled placeholder instead.
            var stickerJson = File.ReadAllText("stickers.json");
            _stickers = JsonSerializer.Deserialize<StickerCatalog>(stickerJson, JsonOptions) ?? new StickerCatalog();

            // "<defindex>_<paintindex>" -> skin image. The decoded item carries no image, so the
            // item page relies on this; keyed by weapon too since a paint kit looks different on
            // each weapon it appears on.
            var skinImageJson = File.ReadAllText("skin-images.json");
            _skinImages = JsonSerializer.Deserialize<Dictionary<string, string>>(skinImageJson, JsonOptions) ?? [];
        }

        public StickerKit? ResolveSticker(uint stickerId) => Resolve(_stickers.Stickers, stickerId);

        public StickerKit? ResolveKeychain(uint keychainId) => Resolve(_stickers.Keychains, keychainId);

        private static StickerKit? Resolve(Dictionary<string, StickerKit>? map, uint id)
            => map != null && map.TryGetValue(id.ToString(), out var kit) ? kit : null;

        // Representative image for a skin, or "" for vanilla/unknown combos (paintindex 0 and
        // anything the catalog predates).
        public string ResolveSkinImage(uint defindex, uint paintindex)
            => _skinImages.TryGetValue($"{defindex}_{paintindex}", out var image) ? image : "";

        public ItemInformation GetItemInformation(CEconItemPreviewDataBlock item)
        {
            // Paint-less items (passes, some medals, ...) reuse this lookup for their
            // display name, but gaps are expected for types the community data doesn't
            // cover and the frontend shows Steam's own name for them anyway. Only a
            // skinned weapon's absence is a signal that const.json needs regenerating.
            var weaponType = GetWeaponName(item.defindex, warnIfMissing: item.paintindex != 0);
            var pattern = GetPatternName(item.paintindex);
            var paintseed = (int)item.paintseed;
            var paintindex = (int)item.paintindex;
            var paintWear = GetPaintWear(item.paintwear);
            var wearName = GetWearFromFloat(paintWear);
            var isKnifeOrGlove = IsKnifeOrGlove(item.defindex);
            var isSouvenir = IsSouvenir(item.quality);

            var special = "";

            if (pattern == "Marble Fade" && _constData.Fireice?.Contains(weaponType) == true
                && paintseed >= 0 && paintseed < _constData.FireiceOrder!.Length)
            {
                var fireiceIndex = _constData.FireiceOrder[paintseed];
                if (fireiceIndex >= 0 && fireiceIndex < ConstData.FireIceNames.Length)
                {
                    special = ConstData.FireIceNames[fireiceIndex];
                }
            }
            else if (pattern == "Fade" && _constData.Fades?.ContainsKey(weaponType) == true)
            {
                special = GetFadePercent(paintseed, _constData.Fades[weaponType]) + "%";
            }
            else if (pattern == "Amber Fade" && _constData.AmberFades?.ContainsKey(weaponType) == true)
            {
                special = GetFadePercent(paintseed, _constData.AmberFades[weaponType]) + "%";
            }
            else if ((pattern == "Doppler" || pattern == "Gamma Doppler") && _constData.Doppler?.ContainsKey(paintindex.ToString()) == true)
            {
                special = _constData.Doppler[paintindex.ToString()];
            }
            else if (pattern == "Crimson Kimono" && _constData.Kimonos?.ContainsKey(paintseed.ToString()) == true)
            {
                special = _constData.Kimonos[paintseed.ToString()];
            }

            return new ItemInformation
            {
                Name = pattern,
                Type = weaponType,
                Special = special,
                WearName = wearName,
                RarityName = GetRarityFromNumber(item.rarity),
                QualityName = GetQualityFromNumber(item.quality),
                OriginName = GetOriginFromNumber(item.origin),
                PaintWear = paintWear,
                IsKnifeOrGlove = isKnifeOrGlove,
                IsSouvenir = isSouvenir,
                MarketHashName = GetMarketHashName(weaponType, pattern, wearName, isKnifeOrGlove, isSouvenir, item.ShouldSerializekilleatervalue())
            };
        }

        private string GetMarketHashName(string weaponType, string pattern, string wearName, bool isKnifeOrGlove, bool isSouvenir, bool isStatTrak)
        {
            var marketHashName = "";
            if (isKnifeOrGlove) marketHashName += GetQualityFromNumber(3) + " "; // ★
            if (isSouvenir) marketHashName += GetQualityFromNumber(12) + " "; // Souvenir
            else if (isStatTrak) marketHashName += GetQualityFromNumber(9) + " "; // StatTrak™

            marketHashName += weaponType;

            if (pattern != GetPatternName(0)) // Vanilla
            {
                marketHashName += $" | {pattern} ({wearName})";
            }
            return marketHashName;
        }

        private double GetFadePercent(int paintseed, bool reversed)
        {
            const int minimumFadePercent = 80;
            // paintseed comes from the (attacker-controllable) item cert; a value outside the
            // pattern table would otherwise throw IndexOutOfRange.
            if (paintseed < 0 || paintseed >= _constData.FadeOrder!.Length)
            {
                return 0;
            }
            var fadeIndex = _constData.FadeOrder[paintseed];
            if (reversed)
            {
                fadeIndex = 1000 - fadeIndex;
            }
            var actualFadePercent = (double)fadeIndex / 1001;
            var scaledFadePercent = Math.Round(minimumFadePercent + actualFadePercent * (100 - minimumFadePercent), 1);
            return scaledFadePercent;
        }

        private string GetWeaponName(uint defIndex, bool warnIfMissing = true)
        {
            if (_constData.Items?.TryGetValue(defIndex.ToString(), out var weapon) == true)
            {
                return weapon;
            }

            if (warnIfMissing)
            {
                Console.WriteLine($"Item {defIndex} is missing from constants");
            }
            return "";
        }

        private string GetPatternName(uint paintIndex)
        {
            if (_constData.Skins?.TryGetValue(paintIndex.ToString(), out var pattern) == true)
            {
                return pattern;
            }

            Console.WriteLine($"Skin {paintIndex} is missing from constants");
            return "";
        }

        private static double GetPaintWear(uint paintWear)
        {
            return (double)BitConverter.UInt32BitsToSingle(paintWear);
        }

        private static string GetWearFromFloat(double paintWear)
        {
            if (paintWear < 0.07) return "Factory New";
            if (paintWear < 0.15) return "Minimal Wear";
            if (paintWear < 0.38) return "Field-Tested";
            if (paintWear < 0.45) return "Well-Worn";
            return "Battle-Scarred";
        }

        private string GetRarityFromNumber(uint rarity)
        {
            if (_constData.Rarities != null && rarity < _constData.Rarities.Count)
            {
                return _constData.Rarities[(int)rarity];
            }
            return "Unknown";
        }

        private string GetOriginFromNumber(uint origin)
        {
             if (_constData.Origins?.TryGetValue(origin.ToString(), out var originName) == true)
             {
                 return originName;
             }
             return "Unknown";
        }

        private static bool IsSouvenir(uint quality)
        {
            return quality == 12;
        }

        private string GetQualityFromNumber(uint quality)
        {
             if (_constData.Qualities?.TryGetValue(quality.ToString(), out var qualityName) == true)
             {
                 return qualityName;
             }
             return "Unknown";
        }

        private static bool IsKnifeOrGlove(uint defindex)
        {
            // Knives typically have defindex 500-600
            // Gloves typically have defindex 5000+
            return (defindex >= 500 && defindex < 600) || defindex >= 5000;
        }
    }
}

namespace CSGOSkinAPI.Models
{
    public class SteamAccount
    {
        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("password")]
        public string Password { get; set; } = string.Empty;
    }

    public class ItemInformation
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Special { get; set; } = string.Empty;
        public string WearName { get; set; } = string.Empty;
        public string RarityName { get; set; } = string.Empty;
        public string QualityName { get; set; } = string.Empty;
        public string OriginName { get; set; } = string.Empty;
        public double PaintWear { get; set; }
        public bool IsKnifeOrGlove { get; set; }
        public bool IsSouvenir { get; set; }
        public string MarketHashName { get; set; } = string.Empty;
    }

    public class ConstData
    {
        public Dictionary<string, string>? Items { get; set; }
        public Dictionary<string, string>? Skins { get; set; }
        public Dictionary<string, bool>? Fades { get; set; }
        public Dictionary<string, bool>? AmberFades { get; set; }
        [JsonPropertyName("fade_order")]
        public int[]? FadeOrder { get; set; }
        public string[]? Fireice { get; set; }
        [JsonPropertyName("fireice_order")]
        public int[]? FireiceOrder { get; set; }
        public Dictionary<string, string>? Doppler { get; set; }
        public Dictionary<string, string>? Kimonos { get; set; }
        public List<string>? Rarities { get; set; }
        public Dictionary<string, string>? Qualities { get; set; }
        public Dictionary<string, string>? Origins { get; set; }

        public static readonly string[] FireIceNames = ["", "1st Max", "2nd Max", "3rd Max", "4th Max", "5th Max", "6th Max", "7th Max", "8th Max", "9th Max", "10th Max", "FFI"];
    }

    // stickers.json: two id -> {name, image} maps the server uses to resolve applied
    // stickers and charms. Generated by scripts/update_skin_data.py from the game files.
    public class StickerCatalog
    {
        public Dictionary<string, StickerKit>? Stickers { get; set; }
        public Dictionary<string, StickerKit>? Keychains { get; set; }
    }

    public class StickerKit
    {
        public string Name { get; set; } = string.Empty;
        public string Image { get; set; } = string.Empty;
    }

    public class SteamInventoryResponse
    {
        [JsonPropertyName("assets")]
        public List<SteamAsset>? assets { get; set; }
        
        [JsonPropertyName("descriptions")]
        public List<SteamDescription>? descriptions { get; set; }
        
        [JsonPropertyName("asset_properties")]
        public List<SteamAssetProperties>? asset_properties { get; set; }

        [JsonPropertyName("total_inventory_count")]
        public int total { get; set; }

        [JsonPropertyName("success")]
        public int success { get; set; }
    }

    // Per-asset properties; see GetInventoryData for how %propid:N% inspect-link
    // placeholders resolve against these. Steam sends every value as a string under
    // int_value/float_value/string_value depending on the property's type.
    public class SteamAssetProperties
    {
        [JsonPropertyName("assetid")]
        public string assetid { get; set; } = string.Empty;

        [JsonPropertyName("asset_properties")]
        public List<SteamAssetProperty>? asset_properties { get; set; }
    }

    public class SteamAssetProperty
    {
        [JsonPropertyName("propertyid")]
        public int propertyid { get; set; }

        [JsonPropertyName("int_value")]
        public string? int_value { get; set; }

        [JsonPropertyName("float_value")]
        public string? float_value { get; set; }

        [JsonPropertyName("string_value")]
        public string? string_value { get; set; }
    }

    public class SteamAsset
    {
        [JsonPropertyName("appid")]
        public int appid { get; set; }
        
        [JsonPropertyName("contextid")]
        public string? contextid { get; set; }
        
        [JsonPropertyName("assetid")]
        public string assetid { get; set; } = string.Empty;
        
        [JsonPropertyName("classid")]
        public string classid { get; set; } = string.Empty;
        
        [JsonPropertyName("instanceid")]
        public string instanceid { get; set; } = string.Empty;
        
        [JsonPropertyName("amount")]
        public string? amount { get; set; }
    }

    public class SteamDescription
    {
        [JsonPropertyName("appid")]
        public int appid { get; set; }
        
        [JsonPropertyName("classid")]
        public string classid { get; set; } = string.Empty;
        
        [JsonPropertyName("instanceid")]
        public string instanceid { get; set; } = string.Empty;
        
        [JsonPropertyName("name")]
        public string? name { get; set; }
        
        [JsonPropertyName("market_name")]
        public string? market_name { get; set; }
        
        [JsonPropertyName("market_hash_name")]
        public string? market_hash_name { get; set; }
        
        [JsonPropertyName("name_color")]
        public string? name_color { get; set; }
        
        [JsonPropertyName("background_color")]
        public string? background_color { get; set; }
        
        [JsonPropertyName("icon_url")]
        public string? icon_url { get; set; }
        
        [JsonPropertyName("icon_url_large")]
        public string? icon_url_large { get; set; }
        
        [JsonPropertyName("type")]
        public string? type { get; set; }
        
        [JsonPropertyName("tradable")]
        public int tradable { get; set; }
        
        [JsonPropertyName("marketable")]
        public int marketable { get; set; }
        
        [JsonPropertyName("commodity")]
        public int commodity { get; set; }
        
        [JsonPropertyName("market_tradable_restriction")]
        public int market_tradable_restriction { get; set; }
        
        [JsonPropertyName("actions")]
        public List<SteamAction>? actions { get; set; }

        [JsonPropertyName("descriptions")]
        public List<SteamItemDescription>? descriptions { get; set; }

        [JsonPropertyName("tags")]
        public List<SteamTag>? tags { get; set; }
    }

    public class SteamAction
    {
        [JsonPropertyName("link")]
        public string? link { get; set; }
        
        [JsonPropertyName("name")]
        public string? name { get; set; }
    }

    public class SteamItemDescription
    {
        [JsonPropertyName("type")]
        public string? type { get; set; }

        [JsonPropertyName("name")]
        public string? name { get; set; }

        [JsonPropertyName("value")]
        public string? value { get; set; }
    }

    public class SteamTag
    {
        [JsonPropertyName("category")]
        public string? category { get; set; }
        
        [JsonPropertyName("internal_name")]
        public string? internal_name { get; set; }
        
        [JsonPropertyName("localized_category_name")]
        public string? localized_category_name { get; set; }
        
        [JsonPropertyName("localized_tag_name")]
        public string? localized_tag_name { get; set; }
    }
}
