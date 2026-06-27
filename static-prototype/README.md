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
# open http://localhost:8777            (single-item page)
# open http://localhost:8777/inventory.html   (full inventory page)

# the inventory page's "live" mode defaults to public CORS proxies (no setup, but flaky).
# for a reliable local fetch instead, run the bundled shim:
node static-prototype/build/cors-proxy.mjs            # dumb CORS shim on :8788
```

A full **measured comparison of this prototype vs the existing app** — cold/warm, with/without
stickers, inventories, across fast and slow networks, plus the complete tradeoff matrix — is in
[`COMPARISON.md`](COMPARISON.md).

- **`index.html`** — click a sample or paste a hex inspect link (or deep-link with
  `index.html#<hex>`, which auto-decodes one item like the existing app's `/#<hex>`). Each
  card's "network for this item" panel shows exactly which shards that lookup fetched vs reused
  from cache. The footer shows the idle prefetcher warming the rest of the catalog.
- **`inventory.html`** — loads a whole inventory and decodes every embedded cert client-side.
  Defaults to a bundled real 281-item fixture; flip to "live" to fetch a SteamID64 through a
  public CORS proxy (or the local shim).

`screenshot.png` (item page) and `screenshot-inventory.png` show them running.

This page set tests three follow-on questions on top of the original prototype:
**(1)** prefetch the catalog during idle time so the first query is also instant,
**(2)** a better, size-balanced bucketing, and
**(3)** a full inventory page that fetches Steam and decodes the lot in the browser.
Each is written up in its own section below.

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
The build (`build/build.mjs`) splits id-keyed catalogs into **contiguous id-range shards**
and the client fetches only the shard an item needs (binary-searching the range boundaries
the manifest carries).

Measured output (gzipped, what the wire actually carries):

| Shard | Size (gz) | When fetched |
|---|---|---|
| `manifest.json` | **0.4 KB** | every page load (no-cache) |
| `const-core` | **24 KB** | once per session — needed to name any item |
| `const-special` (fade/F&I tables) | **2.3 KB** | only for a Fade / Amber Fade / Marble Fade item |
| one `stickers-N` shard | **~30 KB** | only when an item has a sticker in that range |
| one `images-N` shard | **10–36 KB** | only when an item has an image in that range |
| `keychains` (whole, 78 entries) | **5.8 KB** | only when an item has a charm |
| `float-ranges` (whole) | **4.8 KB** | cosmetic float bar (lazy) |

**Against the alternatives the prior doc weighed:**

- vs **ship everything**: a sticker lookup pulls ~30 KB, not the 669 KB full catalog — **~22× less**, and most items pull *zero* sticker bytes.
- vs **per-id round-trips**: one ranged fetch covers a whole id range, so a multi-sticker item is usually **1 request, not N**, and a second sticker in the same range is free.
- The unavoidable floor is `manifest (0.4) + const-core (24)` once, then items are mostly just their range shards — and even that floor disappears once the idle prefetcher (below) has run.

### Better bucketing: size-balanced ranges (tested)

The first cut used fixed `floor(id / 1000)` buckets, which were lopsided — real ids cluster,
so `images-0` ballooned to 109 KB while `images-10` was 6 KB. The build now picks **contiguous
ranges sized to a byte target** instead: fill a shard in id order, start a new one when it
would overflow ~130 KB raw. This keeps range locality (a multi-decal item still tends to hit
one shard) while flattening the size distribution. `build.mjs` prints the comparison:

```
bucketing comparison (raw KB per shard):
  catalog    strategy   shards   max     min     avg     p95
  stickers   fixed/1000   12      270    48.6     234     270
  stickers   balanced     22    128.2   120.1   127.7   128.1
  images     fixed/1000    3    412.8    20.1   181.8   412.8
  images     balanced      5      128    33.6   109.1     128
```

The worst-case shard a single lookup can pull drops from **270 → 128 KB** (stickers) and
**413 → 128 KB** (images) — the latter a **3.2× cut** to the worst case — at the cost of a few
more, smaller files. Tuning the byte target trades per-lookup latency against request count.
A pure hash bucketing would balance even better but destroys range locality (each sticker on
an item lands in a different shard), so contiguous-but-balanced is the sweet spot here.

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
- **Client owns the routing.** The id→shard mapping (binary search over range boundaries)
  lives in the client, and the boundaries come from the manifest — so the shard layout, and
  even the bucketing *strategy*, can change between data versions with no client release.

**The app code itself** (`*.js`, `index.html`) updates like any static site: hash those
filenames too (or use a service worker) and the same immutable-asset + one-pointer rule
applies. The pointer there is `index.html` (short cache), which references hashed JS.

## Idle prefetch: warming the cache before the first query (tested)

The "first-visit cost" caveat above has a cheap fix: after load, warm the rest of the catalog
during browser idle time. `app.js` walks every shard the manifest lists via
`requestIdleCallback`, one per idle tick, fetching each with `cache: "force-cache"` — which
populates both the in-memory map *and* the HTTP cache. Because the shards are immutable, this
is pure prefetch with no staleness risk.

Measured in the browser: the footer ticks up to *"catalog fully prefetched (30 shards warm)"*,
and a subsequent query then reports **"0 shards fetched, N reused from cache"** — it resolves
with **zero network**. A real query that arrives mid-prefetch isn't blocked: its fetch just
joins the same cache. The trade is bandwidth — you eagerly pull the whole ~1 MB-gz catalog
even if the user looks up one item — so in practice you'd prefetch the small always-needed
shards (`const-core` is already loaded; `const-special`, `keychains`) eagerly and gate the big
sticker/image ranges behind a signal (a hover, a focused input, a Save-Data check).

## The inventory page (tested, with a real Steam fetch)

`inventory.html` loads a full CS2 inventory and decodes **every item client-side**, reusing
`proto.js` + `resolve.js` + the same shards. It mirrors the server's `GetInventoryData`
stitching (the three parallel `assets` / `descriptions` / `asset_properties` arrays) but does
the cert decode + enrichment in the browser instead of via SteamKit2 + the GC + SQLite.

Measured on a real 281-item inventory: **159 items decoded from their embedded certificates in
~20 ms** — full floats, seeds, patterns, StatTrak counts, stickers, Doppler/Fire&Ice/Kimono
specials — that Steam's own inventory JSON does *not* expose. The other 122 (cases, capsules,
music kits, graffiti, passes) carry no cert and render from Steam's description. **That's 159
Game Coordinator round-trips and 159 database rows the old backend needed — now zero.**

**The CORS finding (the one unavoidable server piece).** A browser *cannot* fetch
`steamcommunity.com/inventory/...` cross-origin: the endpoint sends no
`Access-Control-Allow-Origin`, so `fetch` throws `TypeError: Failed to fetch` (verified live).
A static inventory page therefore needs a CORS unblocker for the fetch. The page tries
**public CORS proxies** by default (zero setup — but they rate-limit, go down, and see the
SteamID you look up, so they're a "for now" convenience, not a real answer). The production
form is your own shim: `build/cors-proxy.mjs` is ~40 lines, forwards one whitelisted
`?steamid=<id64>` shape (no open proxy / SSRF), and stamps CORS headers. **No** Game
Coordinator, **no** database, **no** Steam login, **no** business logic — all of that moved
into the browser. Compare `Controllers/SkinController.cs` (~600 lines) + `SteamService` +
`DatabaseService`. The page also ships a bundled real inventory fixture so the demo runs with
no network at all.

So the scorecard for a fully static app:

| Surface | Static? | Server needed |
|---|---|---|
| Hex-cert single item | **Yes** | none |
| Inventory by SteamID | **Almost** | a dumb ~40-line CORS shim for the fetch only |
| `S/A/D/M` single item | **No** | a logged-in Steam bot (GC token) — real backend |

### Honest caveats

- **First-visit cost** is real but now mitigated: ~24 KB (const-core) + manifest before the
  first card, then range shards — and the idle prefetcher erases it for any session that sits
  for a few seconds. Still a first-paint regression vs one server round-trip on slow mobile.
- **No CRC validation** on decode (we drop the 4 CRC bytes, matching the server). Fine for
  display; a real app taking untrusted links should still treat decoded fields as untrusted
  (the server's `GetFadePercent` bounds-checks attacker-controlled `paintseed`; `resolve.js`
  mirrors that).
- **Inventory thumbnails** use Steam's in-response `icon_url` (comprehensive, free) rather than
  our image shards — the shards matter for the single-item page, which has no Steam description.
- The inventory fetch still needs the shim, and `S/A/D/M` links still need a real backend.

## Files

```
static-prototype/
  build/
    build.mjs         # generates site/data/ shards (balanced) + manifest + sample links
    cors-proxy.mjs    # ~40-line CORS shim for the inventory page's live mode
    static-gzip.mjs   # CDN-like gzip + immutable-cache static server (for fair benchmarking)
  site/
    index.html        # single-item page (#<hex> deep-links; ?preload=eager = proactive preload)
    app.js            # wiring + per-item network panel + interruptible bulk warm + eager mode
    inventory.html    # full inventory page
    inventory.js      # stitch + decode every cert client-side; CORS shim / fixture sources
    inventory.css
    proto.js          # protobuf reader/writer + cert XOR unwrap (browser + Node)
    resolve.js        # client port of ConstDataService (incl. special-pattern logic)
    loader.js         # manifest-driven shard loader: balanced-range search + interruptible bulk warm (pause/resume) + preloadAll
    styles.css
    fixtures/inventory-sample.json   # a real 281-item inventory, so the demo needs no proxy
    data/             # generated (gitignored) — run the build
    samples.json      # generated — real cert links from real catalog ids
  screenshot.png            # single-item page running
  screenshot-inventory.png  # inventory page running
```
