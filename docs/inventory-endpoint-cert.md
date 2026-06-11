# Inventory Endpoint Certificate — Findings & Migration Plan

_Last updated: 2026-06-07. Branch: `inventory_page`._

## TL;DR

As of the March 2026 Steam inspect-URL change, the Steam Community **inventory
endpoint now embeds the full item data for each item** as an "Item Certificate".
Decoding that certificate yields the **same `CEconItemPreviewDataBlock`** we used
to fetch one-at-a-time from the Game Coordinator (GC) — verified field-for-field
against real GC ground truth. This means we can resolve an entire inventory
(floats, seeds, stickers, keychains, StatTrak counts, origin, etc.) **without a
single GC round-trip and without our database**.

We are not flipping the switch yet. This doc records what we learned and the
work to do before we lean on the endpoint as the source of truth.

---

## Background: what changed in March 2026

Old inspect links baked the lookup token directly into the URL:
`...csgo_econ_action_preview S<owner>A<assetid>D<dvalue>`. The `D` value was the
GC's authorization token; we passed `s/a/d/m` to the GC and it returned the item.

The new format replaced the inline `D` with a placeholder, e.g.
`steam://run/730//+csgo_econ_action_preview%20%propid:6%`, and moved the real
payload into a new top-level `asset_properties` array on the inventory response.

This also broke the old hex path until the XOR-deobfuscation fix landed on
`master` ("Add support for xor byte in inspect URLs").

---

## How the inventory endpoint delivers item data (the certificate)

The community inventory response (`/inventory/<steamid>/730/2`) is three parallel
arrays we stitch by id:

```
{
  "assets":           [ { "assetid", "classid", "instanceid" }, ... ],
  "descriptions":     [ { "classid", "instanceid",            // shared per item kind
                          "actions": [ { "link": "...%propid:6%" } ], "tags": [...] }, ... ],
  "asset_properties": [ { "assetid",                           // per-asset, per copy
                          "asset_properties": [
                            { "propertyid": 1, "int_value": "308",  "name": "Pattern Template" },
                            { "propertyid": 2, "float_value": "0.0467...", "name": "Wear Rating" },
                            { "propertyid": 6, "string_value": "<hex>", "name": "Item Certificate" }
                          ] }, ... ]
}
```

The inspect link lives on the **description** (shared by every copy of a skin), so
it cannot carry per-copy data inline. Steam templates it with placeholders:

- `%owner_steamid%` / `%assetid%` — identify the copy (the classic S/A/D form).
- `%propid:N%` — the value of property `N` in **this asset's** `asset_properties`.

`propid 6` ("Item Certificate") is a self-contained, XOR-obfuscated hex payload.
Once substituted into the link, the link is the hex form our existing parser
decodes locally — no GC needed.

### Decode algorithm (`ParseInspectUrl`, `Program.cs`)

```
hex     = <the [0-9A-F]+ after csgo_econ_action_preview>
raw     = FromHexString(hex)
key     = raw[0]
raw[i] ^= key            for all i        # XOR-deobfuscate, key is the first byte
payload = raw[1 .. len-4]                 # drop the key byte + 4-byte trailing CRC
block   = Deserialize<CEconItemPreviewDataBlock>(payload)
```

So the certificate is exactly `[1-byte XOR key][CEconItemPreviewDataBlock][4-byte CRC]`.

### Relevant code paths

- **Cert / inventory path** — `GetInventoryData` resolves `%propid:6%` from
  `asset_properties` (see `propsByAsset`), then `ParseInspectUrl` returns the
  decoded block as `directItem`. Built into the response via `CreateResponse`
  **without any GC call or DB read**.
- **GC path** — `GetSkinData` (`/api`) only calls `steamService.GetItemInfoAsync`
  (which sends `param_s/a/d/m` and reads `response.Body.iteminfo`) when there is
  no `directItem` and no DB hit. On success it persists via
  `SaveItemWithExtrasAsync`.
- Both deserialize the **identical** `CEconItemPreviewDataBlock` type.

---

## Data-parity findings (certificate vs GC)

### Method
1. Matched 4 items already in our DB (genuine past GC writes) against their
   current certificate decode.
2. Harvested real `S…A…D…` inspect links from the web, queried them through the
   GC (ground truth), then fetched the owner's inventory to compare the
   certificate for the same item.
3. Wire-level field census across all 196 items of a test inventory
   (`mattrb` / `76561198261551396`).

### Results — every matched field is identical

| Item | Source of GC truth | Coverage | Result |
|---|---|---|---|
| Souvenir MP5-SD \| Lab Rats | DB (past GC) | paint + 1 sticker | all fields match |
| ★ Talon Knife \| Doppler FN | DB (past GC) | knife, paint/seed | all fields match |
| StatTrak™ MAG-7 \| Insomnia | DB (past GC) | StatTrak | all fields match |
| SSG 08 \| Blue Spruce MW | DB (past GC) | paint/seed | all fields match |
| Silver Breakout Coin | **live web link, independent owner** | all block fields | all fields match |

Plus an **M4A4 | Howl** (live web link) that the owner had since traded away: the
GC returned the old item (float 0.054, 2× sticker 1052) but it is no longer in any
inventory, so it is unmatchable — the expected "stale link" case. It still
confirms the GC returns full paint+seed+sticker data for S-form links.

**5 matched pairs, 0 discrepancies, 2 of them from owners entirely outside our
own data.**

### Results — large-scale re-run against `remote_searches.db` (2026-06-07)

The original run only had 4 usable rows in `searches.db`. Re-running the same
method against the server's `remote_searches.db` (22,421 `searches` rows, 5,003
stickers, 273 keychains) over `mattrb`'s live inventory yielded **18 same-item
GC↔certificate pairs across 8 item categories** — every one a clean match.
Driver: `scripts/cert_gc_compare.py`; full output: `docs/cert-gc-comparison-report.md`.

| Result | Value |
|---|---|
| Matched same-item pairs | 18 |
| **Immutable-field discrepancies** (`defindex, paintindex, rarity, quality, paintseed, paintwear, origin`) | **0** |
| StatTrak presence diffs | 0 |
| Sticker/keychain hard mismatches | 0 (see note) |
| Categories covered | Gloves, Knife, Rifle, Pistol, SMG, Shotgun, Sniper Rifle, Charm/Keychain |

Highlights that close prior gaps:

- **Glove pair matched** (★ Specialist Gloves | Crimson Kimono WW, itemid
  `13849926729`): all immutable fields byte-identical. Closes risk #3's "no
  matched glove" gap.
- **Sticker + keychain parity on both-sides pairs**: StatTrak M4A1-S | Cyrex
  (3 stickers **and** 1 keychain) and Souvenir MP5-SD | Lab Rats (1 sticker)
  match id-for-id and float-for-float — the first time we've validated the
  keychain sub-block against GC ground truth.
- **`paintwear` matches at full uint32 precision** (the raw IEEE-754 bits), not
  just the rounded float, on all 18.
- **StatTrak is richer on the cert side**: presence agrees with the DB bool
  everywhere, and the cert additionally carries the live `killeatervalue` count
  (e.g. Cyrex = 5317, USP-S | Kill Confirmed = 1991) that the DB throws away.

**Note on the one apparent sticker "mismatch":** a StatTrak M4A4 | Evil Daimyo
(itemid `45514468043`) shows 5 stickers in the cert but its DB row (snapshot
2025-08-27) stores **zero**. This is a stale/dropped DB row, **not** an item that
mutated in place — see the itemid-stability finding below.

### Confirmed: applying a sticker mints a NEW itemid (controlled test, 2026-06-07)

An earlier draft of this doc speculated (from the M4A4 above) that an itemid might
persist across sticker addition. **A direct controlled test disproved that** and
confirmed the long-standing manual observation:

| | before sticker | after applying one sticker |
|---|---|---|
| assetid / itemid | `13942577340` | `52177828887` (**new**) |
| paintindex / paintseed | 688 / 185 | 688 / 185 (unchanged) |
| paintwear (bits) | 1043574843 | 1043574843 (unchanged) |
| stickers | none | slot 2, sticker_id 4515 |

The UMP-45 | Exposure went from id `13942577340` (0 stickers) to a brand-new id
`52177828887` (1 sticker); the old id vanished from the inventory entirely, while
the immutable paint/seed/wear carried over. **Applying a sticker recreates the
item under a new id.** (Removal, name tags, and similar mutations almost certainly
behave the same — only the kill count and inventory slot change *without* a new
id.)

This reconciles the M4A4: under "stickering mints a new id," id `45514468043` *is*
the 5-sticker version, so a faithful GC record of that id would carry the 5
stickers. The DB row showing 0 is therefore a data-quality artifact from whenever
that row was first written (the DB is ~10 months of lookups across code versions),
most likely a sticker write that never landed — not in-place mutation.

**Why this is good news for the cache:** because every sticker/name-tag change
produces a *new* itemid, a given itemid's paint/seed/sticker/keychain config is
**immutable for the life of that id**. Cached cert/GC data keyed on `itemid` can
never go stale on those fields — the only things that drift under a fixed itemid
are the StatTrak kill count (we store a bool, not the count) and the inventory
slot (we don't rely on it). Keying the cache on `itemid` is sound.

This item also surfaced a real CS2 change: **stickers can now share a slot**
(stacked/positioned crafts — three entries appeared in slot 3). Our schema
already tolerates this (no unique constraint on `slot`), but see the persistence
note below.

### `itemid == 0` reproduced at scale

40 of 197 decoded certificates carry `itemid == 0` — graffiti (6), music kits
(10), passes (17), standalone stickers (4), tools (3) — all decoding fully
otherwise. Same as the original census: the zero is intrinsic to those defindex
types, not a cert deficiency, and confirms these can never be keyed on `itemid`.

### Field-completeness census (196 items, wire-decoded)

Always present: `defindex, paintindex, rarity, quality, inventory, origin`.
Paint items (125): `paintwear, paintseed`. StatTrak items (22): `killeatervalue`
carries the **real kill count** (richer than our DB, which keeps only a bool).
Stickers/keychains present with full sub-fields. Never populated in this sample
(untestable here): `accountid, customname, dropreason, style, variations,
upgrade_level, petindex, entindex`.

---

## Known gaps & risks

1. **`itemid == 0` for 40 / 196 items.** All music kits, graffiti, standalone
   stickers, charms, medals, and bonus ranks decode fully **except** `itemid`,
   which is 0 on the wire. The DB keys on `itemid PRIMARY KEY`, so these would all
   collide on key 0 if persisted. The reliable per-copy id is the inventory
   `assetid` — which is **not** part of `CEconItemPreviewDataBlock` (it lives in
   the `assets` array). For paint items, `itemid` is always non-zero and equals
   the `assetid`. See the key discussion below.
2. **Untested fields.** Nothing in either test inventory exercised `customname`
   (name tags — a real user-facing field), `style`, `variations`,
   `upgrade_level`, `accountid`, `dropreason`, `entindex`, or `petindex`. We
   cannot yet claim parity for these. (`mattrb` owns no name-tagged item, so
   `customname` remains the highest-value untested gap.)
3. **Glove parity now confirmed; agent still open.** The 2026-06-07 re-run
   matched a glove pair (see above). No **agent** pair matched — 5 agents are in
   the inventory and decode fully, but none of their `itemid`s are in
   `remote_searches.db`, so there's no GC row to diff against. Sourcing one
   agent `S/A/D` link through the GC remains the only way to certify agents.
4. **GC stale-link behavior.** The GC will happily return a `CEconItemPreviewDataBlock`
   for an item that has since left the owner's inventory (the Howl ghost). Fine
   for read-only comparison; just don't assume a GC hit means the item is still
   owned.
5. **The DB stops accumulating.** Only a GC success writes to `searches`. If the
   cert path becomes the source of truth, the DB no longer grows as a cache — which
   may be fine (cert decode is free), but is a behavior change to acknowledge.

---

## Proposed next steps (prioritized)

### 1. Prefer inventory-endpoint cert, fall back to GC _(soon, not yet)_
For the single-item `/api` path and anywhere we currently reach for the GC: if we
can obtain a certificate (the item is in a fetchable inventory, or the link is
already the hex/cert form), decode it locally and skip the GC entirely. Keep the
GC strictly as a fallback for legacy `S/A/D` links that carry no embedded payload.
The inventory page already does this; the work is to generalize it and make the
GC the exception rather than the default.

### 2. Decide on a better item key _(design first, then implement)_
The current `itemid` PK is correct **for paint items**: always non-zero, equals
`assetid`, and — now confirmed — encodes an *immutable config*. Any mutation
(applying/removing a sticker, a name tag, etc.) **mints a new itemid**, so the
data behind a given itemid never changes (only the StatTrak kill count and
inventory slot drift, and we cache neither meaningfully). This is exactly what
makes `itemid` a sound cache key: a modified item is simply a *new* key, never a
stale row. The problem is only the 40 zero-`itemid` non-paint items. Options,
roughly in order of preference:

- **(a) Only persist paint items (`itemid != 0`); don't cache the rest.** Music
  kits/graffiti/stickers/charms/medals are fully and cheaply described by
  `defindex` (+`musicindex` etc.) and the descriptions array — there is no
  expensive float/seed to cache. This sidesteps the collision with no schema
  change and matches what the cache is actually _for_.
- **(b) Key non-zero-`itemid` items on `itemid`, others on `assetid`.** Requires
  threading `assetid` through (we already have it as `asset.assetid` in the
  inventory path; it is the `a` param on the single-item path). Caveat: `assetid`
  changes when an item moves (trade / storage unit), so it is unique-at-a-moment
  but **not stable identity** — acceptable for a cache, wrong for long-term
  dedupe.
- **(c) Composite / surface both.** Store both `itemid` and `assetid`; prefer
  `itemid` when non-zero. More flexible, more schema churn.

Open question to settle: **do we even want a persistent cache anymore?** With free
local cert decode and no GC round-trip, the original reason for `searches` (avoid
slow/ratelimited GC calls) is much weaker. If we keep it, decide whether it is a
GC-result cache (legacy links) or a general item store; the answer drives the key.

### 3. Validate the untested behavior _(partly done — 2026-06-07)_
Targeted checks against real GC ground truth, reusing the web-link method.
Status after the large-scale re-run (`scripts/cert_gc_compare.py`):
- ✅ **Glove matched pair** — done (Specialist Gloves, 0 discrepancies).
- ✅ **Sticker & keychain sub-blocks** — done (Cyrex: 3 stickers + 1 keychain;
  MP5-SD: 1 sticker), id- and float-exact.
- ⬜ **Name tag (`customname`)** — still open; no name-tagged item in reach.
  Highest-value remaining gap (user-facing). Needs a live `S/A/D` link for a
  named item in a public inventory.
- ⬜ **Agent matched pair** — still open; agents decode but none are in the DB.
- ⬜ **`style` / `variations` / `upgrade_level`** — still open; nothing exercised
  them.
- ⬜ **Non-paint `itemid==0` against the GC** — we confirmed the cert returns
  `itemid==0` for these types, but haven't confirmed the GC returns the *same*
  zero (vs. a real id) for an S/A/D link to one. Lower priority.

### 3b. Idempotent sticker/keychain persistence _(✅ done — 2026-06-07)_
The write path was asymmetric: `SaveItemAsync` used `INSERT OR REPLACE`
(idempotent on the `itemid` PK), but `SaveStickersAsync` did a **plain `INSERT`
with no prior delete**, so re-saving an item that already had rows *appended
duplicate* sticker/keychain rows and `GetStickersAsync` returned doubled stickers.
Latent today (`GetSkinData` short-circuits on a DB hit, so nothing re-saves) but
**cert-path persistence (step 1) would make it live**.

**Implemented:** `SaveItemWithExtrasAsync` now does the whole write in a single
transaction — upsert the `searches` row, `DELETE FROM stickers/keychains WHERE
itemid = @itemid`, then re-insert. Clear-and-rewrite (not an upsert) because
`(itemid, slot)` is intentionally **not** unique — stacked stickers share a slot —
so there's no key to upsert on. Covered by
`DatabaseServiceTests.SaveItemWithExtrasAsync_ReSavingSameItem_DoesNotDuplicateExtras`
and `…_SupportsMultipleStickersInSameSlot`.

### 3c. Guard persistence on `itemid != 0` _(✅ done — 2026-06-07)_
All 40 zero-`itemid` types collapse onto PK `0`; with `INSERT OR REPLACE` they
would silently overwrite each other (last write wins), poisoning the cache.
**Implemented:** `SaveItemWithExtrasAsync` now early-returns when
`itemInfo.itemid == 0`, so neither the `searches` row nor its extras are written.
This is keying option **(a)** ("only persist paint items") expressed as a guard —
the cleanest of the three options — and is safe today (the GC path's links are
all non-zero paint items) while pre-protecting the future cert path. Covered by
`DatabaseServiceTests.SaveItemWithExtrasAsync_ShouldSkipZeroItemId`.

### 4. Other next steps worth addressing soon
- **Stacked stickers (multiple per `slot`) — checked, no action needed.**
  Confirmed by controlled test (2026-06-07): adding two stickers to the UMP-45
  produced **two entries in the same `slot` 2** (sticker_ids 4515 + 4516), each
  mutation minting a new itemid. The DB schema and `GetStickersAsync` tolerate it
  (no unique constraint; rows read in order), and `CreateResponse` surfaces
  `stickers`/`keychains` as **ordered arrays where `slot` may repeat**. The
  frontend (`wwwroot/inventory.js`) does **not render applied stickers at all**
  (its only `sticker` reference is a rarity-tier colour label), so nothing renders
  wrong today. ⚠️ **Future caveat:** if a renderer is ever added, it must iterate
  the array positionally and **not** key by `slot`, or stacked crafts collapse.
- **`asset_accessories`** — the raw response also exposes sticker/keychain
  classids under `asset_accessories`; we already get these from the cert, but
  worth a note so a future reader doesn't think we're missing data.
- **Inventory size / pagination** — we fetch `count=2000`. Confirm behavior for
  inventories larger than that (and CS items beyond the first page).
- **Robustness when `propid 6` is absent** — currently we assume every item
  carries a certificate. If a future response omits it, the link stays templated;
  decide whether to fall back to GC or drop the item. (We removed the speculative
  guard for being unexercised — revisit if we ever see a propid-less item.)
- **Rate limiting on `steamcommunity.com`** — both inventory fetches and the
  keyless vanity-URL resolution (`?xml=1`) hit the community site rather than the
  keyed Web API. Watch for intermittent failures under load.

---

## Reproduction notes

- **Test profile:** `mattrb` / SteamID64 `76561198261551396` (196 CS2 items).
- **Endpoints:** `GET /api/inventory?steamid=<vanity-or-id>` (cert path, whole
  inventory); `GET /api?url=<encoded inspect link>` or `GET /api?s=&a=&d=&m=`
  (single item; the `s/a/d` form forces the GC for legacy links).
- **Known-good live web link used for matched validation** (may go stale):
  `S76561198084749846A698323590D7935523998312483177` (Silver Breakout Coin).
- **Driver script:** `scripts/cert_gc_compare.py` (now permanent). Re-decodes the
  certificate hex in Python with the exact `ParseInspectUrl` algorithm and diffs
  against a `searches` DB; field numbers were reflected out of SteamKit2 3.3.1.
  Run: `python3 scripts/cert_gc_compare.py --inventory <SteamID64|file.json>
  --db remote_searches.db --report docs/cert-gc-comparison-report.md`. It needs
  no GC creds and no running app. **Caveat:** it found one real bug *in itself*
  worth remembering — the app URL-decodes the link before the hex regex, and a
  naïve `[ %20]+` class will eat leading `0`/`2` hex digits and corrupt the
  decode; decode `%20`→space first, then match `preview ([0-9A-F]+)`.
- **Report:** latest output committed at `docs/cert-gc-comparison-report.md`
  (+ machine-readable `docs/cert-gc-comparison-data.json`).
- **DB note:** `searches.db` is gitignored; `remote_searches.db` is a server copy
  (also large — keep it out of commits). Forcing GC lookups writes rows
  (`SaveItemWithExtrasAsync`); clean up test rows if you need a pristine baseline.
