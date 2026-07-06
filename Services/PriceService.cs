namespace CSGOSkinAPI.Services
{
    // A base market price for one item, in integer cents of PriceService.Currency. Min = cheapest
    // live Skinport listing (null when nothing is listed); Suggested = Skinport's smoothed reference
    // price. UpdatedAtUtc = when this item was last seen in the feed (so a value that has dropped out
    // of the feed can be aged and flagged approximate).
    public record SkinPrice(int? MinCents, int? SuggestedCents, DateTime UpdatedAtUtc);

    // A resolved price for a lookup: the cents plus whether it's approximate. Approximate is true
    // when the exact variant has aged out of the feed (>1 week) or when we fell back to the nearest
    // wear of the same skin because the exact variant was never listed.
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
    // (and shown approximate once it's over a week stale), and a variant that was never listed falls
    // back to the nearest wear of the same skin (also approximate). A kept value is preferred over a
    // nearest-wear guess.
    public class PriceService(IHttpClientFactory httpClientFactory, DatabaseService dbService) : BackgroundService
    {
        public const string Currency = "USD";
        private const string ItemsUrl = "https://api.skinport.com/v1/items?app_id=730&currency=" + Currency + "&tradable=0";

        private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);
        private static readonly TimeSpan MaxStalenessBeforeStartupFetch = TimeSpan.FromHours(6);
        // A kept value older than this is shown with a leading "~" (approximate).
        private static readonly TimeSpan StaleThreshold = TimeSpan.FromDays(7);

        // Wear tiers best -> worst (by float). Used to find the nearest available wear; ties resolve
        // toward the better (lower-float) wear because this list is scanned front to back.
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
        // Preference order: the exact variant (approximate only if it's over a week stale) -> the
        // nearest wear of the same skin (always approximate).
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

        // The suggested price of the closest wear of the same skin (same base name, so it stays
        // within the item's ★ / StatTrak variant), or null when the name has no wear or no sibling
        // is priced. Always approximate.
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

            SkinPrice? best = null;
            var bestDistance = int.MaxValue;
            for (var i = 0; i < WearOrder.Length; i++)
            {
                if (i == wearIdx) continue;
                if (_prices.TryGetValue($"{baseName} ({WearOrder[i]})", out var candidate)
                    && candidate.SuggestedCents != null)
                {
                    var distance = Math.Abs(i - wearIdx);
                    // Strict < with a front-to-back scan means a tie keeps the better (lower-float)
                    // wear, which is encountered first.
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = candidate;
                    }
                }
            }

            return best == null ? null : new PriceResult(best.MinCents, best.SuggestedCents, true);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Serve the persisted snapshot straight away (even if stale) so prices are live from the
            // first request while the network refresh runs.
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

            while (!stoppingToken.IsCancellationRequested)
            {
                var age = _updatedAtUtc == null ? TimeSpan.MaxValue : DateTime.UtcNow - _updatedAtUtc.Value;
                if (age >= MaxStalenessBeforeStartupFetch)
                {
                    await RefreshAsync(stoppingToken);
                }

                try
                {
                    await Task.Delay(RefreshInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private async Task RefreshAsync(CancellationToken cancellationToken)
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
