namespace CSGOSkinAPI.Services
{
    public class DatabaseService
    {
        private readonly string _connectionString;

        public DatabaseService(string? databasePath = null)
        {
            var dbPath = databasePath ?? "searches.db";
            _connectionString = $"Data Source={dbPath};foreign keys=true;";
        }

        // Opens a connection with a busy timeout so a read that lands during a write waits for the
        // lock instead of failing immediately with SQLITE_BUSY. The warm service writes
        // concurrently with cache-hit reads, so this matters under load.
        private async Task<SqliteConnection> OpenConnectionAsync()
        {
            var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            await using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA busy_timeout=5000;";
            await pragma.ExecuteNonQueryAsync();
            return connection;
        }

        public async Task InitializeDatabaseAsync()
        {
            using var connection = await OpenConnectionAsync();

            // WAL lets readers and a writer proceed concurrently (the default rollback journal
            // blocks readers during a write). It is a persistent property of the database file,
            // so setting it once at startup is enough.
            await using (var walCommand = connection.CreateCommand())
            {
                walCommand.CommandText = "PRAGMA journal_mode=WAL;";
                await walCommand.ExecuteNonQueryAsync();
            }

            // `itemid` is a sound PRIMARY KEY because it is immutable identity: the GC mints
            // a new itemid whenever an item's config changes (stickers, name tag, ...), so a
            // row here never goes stale. Caveat: non-paint types (music kits, graffiti,
            // passes, ...) decode with itemid == 0 and would all collide on key 0 — they are
            // filtered out before persistence in SaveItemWithExtrasAsync.
            var createTableCommand = @"
                CREATE TABLE IF NOT EXISTS searches (
                    itemid INTEGER PRIMARY KEY NOT NULL,
                    defindex INTEGER NOT NULL,
                    paintindex INTEGER NOT NULL,
                    rarity INTEGER NOT NULL,
                    quality INTEGER NOT NULL,
                    paintwear INTEGER NOT NULL,
                    paintseed INTEGER NOT NULL,
                    inventory INTEGER NOT NULL,
                    origin INTEGER NOT NULL,
                    stattrak INTEGER NOT NULL,
                    killeatervalue INTEGER
                )";

            using var command = new SqliteCommand(createTableCommand, connection);
            await command.ExecuteNonQueryAsync();

            // killeatervalue (the live StatTrak kill count) was added after this table shipped;
            // back-fill the column on pre-existing databases. SQLite has no "ADD COLUMN IF NOT
            // EXISTS", so a duplicate-column error just means the migration already ran.
            try
            {
                using var alterCommand = new SqliteCommand(
                    "ALTER TABLE searches ADD COLUMN killeatervalue INTEGER", connection);
                await alterCommand.ExecuteNonQueryAsync();
            }
            catch (SqliteException)
            {
                // Column already exists - nothing to migrate.
            }


            foreach (var tableName in new[] { "stickers", "keychains" })
            {
                var createStickerTableCommand = @$"
                    CREATE TABLE IF NOT EXISTS {tableName} (
                        itemid INTEGER NOT NULL,
                        slot INTEGER NOT NULL,
                        sticker_id INTEGER NOT NULL,
                        wear REAL NOT NULL,
                        scale REAL,
                        rotation REAL,
                        tint_id INTEGER,
                        offset_x REAL,
                        offset_y REAL,
                        offset_z REAL,
                        pattern INTEGER,
                        highlight_reel INTEGER,
                        wrapped_sticker INTEGER,
                        FOREIGN KEY (itemid) REFERENCES searches(itemid) ON DELETE CASCADE
                )";

                using var stickersCommand = new SqliteCommand(createStickerTableCommand, connection);
                await stickersCommand.ExecuteNonQueryAsync();

                // wrapped_sticker (the Sticker Slab's sealed sticker id) was added after these
                // tables shipped, so back-fill the column on pre-existing cache databases.
                // SQLite has no "ADD COLUMN IF NOT EXISTS"; a duplicate column just no-ops.
                try
                {
                    using var alterCommand = new SqliteCommand(
                        $"ALTER TABLE {tableName} ADD COLUMN wrapped_sticker INTEGER", connection);
                    await alterCommand.ExecuteNonQueryAsync();
                }
                catch (SqliteException)
                {
                    // Column already exists - nothing to migrate.
                }

                var createIndexCommand = @$"CREATE INDEX IF NOT EXISTS itemid on {tableName} (itemid)";
                using var indexCommand = new SqliteCommand(createIndexCommand, connection);
                await indexCommand.ExecuteNonQueryAsync();
            }

            // Log of background inventory warms (one row per owner steamid, refreshed on
            // each warm). Doubles as the throttle that keeps a burst of cache misses for
            // one owner from re-fetching their inventory over and over.
            var createWarmsTableCommand = @"
                CREATE TABLE IF NOT EXISTS inventory_warms (
                    steamid INTEGER PRIMARY KEY NOT NULL,
                    last_warmed TEXT NOT NULL,
                    items_cached INTEGER NOT NULL
                )";
            using var warmsCommand = new SqliteCommand(createWarmsTableCommand, connection);
            await warmsCommand.ExecuteNonQueryAsync();

            // Skinport base prices, keyed by market_hash_name (the same key the item's decoded name
            // and Steam's inventory descriptions use). Persisted so a restart serves last-known
            // prices immediately while PriceService refreshes in the background. Unlike `searches`,
            // these are time-varying, hence the separate table and the shared updated_at stamp.
            var createPricesTableCommand = @"
                CREATE TABLE IF NOT EXISTS prices (
                    market_hash_name TEXT PRIMARY KEY NOT NULL,
                    min_cents INTEGER,
                    suggested_cents INTEGER,
                    updated_at TEXT NOT NULL
                )";
            using var pricesCommand = new SqliteCommand(createPricesTableCommand, connection);
            await pricesCommand.ExecuteNonQueryAsync();
        }

        // Load the whole persisted price map, each row carrying its own last-seen time (updated_at
        // is per item, refreshed only when that item is in the feed). Empty when never populated.
        public async Task<Dictionary<string, (int? MinCents, int? SuggestedCents, DateTime UpdatedAt)>> LoadPricesAsync()
        {
            using var connection = await OpenConnectionAsync();

            using var command = new SqliteCommand(
                "SELECT market_hash_name, min_cents, suggested_cents, updated_at FROM prices", connection);
            using var reader = await command.ExecuteReaderAsync();

            var prices = new Dictionary<string, (int?, int?, DateTime)>(StringComparer.Ordinal);
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(0);
                int? min = reader.IsDBNull(1) ? null : reader.GetInt32(1);
                int? suggested = reader.IsDBNull(2) ? null : reader.GetInt32(2);
                var updatedAt = DateTime.Parse(reader.GetString(3), null, DateTimeStyles.RoundtripKind);
                prices[name] = (min, suggested, updatedAt);
            }
            return prices;
        }

        // Upsert the current Skinport feed. Items in the feed are (re)written with updatedAt; items
        // NOT in the feed are left untouched, so a delisted variant keeps its last-known value and
        // ages naturally (PriceService flags it approximate once it's over a week stale).
        public async Task SavePricesAsync(IReadOnlyDictionary<string, (int? MinCents, int? SuggestedCents)> prices, DateTime updatedAt)
        {
            using var connection = await OpenConnectionAsync();
            using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

            const string upsert = @"INSERT OR REPLACE INTO prices (market_hash_name, min_cents, suggested_cents, updated_at)
                VALUES (@name, @min, @suggested, @updated_at)";
            var stamp = updatedAt.ToString("o");
            foreach (var (name, price) in prices)
            {
                using var command = new SqliteCommand(upsert, connection, transaction);
                command.Parameters.AddWithValue("@name", name);
                command.Parameters.AddWithValue("@min", (object?)price.MinCents ?? DBNull.Value);
                command.Parameters.AddWithValue("@suggested", (object?)price.SuggestedCents ?? DBNull.Value);
                command.Parameters.AddWithValue("@updated_at", stamp);
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        public async Task<List<CEconItemPreviewDataBlock.Sticker>> GetStickersAsync(ulong itemId, bool stickersTable)
        {
            using var connection = await OpenConnectionAsync();

            const string stickersQuery = "SELECT * FROM stickers WHERE itemid = @itemid ORDER BY slot";
            const string keychainsQuery = "SELECT * FROM keychains WHERE itemid = @itemid ORDER BY slot";
            var query = stickersTable ? stickersQuery : keychainsQuery;
            using var command = new SqliteCommand(query, connection);
            command.Parameters.AddWithValue("@itemid", (long)itemId);

            var stickers = new List<CEconItemPreviewDataBlock.Sticker>();
            using var reader = await command.ExecuteReaderAsync();

            var slotOrd = reader.GetOrdinal("slot");
            var stickerIdOrd = reader.GetOrdinal("sticker_id");
            var wearOrd = reader.GetOrdinal("wear");
            var scaleOrd = reader.GetOrdinal("scale");
            var rotationOrd = reader.GetOrdinal("rotation");
            var tintIdOrd = reader.GetOrdinal("tint_id");
            var offsetXOrd = reader.GetOrdinal("offset_x");
            var offsetYOrd = reader.GetOrdinal("offset_y");
            var offsetZOrd = reader.GetOrdinal("offset_z");
            var patternOrd = reader.GetOrdinal("pattern");
            var highlightReelOrd = reader.GetOrdinal("highlight_reel");
            var wrappedStickerOrd = reader.GetOrdinal("wrapped_sticker");

            while (await reader.ReadAsync())
            {
                var sticker = new CEconItemPreviewDataBlock.Sticker
                {
                    slot = (uint)reader.GetInt32(slotOrd),
                    sticker_id = (uint)reader.GetInt32(stickerIdOrd),
                    wear = reader.GetFloat(wearOrd)
                };
                if (!reader.IsDBNull(scaleOrd)) sticker.scale = reader.GetFloat(scaleOrd);
                if (!reader.IsDBNull(rotationOrd)) sticker.rotation = reader.GetFloat(rotationOrd);
                if (!reader.IsDBNull(tintIdOrd)) sticker.tint_id = (uint)reader.GetInt32(tintIdOrd);
                if (!reader.IsDBNull(offsetXOrd)) sticker.offset_x = reader.GetFloat(offsetXOrd);
                if (!reader.IsDBNull(offsetYOrd)) sticker.offset_y = reader.GetFloat(offsetYOrd);
                if (!reader.IsDBNull(offsetZOrd)) sticker.offset_z = reader.GetFloat(offsetZOrd);
                if (!reader.IsDBNull(patternOrd)) sticker.pattern = (uint)reader.GetInt32(patternOrd);
                if (!reader.IsDBNull(highlightReelOrd)) sticker.highlight_reel = (uint)reader.GetInt32(highlightReelOrd);
                // Re-attach the Sticker Slab's sealed sticker id as proto field 12, so a cached
                // slab looks identical to a freshly-decoded one and resolves the same way.
                if (!reader.IsDBNull(wrappedStickerOrd))
                {
                    StickerSlab.SetWrappedStickerId(sticker, (uint)reader.GetInt32(wrappedStickerOrd));
                }
                stickers.Add(sticker);
            }

            return stickers;
        }

        public async Task<CEconItemPreviewDataBlock?> GetItemAsync(ulong itemId)
        {
            using var connection = await OpenConnectionAsync();

            var query = "SELECT * FROM searches WHERE itemid = @itemid";
            using var command = new SqliteCommand(query, connection);
            command.Parameters.AddWithValue("@itemid", itemId);

            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var itemIdOrd = reader.GetOrdinal("itemid");
                var defIndexOrd = reader.GetOrdinal("defindex");
                var paintIndexOrd = reader.GetOrdinal("paintindex");
                var rarityOrd = reader.GetOrdinal("rarity");
                var qualityOrd = reader.GetOrdinal("quality");
                var paintWearOrd = reader.GetOrdinal("paintwear");
                var paintSeedOrd = reader.GetOrdinal("paintseed");
                var inventoryOrd = reader.GetOrdinal("inventory");
                var originOrd = reader.GetOrdinal("origin");
                var statTrakOrd = reader.GetOrdinal("stattrak");
                var killEaterOrd = reader.GetOrdinal("killeatervalue");

                var item = new CEconItemPreviewDataBlock
                {
                    itemid = (ulong)reader.GetInt64(itemIdOrd),
                    defindex = (uint)reader.GetInt32(defIndexOrd),
                    paintindex = (uint)reader.GetInt32(paintIndexOrd),
                    rarity = (uint)reader.GetInt32(rarityOrd),
                    quality = (uint)reader.GetInt32(qualityOrd),
                    paintwear = (uint)reader.GetInt32(paintWearOrd),
                    paintseed = (uint)reader.GetInt32(paintSeedOrd),
                    inventory = (uint)reader.GetInt64(inventoryOrd),
                    origin = (uint)reader.GetInt32(originOrd)
                };
                // killeatervalue non-null is both the StatTrak flag and the live kill count.
                // Prefer the stored count; fall back to 0 for legacy rows cached before the
                // column existed (StatTrak presence stays correct, count shows 0 until re-cached).
                if (!reader.IsDBNull(killEaterOrd)) item.killeatervalue = (uint)reader.GetInt64(killEaterOrd);
                else if (reader.GetInt32(statTrakOrd) == 1) item.killeatervalue = 0;
                item.stickers.AddRange(await GetStickersAsync(itemId, true));
                item.keychains.AddRange(await GetStickersAsync(itemId, false));

                return item;
            }

            return null;
        }

        public async Task SaveItemAsync(CEconItemPreviewDataBlock item)
        {
            using var connection = await OpenConnectionAsync();
            await InsertSearchRowAsync(item, connection, null);
        }

        private static async Task InsertSearchRowAsync(CEconItemPreviewDataBlock item, SqliteConnection connection, SqliteTransaction? transaction)
        {
            var insert = @"
                INSERT OR REPLACE INTO searches
                (itemid, defindex, paintindex, rarity, quality, paintwear, paintseed, inventory, origin, stattrak, killeatervalue)
                VALUES (@itemid, @defindex, @paintindex, @rarity, @quality, @paintwear, @paintseed, @inventory, @origin, @stattrak, @killeatervalue)";

            using var command = new SqliteCommand(insert, connection, transaction);
            command.Parameters.AddWithValue("@itemid", (long)item.itemid);
            command.Parameters.AddWithValue("@defindex", item.defindex);
            command.Parameters.AddWithValue("@paintindex", item.paintindex);
            command.Parameters.AddWithValue("@rarity", item.rarity);
            command.Parameters.AddWithValue("@quality", item.quality);
            command.Parameters.AddWithValue("@paintwear", item.paintwear);
            command.Parameters.AddWithValue("@paintseed", item.paintseed);
            command.Parameters.AddWithValue("@inventory", item.inventory);
            command.Parameters.AddWithValue("@origin", item.origin);
            command.Parameters.AddWithValue("@stattrak", item.ShouldSerializekilleatervalue() ? 1 : 0);
            // The live kill count when present, so cache hits keep it (not just the flag).
            command.Parameters.AddWithValue("@killeatervalue",
                item.ShouldSerializekilleatervalue() ? item.killeatervalue : DBNull.Value);

            await command.ExecuteNonQueryAsync();
        }

        public async Task SaveItemWithExtrasAsync(CEconItemPreviewDataBlock itemInfo)
        {
            // Music kits, graffiti, passes, standalone stickers, tools, etc. decode
            // with itemid == 0 (it is intrinsic to those defindex types, not a missing
            // value). Since `searches.itemid` is the PRIMARY KEY, persisting any of them
            // would collapse every zero-itemid item onto key 0 and, with INSERT OR
            // REPLACE, silently overwrite each other. These items carry no expensive
            // float/seed worth caching, so skip them. (See docs/inventory-endpoint-cert.md.)
            if (itemInfo.itemid == 0)
            {
                return;
            }

            // Persist the row and its extras atomically, and idempotently: re-saving the
            // same itemid must not duplicate sticker/keychain rows. The searches row uses
            // INSERT OR REPLACE, but the extras are plain INSERTs into tables with no
            // unique constraint on (itemid, slot) — slots are deliberately non-unique so
            // stacked stickers can share a slot — so we clear and rewrite the whole set
            // rather than relying on an upsert. (See docs/inventory-endpoint-cert.md 3b.)
            using var connection = await OpenConnectionAsync();
            using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

            await InsertSearchRowAsync(itemInfo, connection, transaction);
            await ClearExtrasAsync(itemInfo.itemid, connection, transaction);

            if (itemInfo.stickers?.Count > 0)
            {
                await SaveStickersAsync(itemInfo.itemid, itemInfo.stickers, true, connection, transaction);
            }

            if (itemInfo.keychains?.Count > 0)
            {
                await SaveStickersAsync(itemInfo.itemid, itemInfo.keychains, false, connection, transaction);
            }

            await transaction.CommitAsync();
        }

        private static async Task ClearExtrasAsync(ulong itemId, SqliteConnection connection, SqliteTransaction transaction)
        {
            foreach (var table in new[] { "stickers", "keychains" })
            {
                using var command = new SqliteCommand($"DELETE FROM {table} WHERE itemid = @itemid", connection, transaction);
                command.Parameters.AddWithValue("@itemid", (long)itemId);
                await command.ExecuteNonQueryAsync();
            }
        }

        private static async Task SaveStickersAsync(ulong itemId, List<CEconItemPreviewDataBlock.Sticker> items, bool stickerTable, SqliteConnection connection, SqliteTransaction transaction)
        {
            const string insertSchema = @"
                (itemid, slot, sticker_id, wear, scale, rotation, tint_id, offset_x, offset_y, offset_z, pattern, highlight_reel, wrapped_sticker) VALUES
                (@itemid, @slot, @sticker_id, @wear, @scale, @rotation, @tint_id, @offset_x, @offset_y, @offset_z, @pattern, @highlight_reel, @wrapped_sticker)";
            const string insertStickersQuery = @"INSERT INTO stickers " + insertSchema;
            const string insertKeychainsQuery = @"INSERT INTO keychains " + insertSchema;

            var insertQuery = stickerTable ? insertStickersQuery : insertKeychainsQuery;

            foreach (var item in items)
            {
                using var insertCommand = new SqliteCommand(insertQuery, connection, transaction);
                insertCommand.Parameters.AddWithValue("@itemid", (long)itemId);
                insertCommand.Parameters.AddWithValue("@slot", item.slot);
                insertCommand.Parameters.AddWithValue("@sticker_id", item.sticker_id);
                insertCommand.Parameters.AddWithValue("@wear", item.wear);
                insertCommand.Parameters.AddWithValue("@scale", item.ShouldSerializescale() ? item.scale : DBNull.Value);
                insertCommand.Parameters.AddWithValue("@rotation", item.ShouldSerializerotation() ? item.rotation : DBNull.Value);
                insertCommand.Parameters.AddWithValue("@tint_id", item.ShouldSerializetint_id() ? item.tint_id : DBNull.Value);
                insertCommand.Parameters.AddWithValue("@offset_x", item.ShouldSerializeoffset_x() ? item.offset_x : DBNull.Value);
                insertCommand.Parameters.AddWithValue("@offset_y", item.ShouldSerializeoffset_y() ? item.offset_y : DBNull.Value);
                insertCommand.Parameters.AddWithValue("@offset_z", item.ShouldSerializeoffset_z() ? item.offset_z : DBNull.Value);
                insertCommand.Parameters.AddWithValue("@pattern", item.ShouldSerializepattern() ? item.pattern : DBNull.Value);
                insertCommand.Parameters.AddWithValue("@highlight_reel", item.ShouldSerializehighlight_reel() ? item.highlight_reel : DBNull.Value);
                // Sticker Slab's sealed sticker id, carried in the unmodeled proto field 12.
                var wrapped = StickerSlab.GetWrappedStickerId(item);
                insertCommand.Parameters.AddWithValue("@wrapped_sticker", wrapped != 0 ? wrapped : DBNull.Value);
                await insertCommand.ExecuteNonQueryAsync();
            }
        }

        public async Task<DateTime?> GetLastWarmAsync(ulong steamid)
        {
            using var connection = await OpenConnectionAsync();

            using var command = new SqliteCommand("SELECT last_warmed FROM inventory_warms WHERE steamid = @steamid", connection);
            command.Parameters.AddWithValue("@steamid", (long)steamid);

            var value = await command.ExecuteScalarAsync();
            return value is string text
                ? DateTime.Parse(text, null, System.Globalization.DateTimeStyles.RoundtripKind)
                : null;
        }

        public async Task RecordWarmAsync(ulong steamid, int itemsCached)
        {
            using var connection = await OpenConnectionAsync();

            const string upsertQuery = @"INSERT OR REPLACE INTO inventory_warms
                (steamid, last_warmed, items_cached)
                VALUES (@steamid, @last_warmed, @items_cached)";
            using var command = new SqliteCommand(upsertQuery, connection);
            command.Parameters.AddWithValue("@steamid", (long)steamid);
            command.Parameters.AddWithValue("@last_warmed", DateTime.UtcNow.ToString("o"));
            command.Parameters.AddWithValue("@items_cached", itemsCached);
            await command.ExecuteNonQueryAsync();
        }
    }
}
