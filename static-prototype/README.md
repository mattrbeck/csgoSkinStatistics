# Static / client-side prototype

A working prototype of the item-lookup page with **no backend** — the inspect-link
certificate is decoded, named, and rendered entirely in the browser, pulling only the
slices of the catalog each item needs. Built to answer two questions:

1. What does a fully client-side version of this app actually look like?
2. What are the **caching / update implications** of shipping the data as static files?

This is a prototype to learn from, not a rewrite. It lives entirely under
`static-prototype/` and touches nothing in the real app.

> Prior art: `docs/client-side-cert-decode-findings.md` already proved client-side cert
> **decode** is trivial and concluded "ship the whole catalog vs per-id round-trips both
> lose." This prototype tests the option that doc never measured — **ranged shards**, the
> middle ground — and works through the cache-update story end to end.

## Run it

Zero dependencies (Node built-ins + a static server):

```bash
node static-prototype/build/build.mjs                 # generate data/ shards + manifest + sample links
cd static-prototype/site && python3 -m http.server 8777
# open http://localhost:8777
```

Click a sample, or paste a hex inspect link. Each card has a "network for this item"
panel showing exactly which shards that one lookup fetched (and which were cache hits).
`static-prototype/screenshot.png` shows it running.

## How it works

```
hex cert link ──▶ proto.js  ──▶ decode CEconItemPreviewDataBlock (XOR-unwrap, varint reader)
                    │
                    ▼
              resolve.js  ──▶ names, wear, rarity, AND the "special" logic
                    │            (fade %, Doppler phase, Fire & Ice, kimono) — all client-side
                    ▼
              loader.js   ──▶ fetch ONLY the shards this item needs, by id range
                    │            (one sticker bucket, one image bucket, const-special iff fade/F&I)
                    ▼
                app.js    ──▶ render card + per-item network breakdown
```

- **`proto.js`** — a ~120-line dependency-free protobuf reader/writer for the one message
  type (`CEconItemPreviewDataBlock`), plus the cert unwrap
  (`hex → XOR by first byte → strip 1-byte key + 4-byte CRC → protobuf`). Validated by
  round-trip (the encoder mints the sample links the decoder reads). The encoder also lets
  the build generate real test links from real catalog ids.
- **`resolve.js`** — a direct port of the server's `ConstDataService`. Yes, the
  "identify special information" code runs fine on the client: it's pure table lookups
  over `const.json` (fade order, fire & ice order, doppler/kimono maps).
- **`loader.js`** — the lazy, manifest-driven, range-aware fetcher (see below).

### What can and cannot be client-side

| Link form | Client-side? | Why |
|---|---|---|
| **Hex "cert"** (`…preview <hex>`) | **Yes** | The payload *is* the item. No lookup needed — this is the whole point. |
| `S/A/D/M` (`…preview S…A…D…`) | **No** | Carries only a Game Coordinator authorization token; resolving it needs a logged-in Steam bot. The prototype detects this form and says so. |
| Inventory by SteamID | **No** | Steam's inventory endpoint blocks browser CORS; the server proxies it. (But each item Steam returns embeds a cert, so a server proxy + this client decoder is a viable split.) |

So a static app is a complete replacement **only for the hex-cert single-item lookup**.
The other two surfaces still need a thin server. That matches the prior finding.

## The data layer: ranged shards

The catalogs don't fit the "one big file" model well — `stickers.json` is 669 KB gzipped.
The build (`build/build.mjs`) splits id-keyed catalogs into fixed-size **buckets** and the
client computes `bucket = floor(id / size)` to fetch only the one it needs.

Measured output (gzipped, what the wire actually carries):

| Shard | Size (gz) | When fetched |
|---|---|---|
| `manifest.json` | **0.4 KB** | every page load (no-cache) |
| `const-core` | **24 KB** | once per session — needed to name any item |
| `const-special` (fade/F&I tables) | **2.3 KB** | only for a Fade / Amber Fade / Marble Fade item |
| one `stickers-N` bucket (1000 ids) | **~58 KB** | only when an item has a sticker in that range |
| one `images-N` bucket | **6–109 KB** | only when an item has an image in that range |
| `keychains` (whole, 78 entries) | **5.8 KB** | only when an item has a charm |
| `float-ranges` (whole) | **4.8 KB** | cosmetic float bar (lazy) |

**Against the alternatives the prior doc weighed:**

- vs **ship everything**: a sticker lookup pulls ~58 KB, not the 669 KB full catalog — **~11× less**, and most items pull *zero* sticker bytes.
- vs **per-id round-trips**: one ranged fetch covers ~870 stickers, so a 4-sticker item is usually **1 request, not 4**, and the second sticker on the same item is free.
- The unavoidable floor is `manifest (0.4) + const-core (24)` once, then items are mostly just their image shard.

### Tuning knobs the prototype exposes

- **Bucket size** (`STICKER_BUCKET` / `IMAGE_BUCKET` in `build.mjs`). 1000 → ~58 KB/shard.
  Drop to 250 → ~15 KB/shard but 4× the files and more requests for multi-sticker items.
  It's a per-lookup-latency vs request-count dial.
- **Bucketing by id range is naïve.** `images-0` is 109 KB while `images-10` is 6 KB because
  real paintindexes cluster low. Size-balanced or hash bucketing would even shards out — a
  real adoption should bucket by *content distribution*, not raw id ranges.

## Cache & CDN update implications (the main question)

This is the part that actually matters for operating a static app, and the design here is
built around it.

**The invariant: content-addressed shards + one tiny mutable pointer.**

- Every shard's filename embeds a hash of its bytes: `stickers-3.14b83271.json`. The bytes
  can therefore **never change under a given URL**. Serve every shard
  `Cache-Control: public, max-age=31536000, immutable` — browser and CDN keep it forever,
  zero revalidation.
- `manifest.json` is the **only** mutable URL. It maps logical names → current hashed
  filenames and is fetched `cache: "no-store"`. It's 0.4 KB, so paying full price for it on
  every load is free.

**Updating the data (e.g. a new sticker capsule drops):**

1. Re-run the build. Only the shards whose contents changed get new hashes/filenames; the
   rest keep their old names.
2. Deploy the new shards **alongside** the old ones (additive — don't delete yet).
3. Deploy the new `manifest.json` last. This single atomic swap cuts every new page load
   over to the new data.

Why this avoids the classic static-cache traps:

- **No stale data on hashed files.** A client can't get "old bytes at a URL it thinks is
  new" — the URL *is* the hash. Cache staleness on shards is impossible by construction.
- **No cache-busting query strings, no purge API calls.** You never invalidate a shard; you
  publish a new one and re-point the manifest.
- **Mid-session safety.** A user who loaded the old manifest keeps fetching old shards that
  still exist — their session stays consistent. Their *next* load picks up the new manifest.
- **The only thing that can be stale is the manifest**, and it's `no-store`, so the worst
  case is a CDN edge serving a seconds-old manifest until it propagates — bounded and small.
  (If a CDN ignores `no-store`, give the manifest a short `max-age` of 30–60 s instead; the
  staleness window is then that TTL, never longer.)
- **`version` field** lets a long-lived SPA poll the manifest and notice the data moved
  (`"data updated"` → soft reload), since shards it already holds remain valid.
- **Rollback is a one-file revert** — redeploy the previous `manifest.json`; the old shards
  it points to were never deleted.
- **Client owns the routing.** `bucket = floor(id / size)` lives in the client, so there's
  no server-side routing table to keep in sync with the shard layout — the manifest carries
  the bucket size, so even *that* can change between versions without a client release.

**The app code itself** (`*.js`, `index.html`) updates like any static site: hash those
filenames too (or use a service worker) and the same immutable-asset + one-pointer rule
applies. The pointer there is `index.html` (short cache), which references hashed JS.

### Honest caveats

- **First-visit cost is still real.** ~24 KB (const-core) + manifest before the first card,
  then the item's image shard. Fine on broadband, a regression vs one server round-trip on
  slow mobile — exactly what the prior doc measured. Ranged shards shrink the *per-item*
  cost but don't erase the const-core floor.
- **A big `images-0` (109 KB)** means a common legacy skin still pulls a chunky shard.
  Balanced bucketing (above) is the fix.
- **No CRC validation** on decode (we drop the 4 CRC bytes, matching the server). Fine for
  display; a real app taking untrusted links should still treat decoded fields as untrusted
  (the server's `GetFadePercent` already bounds-checks attacker-controlled `paintseed`, and
  `resolve.js` mirrors that).
- This only replaces the **hex-cert** path. Inventory + `S/A/D/M` still need a server.

## Files

```
static-prototype/
  build/build.mjs     # generates site/data/ shards + manifest + sample links
  site/
    index.html
    app.js            # wiring + per-item network panel
    proto.js          # protobuf reader/writer + cert XOR unwrap (browser + Node)
    resolve.js        # client port of ConstDataService (incl. special-pattern logic)
    loader.js         # manifest-driven, range-aware lazy shard loader
    styles.css
    data/             # generated (gitignored) — run the build
    samples.json      # generated — real cert links from real catalog ids
  screenshot.png      # the prototype running
```
