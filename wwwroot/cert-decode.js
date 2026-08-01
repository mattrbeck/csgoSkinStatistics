// Browser-side decoder for a CS2 inspect-link "Item Certificate".
//
// WHY THIS EXISTS
// A hex inspect link is self-contained: the item's raw data travels inside the link itself, so the
// browser can read it without asking anyone. This module does exactly that, so a paste renders the
// scalars instantly instead of waiting on /api. The server request still goes out and still owns
// everything that needs a catalog (names, images, prices) - see docs/client-cert-decode.md.
//
// TRUST
// The payload is attacker-controlled and UNAUTHENTICATED. The trailing 4 bytes are a CRC32
// checksum, not a MAC, so a forged cert is trivially valid - we do not even read them. Nothing
// decoded here may be POSTed back, persisted, or cached as authoritative. It is an optimistic local
// render, nothing more.
//
// WHY A HAND-ROLLED READER RATHER THAN A PROTOBUF LIBRARY
// The CSP is `script-src 'self'` / `connect-src 'self'`, so a CDN-hosted library is blocked
// outright, and the repo has no bundler (wwwroot/*.js are served as-is). We need exactly one
// message type with a couple of dozen scalar fields, so a varint/fixed32/length-delimited reader is
// a fraction of the size of a general library and needs no build step.
//
// GROUND TRUTH
// This mirrors Services/InspectLink.cs (ParseInspectUrl) for the framing and
// Services/ItemResponse.cs (CreateResponse / MakeStickerDto / MakeKeychainDto) for the projection.
// Field numbers were reflected out of SteamKit2 3.3.1's CEconItemPreviewDataBlock, and
// tests/cert-decode.test.js replays fixtures decoded by the real .NET server.
//
// This file is an ES module loaded via a dynamic import() from post.js, so it is NOT part of the
// initial page payload - it downloads only once someone actually pastes a hex cert.

// Matches the server's DoS guard: real certs are a few hundred hex chars.
export const MAX_HEX_CHARS = 2048;
// One XOR key byte + at least one protobuf byte + the 4-byte checksum.
const MIN_CERT_BYTES = 6;

// CEconItemPreviewDataBlock tags (SteamKit2 3.3.1).
const F_ITEMID = 2;
const F_DEFINDEX = 3;
const F_PAINTINDEX = 4;
const F_RARITY = 5;
const F_QUALITY = 6;
const F_PAINTWEAR = 7;
const F_PAINTSEED = 8;
const F_KILLEATERVALUE = 10;
const F_CUSTOMNAME = 11;
const F_STICKERS = 12;
const F_INVENTORY = 13;
const F_ORIGIN = 14;
const F_KEYCHAINS = 20;

// CEconItemPreviewDataBlock.Sticker tags. Tag 12 (wrapped_sticker) is NOT modelled by SteamKit2 -
// it is the sealed sticker id inside a Sticker Slab, which the server reads out of protobuf-net's
// extension data (Services/StickerSlab.cs). An unknown-field-discarding reader would silently blank
// every slab, so this reader keeps it.
const S_SLOT = 1;
const S_STICKER_ID = 2;
const S_WEAR = 3;
const S_SCALE = 4;
const S_ROTATION = 5;
const S_TINT_ID = 6;
const S_OFFSET_X = 7;
const S_OFFSET_Y = 8;
const S_OFFSET_Z = 9;
const S_PATTERN = 10;
const S_HIGHLIGHT_REEL = 11;
const S_WRAPPED_STICKER = 12;

// Thrown internally and turned into an { ok: false, reason } result at the boundary; a malformed
// cert is an ordinary outcome (anyone can paste anything), never an exception the caller must catch.
class CertError extends Error {}

// Reusable scratch for the uint32-bits -> float32 reinterpret. `paintwear` travels as a plain
// varint carrying the IEEE-754 bit pattern of the float; C# does the same with
// BitConverter.UInt32BitsToSingle.
const wearBits = new DataView(new ArrayBuffer(4));

export function paintwearToFloat(bits) {
  wearBits.setUint32(0, bits >>> 0, true);
  return wearBits.getFloat32(0, true);
}

// Hex -> bytes, applying the server's three rejections (too long, odd length, too short) so the two
// decoders agree on what is even a candidate. Returns null rather than throwing.
export function hexToBytes(hex) {
  if (typeof hex !== "string" || hex.length === 0) return null;
  if (hex.length > MAX_HEX_CHARS) return null;
  if (hex.length % 2 !== 0) return null;
  if (!/^[0-9a-fA-F]+$/.test(hex)) return null;
  const bytes = new Uint8Array(hex.length / 2);
  for (let i = 0; i < bytes.length; i++) {
    bytes[i] = parseInt(hex.substr(i * 2, 2), 16);
  }
  return bytes.length < MIN_CERT_BYTES ? null : bytes;
}

// Cheap pre-check used to decide whether to even fetch this module. Deliberately mirrors the hex
// branch of post.js's own `isItem` test plus the server's length rules.
export function looksLikeHexCert(value) {
  const s = String(value || "");
  return s.length >= 34
    && s.length <= MAX_HEX_CHARS
    && s.length % 2 === 0
    && /^[0-9a-fA-F]+$/.test(s);
}

// --- protobuf wire reading -------------------------------------------------------------------

// BigInt throughout: `itemid` is a uint64 and a 64-bit varint cannot be accumulated in a Number
// without losing the top bits. Certs are a few hundred bytes, so the cost is irrelevant.
function readVarint(buf, st) {
  let result = 0n;
  let shift = 0n;
  for (;;) {
    if (st.pos >= st.end) throw new CertError("truncated varint");
    const b = buf[st.pos++];
    result |= BigInt(b & 0x7f) << shift;
    if ((b & 0x80) === 0) return result;
    shift += 7n;
    // 10 groups of 7 bits is the most a valid uint64 varint can use.
    if (shift > 63n) throw new CertError("varint too long");
  }
}

function readFixed32(buf, st) {
  if (st.pos + 4 > st.end) throw new CertError("truncated fixed32");
  const view = new DataView(buf.buffer, buf.byteOffset + st.pos, 4);
  st.pos += 4;
  return view;
}

function readLengthDelimited(buf, st) {
  const len = Number(readVarint(buf, st));
  if (len < 0 || st.pos + len > st.end) throw new CertError("truncated length-delimited field");
  const start = st.pos;
  st.pos += len;
  return { start, end: start + len };
}

// Skip a field we do not model. Keeping this exhaustive is what lets the reader tolerate fields
// Valve adds later instead of desynchronising and returning garbage.
function skipField(buf, st, wireType) {
  switch (wireType) {
    case 0: readVarint(buf, st); return;
    case 1:
      if (st.pos + 8 > st.end) throw new CertError("truncated fixed64");
      st.pos += 8;
      return;
    case 2: readLengthDelimited(buf, st); return;
    case 5: readFixed32(buf, st); return;
    default:
      // Wire types 3/4 are the deprecated group encoding, which no CEconItemPreviewDataBlock uses.
      // Bail rather than guess: a wrong guess desynchronises the whole stream.
      throw new CertError("unsupported wire type " + wireType);
  }
}

// Number, not BigInt, for the uint32 fields: they cannot exceed 2^32 so a Number is exact.
function u32(v) {
  return Number(v & 0xffffffffn);
}

// A uint64 as a Number while that is exact, otherwise as a decimal string. Real itemids are ~1e10,
// far inside the safe range, so this practically always returns a Number - matching the number the
// server's JSON carries. The string fallback exists so a crafted 64-bit id degrades to something
// truthful instead of a silently rounded Number.
function u64(v) {
  return v <= 9007199254740991n ? Number(v) : v.toString();
}

function parseSticker(buf, start, end) {
  const st = { pos: start, end };
  const out = {};
  while (st.pos < st.end) {
    const key = Number(readVarint(buf, st));
    const field = key >>> 3;
    const wireType = key & 7;
    if (wireType === 0) {
      const v = readVarint(buf, st);
      switch (field) {
        case S_SLOT: out.slot = u32(v); break;
        case S_STICKER_ID: out.sticker_id = u32(v); break;
        case S_TINT_ID: out.tint_id = u32(v); break;
        case S_PATTERN: out.pattern = u32(v); break;
        case S_HIGHLIGHT_REEL: out.highlight_reel = u32(v); break;
        case S_WRAPPED_STICKER: out.wrapped_sticker = u32(v); break;
        default: break; // unknown varint field: already consumed, nothing to keep
      }
    } else if (wireType === 5) {
      const view = readFixed32(buf, st);
      const f = view.getFloat32(0, true);
      switch (field) {
        case S_WEAR: out.wear = f; break;
        case S_SCALE: out.scale = f; break;
        case S_ROTATION: out.rotation = f; break;
        case S_OFFSET_X: out.offset_x = f; break;
        case S_OFFSET_Y: out.offset_y = f; break;
        case S_OFFSET_Z: out.offset_z = f; break;
        default: break;
      }
    } else {
      skipField(buf, st, wireType);
    }
  }
  return out;
}

// Decode the protobuf body into a plain object where a key is present only if the field was
// actually on the wire. That presence distinction is load-bearing: protobuf-net exposes it through
// ShouldSerialize<field>(), and the server uses it to tell "StatTrak with 0 kills" from "not
// StatTrak", and "offset 0" from "no offset".
export function parseItemBlock(bytes) {
  const st = { pos: 0, end: bytes.length };
  const out = { stickers: [], keychains: [] };
  while (st.pos < st.end) {
    const key = Number(readVarint(bytes, st));
    const field = key >>> 3;
    const wireType = key & 7;
    if (wireType === 0) {
      const v = readVarint(bytes, st);
      switch (field) {
        case F_ITEMID: out.itemid = u64(v); break;
        case F_DEFINDEX: out.defindex = u32(v); break;
        case F_PAINTINDEX: out.paintindex = u32(v); break;
        case F_RARITY: out.rarity = u32(v); break;
        case F_QUALITY: out.quality = u32(v); break;
        case F_PAINTWEAR: out.paintwear = u32(v); break;
        case F_PAINTSEED: out.paintseed = u32(v); break;
        case F_KILLEATERVALUE: out.killeatervalue = u32(v); break;
        case F_INVENTORY: out.inventory = u32(v); break;
        case F_ORIGIN: out.origin = u32(v); break;
        default: break;
      }
    } else if (wireType === 2) {
      const span = readLengthDelimited(bytes, st);
      if (field === F_STICKERS) {
        out.stickers.push(parseSticker(bytes, span.start, span.end));
      } else if (field === F_KEYCHAINS) {
        out.keychains.push(parseSticker(bytes, span.start, span.end));
      } else if (field === F_CUSTOMNAME) {
        // Decoded for completeness only. The server's response has no customname field, so this
        // never reaches the card - see docs/client-cert-decode.md.
        out.customname = new TextDecoder().decode(bytes.subarray(span.start, span.end));
      }
    } else {
      skipField(bytes, st, wireType);
    }
  }
  return out;
}

// hex -> XOR-deobfuscate -> strip framing -> protobuf. Exactly the shape of ParseInspectUrl's hex
// branch. Returns { ok: true, block } or { ok: false, reason }.
export function decodeCert(hex) {
  const raw = hexToBytes(hex);
  if (!raw) return { ok: false, reason: "malformed" };
  // Self-encoded since March 2026: every byte is XOR'd with the first one. Legacy links start 0x00,
  // which makes this a no-op.
  const key = raw[0];
  if (key !== 0) {
    for (let i = 0; i < raw.length; i++) raw[i] ^= key;
  }
  // Drop the leading key byte and the trailing 4 checksum bytes. The checksum is a CRC32, not a
  // MAC, so verifying it would prove nothing about authenticity - we ignore it, as the server does.
  const body = raw.subarray(1, raw.length - 4);
  try {
    return { ok: true, block: parseItemBlock(body) };
  } catch (e) {
    return { ok: false, reason: e instanceof CertError ? "not-a-cert" : "error" };
  }
}

// --- projection into the /api response shape -------------------------------------------------

// protobuf-net models optional scalars as non-nullable properties, so an absent field reads as 0.
// CreateResponse serialises them straight through, so the client must collapse absent -> 0 too, or
// the two sides would "disagree" on every item that simply omits a field.
function orZero(v) {
  return v === undefined ? 0 : v;
}

// Services/StickerFields.cs pairs each optional sub-field with its ShouldSerialize check and hands
// back a nullable, so absent becomes JSON null. `wear` is the exception: MakeStickerDto reads
// `s.wear` raw, so absent becomes 0, not null. Reproduced exactly.
function makeSticker(s) {
  return {
    sticker_id: orZero(s.sticker_id),
    wear: orZero(s.wear),
    rotation: s.rotation === undefined ? null : s.rotation,
    offset_x: s.offset_x === undefined ? null : s.offset_x,
    offset_y: s.offset_y === undefined ? null : s.offset_y,
  };
}

function makeKeychain(k) {
  const wrapped = orZero(k.wrapped_sticker);
  return {
    sticker_id: orZero(k.sticker_id),
    wear: orZero(k.wear),
    offset_x: k.offset_x === undefined ? null : k.offset_x,
    offset_y: k.offset_y === undefined ? null : k.offset_y,
    pattern: k.pattern === undefined ? null : k.pattern,
    slab: wrapped !== 0,
    wrapped_sticker: wrapped,
  };
}

// Everything on this list is derived from the cert alone, so the client can produce it. Everything
// the server adds on top needs const.json / skin-images.json / stickers.json / the price feed, and
// is left out entirely (NOT zeroed or blanked) so the caller can render it as "still loading"
// rather than as "no value".
export const CLIENT_FIELDS = [
  "itemid", "defindex", "paintindex", "rarity", "quality", "paintwear", "paintseed",
  "inventory", "origin", "stattrak", "stattrak_kills", "paintwear_float",
  "souvenir", "is_knife_or_glove", "wear_name", "stickers", "keychains",
];

// Fields only the server can fill. The card shows a loading affordance for each until /api answers.
export const SERVER_FIELDS = [
  "weapon", "skin", "market_hash_name", "special", "image",
  "rarity_name", "quality_name", "origin_name", "price",
];

// Wear band. These thresholds are hardcoded in ConstDataService.GetWearFromFloat - they are NOT a
// const.json lookup - so the client can reproduce the exact string.
const WEAR_BANDS = [
  [0.07, "Factory New"],
  [0.15, "Minimal Wear"],
  [0.38, "Field-Tested"],
  [0.45, "Well-Worn"],
  [Infinity, "Battle-Scarred"],
];

export function wearNameFromFloat(paintWear) {
  return WEAR_BANDS.find(([max]) => paintWear < max)[1];
}

// Knives 500-599, gloves 5000+ - arithmetic on defindex, not a catalog lookup
// (ConstDataService.IsKnifeOrGlove).
function isKnifeOrGlove(defindex) {
  return (defindex >= 500 && defindex < 600) || defindex >= 5000;
}

// Project a decoded block into the same field names the /api response uses, so populateCard can
// consume either without knowing which it got. Only CLIENT_FIELDS are set.
export function certToItemInfo(block) {
  const paintwear = orZero(block.paintwear);
  const defindex = orZero(block.defindex);
  const quality = orZero(block.quality);
  const paintWearFloat = paintwearToFloat(paintwear);
  return {
    itemid: block.itemid === undefined ? 0 : block.itemid,
    defindex,
    paintindex: orZero(block.paintindex),
    rarity: orZero(block.rarity),
    quality,
    paintwear,
    paintseed: orZero(block.paintseed),
    inventory: orZero(block.inventory),
    origin: orZero(block.origin),
    // Presence of killeatervalue IS the StatTrak flag (ShouldSerializekilleatervalue), which is why
    // a StatTrak item with 0 kills still reports stattrak: true.
    stattrak: block.killeatervalue !== undefined,
    stattrak_kills: block.killeatervalue === undefined ? null : block.killeatervalue,
    paintwear_float: paintWearFloat,
    souvenir: quality === 12,
    is_knife_or_glove: isKnifeOrGlove(defindex),
    wear_name: wearNameFromFloat(paintWearFloat),
    stickers: block.stickers.map(makeSticker),
    keychains: block.keychains.map(makeKeychain),
  };
}

// The one call post.js makes. { ok: true, item } or { ok: false, reason }.
export function decodeCertToItemInfo(hex) {
  const decoded = decodeCert(hex);
  if (!decoded.ok) return decoded;
  try {
    return { ok: true, item: certToItemInfo(decoded.block), block: decoded.block };
  } catch {
    return { ok: false, reason: "error" };
  }
}

// --- reconciliation ---------------------------------------------------------------------------

// Raw fields the client and the server must agree on. An itemid encodes an immutable config (see
// docs/inventory-endpoint-cert.md: any mutation mints a new itemid), so a disagreement here means
// one of the two decoders is wrong - worth surfacing, not papering over.
export const IMMUTABLE_FIELDS = [
  "itemid", "defindex", "paintindex", "rarity", "quality", "paintwear", "paintseed", "origin",
];

// Compare a client decode against the server's answer. `stattrak_kills` is excluded on purpose: it
// is the one field that legitimately drifts under a fixed itemid, and a cached server row can be
// behind the live count. `inventory` (the slot) drifts too. Returns the field names that differ.
export function diffAgainstServer(clientItem, serverItem) {
  if (!clientItem || !serverItem) return [];
  return IMMUTABLE_FIELDS.filter((f) => String(clientItem[f]) !== String(serverItem[f]));
}
