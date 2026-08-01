namespace CSGOSkinAPI.Services
{
    // The JSON an item is reported as. These build the anonymous objects the /api and
    // /api/inventory responses are serialized from - the field names here ARE the contract the
    // browser reads, which is why the unit tests assert on the serialized JSON rather than on
    // these types.
    //
    // Static, with the catalog and price services passed in rather than held, because building a
    // response needs no controller state: it is a projection of a decoded item plus the two
    // lookups (decal names/images, Skinport prices) that fill it out.
    internal static class ItemResponse
    {
        internal static object CreateResponse(CEconItemPreviewDataBlock item, ConstDataService constDataService, PriceService priceService, ulong s, ulong a, ulong d, ulong m)
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
        internal static object? BuildPrice(PriceService priceService, string marketHashName)
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
}
