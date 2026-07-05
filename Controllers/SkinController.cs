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
            // Unhandled exceptions bubble to the global handler in Program.cs (generic 500).
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
            // Anything else bubbles to the global handler in Program.cs (generic 500).
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

            // Unhandled exceptions bubble to the global handler in Program.cs (generic 500).
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
                since_year = profile.SinceYear,
                // Prefer the vanity URL (/id/<vanity>) when the profile exposes one; Steam omits
                // customURL for some profiles, so fall back to the /profiles/<id64> form.
                profile_url = string.IsNullOrEmpty(profile.CustomUrl)
                    ? $"https://steamcommunity.com/profiles/{profile.SteamId}"
                    : $"https://steamcommunity.com/id/{profile.CustomUrl}"
            });
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
