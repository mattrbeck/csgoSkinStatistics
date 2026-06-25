# Client-side cert decoding — feasibility findings

_Investigated 2026-06-25. Prototype built, measured, and removed; this records what we learned._

## TL;DR

Decoding the inspect-link Item Certificate **in the browser is feasible and trivial** (a ~60-line,
dependency-free protobuf reader, validated field-for-field against the server's `protobuf-net`).
But it is **not worth adopting** for our workload:

- The decode itself is microseconds. The real cost is the **enrichment data** (id → name / image),
  which must either be shipped to the client (a large download) or fetched per lookup (a round-trip —
  the very thing client-side decode was meant to remove).
- Measured across throttled networks, **shipping the catalogs is 7–11× slower than today's single
  enriched server round-trip** for a first/one-off lookup.
- A batched `/api/resolve` endpoint would land on the same cost as today's `/api?url=` call, so it's
  no improvement.
- Net: **keep cert decode + enrichment server-side.** Client-side decode is a repeat-use
  optimization, and neither the item page (one-off) nor the inventory page (server already bulk-decodes;
  browser can't fetch the inventory anyway) is that workload.

## Feasibility (proven)

A prototype decoded a constructed cert (`hex → XOR key → strip 1-byte key + 4-byte CRC → protobuf`)
with a hand-rolled reader (varint / fixed32 / length-delimited) and matched the server's
`protobuf-net` output field-for-field: `itemid, defindex, paintindex, rarity, quality, paintwear,
paintseed, origin, killeatervalue`, nested `stickers` (`slot, sticker_id, wear`), and the
`paintwear` uint→float reinterpret. **No protobuf library is needed** — it's one stable message type
(`CEconItemPreviewDataBlock`). The CRC is dropped, not verified, so test certs can use arbitrary CRC
bytes.

## The decode-vs-enrichment split

| Data | Source | Size (wire) | Notes |
|---|---|---|---|
| Scalars + **special patterns** (fade %, Doppler phase, fire & ice, kimono) | `const.json` | **37 KB** | Small. Patterns/fade_order/fireice all live here. |
| Skin image (def_paint → CDN url) | `skin-images.json` | **182 KB** | Opaque Steam hash; can't be derived, must be looked up. |
| Sticker/keychain name + image | `stickers.json` | **835 KB** | The heavy one. Only needed when an item has decals. |

Image and sticker URLs are opaque Steam CDN hashes the cert does not carry, so they always require a
lookup (ship the map or call the server).

## Measurements (Chrome throttling presets)

Wire sizes above are server-compressed. **Parse time was negligible everywhere (≤7 ms even for
stickers) — Tier-2 latency is entirely download time.**

| Network | const.json | skin-images | stickers | **Tier-2** (img+stickers ∥) | **Server round-trip** (today) |
|---|---|---|---|---|---|
| No throttle (localhost) | 7 ms | 6 ms | 17 ms | 19 ms | 40 ms |
| Fast 4G | 208 ms | 356 ms | 1019 ms | **1203 ms** | **172 ms** |
| Slow 4G | 779 ms | 1609 ms | 5322 ms | **6368 ms** | **568 ms** |
| Fast 3G | 782 ms | 1609 ms | 5322 ms | **6358 ms** | **569 ms** |
| Slow 3G | 2784 ms | 5734 ms | 19113 ms | **22857 ms** | **2033 ms** |

(Chrome aliases "Slow 4G" and "Fast 3G" to the same ~1.6 Mbps profile, hence identical rows.)

## Why the options don't pay off

- **Ship the full maps** → 7× slower than a round-trip on Fast 4G, ~11× on 3G, 23 s on Slow 3G for a
  first visit. Only wins on the 2nd+ lookup once cached.
- **Per-id endpoints** → tiny payloads but N+1 round-trips per item (one per sticker); worse than
  today's single call.
- **Batched `/api/resolve`** → one small round-trip ≈ the "server round-trip" column above, i.e.
  identical to today's single enriched `/api?url=`. No gain.
- **Ship only `const.json`** (for instant scalars + special) → still a first-visit regression on slow
  links (2.8 s on Slow 3G delays the "instant" render past today's 2 s full-card response). Wins only
  on repeat lookups.

## When client-side decode *would* win

Only for **repeated lookups in a session**, where the catalog download amortizes and subsequent
lookups drop to ~0 ms (and work offline). Our two surfaces aren't that:

- **Item page** — typically one-off (paste a link, read the result).
- **Inventory page** — many items, but the server already bulk-decodes them in the `/inventory`
  response, and the browser can't fetch the Steam inventory directly (CORS; the server proxies it).

## Incidental win worth keeping

The catalogs are served with `ResponseCompression` at gzip **"Fastest"** (stickers 835 KB vs ~653 KB
at a better level). Switching to **Brotli / Optimal** would cut ~20–35 % off every static transfer for
free — worth doing regardless of this decision.

## If we ever revisit

The prototype (removed) was `wwwroot/cert-prototype.{html,js}`: a hand-rolled protobuf reader, eager
`const.json` for instant scalar + special render, and lazy `skin-images.json` / `stickers.json` (only
when an item had decals) back-filling the image and sticker chips via the existing placeholder
machinery (`?` chips + image-placeholder glyph). Reconstruct from this doc plus
`docs/inventory-endpoint-cert.md` (decode algorithm) if needed.
