using Microsoft.AspNetCore.Mvc.Filters;

namespace CSGOSkinAPI.Controllers
{
    [ApiController]
    [Route("api")]
    [EnableRateLimiting("api")]
    [InvalidModelStateAsError]
    public partial class SkinController(SteamService steamService, DatabaseService dbService, ConstDataService constDataService, IHttpClientFactory httpClientFactory, InventoryWarmService warmService, IMemoryCache cache, PriceService priceService, ILoggerFactory loggerFactory) : ControllerBase
    {
        // Everything this controller reports about its own work.
        private readonly ILogger _logger = loggerFactory.CreateLogger<SkinController>();

        // Malformed inspect links, which are a different kind of event and belong on a
        // different knob: they are not a fault of ours, the caller already has their answer
        // (a 400), their volume is set by whoever is calling rather than by anything the app
        // is doing, and the line carries up to MaxLoggedLength characters of the caller's
        // choosing. Sharing a category with the app's own diagnostics would mean muting a
        // flood of them costs you the diagnostics too. CSGOSkinAPI.RateLimiting exists for
        // exactly the same reason. Both are documented in appsettings.json.
        internal const string InspectLinkLogCategory = "CSGOSkinAPI.InspectLinks";
        private readonly ILogger _inspectLogger = loggerFactory.CreateLogger(InspectLinkLogCategory);

        // SteamID64 of the first individual account; anything below is not a profile id.
        private const ulong MinSteamId64 = 76561197960265729;

        // How long a fetched /api/inventory response stays served from memory. Short by design:
        // long enough to absorb a reload storm and repeat viewers, short enough that a user who
        // just traded sees the change within a few minutes. Paired with the byte-bounded
        // MemoryCache registered in Program.cs.
        private static readonly TimeSpan InventoryCacheTtl = TimeSpan.FromMinutes(5);

        // Brief negative caching so a reload storm during a Steam throttle (or against a private
        // profile) doesn't re-hit steamcommunity.com on every request and extend the IP ban.
        private static readonly TimeSpan NegativeInventoryCacheTtl = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan RateLimitedInventoryCacheTtl = TimeSpan.FromSeconds(60);

        // Single-flight per resolved SteamId64: the first viewer of an uncached inventory does the
        // fetch while any concurrent viewers wait on this gate and then read the freshly-cached
        // result, instead of all K stampeding steamcommunity.com at once. Keyed by the "inv:{id}"
        // cache key; a gate is dropped once released and idle, so the dictionary stays bounded.
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> InventoryFetchGates = new();

        // A cached inventory failure. Positive results are cached as the raw response byte[].
        private sealed record NegativeInventory(int StatusCode, string Error);

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
            // Unhandled exceptions bubble to the global handler in Program.cs (generic 500).
            if (!string.IsNullOrEmpty(url))
            {
                // ParseInspectUrl has already logged the failure, with the URL as a field and
                // under the inspect-link category; a second line here would say strictly less
                // about the same request.
                var parsed = ParseInspectUrl(url, _inspectLogger);
                if (parsed == null)
                {
                    return BadRequest(new { error = "Invalid inspect URL format" });
                }

                (s, a, d, m, var directItem) = parsed.Value;

                if (directItem != null)
                {
                    return Ok(CreateResponse(directItem, constDataService, priceService, s, a, d, m));
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
                return Ok(CreateResponse(existingItem, constDataService, priceService, s, a, d, m));
            }

            // A classic S-form link that missed the cache still goes through the GC below,
            // but it also tells us whose inventory the wild link points into. Queue a
            // background warm of that whole inventory (cert decode, no GC traffic) so
            // follow-up lookups of the owner's other items become DB hits. M-form market
            // links carry no owner id, so they can't be warmed.
            if (s >= MinSteamId64)
            {
                _logger.LogDebug(
                    "Cache miss for item {ItemId}; queueing inventory warm for owner {SteamId}", a, s);
                warmService.Enqueue(s);
            }

            // An itemid (a) of 0 - or a request naming neither an owner (s) nor a market listing (m)
            // - can't identify an item to the GC. Worse, a==0 pollutes SteamService's pending-request
            // map (keyed by itemid), where any unrelated null-iteminfo response resolves key 0.
            // Reject before touching the GC.
            if (a == 0 || (s == 0 && m == 0))
            {
                return BadRequest(new { error = "Invalid inspect parameters" });
            }

            var itemInfo = await steamService.GetItemInfoAsync(s, a, d, m);
            if (itemInfo == null)
            {
                _logger.LogWarning("Steam GC returned no item for itemid {ItemId}", a);
                return NotFound(new { error = "Steam GC did not return an item" });
            }

            await dbService.SaveItemWithExtrasAsync(itemInfo);
            return Ok(CreateResponse(itemInfo, constDataService, priceService, s, a, d, m));
        }

        // `steamid` is deliberately nullable. A non-nullable string parameter on an [ApiController]
        // is implicitly [Required], so MVC would reject a missing/blank value with an RFC-9110
        // ProblemDetails body *before* this action runs - the one error on this endpoint with no
        // `error` field, and one the guard below could never reach. Nullable hands the decision back
        // here; IsNullOrWhiteSpace (not IsNullOrEmpty) keeps rejecting the all-whitespace value that
        // the implicit RequiredAttribute used to catch, so "   " never reaches a Steam lookup.
        [HttpGet("inventory")]
        public async Task<IActionResult> GetInventoryData([FromQuery] string? steamid)
        {
            SemaphoreSlim? gate = null;
            string? gateKey = null;
            var acquired = false;
            try
            {
                if (string.IsNullOrWhiteSpace(steamid))
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

                // Serve a recent copy (or a recent failure) without touching Steam. Keyed by resolved
                // SteamId64 so a vanity URL and the raw id (which resolve to the same account) share
                // one entry.
                var cacheKey = $"inv:{steamId}";
                var cached = InventoryFromCache(cacheKey);
                if (cached != null)
                {
                    return cached;
                }

                // Single-flight the fetch so K concurrent first-viewers don't all stampede Steam.
                gate = InventoryFetchGates.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
                gateKey = cacheKey;
                await gate.WaitAsync();
                acquired = true;

                // Re-check now we hold the gate: a concurrent first-viewer may have just populated it.
                cached = InventoryFromCache(cacheKey);
                if (cached != null)
                {
                    return cached;
                }

                using var httpClient = httpClientFactory.CreateClient("steam");
                httpClient.Timeout = TimeSpan.FromSeconds(10);
                
                var inventoryUrl = $"https://steamcommunity.com/inventory/{steamid}/730/2?l=english&count=2000";
                _logger.LogDebug("Fetching inventory for {SteamId} from {InventoryUrl}", steamId, inventoryUrl);
                
                var response = await httpClient.GetAsync(inventoryUrl);
                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        // Steam is throttling this server's IP for the inventory endpoint. Surface it
                        // loudly in the logs (with Retry-After when present) so the throttle is visible.
                        var retryAfter = response.Headers.RetryAfter?.Delta;
                        if (retryAfter is TimeSpan retryDelay)
                        {
                            _logger.LogWarning(
                                "Steam inventory rate limited (429) for {SteamId}; Retry-After {RetryAfterSeconds:0}s",
                                steamId, retryDelay.TotalSeconds);
                        }
                        else
                        {
                            _logger.LogWarning(
                                "Steam inventory rate limited (429) for {SteamId}", steamId);
                        }
                        return CacheInventoryFailure(cacheKey, StatusCodes.Status429TooManyRequests,
                            "Steam is rate limiting inventory requests right now. Please try again in a minute.",
                            RateLimitedInventoryCacheTtl);
                    }
                    if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                    {
                        return CacheInventoryFailure(cacheKey, StatusCodes.Status400BadRequest,
                            "Inventory is private or user does not exist", NegativeInventoryCacheTtl);
                    }
                    _logger.LogWarning(
                        "Steam inventory fetch for {SteamId} failed: {StatusCode} {StatusName}",
                        steamId, (int)response.StatusCode, response.StatusCode);
                    return CacheInventoryFailure(cacheKey, StatusCodes.Status400BadRequest,
                        $"Failed to fetch inventory: {response.StatusCode}", NegativeInventoryCacheTtl);
                }

                var jsonContent = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrEmpty(jsonContent))
                {
                    return CacheInventoryFailure(cacheKey, StatusCodes.Status400BadRequest,
                        "Empty response from Steam API", NegativeInventoryCacheTtl);
                }

                var inventoryData = JsonSerializer.Deserialize<SteamInventoryResponse>(jsonContent);
                if (inventoryData?.assets == null || inventoryData.descriptions == null)
                {
                    return CacheInventoryFailure(cacheKey, StatusCodes.Status400BadRequest,
                        "Invalid inventory data or inventory is empty", NegativeInventoryCacheTtl);
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

                // Index descriptions by (classid, instanceid) once. The inspect link, tags, name and
                // price all live on the shared description, and a per-asset FirstOrDefault over the
                // descriptions array is O(assets x descriptions) - 2000x2000 on a maxed inventory.
                var descriptionByClassInstance = new Dictionary<(string classid, string instanceid), SteamDescription>();
                foreach (var d in inventoryData.descriptions)
                {
                    descriptionByClassInstance.TryAdd((d.classid, d.instanceid), d);
                }

                // First pass: resolve each inspectable asset's link and parsed identity, and collect
                // the itemids that need a cache lookup (everything that didn't decode locally from a
                // cert) so they can be fetched in one batch instead of one connection per asset.
                var prepared = new List<(SteamAsset asset, SteamDescription description, string inspectLink,
                    (ulong s, ulong a, ulong d, ulong m, CEconItemPreviewDataBlock? directItem)? parsed)>();
                var idsToLookUp = new List<ulong>();
                foreach (var asset in inventoryData.assets)
                {
                    if (!descriptionByClassInstance.TryGetValue((asset.classid, asset.instanceid), out var description)
                        || description.actions == null)
                    {
                        continue;
                    }

                    var inspectAction = description.actions.FirstOrDefault(a =>
                        a.link?.Contains("csgo_econ_action_preview") == true);
                    if (inspectAction?.link == null)
                    {
                        continue;
                    }

                    // Fill the template placeholders described above.
                    propsByAsset.TryGetValue(asset.assetid, out var assetProps);
                    var inspectLink = BuildInspectLink(inspectAction.link, assetProps, steamid, asset.assetid);

                    var parsed = ParseInspectUrl(inspectLink, _inspectLogger);
                    if (parsed.HasValue && parsed.Value.directItem == null)
                    {
                        idsToLookUp.Add(parsed.Value.a);
                    }
                    prepared.Add((asset, description, inspectLink, parsed));
                }

                // One batched cache read for every non-cert item, over a single connection.
                var cachedItems = await dbService.GetItemsAsync(idsToLookUp);

                var csgoItems = new List<object>();
                foreach (var (asset, description, inspectLink, parsed) in prepared)
                {
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

                    // Attach decoded data: a cert decodes locally, otherwise fall back to the batched
                    // cache hit (absent if we've never seen this itemid).
                    object? existingItemData = null;
                    if (parsed.HasValue)
                    {
                        var (s, a, d, m, directItem) = parsed.Value;
                        if (directItem != null)
                        {
                            existingItemData = CreateResponse(directItem, constDataService, priceService, s, a, d, m);
                        }
                        else if (cachedItems.TryGetValue(a, out var existingItem))
                        {
                            existingItemData = CreateResponse(existingItem, constDataService, priceService, s, a, d, m);
                        }
                    }

                    csgoItems.Add(new
                    {
                        name = description.name ?? description.market_name ?? "Unknown Item",
                        market_name = description.market_name,
                        // Base price keyed on Steam's own market_hash_name (authoritative,
                        // language-independent), so every item is priced even when it has no
                        // decoded existing_data yet.
                        price = BuildPrice(priceService, description.market_hash_name ?? ""),
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

                // Profile info (avatar, persona, trade-ban) is fetched separately by the browser
                // via /api/profile so item rendering never waits on Steam's profile feed.
                //
                // We fetch a single count=2000 page, so an inventory larger than that comes back
                // capped while `total` still reports the full count. Flag that so the UI doesn't
                // present the capped view as complete. (See L2 in the audit; full pagination is the
                // fuller fix.)
                var truncated = inventoryData.total > inventoryData.assets.Count;
                var result = new
                {
                    total = inventoryData.total,
                    truncated,
                    success = 1,
                    steamid = steamId.ToString(),
                    csgo_items = csgoItems
                };

                _logger.LogDebug(
                    "Successfully parsed {ParsedCount} CS2 items from {TotalCount} total items",
                    csgoItems.Count, inventoryData.total);

                // Cache the exact bytes we return, keyed by resolved SteamId64. Size is the byte
                // length so the MemoryCache's byte SizeLimit bounds total memory; expires after
                // InventoryCacheTtl so idle entries free themselves.
                var payload = JsonSerializer.SerializeToUtf8Bytes(result);
                cache.Set(cacheKey, payload, new MemoryCacheEntryOptions
                {
                    Size = payload.Length,
                    AbsoluteExpirationRelativeToNow = InventoryCacheTtl,
                });
                return File(payload, "application/json");
            }
            catch (TaskCanceledException)
            {
                return BadRequest(new { error = "Request timed out while fetching inventory" });
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "HTTP error fetching inventory");
                return BadRequest(new { error = "Failed to connect to Steam API" });
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "JSON parsing error reading the Steam inventory response");
                return BadRequest(new { error = "Invalid response from Steam API" });
            }
            // Anything else bubbles to the global handler in Program.cs (generic 500). Note the
            // network-failure catches above intentionally don't negative-cache: a transient blip
            // should let the next request retry, not be pinned as an error for 30s.
            finally
            {
                if (acquired && gate != null)
                {
                    gate.Release();
                    // Drop the gate once it's fully released and idle, to bound the dictionary. The
                    // key-and-value TryRemove overload only removes if it's still this same gate.
                    if (gate.CurrentCount == 1 && gateKey != null)
                    {
                        InventoryFetchGates.TryRemove(new KeyValuePair<string, SemaphoreSlim>(gateKey, gate));
                    }
                }
            }
        }

        // Returns a cached inventory response - the raw bytes (positive) or a stored failure
        // (negative) - or null when nothing is cached for this key.
        private IActionResult? InventoryFromCache(string cacheKey)
        {
            if (cache.TryGetValue(cacheKey, out object? entry))
            {
                if (entry is byte[] bytes) return File(bytes, "application/json");
                if (entry is NegativeInventory neg) return StatusCode(neg.StatusCode, new { error = neg.Error });
            }
            return null;
        }

        // Caches an inventory failure briefly and returns it, so a reload storm during a throttle
        // isn't re-fetched from Steam on every request. Size is a small constant (the MemoryCache is
        // byte-bounded); the entry expires after ttl.
        private IActionResult CacheInventoryFailure(string cacheKey, int statusCode, string error, TimeSpan ttl)
        {
            cache.Set(cacheKey, new NegativeInventory(statusCode, error), new MemoryCacheEntryOptions
            {
                Size = 64,
                AbsoluteExpirationRelativeToNow = ttl,
            });
            return StatusCode(statusCode, new { error });
        }

        // Nullable, and whitespace-rejecting, for the same reason as GetInventoryData above.
        [HttpGet("profile")]
        public async Task<IActionResult> GetProfile([FromQuery] string? steamid)
        {
            if (string.IsNullOrWhiteSpace(steamid))
            {
                return BadRequest(new { error = "Steam ID is required" });
            }

            var xmlUrl = GetProfileXmlUrl(steamid);
            if (xmlUrl == null)
            {
                return BadRequest(new { error = "Unable to determine profile for the given Steam ID" });
            }

            using var httpClient = httpClientFactory.CreateClient("steam");
            httpClient.Timeout = TimeSpan.FromSeconds(5);

            // Fetching and reading the feed is the only part that can fail at the transport level.
            // Handled the same way GetInventoryData handles it - a connection reset, a timeout, or a
            // response over the client's MaxResponseContentBufferSize (see Program.cs) is an upstream
            // failure, not a bug in this server, so it answers 400 in the house error shape rather
            // than bubbling to the global handler's generic 500. Anything else still bubbles.
            string xml;
            try
            {
                var response = await httpClient.GetAsync(xmlUrl);
                if (!response.IsSuccessStatusCode)
                {
                    return BadRequest(new { error = $"Failed to fetch profile: {response.StatusCode}" });
                }

                xml = await response.Content.ReadAsStringAsync();
            }
            catch (TaskCanceledException)
            {
                return BadRequest(new { error = "Request timed out while fetching profile" });
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "HTTP error fetching profile");
                return BadRequest(new { error = "Failed to connect to Steam API" });
            }

            var profile = ParseProfileXml(xml);
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
                since_year = profile.SinceYear,
                // Prefer the vanity URL (/id/<vanity>) when the profile exposes one; Steam omits
                // customURL for some profiles, so fall back to the /profiles/<id64> form.
                profile_url = string.IsNullOrEmpty(profile.CustomUrl)
                    ? $"https://steamcommunity.com/profiles/{profile.SteamId}"
                    : $"https://steamcommunity.com/id/{profile.CustomUrl}"
            });
        }

        // Longest prefix of an untrusted value we will put in a log line.
        private const int MaxLoggedLength = 200;

        // Renders an untrusted request value for the log. Applied to the two values a caller
        // controls outright: the ?url= inspect link and the vanity name.
        //
        // Since these travel as *parameters* of a structured message template rather than as text
        // spliced into it, a CR/LF can no longer forge a record in a structured sink - the value is
        // one field, whatever is in it. What it can still do is forge a *line*: the default console
        // formatter renders the template back into a single line of text, and this app's log is
        // read through `docker compose logs`, so an embedded CR/LF there still produces output that
        // looks like separate entries. Control characters therefore still become '?'.
        //
        // Truncation is independent of all that and outlives any sink change: ?url= is unbounded,
        // and a request-sized value has no business being copied into a log record at all.
        internal static string ForLog(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "(empty)";
            }

            var clipped = value.Length > MaxLoggedLength
                ? string.Concat(value.AsSpan(0, MaxLoggedLength), "...(truncated)")
                : value;
            return new string([.. clipped.Select(c => char.IsControl(c) ? '?' : c)]);
        }

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
                    _logger.LogWarning("Steam profile request failed: {StatusCode}", response.StatusCode);
                    return null;
                }

                var xmlContent = await response.Content.ReadAsStringAsync();
                var match = Regex.Match(xmlContent, @"<steamID64>(\d+)</steamID64>");
                if (match.Success && ulong.TryParse(match.Groups[1].Value, out var steamId))
                {
                    return steamId;
                }

                _logger.LogWarning(
                    "Failed to resolve custom URL '{Vanity}' to SteamId64", ForLog(customUrl));
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error resolving custom URL '{Vanity}'", ForLog(customUrl));
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
            // Year the account was created, parsed from <memberSince> (e.g. "July 12, 2015" -> 2015).
            // Null when the profile feed omits the element or it can't be parsed.
            public int? SinceYear { get; init; }
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

        // Static because it is shared with InventoryWarmService (and driven directly by tests),
        // so the logger arrives as an argument rather than through a field. Optional, and null-sunk
        // when absent, so a test that is asserting on the parse result need not supply one.
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
                    logger.LogWarning("Failed to decode URL: {InspectUrl}", ForLog(url));
                    return null;
                }
                var hexValue = hexMatch.Groups[1].Value;
                // Real inspect certs are a few hundred hex chars; cap the length so a crafted
                // multi-megabyte payload can't force a huge allocation and protobuf parse on the
                // request thread.
                if (hexValue.Length > 2048)
                {
                    logger.LogWarning("Hex payload too long: {InspectUrl}", ForLog(url));
                    return null;
                }
                // The regex matches odd-length runs too, which Convert.FromHexString rejects with a
                // FormatException - guard it so a bad link is a 400, not an unhandled 500.
                if (hexValue.Length % 2 != 0)
                {
                    logger.LogWarning("Hex payload has odd length: {InspectUrl}", ForLog(url));
                    return null;
                }
                var rawBytes = Convert.FromHexString(hexValue);
                // Need at least the leading byte, one protobuf byte, and the 4-byte checksum.
                if (rawBytes.Length < 6)
                {
                    logger.LogWarning("Hex payload too short: {InspectUrl}", ForLog(url));
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
                    logger.LogWarning(ex, "Failed to decode inspect cert payload from {InspectUrl}", ForLog(url));
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
                logger.LogWarning("Inspect URL has out-of-range numeric fields: {InspectUrl}", ForLog(url));
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

        private static object CreateResponse(CEconItemPreviewDataBlock item, ConstDataService constDataService, PriceService priceService, ulong s, ulong a, ulong d, ulong m)
        {
            var itemInfo = constDataService.GetItemInformation(item);

            return new
            {
                price = BuildPrice(priceService, itemInfo.MarketHashName),
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
                stattrak_kills = item.StatTrakKills(),
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

        // Skinport base price for a market_hash_name, or null when we have nothing to show. May be
        // approximate (a value that aged out of the feed, or the nearest wear of the same skin) -
        // the client prefixes a "~" then. Cents keep the value exact; the client formats it.
        private static object? BuildPrice(PriceService priceService, string marketHashName)
        {
            var price = priceService.Resolve(marketHashName);
            if (price == null || price.SuggestedCents == null)
            {
                return null;
            }
            return new
            {
                min = price.MinCents,
                suggested = price.SuggestedCents,
                currency = PriceService.Currency,
                source = "skinport",
                approximate = price.Approximate,
            };
        }

        internal static object MakeStickerDto(CEconItemPreviewDataBlock.Sticker s, ConstDataService constData)
        {
            var kit = constData.ResolveSticker(s.sticker_id);
            return new
            {
                s.sticker_id,
                s.wear,
                rotation = s.Rotation(),
                offset_x = s.OffsetX(),
                offset_y = s.OffsetY(),
                name = kit?.Name ?? "",
                image = kit?.Image ?? "",
            };
        }

        // A charm, or a Sticker Slab. A slab is a single-use charm that seals a sticker inside
        // it; the sealed sticker's id rides in proto field 12 (see StickerSlab). When present we
        // display the sealed sticker (the slab container itself isn't in our keychain catalog)
        // and flag it, so the client can mark it as a slab.
        internal static object MakeKeychainDto(CEconItemPreviewDataBlock.Sticker k, ConstDataService constData)
        {
            var wrapped = StickerSlab.GetWrappedStickerId(k);
            var kit = wrapped != 0 ? constData.ResolveSticker(wrapped) : constData.ResolveKeychain(k.sticker_id);
            return new
            {
                k.sticker_id,
                k.wear,
                offset_x = k.OffsetX(),
                offset_y = k.OffsetY(),
                pattern = k.Pattern(),
                name = kit?.Name ?? "",
                image = kit?.Image ?? "",
                slab = wrapped != 0,
                wrapped_sticker = wrapped,
            };
        }
    }

    // Every error this API returns is a status code plus a JSON `{ "error": "..." }` body - except
    // the ones model binding produces, which never reach an action at all. [ApiController] installs
    // its own filter (order -2000) that turns invalid model state into an RFC-9110 ProblemDetails:
    // a different content type and a body with no `error` field, on the very same endpoints. That
    // is what made `/api?a=abc` answer in one shape while `/api?a=0` answered in another.
    //
    // This filter runs ahead of that one and short-circuits with the house shape, so a caller has
    // exactly one error body to parse. The remaining way to trip it on this controller is an
    // unparseable numeric parameter on /api (`s`, `a`, `d`, `m`) - `steamid` and `url` are nullable,
    // so a missing value binds to null and the action's own guard answers.
    internal sealed class InvalidModelStateAsErrorAttribute : ActionFilterAttribute
    {
        // [ApiController]'s ModelStateInvalidFilter sits at -2000; a lower order runs first, and
        // setting a Result there short-circuits the rest of the pipeline.
        private const int BeforeApiControllerModelStateFilter = -2100;

        public InvalidModelStateAsErrorAttribute() => Order = BeforeApiControllerModelStateFilter;

        public override void OnActionExecuting(ActionExecutingContext context)
        {
            if (context.ModelState.IsValid)
            {
                return;
            }

            // Name the parameter so the caller can fix the request. The key comes from our own
            // action signature, but it is checked rather than trusted: nothing that isn't a plain
            // identifier is echoed back into a response body.
            var key = context.ModelState.FirstOrDefault(entry => entry.Value?.Errors.Count > 0).Key;
            var namesAParameter = !string.IsNullOrEmpty(key) && key.Length <= 64
                && key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

            context.Result = new BadRequestObjectResult(new
            {
                error = namesAParameter
                    ? $"Invalid value for parameter '{key}'"
                    : "Invalid request parameters",
            });
        }
    }
}
