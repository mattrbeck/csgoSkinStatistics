namespace CSGOSkinAPI.Services
{
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
}
