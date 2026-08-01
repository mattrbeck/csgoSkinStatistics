namespace CSGOSkinAPI.Services
{
    // A base market price for one item, in integer cents of PriceService.Currency. Min = cheapest
    // live Skinport listing (null when nothing is listed); Suggested = Skinport's smoothed reference
    // price. UpdatedAtUtc = when this item was last seen in the feed (so a value that has dropped out
    // of the feed can be aged and flagged approximate).
    public record SkinPrice(int? MinCents, int? SuggestedCents, DateTime UpdatedAtUtc);

    // A resolved price for a lookup: the cents plus whether it's approximate. Approximate is true
    // when the exact variant has aged out of the feed (>1 week) or when we fell back to an adjacent
    // wear of the same skin because the exact variant was never listed. In the fallback case the
    // cents can be an average of the two neighbouring wears, and so correspond to no real listing -
    // which is the whole reason the flag has to travel with the number.
    public record PriceResult(int? MinCents, int? SuggestedCents, bool Approximate);

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
    public class PriceService(IHttpClientFactory httpClientFactory, DatabaseService dbService) : BackgroundService
    {
        public const string Currency = "USD";
        private const string ItemsUrl = "https://api.skinport.com/v1/items?app_id=730&currency=" + Currency + "&tradable=0";

        private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);
        private static readonly TimeSpan MaxStalenessBeforeStartupFetch = TimeSpan.FromHours(6);
        // Cold start with a failing feed: retry on this escalating backoff (last value repeats)
        // instead of the full RefreshInterval, so a transient outage doesn't leave the site with no
        // prices at all for 6h. Only used while we've never successfully loaded any prices.
        private static readonly TimeSpan[] ColdStartBackoff =
            [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];
        // A kept value older than this is shown with a leading "~" (approximate).
        private static readonly TimeSpan StaleThreshold = TimeSpan.FromDays(7);

        // Wear tiers best -> worst (by float). A variant that was never listed may borrow from its
        // immediate neighbours in this list, and from nowhere else.
        private static readonly string[] WearOrder =
            ["Factory New", "Minimal Wear", "Field-Tested", "Well-Worn", "Battle-Scarred"];

        // Preserves every item ever seen (feed items are refreshed in place; items that leave the
        // feed keep their last value + timestamp). volatile so readers see each swap atomically.
        private volatile IReadOnlyDictionary<string, SkinPrice> _prices =
            new Dictionary<string, SkinPrice>(StringComparer.Ordinal);
        private DateTime? _updatedAtUtc;

        // When the feed was last successfully fetched (UTC), or null before the first load.
        public DateTime? UpdatedAtUtc => _updatedAtUtc;

        // Resolve a displayable price for a market_hash_name, or null when we have nothing to show.
        // Preference order: the exact variant (approximate only if it's over a week stale) -> an
        // adjacent wear of the same skin (always approximate).
        public PriceResult? Resolve(string? marketHashName)
        {
            if (string.IsNullOrEmpty(marketHashName)) return null;

            if (_prices.TryGetValue(marketHashName, out var exact) && exact.SuggestedCents != null)
            {
                var approximate = DateTime.UtcNow - exact.UpdatedAtUtc > StaleThreshold;
                return new PriceResult(exact.MinCents, exact.SuggestedCents, approximate);
            }

            return NearestWear(marketHashName);
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

            var better = Neighbour(wearIdx - 1);
            var worse = Neighbour(wearIdx + 1);
            if (better == null && worse == null) return null;

            return new PriceResult(
                Mean(better?.MinCents, worse?.MinCents),
                Mean(better?.SuggestedCents, worse?.SuggestedCents),
                true);
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

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Serve the persisted snapshot straight away (even if stale) so prices are live from the
            // first request while the network refresh runs.
            await LoadPersistedPricesAsync();

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
                    Console.WriteLine($"Skinport prices still unavailable; retrying in {delay.TotalMinutes:0} min.");
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
                    Console.WriteLine($"Loaded {persisted.Count} persisted Skinport prices (latest {_updatedAtUtc:u}).");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load persisted prices: {ex.Message}");
            }
        }

        internal async Task RefreshAsync(CancellationToken cancellationToken)
        {
            try
            {
                var client = httpClientFactory.CreateClient("skinport");
                using var response = await client.GetAsync(ItemsUrl, cancellationToken);
                if ((int)response.StatusCode == 429)
                {
                    var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds;
                    Console.WriteLine($"Skinport RATE LIMITED (429){(retryAfter is double s ? $", retry after {s}s" : "")}; keeping current prices.");
                    return;
                }
                response.EnsureSuccessStatusCode();

                var items = await response.Content.ReadFromJsonAsync<List<SkinportItem>>(cancellationToken);
                if (items == null || items.Count == 0)
                {
                    Console.WriteLine("Skinport returned no items; keeping current prices.");
                    return;
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
                Console.WriteLine($"Refreshed {fed.Count} Skinport prices ({merged.Count} kept in total).");
            }
            catch (Exception ex)
            {
                // Keep serving whatever we already have; try again next cycle.
                Console.WriteLine($"Failed to refresh Skinport prices: {ex.Message}");
            }
        }
    }
}
