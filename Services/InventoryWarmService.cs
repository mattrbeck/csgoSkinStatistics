namespace CSGOSkinAPI.Services
{
    // Background cache warmer: when a single-item lookup misses the DB, the owner's whole
    // inventory becomes interesting - wild inspect links tend to come in clusters from one
    // inventory (trade threads, showcases). This fetches that inventory once, decodes each
    // item's embedded certificate locally (see docs/inventory-endpoint-cert.md), and
    // persists the results, so follow-up lookups become DB hits with zero GC traffic.
    public class InventoryWarmService(IHttpClientFactory httpClientFactory, DatabaseService dbService,
        ILogger<InventoryWarmService> logger) : BackgroundService
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
                    logger.LogError(ex, "Inventory warm for {SteamId} failed", steamid);
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
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    // Split rather than conditionally concatenated, for the same reason as the
                    // Skinport 429 in PriceService: Retry-After is optional and belongs in a field.
                    var retryAfter = response.Headers.RetryAfter?.Delta;
                    if (retryAfter is TimeSpan delay)
                    {
                        logger.LogWarning(
                            "Inventory warm for {SteamId}: Steam rate limited (429); Retry-After {RetryAfterSeconds:0}s",
                            steamid, delay.TotalSeconds);
                    }
                    else
                    {
                        logger.LogWarning(
                            "Inventory warm for {SteamId}: Steam rate limited (429)", steamid);
                    }
                }
                else
                {
                    logger.LogWarning(
                        "Inventory warm for {SteamId}: fetch failed with {StatusCode}",
                        steamid, response.StatusCode);
                }
                return;
            }

            var inventoryData = JsonSerializer.Deserialize<SteamInventoryResponse>(
                await response.Content.ReadAsStringAsync(cancellationToken));
            if (inventoryData?.assets == null || inventoryData.descriptions == null)
            {
                logger.LogDebug("Inventory warm for {SteamId}: empty or invalid inventory", steamid);
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
            // Single count=2000 page, same as the inventory endpoint: a bigger inventory is warmed
            // only up to the first page. That's fine for a best-effort warmer, but note it.
            var truncated = inventoryData.total > inventoryData.assets.Count;
            logger.LogInformation(
                "Inventory warm for {SteamId}: cached {CachedCount} of {AssetCount} items "
                + "(truncated at one page: {Truncated})",
                steamid, cached, inventoryData.assets.Count, truncated);
        }
    }
}
