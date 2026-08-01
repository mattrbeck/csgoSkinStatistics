# Browser-side inspect-cert decoding (prototype)

_Built 2026-08-01 on branch `client-cert-decode`. This is an exploratory prototype, not a shipped
feature. Read "What is not finished" at the bottom before relying on any of it._

## What it does

A modern CS2 inspect link of the hex form carries the item's own data inside the URL:

```
steam://run/730//+csgo_econ_action_preview 0010BCB1AB...DEADBEEF
                                           ^ [1 XOR key byte][CEconItemPreviewDataBlock][4 CRC bytes]
```

So the browser can read it without asking anyone. When you paste one:

1. `post.js` sees the input looks like a hex cert and dynamically `import()`s `wwwroot/cert-decode.js`.
2. That module hex-decodes, XOR-deobfuscates with the first byte, drops the key byte and the 4-byte
   checksum, and hand-parses the protobuf body.
3. The card renders immediately from that: float, wear band, pattern seed, item id, StatTrak badge +
   live kill count, and one chip per applied sticker/charm (by id).
4. The `/api` request goes out in parallel exactly as before. When it answers, its values overwrite
   everything.
5. If it never answers, the locally decoded data stays on screen and the server-only slots say
   `unavailable` instead of reverting to an error card.

Nothing else changed: `/api` is still called for every lookup, the server is untouched.

## The decoder

`wwwroot/cert-decode.js` — 398 lines, ~270 of them code, 16.7 KB raw / **6.4 KB gzipped**. Hand-rolled
varint / fixed32 / length-delimited reader for one message type. No dependency, no build step.

Why not a protobuf library: the CSP is `script-src 'self'` / `connect-src 'self'`, so a CDN-hosted
library is blocked outright, and there is no bundler in this repo (`wwwroot/*.js` are served as-is).
A general library would also be many times the size for one message with ~25 fields. The trade is
that the field numbers are pinned by hand — see the caveats.

The reader **keeps unknown fields it needs and skips the rest by wire type**. That matters twice:

- A Sticker Slab's sealed sticker id rides in `Sticker` field 12, which SteamKit2 does not model (the
  server reads it out of protobuf-net's extension data — `Services/StickerSlab.cs`). A reader that
  discarded unknown fields would silently blank every slab.
- Skipping by wire type (not by guessing) is what lets a field Valve adds later pass through without
  desynchronising the stream and returning plausible garbage.

Wire types 3/4 (deprecated groups) are rejected outright rather than guessed at.

## Proof of equivalence with the .NET decoder

`tests/fixtures/cert-fixtures.json` is **not hand-written**:

- each `hex` was produced by `protobuf-net` serialising a real SteamKit2 3.3.1
  `CEconItemPreviewDataBlock` (same framing as `csgoSkinStatistics.Tests/InspectCert.cs`, extended so
  some fixtures carry a non-zero XOR key);
- each `server` block is the verbatim JSON the **running application** returned from
  `GET /api?url=<that link>` — i.e. the output of the real `InspectLink.ParseInspectUrl` +
  `ItemResponse.CreateResponse`. Only the live `price` object and the long Steam CDN image URLs were
  stripped.

`tests/cert-decode.test.js` then asserts, for all 9 fixtures, that the JS decoder produces the same
value for every field it claims to produce. The same bytes, two independent decoders, identical
results.

| Fixture | What it pins down |
|---|---|
| `plain_skin` | ordinary paint item, legacy `0x00` key (XOR is a no-op) |
| `plain_skin_xor` | byte-for-byte different hex, key `0x5A`, must decode identically |
| `plain_skin_xor_ff` | same, key `0xFF` |
| `stattrak` | `killeatervalue` present, live kill count 5317 |
| `stattrak_zero_kills` | **presence vs zero**: StatTrak with 0 kills must stay `stattrak: true` |
| `stickers` | 3 stickers, two stacked in one slot, optional sub-fields set, one bare |
| `keychain_slab` | a charm + a Sticker Slab (unmodelled field 12 → `wrapped_sticker: 4352`) |
| `zero_itemid` | music kit: `itemid 0`, no paint |
| `customname` | length-delimited attacker-controlled string in field 11 |

Fields proven equal: `itemid, defindex, paintindex, rarity, quality, paintwear, paintseed, inventory,
origin, stattrak, stattrak_kills, paintwear_float, souvenir, is_knife_or_glove, wear_name`, and per
decal `sticker_id, wear, rotation, offset_x, offset_y, pattern, slab, wrapped_sticker`.

`paintwear_float` matches to the full double: the server does
`(double)BitConverter.UInt32BitsToSingle(bits)` and the client does the same reinterpret through a
`DataView`, so both land on e.g. `0.17547695338726044` exactly.

## The field split

I re-derived this from the code rather than taking the brief's list, and **four fields turned out to
be on the wrong side of the line** — they need no catalog at all:

| Field | Where the brief put it | Where it actually belongs | Why |
|---|---|---|---|
| `paintwear_float` | server | **client** | pure IEEE-754 uint32→float32 reinterpret |
| `wear_name` | server | **client** | thresholds are hardcoded in `ConstDataService.GetWearFromFloat`, not in `const.json` |
| `souvenir` | server | **client** | `quality == 12` |
| `is_knife_or_glove` | server | **client** | `defindex` in 500–599, or ≥ 5000 |

Full split as implemented:

**Client (from the cert alone)** — `itemid`, `defindex`, `paintindex`, `rarity` (the number),
`quality` (the number), `paintwear` (the raw bits), `paintseed`, `inventory`, `origin` (the number),
`stattrak`, `stattrak_kills`, `paintwear_float`, `souvenir`, `is_knife_or_glove`, `wear_name`,
`stickers[].{sticker_id, wear, rotation, offset_x, offset_y}`,
`keychains[].{sticker_id, wear, offset_x, offset_y, pattern, slab, wrapped_sticker}`.

**Server only** — `weapon`, `skin`, `market_hash_name`, `special` (fade %, Doppler phase, blue gem,
fire & ice, kimono tier), `image`, `rarity_name`, `quality_name`, `origin_name`, every decal's `name`
and `image`, and `price`. These need `const.json` (112 KB), `skin-images.json` (551 KB),
`stickers.json` (2.85 MB), `fade.json` (160 KB), `blue-gem.json` (211 KB) or the live Skinport feed —
~3.9 MB, so shipping them is off the table. `docs/client-side-cert-decode-findings.md` measured that
in June and found it 7–11× slower than one enriched round trip.

Two things worth flagging about that list:

- `rarity_name` / `quality_name` / `origin_name` are *small* enumerations (a few dozen strings between
  them, ~1 KB). They are server-only here purely to keep one source of truth, not because of size. If
  the flash of a pending rarity colour on the card's left edge is annoying, inlining just those three
  tables is the cheapest fix available — at the cost of a second copy of the mapping.
- `customname` (name tags) is in the cert and the decoder reads it, but **`CreateResponse` has no
  `customname` field**, so the server never returns it and the card never shows it. The prototype
  deliberately keeps it out of the rendered projection: rendering a field the server cannot
  corroborate would put unauthenticated attacker-controlled text on screen with nothing to reconcile
  it against. (It is also the highest-value untested gap noted in `docs/inventory-endpoint-cert.md`.)

## Reconciliation policy

**The server wins every field, unconditionally, the moment it answers.** `populateCard` simply
overwrites the optimistic render. Rationale: the server used the authoritative parser *and* has the
catalogs, and the client's data is unauthenticated — there is no case where the client should win.

On top of that, one check runs:

- `diffAgainstServer` compares the **immutable** raw fields — `itemid, defindex, paintindex, rarity,
  quality, paintwear, paintseed, origin`. An itemid encodes an immutable config (any mutation mints a
  new itemid; see `docs/inventory-endpoint-cert.md`), so these must agree. If they do not, one of the
  two decoders is wrong: the card gets `data-cert-mismatch`, a visible note naming the fields, and a
  `console.warn`. The server's values are still what is displayed.
- **`stattrak_kills` is excluded on purpose.** It legitimately drifts under a fixed itemid, and a
  cached server row can trail the live count. The server's value is shown; a difference is not an
  error. (Arguably the *cert* is fresher here — see the open questions.)
- **`inventory` is excluded too**, for the same reason: it is the storage slot and moves.

Failure paths:

| Situation | What happens |
|---|---|
| Server answers normally | server values overwrite everything; loading states clear |
| Server answers, immutable field differs | server values shown + explicit mismatch note |
| `fetch` rejects (offline, timeout, DNS) | local render **kept**; pending slots → `unavailable`; submessage "Offline — showing only what the inspect link itself contains."; the card is dropped from the session cache so a re-search retries |
| Server returns `{error}` but we decoded locally | local render kept, server's message shown alongside |
| Server fails and there was **no** local decode | unchanged: the ordinary error card |

## Loading states

The rule the prototype enforces: **a field we have not looked up must not look like a field we looked
up and found empty.** The template's `-` and `0` placeholders mean "no value", so they are never used
for a pending field.

- `.field-pending` — a shimmering bar sized (`--pending-w`) to the value it will hold, with
  `aria-label="Loading"` and a `title`. Used for the item name, rarity label, quality and origin.
- `.card-image-frame.media-pending` — the frame shimmers and the "no image" glyph is **hidden**,
  because that glyph is the real answer for medals, music kits and vanilla knives.
- `.special-chip.pending` — a skin gets a pending rare-pattern chip. Without it, a 98 % Fade would
  read as an ordinary pattern for the whole wait, since absence of a chip is itself a statement.
- `.sticker-chip.pending` — decal chips render immediately from the ids (labelled `Sticker #4515`)
  and pulse until their names/images arrive. This is distinct from the existing `?` placeholder chip,
  which means "the catalog genuinely does not know this id".
- `.field-unavailable` — after a failed request: the italic word `unavailable`, not a dash.
- All of it collapses to a static appearance under `prefers-reduced-motion`.

Fields the client *does* know are filled in straight away, so `wear_name` shows "Field-Tested" from
the first frame rather than shimmering.

## Trust caveat

**Client-decoded data is unauthenticated.** The trailing 4 bytes are a CRC32 checksum, not a MAC — the
decoder does not even read them, and neither does the server. Anyone can craft an inspect link
carrying any values they like. Therefore, in this prototype:

- it is **never POSTed** anywhere;
- it is **never persisted** — in particular `addRecentItem` (localStorage) and `card._iteminfo` are
  only ever fed the *server's* response, and a test asserts `localStorage` stays empty across the
  whole optimistic + offline path;
- it never touches the inventory `sessionStorage` cache (this code path does not go near
  `inventory.js`);
- every value is written with `textContent` / DOM nodes, never `innerHTML`.

It is an optimistic local render and nothing more.

## Lazy loading

`cert-decode.js` appears **nowhere in `index.html`** — grep confirms 0 references. It is fetched by
`import("./cert-decode.js")` inside `post.js`, memoised, and only when `isHexCertKey(key)` passes. So
an s/a/d link, a profile lookup, or simply visiting the page never downloads it.

`isHexCertKey` deliberately duplicates the module's own `looksLikeHexCert` — you cannot ask the module
before you have loaded it. A test asserts the two agree on a spread of inputs so they cannot drift.

A failed import resolves to `null` and the ordinary server-backed flow continues untouched.

**Verification status:** the module is served as `/cert-decode.js` with `Content-Type:
text/javascript` under the real CSP header, parses as native ESM under Node (`node --input-type=module`
decodes a fixture identically), and `script-src 'self'` permits same-origin module imports — no
`unsafe-eval` is involved, the module uses no `eval`/`Function`. **But I could not run it in a real
browser**: headless Chrome will not start in this sandbox and the DevTools-MCP browser was held by
another session. See the caveats.

## What is not finished, and what I am unsure about

Honest list. Several of these are more important than the feature.

1. **No real browser run.** Everything above is verified by jest (jsdom), Node's ESM loader, and curl
   against the running server. The dynamic `import()` under the live CSP has *not* been executed in
   Chrome. I believe it works — `script-src 'self'` covers module imports and the MIME type is right —
   but that is reasoning, not evidence. **This is the first thing to check by hand.**
2. **No real captured Steam cert is tested.** Every fixture was minted by `protobuf-net` from a
   constructed item. If Steam's real encoder ever differs (field ordering, packed encoding, a field we
   have never seen), the fixtures would not catch it. The repo contains no captured cert to test
   against, and `docs/inventory-endpoint-cert.md` references a `scripts/cert_gc_compare.py` that
   **does not exist in the repo or in git history** — so the 18-pair comparison it describes is not
   reproducible from here.
3. **Field numbers are pinned by hand.** They were reflected out of SteamKit2 3.3.1 (via a throwaway
   project outside the repo, since I could not touch `.cs` files) and are now hardcoded in
   `cert-decode.js`. A SteamKit2 bump that renumbers or *models* `wrapped_sticker` would move the
   server without moving the client, and nothing fails loudly. The existing `StickerSlabTests` warn
   about the same bump on the server side; there is no equivalent guard here. A generated-from-schema
   approach, or a build-time check, would be better.
4. **The honest value of this feature is smaller than it sounds.** The fields the client can produce
   are the *numeric* ones. The name, the picture, the rare-pattern label and the price — the things a
   user actually looks at — are all catalog-bound. So the optimistic card is a float, a seed and a
   shimmering name. On localhost the server answers in ~40 ms and you will never see it.
   `docs/client-side-cert-decode-findings.md` reached the same conclusion in June from the other
   direction. It is a real win only on a slow/flaky link, which is exactly the case I could not
   measure here.
5. **The pending rare-pattern chip is a design liability.** Most items have no rare pattern, so for
   most items the chip shimmers and then vanishes. That flicker may be worse than the (misleading)
   alternative of showing nothing. I chose correctness over calm; a human should look at it.
   Similarly the card's left border and rarity colour stay neutral until the server answers, then snap
   to a colour — a visible flash on every lookup.
6. **`stattrak_kills` reconciliation may be backwards.** I excluded it from the mismatch check and let
   the server win. But the cert is decoded from the *link the user just pasted*, while the server may
   answer from a cached DB row that is months old — so the client's count can be the fresher one. I
   did not implement "prefer the higher count" because I am not certain the count is monotonic
   (StatTrak swap tools exist). Worth deciding deliberately.
7. **Sticker `wear` collapses absent to 0.** That is faithful to `MakeStickerDto` (which reads
   `s.wear` raw, without its `ShouldSerialize` check) — but it means a pristine sticker and a sticker
   with no wear field on the wire are indistinguishable, on *both* sides. Faithful, and arguably a
   latent bug in the server DTO; I mirrored it rather than diverging.
8. **`wear_name` for paint-less items is meaningless on both sides.** `paintwear` absent → 0.0 →
   "Factory New", so a music kit's card says "Factory New". The server does exactly the same, and I
   deliberately mirrored it rather than showing `-`, so the value does not change under the user when
   `/api` lands. It is a pre-existing oddity this prototype now faithfully reproduces.
9. **No timeout on the fetch.** "The server is slow" is currently handled only by the optimistic
   render arriving first; there is no `AbortController`, so a request that hangs for 60 s leaves the
   card in its pending state for 60 s rather than degrading to `unavailable`. Adding a timeout is
   probably the single highest-value follow-up.
10. **Only the item page.** The inventory page is untouched. It could not benefit anyway — the server
    already bulk-decodes there and the browser cannot fetch a Steam inventory directly (CORS).
11. **`floatRanges` is a separate static fetch** (`float-ranges.json`, loaded by `inventory.js`). If it
    has not arrived, the optimistic float bar draws without its dimmed unreachable ends. Minor, but it
    means the "instant" render still has a static-asset dependency.
12. **The uppercase-hex subtlety.** The server's regex is `csgo_econ_action_preview ([0-9A-F]+)` —
    uppercase only. `post.js` uppercases before sending, so lowercase pasted links work today, but the
    two sides would disagree about what a lowercase cert even *is* if that ever changed. The client
    gate accepts both cases on purpose.
13. **`import()` resolves against the document base URL** in a classic script, so `"./cert-decode.js"`
    is `/cert-decode.js` only because the app is served from the site root. Hosting under a sub-path
    would break it silently.
14. **A 64-bit itemid degrades to a string.** Real itemids are ~1e10, far inside `Number`'s safe
    range, so this never fires in practice — but if it ever did, the client would hand back a string
    where the server's JSON has a (rounded) number, and they would compare unequal. Deliberate: a
    truthful string beats a silently wrong number.
