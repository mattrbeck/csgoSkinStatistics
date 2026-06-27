# Existing app vs static prototype — in-depth comparison

What the two architectures cost, scenario by scenario, on fast and slow networks, and the
full set of tradeoffs. The single-item cold and warm grids are **real throttled-browser
benchmarks** (both apps run locally, DevTools network throttling, fresh cache per run); the
inventory grids are **modeled** from measured transfer sizes + profiles calibrated against the
real Chrome-throttled runs in `docs/client-side-cert-decode-findings.md` (same app, same data).

## The two architectures

| | Existing app | Static prototype |
|---|---|---|
| Hosting | Always-on ASP.NET server | Static files on a CDN |
| Cert decode | Server (SteamKit2 + protobuf-net) | Browser (`proto.js`) |
| Enrichment (names, special, stickers) | Server (`ConstDataService`, ~600-line `SkinController`) | Browser (`resolve.js` + ranged shards) |
| Catalog data | Lives on the server, never shipped | Sharded, lazy-loaded to the browser |
| `S/A/D/M` links | Logged-in Steam **Game Coordinator** bot | **Not possible** (no bot) |
| Inventory fetch | Server proxies Steam | Browser via CORS shim / public proxy |
| Per-item DB | SQLite cache of GC results | None |

## Measured inputs

**Transfer sizes (gzipped, as served):**

| Existing app | gz | | Static prototype | gz |
|---|---|---|---|---|
| item page assets (html+post+styles+decals) | **~17 KB** | | item page assets (html+4 modules+css) | **~12 KB** |
| `/api?url=` response, item w/ stickers | **0.9 KB** | | `manifest.json` | **0.4 KB** |
| `/api?url=` response, item w/o stickers | **0.7 KB** | | `const-core` (names every item) | **24 KB** |
| inventory page assets | **~42 KB** | | `const-special` (fade/F&I) | **2.3 KB** |
| `/api/inventory` (197 items, enriched) | **72 KB** | | one sticker shard / one image shard | **~30 / ~33 KB** |
| | | | raw Steam inventory via proxy | **~78 KB** |
| | | | inventory cold catalog (const-core + ~10 shards) | **~300 KB** |

**Processing (measured):** existing `/api` hex decode+enrich = **~0.8 ms** server-side (so its
per-lookup cost is essentially *all* network). Prototype decode = **~microseconds** for one
item, **~20–28 ms** to decode all 159 certs of a 281-item inventory in the browser.

**Network profiles** (calibrated from the prior doc's real throttled runs):

| Profile | RTT/request | Bandwidth |
|---|---|---|
| Broadband (est.) | ~25 ms | ~1500 KB/s |
| Fast 4G | ~170 ms | ~980 KB/s |
| Slow 4G / Fast 3G | ~567 ms | ~175 KB/s |
| Slow 3G | ~2033 ms | ~49 KB/s |

Model: `time ≈ (sequential round-trips × RTT) + (transferred KB ÷ bandwidth)`. The number of
**sequential** round-trips is the thing the two architectures differ on most:

- Existing item lookup: HTML → assets → `/api` = **3 RTT cold, 1 RTT warm**.
- Prototype item lookup: HTML → modules → manifest → const-core → shard(s) = **5 RTT cold, 0 RTT warm** (decode is local once the catalog is cached).

## Scenario grids (time to first rendered item)

Scenarios 1 and 2 are **real throttled-browser measurements** (method below); 3–5 are
**modeled** (the inventory path is dominated by the external Steam fetch, which is too noisy to
benchmark cleanly through a public proxy).

> **Fairness fix — gzip.** Both apps are now served **gzipped** (the existing app via ASP.NET
> ResponseCompression; the prototype via a small CDN-like gzip server, `build/static-gzip.mjs`).
> An earlier pass served the prototype through Python's `http.server`, which sends everything
> **uncompressed** — unfairly inflating its shards ~4× (const-core 87 KB raw vs 24 KB gz). The
> numbers below are the corrected gzip-served ones; gzip cut the prototype's cold times ~20–25%
> (Fast 4G 1.64 s → 1.32 s, Slow 3G 21.9 s → 16.4 s).
>
> **Model vs measured.** With gzip, the model is close: measured Fast 4G 905 ms / 1.32 s vs
> modeled ~530 / ~950 — still ~1.4× high on the cold absolute (real serialization the model
> omits: the existing app's **render-blocking Google Fonts**, per-origin setup). **Warm**
> matched the model almost exactly (measured 177 / 580 ms vs modeled ~170 / ~570 ms).

### 1. Single item **with stickers**, cold (first-ever visit) — **measured (gzip-served)**

Real stopwatch, throttled Chrome, fresh isolated cache per run, same real cert (UMP-45 |
Exposure, 2 stickers), time from navigation start to the rendered card:

| Profile | Existing | Prototype (lazy) | Ratio |
|---|---|---|---|
| Broadband (modeled) | ~90 ms | ~150 ms | ~1.7× |
| Fast 4G | **905 ms** | **1.32 s** | 1.5× |
| Slow 4G | **2.64 s** | **4.62 s** | 1.8× |
| Slow 3G | **9.11 s** | **16.4 s** | **1.8×** |

The prototype's 24 KB `const-core` + the manifest→core→shard round-trip chain lose to one
enriched server response. Confirmed the chain empirically — the prototype fetched exactly
`manifest → const-core → one sticker shard → one image shard` (≈90 KB gz) before painting.

### 2. Single item, **warm** (any later lookup in the session) — **measured**

A second, different item looked up in an already-loaded page (prototype: catalog cached;
existing: page cached, but the item still needs `/api`):

| Profile | Existing | Prototype | Winner |
|---|---|---|---|
| Fast 4G | **177 ms** | **~6 ms** | prototype |
| Slow 4G | **580 ms** | **5.8 ms** | prototype |
| Slow 3G | ~2 s (one round-trip) | **~6 ms** | **prototype (no network at all)** |

This is the inversion, and it's stark: the existing app pays a full `/api` round-trip on
**every** lookup forever (and it scales with RTT: 177 ms → 580 ms → ~2 s), while the prototype
resolves each new item with **zero network** — the 5.8 ms figure was measured *under Slow 4G
throttle*, because no request is made at all. The background warm makes the page warm within
seconds of load, and warm lookups work fully offline.

### 2c. Prototype strategies: **lazy** vs **eager (proactive) full preload** — measured

There are three ways the static prototype can handle its catalog. All three end up "warm" (item
#2+ resolve in ~6 ms offline); they differ in **when** the catalog is downloaded and whether the
first paint blocks on it:

- **Lazy**: fetch only the shards each item needs, on demand.
- **Lazy + interruptible bulk warm** (the default): lazy *plus* warming the rest in the
  background — one shard at a time, at low priority, **pausing whenever a query needs the pipe**
  (see 2d). Item #1 is quick and the cache fills without ever slowing a lookup.
- **Eager** (`?preload=eager`): download the **entire** catalog (834 KB gz, 32 shards) up front
  and block the first paint until it's all in cache. "Spinner for a beat, then everything —
  including item #1 — is instant and offline."

Measured **time to the first item** (the eager column is the full proactive download + decode):

| Profile | Existing | Lazy (1st item) | **Eager (1st item)** | Catalog size |
|---|---|---|---|---|
| localhost (≈ broadband floor) | — | — | **89 ms** | 834 KB gz |
| Broadband (real, est.) | ~90 ms | ~150 ms | **~1 s** | |
| Fast 4G | 905 ms | 1.32 s | **2.24 s** | |
| Slow 4G | 2.64 s | 4.62 s | **8.58 s** | |
| Slow 3G | 9.11 s | 16.4 s | **30.5 s** | |

After that first paint, **every** item costs ~6 ms for both lazy and eager.

**So is the "1–2 s proactive download" worth it?** It only *is* 1–2 s on a fast link: ~1 s on
real broadband, ~2.2 s on Fast 4G. On Slow 4G it's ~8.6 s and on Slow 3G ~30 s of blank screen,
because you're now blocking first paint on 834 KB instead of the ~90 KB one item needs. And the
payoff it buys — every later item instant and offline — is **already delivered by lazy +
interruptible warm**, which paints item #1 in 1.3 s (Fast 4G) and backfills the same catalog in
the background without ever blocking. The one thing eager adds is a *guarantee*: no item ever stalls,
even if the user races ahead during the idle-prefetch window. That's a niche worth paying for
only on fast connections (gate it behind a `navigator.connection`/Save-Data check), or for an
explicitly offline-first "download for offline" button. As a default it's strictly worse than
lazy + interruptible warm on first-paint, and dramatically so on slow networks.

### 2d. **Interruptible** bulk warm — a query mid-warm preempts the download — measured

The catch with any background warm is **bandwidth contention**: if the user queries *while* the
catalog is still downloading, the query's shards compete with the warm. The fix is to make the
warm yield. Two implementations, benchmarked:

- **Flood** (`?bulk=flood`, the old idle-prefetch): kicks off all 32 shards at once, saturating
  the connection pool. A query that arrives mid-warm queues **behind** the in-flight warm.
- **Interruptible** (now the default): the warm fetches **one shard at a time at low priority**,
  and `handle()` calls `pauseBulk()` the instant a query starts — so the query's shards download
  over a near-empty pipe — then `resumeBulk()` hands the pipe back. This is exactly the
  user-proposed flow: *load → bulk starts → query → bulk pauses → query resolves → bulk resumes.*

Measured **query-to-card time for a 2-sticker item fired while the catalog was still warming**
(Slow 4G, fresh cache; the query needed ~3 uncached shards):

| Query fires at | Flood (old) | **Interruptible (new)** | Speedup |
|---|---|---|---|
| 700 ms in (pool fully saturated) | 2.77 s | **1.53 s** | **1.8×** |
| 1500 ms in | 1.92 s | **1.53 s** | 1.25× |

The headline isn't just that interruptible is faster — it's that its query time is **flat at
~1.53 s regardless of when the query fires**, because the query always gets a clear pipe. The
flood's query time **degrades with contention** (2.77 s when the warm is busiest). Interruptible
keeps the page's core promise — *fast lookups* — intact while still warming toward offline in the
background. It's the default; `?bulk=flood` is kept only to reproduce this comparison.

### 3. Single item **without stickers**, cold (modeled)

| Profile | Existing | Prototype |
|---|---|---|
| Fast 4G | ~530 ms | ~920 ms |
| Slow 4G | ~1.8 s | ~3.2 s |
| Slow 3G | ~6.5 s | ~11.6 s |

Modeled, but the takeaway is robust: dropping the sticker shard saves one ~30 KB shard, and
scenario 1 showed the **image shard + `const-core` dominate** anyway — so a no-sticker item is
only marginally cheaper than a with-sticker one. (Apply the same ~1.5–1.7× real-world factor as
scenario 1.) Warm: identical to scenario 2 — prototype ~6 ms, existing a full round-trip.

### 4. Inventory, cold (first visit) — modeled. `+P` = the Steam fetch (~0.5–2 s, similar for both)

| Profile | Existing `+P_server` | Prototype `+P_proxy` |
|---|---|---|
| Broadband | ~150 ms | ~430 ms |
| Fast 4G | ~630 ms | ~1.4 s |
| Slow 4G | ~2.4 s | ~5.8 s |
| Slow 3G | ~8.4 s | ~20.7 s |

The server returns a **trimmed, enriched 72 KB** in one shot; the prototype must pull the **raw
78 KB inventory plus ~300 KB of catalog shards** cold, over more round-trips. On slow networks
that's brutal.

### 5. Inventory, warm (page + catalog cached) — modeled. `+P` = Steam fetch

| Profile | Existing `+P_server` | Prototype `+P_proxy` |
|---|---|---|
| Broadband | ~75 ms | ~105 ms |
| Fast 4G | ~240 ms | ~280 ms |
| Slow 4G | ~980 ms | ~1.0 s |
| Slow 3G | ~3.5 s | ~3.7 s |

Warm, they converge: both move ~72–78 KB once. The prototype adds ~28 ms local decode but
removes the server's per-request CPU; the existing app keeps doing the work server-side. Both
are dominated by the Steam fetch itself.

## The full tradeoff matrix

| Dimension | Existing app | Static prototype |
|---|---|---|
| **Cold first paint** (measured gz, Slow 4G) | Faster — **2.6 s** (1 enriched round-trip) | Slower — **4.6 s** lazy / **8.6 s** eager (catalog + more round-trips) |
| **Warm/repeat lookup** (measured, Slow 4G) | **580 ms** round-trip every time | **5.8 ms**, zero-network, offline |
| **Inventory cold** | Lighter wire (server trims to 72 KB) | Heavier (raw inv + ~300 KB catalog) |
| **Inventory warm** | ~same | ~same (slightly heavier wire, no server CPU) |
| **Infra** | Always-on server, Steam-bot login, SQLite, GC connection | Static CDN (+ ~40-line CORS shim for inventory only) |
| **Marginal cost / scaling** | CPU + GC + DB per request; bot rate-limits cap throughput | Near-zero per request; CDN scales infinitely |
| **`S/A/D/M` links** | Supported (GC bot) | **Unsupported** — needs a real backend |
| **Steam-bot dependency** | Bans / maintenance / rate-limits are an operational risk | None for hex/inventory; absent entirely |
| **Privacy** | SteamID/links stay server-side | Single-item: **fully private** (nothing leaves the browser). Inventory via *public* proxy: leaks SteamID to a third party (own shim fixes this) |
| **Update / freshness** | Redeploy server; const.json reloads on restart; instantly consistent | Content-hashed shards + manifest swap; immutable, cache-forever; *eventually* consistent within the manifest TTL |
| **Data version skew** | Impossible (client+data in lockstep) | Possible; managed by the manifest `version` (client can detect + soft-reload) |
| **Offline / flaky network** | Unusable offline; every lookup needs the server | Warm catalog ⇒ lookups work offline; only the inventory fetch needs the network |
| **Failure modes** | Server down = full outage; GC down = `S/A/D/M` fails; single place to fix | CDN outages rare; a bad shard is caught by hashing + one-file manifest rollback; public inventory proxy is flaky/rate-limited |
| **Catalog bandwidth** | Never ships the catalog (per-item only) | Ships up to ~1 MB-gz of catalog to an active user (amortized; warmed interruptibly in the background) |
| **Code / maintenance** | ~600-line controller + services + SteamKit + DB + GC mgmt | proto + resolve + loader + build; no runtime server logic except the shim |
| **Security posture** | Validates/bounds server-side | Must treat decoded fields as untrusted in-browser (names rendered via `textContent`; `paintseed` bounds-checked in `resolve.js`, mirroring the server) |
| **Trust** | Users trust the server's numbers | Decode is inspectable client-side; reproducible from the public cert |

## When each wins

- **Existing app wins** for: one-off/cold lookups (paste a link, read it, leave), `S/A/D/M`
  links (only it can), inventory on slow mobile cold, and keeping SteamIDs private without
  running your own proxy.
- **Static prototype wins** for: repeat/session use (browse many items — each is then free and
  offline), operational simplicity and cost (no server, no Steam bot, no DB to babysit),
  infinite cheap scaling, and privacy on the single-item page (nothing leaves the browser).
- **Catalog strategy:** default to **lazy + interruptible bulk warm** — fast first item, instant
  everything after, and a mid-warm query preempts the download (flat ~1.5 s vs the flood's 2.8 s
  on Slow 4G). Reserve **eager full preload** (`?preload=eager`, ~1 s on broadband but ~30 s on
  Slow 3G) for fast connections or an explicit "make available offline" action; never as default.
- **Hybrid is the real sweet spot:** serve the **single-item hex page fully static** (it's a
  strict win on infra with no downside beyond cold first-paint), keep a **thin server only for
  what genuinely needs it** — `S/A/D/M` (GC bot) and the inventory CORS fetch (a logic-free
  shim). That removes the always-on enrichment/DB tier while preserving the two capabilities a
  pure static app can't have.

## Methodology notes / honesty

- **Scenarios 1, 2, 2c, 2d are real benchmarks.** Both apps run locally and **gzip-served** — the
  existing app via ASP.NET ResponseCompression on :5050, the prototype via `build/static-gzip.mjs`
  on :8780 (a small CDN-like server with gzip + immutable caching for hashed shards). Driven with
  Chrome DevTools network throttling. Each cold run used a **fresh isolated browser context**
  (guaranteed empty cache) and a **single navigation** to a deep-link URL; time-to-card was read
  from `performance.now()` at the moment the card rendered (the prototype records it directly; the
  existing app via the `/api` resource-timing entry, which lands a few ms before paint). Same real
  cert for both apps. Single runs (±10–15% under throttling), enough for the 1.5–1.8× ratios.
- **Scenarios 3–5 remain modeled** — same formula, calibrated to the prior doc; treat as
  estimates (±20–30%).
- The Steam fetch (`+P`) is left as a symbol because it's the same external dependency for both
  and swamps the rest on the inventory path; the public proxy adds a hop and variance the
  existing server-side fetch avoids.
- The existing `/api` numbers, asset sizes, and the 72 KB inventory response are **measured**
  against the app running locally (`dotnet run`, hex path, no GC needed). Prototype sizes and
  decode times are **measured** in the browser.
- "Warm" assumes the background warm (or a prior visit) has populated the HTTP cache with the
  immutable shards — safe precisely because they're content-hashed.
