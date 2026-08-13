namespace CSGOSkinAPI.Services
{
    // A base market price for one item, in integer cents of PriceService.Currency. Min = cheapest
    // live Skinport listing (null when nothing is listed); Suggested = Skinport's smoothed reference
    // price. UpdatedAtUtc = when this item was last seen in the feed (so a value that has dropped out
    // of the feed can be aged and flagged approximate).
    public record SkinPrice(int? MinCents, int? SuggestedCents, DateTime UpdatedAtUtc);

    // What one item actually sold for: the median of completed sales in the narrowest Skinport
    // window that had enough of them, plus that window's sale count. UpdatedAtUtc is when we last
    // saw this item selling at all, so an item that stops trading ages from here.
    public record SaleStat(int MedianCents, int Volume, string Window, bool Pooled, DateTime UpdatedAtUtc);

    // Where a resolved price came from. Ordered best to worst, which is the order Resolve tries.
    public enum PriceBasis
    {
        // Median of this exact item's recent completed sales. What we want wherever it exists.
        Sale,
        // Cheapest live listing / Skinport's smoothed reference for this exact item. A listing is
        // only evidence of what someone is *asking*: by construction it has not sold.
        Listing,
        // A sale median for this exact item that has aged out of even the 90-day window, so the
        // item has not traded in months. Still this item, so still better than a different wear.
        StaleSale,
        // Median sales of an adjacent wear of the same skin. A guess at a different item.
        NearestWearSale,
        // Listings of an adjacent wear of the same skin. A guess at a different item's asks.
        NearestWearListing,
    }

    // A resolved price for a lookup. ValueCents is the headline number the UI shows: the best
    // available estimate of what this item is worth *in a sale*. Basis says where it came from,
    // and Approximate is true whenever the value doesn't describe recent sales of this exact
    // variant - because it's an ask, a stale sale, a different wear, or too thin a sample to trust.
    // In the adjacent-wear case the cents can be an average of the two neighbouring wears, and so
    // correspond to no real listing or sale - which is the whole reason the flag travels with the
    // number. MinCents/SuggestedCents are kept alongside for callers that want the listing detail.
    public record PriceResult(
        int? ValueCents,
        PriceBasis Basis,
        bool Approximate,
        int? MinCents,
        int? SuggestedCents,
        int SaleVolume,
        string? SaleWindow = null);

    // Skinport base pricing. Skinport's free, no-auth /v1/items endpoint returns the entire CS2
    // catalogue in a single call, so one request keeps every item priced. We hold the result in
    // memory for O(1) lookup by market_hash_name (an inventory prices ~2000 items per view, far too
    // many for per-item DB hits) and persist it so a restart serves last-known prices immediately.
    //
    // Prices drift slowly and Skinport caches its feed ~5 min while rate-limiting to 8 req / 5 min,
    // so there is nothing to gain from polling hard - we refresh a few times a day. The feed is
    // Brotli-only (a plain request 406s), handled by the "skinport" client's AutomaticDecompression
    // in Program.cs.
    //
    // The feed only carries variants that have actually listed on Skinport, so odd wears / StatTrak
    // combos are often absent. We fill those two ways: a value that drops out of the feed is kept
    // (and shown approximate once it's over a week stale), and a variant that was never listed
    // borrows from an adjacent wear of the same skin (also approximate). A kept value is preferred
    // over an adjacent-wear guess. The borrow is capped at one wear tier - see NearestWear.
    //
    // We pull a SECOND feed, /v1/sales/history, and prefer it for the headline value. The question
    // an inventory page answers is "what is this worth", and a listing is a poor answer to that: it
    // is by definition a price at which the item has NOT sold. Measured against completed sales on
    // the live feed, suggested_price runs ~25% high at the median, which would inflate a whole
    // inventory total. Sales history also reaches far more of the catalogue where it matters most -
    // ~11k names that sold in the last 90 days have no live listing at all, and a third of those
    // never appear in /v1/items - so today they can only be priced by borrowing an adjacent wear,
    // which is off by more than 2x for 43% of the items we can check. An adjacent wear is a
    // different item; a past sale is this one, so a sale wins even when it is old.
    //
    // Sales history is a running index, not a mirror: Skinport's windows only span 90 days, so a
    // rarely-traded item drops out of them entirely. We persist every median we ever see and never
    // delete, so the last observed sale survives and simply ages into an approximate value.
    public class PriceService(IHttpClientFactory httpClientFactory, DatabaseService dbService,
        ILogger<PriceService> logger) : BackgroundService
    {
        public const string Currency = "USD";
        private const string ItemsUrl = "https://api.skinport.com/v1/items?app_id=730&currency=" + Currency + "&tradable=0";
        private const string SalesUrl = "https://api.skinport.com/v1/sales/history?app_id=730&currency=" + Currency;

        private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);
        private static readonly TimeSpan MaxStalenessBeforeStartupFetch = TimeSpan.FromHours(6);
        // Cold start with a failing feed: retry on this escalating backoff (last value repeats)
        // instead of the full RefreshInterval, so a transient outage doesn't leave the site with no
        // prices at all for 6h. Only used while we've never successfully loaded any prices.
        private static readonly TimeSpan[] ColdStartBackoff =
            [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];
        // A kept value older than this is shown with a leading "~" (approximate).
        private static readonly TimeSpan StaleThreshold = TimeSpan.FromDays(7);

        // Sale windows narrowest (most recent) first. They nest, so scanning in this order and
        // taking the first with enough volume yields the freshest median backed by a real sample.
        private static readonly (string Label, Func<SkinportSalesItem, SkinportSalesWindow?> Select)[] SaleWindows =
        [
            ("24h", s => s.last_24_hours),
            ("7d", s => s.last_7_days),
            ("30d", s => s.last_30_days),
            ("90d", s => s.last_90_days),
        ];

        // Sales needed before a window's median is preferred over a wider, better-sampled one. A
        // median of one or two sales is barely a median - for a thinly traded item a single odd
        // float or pattern sets it - so we widen the window rather than trust it.
        private const int MinConfidentVolume = 3;

        // Wear tiers best -> worst (by float). A variant that was never listed may borrow from its
        // immediate neighbours in this list, and from nowhere else.
        private static readonly string[] WearOrder =
            ["Factory New", "Minimal Wear", "Field-Tested", "Well-Worn", "Battle-Scarred"];

        // Preserves every item ever seen (feed items are refreshed in place; items that leave the
        // feed keep their last value + timestamp). volatile so readers see each swap atomically.
        private volatile IReadOnlyDictionary<string, SkinPrice> _prices =
            new Dictionary<string, SkinPrice>(StringComparer.Ordinal);
        private DateTime? _updatedAtUtc;

        // The running sale index, same keep-everything merge semantics as _prices: an item that
        // stops selling keeps its last observed median and timestamp instead of disappearing.
        private volatile IReadOnlyDictionary<string, SaleStat> _sales =
            new Dictionary<string, SaleStat>(StringComparer.Ordinal);

        // When the feed was last successfully fetched (UTC), or null before the first load.
        public DateTime? UpdatedAtUtc => _updatedAtUtc;

        // Resolve a displayable price for a market_hash_name, or null when we have nothing to show.
        //
        // Preference order, best evidence first:
        //   1. recent completed sales of this exact item          (what it actually sells for)
        //   2. live listings of this exact item                   (an ask, not a sale)
        //   3. an aged-out sale median for this exact item        (old, but still this item)
        //   4. sales, then listings, of an adjacent wear          (a different item entirely)
        //
        // Two orderings there are deliberate. A fresh sale outranks a live listing because a
        // listing is a price at which the item demonstrably did not sell. And a stale sale of this
        // exact item outranks anything from a neighbouring wear, because wear-to-wear price ratios
        // are wild - on the live feed an adjacent wear misses the true sale median by more than 2x
        // for 43% of items - whereas an old sale is at worst the right item at the wrong time.
        public PriceResult? Resolve(string? marketHashName)
        {
            if (string.IsNullOrEmpty(marketHashName)) return null;

            _sales.TryGetValue(marketHashName, out var sale);
            var listed = _prices.TryGetValue(marketHashName, out var exact) && exact.SuggestedCents != null
                ? exact
                : null;

            return ResolveExact(sale, listed, DateTime.UtcNow) ?? NearestWear(marketHashName);
        }

        // Tiers 1-3 of the ladder - everything we know about this exact market_hash_name - or null
        // when we know nothing and the caller should fall back to an adjacent wear. Static and pure
        // so the ordering can be tested without a feed, a database, or a clock.
        public static PriceResult? ResolveExact(SaleStat? sale, SkinPrice? listing, DateTime nowUtc)
        {
            if (listing != null && listing.SuggestedCents == null) listing = null;

            // 1. Recent sales of this exact item. Approximate when the sample is a single sale or
            // when several Doppler phases / gem tiers were pooled under this one name.
            if (sale != null && nowUtc - sale.UpdatedAtUtc <= StaleThreshold)
            {
                return new PriceResult(
                    sale.MedianCents, PriceBasis.Sale,
                    sale.Pooled || sale.Volume < 2,
                    listing?.MinCents, listing?.SuggestedCents, sale.Volume, sale.Window);
            }

            // 2. Live listings of this exact item.
            if (listing != null)
            {
                return new PriceResult(
                    listing.SuggestedCents, PriceBasis.Listing,
                    nowUtc - listing.UpdatedAtUtc > StaleThreshold,
                    listing.MinCents, listing.SuggestedCents, 0);
            }

            // 3. A sale median that has aged out of every window - the item has not traded in
            // months. Always approximate, but still this item rather than a neighbour.
            if (sale != null)
            {
                return new PriceResult(
                    sale.MedianCents, PriceBasis.StaleSale, true, null, null, sale.Volume, sale.Window);
            }

            return null;
        }

        // A price for a variant that was never listed, inferred from the wears immediately either
        // side of it in WearOrder. Keys on the full base name, so it stays within the item's ★ /
        // StatTrak variant. Null when the name carries no wear at all.
        //
        // The borrow is capped at one tier. Two tiers apart the same skin can differ by an order of
        // magnitude, and the only warning the user gets is a leading "~" - a wrong number is worse
        // than no number, so beyond one tier we return nothing rather than reaching further out.
        //
        // Both neighbours priced -> their mean; one -> that one unchanged; neither -> null. Factory
        // New and Battle-Scarred sit at the ends of the list, so they have a single neighbour each
        // and never average. Averaging is the unbiased choice between two equidistant siblings, but
        // it invents a figure matching no listing anywhere - hence always approximate.
        private PriceResult? NearestWear(string marketHashName)
        {
            var wearIdx = -1;
            string? baseName = null;
            for (var i = 0; i < WearOrder.Length; i++)
            {
                var suffix = $" ({WearOrder[i]})";
                if (marketHashName.EndsWith(suffix, StringComparison.Ordinal))
                {
                    wearIdx = i;
                    baseName = marketHashName[..^suffix.Length];
                    break;
                }
            }
            if (wearIdx < 0) return null;

            // Off either end of WearOrder, or with no displayable SuggestedCents, there is no
            // usable neighbour on that side.
            SkinPrice? Neighbour(int i) =>
                i >= 0 && i < WearOrder.Length
                && _prices.TryGetValue($"{baseName} ({WearOrder[i]})", out var candidate)
                && candidate.SuggestedCents != null
                    ? candidate
                    : null;

            SaleStat? SaleNeighbour(int i) =>
                i >= 0 && i < WearOrder.Length
                && _sales.TryGetValue($"{baseName} ({WearOrder[i]})", out var candidate)
                    ? candidate
                    : null;

            var better = Neighbour(wearIdx - 1);
            var worse = Neighbour(wearIdx + 1);
            var betterSale = SaleNeighbour(wearIdx - 1);
            var worseSale = SaleNeighbour(wearIdx + 1);

            // The listing detail rides along either way; only the headline changes.
            var min = Mean(better?.MinCents, worse?.MinCents);
            var suggested = Mean(better?.SuggestedCents, worse?.SuggestedCents);

            // Prefer what the neighbours actually sold for over what they are listed at, for the
            // same reason the exact item does - averaged across the same one-tier window, so a
            // neighbour we have sales for is used even when only the other one is listed.
            if (betterSale != null || worseSale != null)
            {
                return new PriceResult(
                    Mean(betterSale?.MedianCents, worseSale?.MedianCents),
                    PriceBasis.NearestWearSale, true, min, suggested,
                    (betterSale?.Volume ?? 0) + (worseSale?.Volume ?? 0),
                    betterSale?.Window ?? worseSale?.Window);
            }

            if (better == null && worse == null) return null;

            return new PriceResult(suggested, PriceBasis.NearestWearListing, true, min, suggested, 0);
        }

        // Mean of whichever of the two values is present, in integer cents, rounded to nearest
        // (halves away from zero); null when neither is. Each field is averaged only over the
        // neighbours that actually carry it, so a null MinCents on one side yields the other side's
        // MinCents rather than halving it - treating the null as a zero would render a real item as
        // near-free.
        private static int? Mean(int? a, int? b)
        {
            if (a == null) return b;
            if (b == null) return a;
            return (int)Math.Round((a.Value + (double)b.Value) / 2, MidpointRounding.AwayFromZero);
        }

        // Pick the sale statistic for one market_hash_name from every feed row carrying that name.
        //
        // Windows nest, so we walk them narrowest first and take the first that clears
        // MinConfidentVolume; failing that we fall back to the widest window with any sales at all,
        // which is the best-sampled thing available. Rows are pooled volume-weighted because one
        // name can cover several Doppler phases or gem tiers, and Steam's market_hash_name doesn't
        // say which one an inventory item is - so the volume-weighted median across them is the
        // honest expectation rather than a coin flip between a Sapphire and a Phase 1.
        //
        // Returns null when nothing under this name has sold in 90 days. Static and pure so the
        // window and pooling rules can be tested without a feed.
        public static (int MedianCents, int Volume, string Window, bool Pooled)? ChooseSale(
            IReadOnlyList<SkinportSalesItem> rows)
        {
            (int MedianCents, int Volume, string Window, bool Pooled)? fallback = null;

            foreach (var (label, select) in SaleWindows)
            {
                double weighted = 0;
                var volume = 0;
                var contributors = 0;
                foreach (var row in rows)
                {
                    var window = select(row);
                    if (window == null || window.volume <= 0 || window.median is not > 0) continue;
                    weighted += window.median.Value * window.volume;
                    volume += window.volume;
                    contributors++;
                }
                if (volume == 0) continue;

                var candidate = ((int)Math.Round(weighted / volume * 100), volume, label, contributors > 1);
                if (volume >= MinConfidentVolume) return candidate;
                // Too thin to trust on its own; remember it and try the next (wider) window, which
                // contains it and can only be better sampled.
                fallback = candidate;
            }

            return fallback;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Serve the persisted snapshot straight away (even if stale) so prices are live from the
            // first request while the network refresh runs.
            await LoadPersistedPricesAsync();
            await LoadPersistedSalePricesAsync();

            var coldStartAttempts = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                var age = _updatedAtUtc == null ? TimeSpan.MaxValue : DateTime.UtcNow - _updatedAtUtc.Value;
                if (age >= MaxStalenessBeforeStartupFetch)
                {
                    await RefreshAsync(stoppingToken);
                }

                // If we've still never loaded any prices, the feed is failing on a cold start; retry
                // soon on the escalating backoff rather than going dark for the full interval. Once a
                // load succeeds, fall back to the normal cadence.
                TimeSpan delay;
                if (_updatedAtUtc == null)
                {
                    delay = ColdStartBackoff[Math.Min(coldStartAttempts, ColdStartBackoff.Length - 1)];
                    coldStartAttempts++;
                    logger.LogWarning(
                        "Skinport prices still unavailable; retrying in {RetryInMinutes:0} min.",
                        delay.TotalMinutes);
                }
                else
                {
                    delay = RefreshInterval;
                    coldStartAttempts = 0;
                }

                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        // Populate the in-memory map from the DB snapshot. A failure here is non-fatal: the refresh
        // below will repopulate from the feed. internal so tests can drive the load path directly
        // rather than through the background loop's timing.
        internal async Task LoadPersistedPricesAsync()
        {
            try
            {
                var persisted = await dbService.LoadPricesAsync();
                if (persisted.Count > 0)
                {
                    _prices = persisted.ToDictionary(
                        kv => kv.Key,
                        kv => new SkinPrice(kv.Value.MinCents, kv.Value.SuggestedCents, kv.Value.UpdatedAt),
                        StringComparer.Ordinal);
                    _updatedAtUtc = persisted.Values.Max(v => v.UpdatedAt);
                    logger.LogInformation(
                        "Loaded {PriceCount} persisted Skinport prices (latest {PricesUpdatedAt:u}).",
                        persisted.Count, _updatedAtUtc);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load persisted prices");
            }
        }

        // The sale index is the primary source for the headline value, and unlike the listing feed
        // it accumulates across restarts rather than being reconstructible from one fetch (an item
        // that stopped selling months ago appears in no current window). internal for the same
        // reason as LoadPersistedPricesAsync: tests drive it without the background loop's timing.
        internal async Task LoadPersistedSalePricesAsync()
        {
            try
            {
                var persisted = await dbService.LoadSalePricesAsync();
                if (persisted.Count > 0)
                {
                    _sales = persisted.ToDictionary(
                        kv => kv.Key,
                        kv => new SaleStat(kv.Value.MedianCents, kv.Value.Volume, kv.Value.Window,
                            kv.Value.Pooled, kv.Value.UpdatedAt),
                        StringComparer.Ordinal);
                    logger.LogInformation(
                        "Loaded {SaleCount} persisted Skinport sale medians.", persisted.Count);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load persisted sale prices");
            }
        }

        // What a single feed fetch did, to the extent the caller needs to care.
        private enum RefreshOutcome
        {
            Ok,
            // Skinport turned us away. The two feeds share one 8-request-per-5-minute budget, so
            // this is the one outcome that must stop the other fetch.
            RateLimited,
            Failed,
        }

        internal async Task RefreshAsync(CancellationToken cancellationToken)
        {
            // The feeds are otherwise independent: an empty or broken listings response must not
            // also cost us the sale medians, which are the headline value and the only source for
            // the thousands of items that have no live listing at all.
            if (await RefreshListingsAsync(cancellationToken) == RefreshOutcome.RateLimited) return;
            await RefreshSalesAsync(cancellationToken);
        }

        private async Task<RefreshOutcome> RefreshListingsAsync(CancellationToken cancellationToken)
        {
            try
            {
                var client = httpClientFactory.CreateClient("skinport");
                using var response = await client.GetAsync(ItemsUrl, cancellationToken);
                if ((int)response.StatusCode == 429)
                {
                    // Two templates rather than one with a spliced-in fragment: Retry-After is
                    // optional on Skinport's 429s, and a conditional inside the message would put
                    // the seconds back into the text instead of leaving them a queryable field.
                    var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds;
                    if (retryAfter is double seconds)
                    {
                        logger.LogWarning(
                            "Skinport rate limited (429), retry after {RetryAfterSeconds}s; keeping current prices.",
                            seconds);
                    }
                    else
                    {
                        logger.LogWarning("Skinport rate limited (429); keeping current prices.");
                    }
                    return RefreshOutcome.RateLimited;
                }
                response.EnsureSuccessStatusCode();

                var items = await response.Content.ReadFromJsonAsync<List<SkinportItem>>(cancellationToken);
                if (items == null || items.Count == 0)
                {
                    logger.LogWarning("Skinport returned no items; keeping current prices.");
                    return RefreshOutcome.Failed;
                }

                static int? Cents(double? price) => price is double p ? (int)Math.Round(p * 100) : null;

                var now = DateTime.UtcNow;
                // Merge over the existing map rather than replacing it: feed items are refreshed to
                // `now`, and items no longer in the feed keep their last value + older timestamp so
                // they can still be shown (approximate once over a week old).
                var merged = new Dictionary<string, SkinPrice>(_prices, StringComparer.Ordinal);
                var fed = new Dictionary<string, (int?, int?)>(items.Count, StringComparer.Ordinal);
                foreach (var item in items)
                {
                    if (string.IsNullOrEmpty(item.market_hash_name)) continue;
                    var min = Cents(item.min_price);
                    var suggested = Cents(item.suggested_price);
                    merged[item.market_hash_name] = new SkinPrice(min, suggested, now);
                    fed[item.market_hash_name] = (min, suggested);
                }

                _prices = merged;
                _updatedAtUtc = now;

                await dbService.SavePricesAsync(fed, now);
                logger.LogInformation(
                    "Refreshed {RefreshedCount} Skinport prices ({KeptCount} kept in total).",
                    fed.Count, merged.Count);
                return RefreshOutcome.Ok;
            }
            catch (Exception ex)
            {
                // Keep serving whatever we already have; try again next cycle.
                logger.LogError(ex, "Failed to refresh Skinport prices");
                return RefreshOutcome.Failed;
            }
        }

        // Pull the sales-history feed and fold it into the running sale index. Same merge rule as
        // prices: names in the feed are refreshed, names absent from it keep their last observed
        // median and timestamp so a since-stopped-trading item stays priced (and ages naturally
        // into an approximate value) rather than vanishing.
        internal async Task RefreshSalesAsync(CancellationToken cancellationToken)
        {
            try
            {
                var client = httpClientFactory.CreateClient("skinport");
                using var response = await client.GetAsync(SalesUrl, cancellationToken);
                if ((int)response.StatusCode == 429)
                {
                    logger.LogWarning(
                        "Skinport sales history rate limited (429); keeping current sale medians.");
                    return;
                }
                response.EnsureSuccessStatusCode();

                var rows = await response.Content.ReadFromJsonAsync<List<SkinportSalesItem>>(cancellationToken);
                if (rows == null || rows.Count == 0)
                {
                    logger.LogWarning("Skinport sales history returned no items; keeping current sale medians.");
                    return;
                }

                // Group first: a market_hash_name can span several rows (Doppler phases, gem
                // tiers), and ChooseSale pools them.
                var grouped = new Dictionary<string, List<SkinportSalesItem>>(StringComparer.Ordinal);
                foreach (var row in rows)
                {
                    if (string.IsNullOrEmpty(row.market_hash_name)) continue;
                    if (!grouped.TryGetValue(row.market_hash_name, out var list))
                    {
                        grouped[row.market_hash_name] = list = [];
                    }
                    list.Add(row);
                }

                var now = DateTime.UtcNow;
                var merged = new Dictionary<string, SaleStat>(_sales, StringComparer.Ordinal);
                var observed = new Dictionary<string, (int, int, string, bool)>(StringComparer.Ordinal);
                foreach (var (name, group) in grouped)
                {
                    // No sales in any window: leave whatever we already had for this name in place.
                    // That is the whole point of the index - the rarely-traded items are exactly the
                    // ones the feed stops reporting.
                    if (ChooseSale(group) is not { } chosen) continue;
                    merged[name] = new SaleStat(chosen.MedianCents, chosen.Volume, chosen.Window, chosen.Pooled, now);
                    observed[name] = (chosen.MedianCents, chosen.Volume, chosen.Window, chosen.Pooled);
                }

                _sales = merged;

                await dbService.SaveSalePricesAsync(observed, now);
                logger.LogInformation(
                    "Refreshed {RefreshedCount} Skinport sale medians ({KeptCount} kept in total).",
                    observed.Count, merged.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to refresh Skinport sales history");
            }
        }
    }
}
