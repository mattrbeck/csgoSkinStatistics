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

> **Model vs measured.** The model got the *shape and ratio* right but **underestimated cold
> absolute latency by ~1.5–1.7×**: measured Fast 4G was 905 ms / 1.64 s vs modeled ~530 / ~950.
> The gap is real-world serialization the simple model ignores — chiefly the existing app's
> **render-blocking Google Fonts** (two extra third-party origins, each its own DNS+TLS+RTT)
> and per-origin connection setup. The prototype uses system fonts (no such tax) but still
> trails because of `const-core` + the deeper round-trip chain. **Warm** numbers matched the
> model almost exactly (measured 177 / 580 ms vs modeled ~170 / ~570 ms).

### 1. Single item **with stickers**, cold (first-ever visit) — **measured**

Real stopwatch, throttled Chrome, fresh isolated cache per run, same real cert (UMP-45 |
Exposure, 2 stickers), time from navigation start to the rendered card:

| Profile | Existing | Prototype | Ratio |
|---|---|---|---|
| Broadband (modeled) | ~90 ms | ~190 ms | ~2× |
| Fast 4G | **905 ms** | **1.64 s** | 1.8× |
| Slow 4G | **2.64 s** | **6.18 s** | 2.3× |
| Slow 3G | **9.11 s** | **21.9 s** | **2.4×** |

The prototype's 24 KB `const-core` + the manifest→core→shard round-trip chain lose to one
enriched server response. Confirmed the chain empirically — the prototype fetched exactly
`manifest → const-core → one sticker shard → one image shard` before painting.

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
throttle*, because no request is made at all. The idle prefetcher makes the page warm within
seconds of load, and warm lookups work fully offline.

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
| **Cold first paint** (measured, Slow 4G) | Faster — **2.6 s** (1 enriched round-trip) | Slower — **6.2 s** (catalog + more round-trips) |
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
| **Catalog bandwidth** | Never ships the catalog (per-item only) | Ships up to ~1 MB-gz of catalog to an active user (amortized; prefetched at idle) |
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
- **Hybrid is the real sweet spot:** serve the **single-item hex page fully static** (it's a
  strict win on infra with no downside beyond cold first-paint), keep a **thin server only for
  what genuinely needs it** — `S/A/D/M` (GC bot) and the inventory CORS fetch (a logic-free
  shim). That removes the always-on enrichment/DB tier while preserving the two capabilities a
  pure static app can't have.

## Methodology notes / honesty

- **Scenarios 1 & 2 are real benchmarks.** Both apps were run locally (`dotnet run` on :5050;
  static files on :8777), driven with Chrome DevTools network throttling. Each cold run used a
  **fresh isolated browser context** (guaranteed empty cache) and a **single navigation** to a
  deep-link URL; time-to-card was read from `performance.now()` at the moment the result card
  rendered (the prototype records it directly; the existing app via the `/api` resource-timing
  entry, which lands a few ms before paint). The same real cert was used for both apps. Numbers
  are single runs (±10–15% run-to-run under throttling), enough to show ratios that are 1.8–2.4×.
- **Scenarios 3–5 remain modeled** — same formula, calibrated to the prior doc; treat as
  estimates (±20–30%), and apply the ~1.5–1.7× cold real-world factor scenarios 1–2 revealed.
- The Steam fetch (`+P`) is left as a symbol because it's the same external dependency for both
  and swamps the rest on the inventory path; the public proxy adds a hop and variance the
  existing server-side fetch avoids.
- The existing `/api` numbers, asset sizes, and the 72 KB inventory response are **measured**
  against the app running locally (`dotnet run`, hex path, no GC needed). Prototype sizes and
  decode times are **measured** in the browser.
- "Warm" assumes the idle prefetcher (or a prior visit) has populated the HTTP cache with the
  immutable shards — safe precisely because they're content-hashed.
