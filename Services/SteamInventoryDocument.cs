namespace CSGOSkinAPI.Services
{
    // One inspectable copy of an item: the asset (per-copy identity), the description it resolved
    // to (shared by every copy of that class/instance), and the inspect link built for this copy.
    //
    // A struct because it is yielded once per asset - up to 2000 per inventory - and never stored;
    // a class here would be 2000 short-lived allocations per fetch for no benefit.
    internal readonly record struct InspectableAsset(
        SteamAsset Asset, SteamDescription Description, string InspectLink);

    // A fetched CS2 inventory page, indexed for O(1) per-asset lookups.
    //
    // The Steam Community inventory response is split across three parallel arrays that have to be
    // stitched together to build a usable inspect link per item:
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
    // Two callers need the same five steps out of steamcommunity.com's inventory endpoint - build
    // the URL, fetch it, deserialize it, index the two per-asset side tables, and walk the assets
    // that carry an inspect link - and they used to do all five separately. They diverged, as
    // duplicated code does: the endpoint's copy indexes descriptions by (classid, instanceid) while
    // the warmer's copy kept a per-asset FirstOrDefault over the descriptions array, which is
    // O(assets x descriptions) - 2000x2000 on a maxed inventory. One copy got the fix; the other
    // never did. There is one copy now, so there is one place for that to be true.
    //
    // What is deliberately *not* here is everything the two callers do differently. The endpoint
    // builds a DTO per asset (tags, StatTrak kills, prices, a batched item-cache read) and caches
    // the response; the warmer only persists locally-decodable certs and records the warm. They
    // also parse the inspect link under different logger categories. Those are different
    // operations that happen to start from the same bytes, so this type stops at the bytes.
    internal sealed class SteamInventoryDocument
    {
        // Steam's own cap for one page of an inventory. Both callers fetch a single page, so both
        // see the same truncation, and both report it.
        private const int PageSize = 2000;

        // Matches the timeout both callers set on their own client before this was shared.
        private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);

        private readonly SteamInventoryResponse _response;

        // assetid -> that asset's properties, so resolving a %propid:N% placeholder is a lookup
        // rather than a scan. (See BuildInspectLink for what the placeholders mean.)
        private readonly Dictionary<string, List<SteamAssetProperty>> _propertiesByAsset;

        // (classid, instanceid) -> description. The pair, not classid alone: Steam mints a new
        // instanceid under the same class whenever a copy's description differs (a name tag,
        // applied stickers, a StatTrak score line), so keying on classid alone silently hands one
        // copy another copy's name, tags, price and - worst - another copy's inspect-link template.
        private readonly Dictionary<(string classid, string instanceid), SteamDescription> _descriptionByClassInstance;

        private SteamInventoryDocument(SteamInventoryResponse response,
            List<SteamAsset> assets, List<SteamDescription> descriptions)
        {
            _response = response;
            Assets = assets;

            _propertiesByAsset = response.asset_properties?
                .ToDictionary(ap => ap.assetid, ap => ap.asset_properties ?? [])
                ?? [];

            _descriptionByClassInstance = new Dictionary<(string, string), SteamDescription>(descriptions.Count);
            foreach (var description in descriptions)
            {
                // TryAdd, not Add: Steam has been seen to repeat a (classid, instanceid) pair, and
                // first-wins matches what a scan for the first match would have picked.
                _descriptionByClassInstance.TryAdd((description.classid, description.instanceid), description);
            }
        }

        // The single count=2000 page both callers ask for. One format string, so the two can no
        // longer drift in language, page size or context id.
        internal static string BuildUrl(string ownerSteamId) =>
            $"https://steamcommunity.com/inventory/{ownerSteamId}/730/2?l=english&count={PageSize}";

        // Fetches that page over the pooled "steam" client.
        //
        // The client wrapper is disposed here while the response is handed back, which is safe and
        // deliberate: IHttpClientFactory hands out a client over a lifetime-tracking handler whose
        // Dispose does not touch the pooled connection, and the default
        // HttpCompletionOption.ResponseContentRead has already buffered the whole body into memory
        // by the time GetAsync returns. The caller owns the response and reads status, headers and
        // content from it exactly as before - which is the point, because the two callers do
        // completely different things with a non-success status.
        internal static async Task<HttpResponseMessage> FetchAsync(IHttpClientFactory httpClientFactory,
            string ownerSteamId, CancellationToken cancellationToken = default)
        {
            using var httpClient = httpClientFactory.CreateClient("steam");
            httpClient.Timeout = FetchTimeout;
            return await httpClient.GetAsync(BuildUrl(ownerSteamId), cancellationToken);
        }

        // Deserializes a fetched body into an indexed document, or null when the body is not an
        // inventory we can use: Steam answers a private profile with a bare `null`, and a response
        // missing either array has nothing to stitch. Malformed JSON throws JsonException, which
        // each caller already handles its own way (the endpoint turns it into a 400, the warmer
        // lets its drain loop log it), so it is deliberately not swallowed here.
        internal static SteamInventoryDocument? TryParse(string json)
        {
            var response = JsonSerializer.Deserialize<SteamInventoryResponse>(json);
            if (response?.assets == null || response.descriptions == null)
            {
                return null;
            }
            return new SteamInventoryDocument(response, response.assets, response.descriptions);
        }

        // The assets on this page, in Steam's order.
        internal List<SteamAsset> Assets { get; }

        // Steam's count of the whole inventory, which is not the same as what this page carries.
        internal int Total => _response.total;

        // True when the inventory is bigger than the one page we fetched. Both callers report it;
        // neither paginates.
        internal bool Truncated => _response.total > Assets.Count;

        // The description shared by every copy with this (classid, instanceid), or null if the page
        // did not carry one - which happens, and means the asset cannot be rendered or inspected.
        internal SteamDescription? FindDescription(string classid, string instanceid) =>
            _descriptionByClassInstance.TryGetValue((classid, instanceid), out var description)
                ? description
                : null;

        // Walks the assets that can actually be inspected, pairing each with its description and
        // the inspect link built for that specific copy.
        //
        // An asset is skipped when it has no description on this page, when that description
        // carries no actions, or when none of its actions is an inspect action - Steam sends other
        // actions (a market listing link, for one) and the inspect one is not necessarily first.
        //
        // Exactly one dictionary lookup per asset, so the walk is O(assets) rather than
        // O(assets x descriptions).
        internal IEnumerable<InspectableAsset> InspectableAssets(string ownerSteamId)
        {
            foreach (var asset in Assets)
            {
                if (!_descriptionByClassInstance.TryGetValue((asset.classid, asset.instanceid), out var description)
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

                _propertiesByAsset.TryGetValue(asset.assetid, out var assetProps);
                var inspectLink = Controllers.SkinController.BuildInspectLink(
                    inspectAction.link, assetProps, ownerSteamId, asset.assetid);
                yield return new InspectableAsset(asset, description, inspectLink);
            }
        }
    }
}
