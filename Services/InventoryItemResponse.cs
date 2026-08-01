namespace CSGOSkinAPI.Services
{
    // The per-item JSON of the /api/inventory response: one object per inspectable copy in a fetched
    // inventory page.
    //
    // This is the projection half of that endpoint, split out from the controller action, which was
    // doing four unrelated jobs in one method - single-flight gating, the Steam fetch, this
    // stitching, and response caching. The three that remain are all about *the request*: who else
    // is asking for the same inventory, what Steam answered, and what we hand back and cache. This
    // one is about *an item*, and needs nothing from the request at all - which is what makes it
    // separable, and why it sits beside ItemResponse, where the /api projection already lives.
    //
    // SteamInventoryDocument stops at the bytes: it fetches, parses, indexes the two per-asset side
    // tables and walks the assets that carry an inspect link. What it deliberately leaves to its two
    // callers - the DTO per asset, the batched item-cache read - is exactly what this type is. The
    // warmer, its other caller, does none of it.
    //
    // Static, with the services passed in rather than held, for the same reason as ItemResponse:
    // building a response needs no state beyond the document and the two catalog/price lookups.
    internal static class InventoryItemResponse
    {
        // Builds the `csgo_items` array for one fetched inventory page, in Steam's asset order.
        //
        // `inspectLogger` is the caller's own inspect-link category rather than a logger of this
        // type's: a malformed link is the *caller's* event to report (the endpoint logs these under
        // CSGOSkinAPI.InspectLinks, the warmer under its own), and the two must stay separable.
        internal static async Task<List<object>> BuildItemsAsync(SteamInventoryDocument inventory,
            string ownerSteamId, DatabaseService dbService, ConstDataService constDataService,
            PriceService priceService, ILogger inspectLogger)
        {
            // First pass: parse each inspectable asset's link and collect the itemids that need
            // a cache lookup (everything that didn't decode locally from a cert) so they can be
            // fetched in one batch instead of one connection per asset.
            //
            // Parsed here, not in the shared walk, because these links are logged under the caller's
            // own inspect-link category; the warmer logs its own parse failures under its own.
            var prepared = new List<(SteamAsset asset, SteamDescription description, string inspectLink,
                (ulong s, ulong a, ulong d, ulong m, CEconItemPreviewDataBlock? directItem)? parsed)>();
            var idsToLookUp = new List<ulong>();
            foreach (var (asset, description, inspectLink) in inventory.InspectableAssets(ownerSteamId))
            {
                var parsed = InspectLink.ParseInspectUrl(inspectLink, inspectLogger);
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
                csgoItems.Add(BuildItem(asset, description, inspectLink, parsed, cachedItems,
                    constDataService, priceService));
            }
            return csgoItems;
        }

        // One inspectable copy, as the browser reads it. The field names here ARE the contract (see
        // ItemResponse), which is why the tests assert on the serialized JSON rather than on a type.
        private static object BuildItem(SteamAsset asset, SteamDescription description, string inspectLink,
            (ulong s, ulong a, ulong d, ulong m, CEconItemPreviewDataBlock? directItem)? parsed,
            Dictionary<ulong, CEconItemPreviewDataBlock> cachedItems,
            ConstDataService constDataService, PriceService priceService)
        {
            // Extract wear, rarity, and item type from tags
            var wearTag = description.tags?.FirstOrDefault(t => t.category == "Exterior");
            var rarityTag = description.tags?.FirstOrDefault(t => t.category == "Rarity");
            var qualityTag = description.tags?.FirstOrDefault(t => t.category == "Quality");
            var typeTag = description.tags?.FirstOrDefault(t => t.category == "Type");

            // Attach decoded data: a cert decodes locally, otherwise fall back to the batched
            // cache hit (absent if we've never seen this itemid).
            object? existingItemData = null;
            if (parsed.HasValue)
            {
                var (s, a, d, m, directItem) = parsed.Value;
                if (directItem != null)
                {
                    existingItemData = ItemResponse.CreateResponse(directItem, constDataService, priceService, s, a, d, m);
                }
                else if (cachedItems.TryGetValue(a, out var existingItem))
                {
                    existingItemData = ItemResponse.CreateResponse(existingItem, constDataService, priceService, s, a, d, m);
                }
            }

            return new
            {
                name = description.name ?? description.market_name ?? "Unknown Item",
                market_name = description.market_name,
                // Base price keyed on Steam's own market_hash_name (authoritative,
                // language-independent), so every item is priced even when it has no
                // decoded existing_data yet.
                price = ItemResponse.BuildPrice(priceService, description.market_hash_name ?? ""),
                type = description.type,
                inspect_link = inspectLink,
                wear = wearTag?.localized_tag_name,
                rarity = rarityTag?.localized_tag_name,
                quality = qualityTag?.localized_tag_name,
                item_type = typeTag?.localized_tag_name,
                stattrak_kills = StatTrakKills(description),
                name_color = description.name_color,
                icon_url = description.icon_url,
                icon_url_large = description.icon_url_large,
                assetid = asset.assetid,
                classid = asset.classid,
                instanceid = asset.instanceid,
                existing_data = existingItemData
            };
        }

        // StatTrak kill count, when Steam exposes it on the StatTrak score line
        // (e.g. "StatTrak™ Confirmed Kills: 1234"). Some copies only carry the
        // generic "This item tracks Confirmed Kills." line, which has no number.
        private static int? StatTrakKills(SteamDescription description)
        {
            var scoreLine = description.descriptions?
                .FirstOrDefault(l => l.name == "stattrak_score")?.value;
            if (scoreLine == null)
            {
                return null;
            }

            var killMatch = Regex.Match(scoreLine, @"Confirmed Kills:\s*([\d,]+)");
            if (killMatch.Success &&
                int.TryParse(killMatch.Groups[1].Value.Replace(",", ""), out var kills))
            {
                return kills;
            }
            return null;
        }
    }
}
