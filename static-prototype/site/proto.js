// Dependency-free protobuf reader/writer for the one message type this app needs:
// CEconItemPreviewDataBlock — the payload Steam embeds in a CS2 "Item Certificate"
// inspect link. The server (protobuf-net + SteamKit2) decodes the exact same bytes;
// the field numbers below are pinned to that proto and validated field-for-field
// (see docs/client-side-cert-decode-findings.md).
//
// A cert link is, after hex-decoding:
//     [ 1-byte XOR key ][ CEconItemPreviewDataBlock protobuf ][ 4-byte CRC ]
// every byte XOR-ed with the key. We strip the key + CRC and parse the middle.
//
// Works in both the browser and Node (build script mints sample links with the encoder).

// --- wire-format primitives -------------------------------------------------

const WIRE = { VARINT: 0, I64: 1, LEN: 2, I32: 5 };

class Reader {
  constructor(bytes) { this.b = bytes; this.p = 0; }
  get eof() { return this.p >= this.b.length; }
  byte() { return this.b[this.p++]; }
  varint() {
    // Returns a BigInt so 64-bit ids (itemid) survive without precision loss.
    let shift = 0n, result = 0n, byte;
    do { byte = this.b[this.p++]; result |= BigInt(byte & 0x7f) << shift; shift += 7n; }
    while (byte & 0x80);
    return result;
  }
  fixed32() {
    const v = this.b[this.p] | (this.b[this.p + 1] << 8) | (this.b[this.p + 2] << 16) | (this.b[this.p + 3] << 24);
    this.p += 4;
    return v >>> 0;
  }
  bytes(len) { const out = this.b.subarray(this.p, this.p + len); this.p += len; return out; }
  // Skip a field we don't model, by wire type, so unknown/newer fields never derail us.
  skip(wire) {
    if (wire === WIRE.VARINT) { while (this.b[this.p++] & 0x80); }
    else if (wire === WIRE.I64) this.p += 8;
    else if (wire === WIRE.I32) this.p += 4;
    else if (wire === WIRE.LEN) this.p += Number(this.varint());
  }
}

class Writer {
  constructor() { this.parts = []; }
  varint(v) {
    let n = BigInt(v); const out = [];
    do { let b = Number(n & 0x7fn); n >>= 7n; if (n) b |= 0x80; out.push(b); } while (n);
    this.parts.push(Uint8Array.from(out)); return this;
  }
  tag(field, wire) { return this.varint((field << 3) | wire); }
  fixed32(v) { const a = new Uint8Array(4); new DataView(a.buffer).setUint32(0, v >>> 0, true); this.parts.push(a); return this; }
  len(bytes) { this.varint(bytes.length); this.parts.push(bytes); return this; }
  bytes() { const total = this.parts.reduce((n, p) => n + p.length, 0); const out = new Uint8Array(total); let o = 0; for (const p of this.parts) { out.set(p, o); o += p.length; } return out; }
}

const u32 = (bits) => bits >>> 0;
const bitsToFloat = (bits) => { const a = new Uint8Array(4); new DataView(a.buffer).setUint32(0, bits >>> 0, true); return new DataView(a.buffer).getFloat32(0, true); };
const floatToBits = (f) => { const a = new Uint8Array(4); new DataView(a.buffer).setFloat32(0, f, true); return new DataView(a.buffer).getUint32(0, true); };

// --- CEconItemPreviewDataBlock.Sticker --------------------------------------
// slot=1 sticker_id=2 wear=3(float) scale=4 rotation=5 tint_id=6 offset_x/y/z=7/8/9
// pattern=10 highlight_reel=11 wrapped_sticker=12 (slab: sealed sticker id)

function readSticker(r, len) {
  const end = r.p + len;
  const s = {};
  while (r.p < end) {
    const tag = Number(r.varint()); const field = tag >>> 3; const wire = tag & 7;
    switch (field) {
      case 1: s.slot = Number(r.varint()); break;
      case 2: s.sticker_id = Number(r.varint()); break;
      case 3: s.wear = bitsToFloat(r.fixed32()); break;
      case 4: s.scale = bitsToFloat(r.fixed32()); break;
      case 5: s.rotation = bitsToFloat(r.fixed32()); break;
      case 6: s.tint_id = Number(r.varint()); break;
      case 10: s.pattern = Number(r.varint()); break;
      case 11: s.highlight_reel = Number(r.varint()); break;
      case 12: s.wrapped_sticker = Number(r.varint()); break; // Sticker Slab
      default: r.skip(wire);
    }
  }
  return s;
}

function writeSticker(s) {
  const w = new Writer();
  if (s.slot != null) w.tag(1, WIRE.VARINT).varint(s.slot);
  if (s.sticker_id != null) w.tag(2, WIRE.VARINT).varint(s.sticker_id);
  if (s.wear != null) w.tag(3, WIRE.I32).fixed32(floatToBits(s.wear));
  if (s.wrapped_sticker != null) w.tag(12, WIRE.VARINT).varint(s.wrapped_sticker);
  return w.bytes();
}

// --- CEconItemPreviewDataBlock ----------------------------------------------

export function decodeItemBlock(bytes) {
  const r = new Reader(bytes);
  const item = { stickers: [], keychains: [], _present: new Set() };
  while (!r.eof) {
    const tag = Number(r.varint()); const field = tag >>> 3; const wire = tag & 7;
    item._present.add(field);
    switch (field) {
      case 1: item.accountid = Number(r.varint()); break;
      case 2: item.itemid = r.varint(); break;              // BigInt (64-bit)
      case 3: item.defindex = Number(r.varint()); break;
      case 4: item.paintindex = Number(r.varint()); break;
      case 5: item.rarity = Number(r.varint()); break;
      case 6: item.quality = Number(r.varint()); break;
      case 7: { const bits = Number(r.varint()); item.paintwear_bits = u32(bits); item.paintwear_float = bitsToFloat(bits); break; }
      case 8: item.paintseed = Number(r.varint()); break;
      case 9: item.killeaterscoretype = Number(r.varint()); break;
      case 10: item.killeatervalue = Number(r.varint()); break;
      case 11: item.customname = new TextDecoder().decode(r.bytes(Number(r.varint()))); break;
      case 12: item.stickers.push(readSticker(r, Number(r.varint()))); break;
      case 13: item.inventory = Number(r.varint()); break;
      case 14: item.origin = Number(r.varint()); break;
      case 17: item.musicindex = Number(r.varint()); break;
      case 20: item.keychains.push(readSticker(r, Number(r.varint()))); break;
      default: r.skip(wire);
    }
  }
  // StatTrak presence mirrors the server's ShouldSerializekilleatervalue(): the field
  // appearing on the wire is what marks the item as StatTrak (count may be 0).
  item.stattrak = item._present.has(10);
  item.itemid = item.itemid != null ? item.itemid.toString() : "0";
  return item;
}

export function encodeItemBlock(item) {
  const w = new Writer();
  const put = (field, v) => { if (v != null) w.tag(field, WIRE.VARINT).varint(v); };
  if (item.accountid != null) put(1, item.accountid);
  if (item.itemid != null) w.tag(2, WIRE.VARINT).varint(item.itemid);
  put(3, item.defindex); put(4, item.paintindex); put(5, item.rarity); put(6, item.quality);
  if (item.paintwear_bits != null) w.tag(7, WIRE.VARINT).varint(item.paintwear_bits);
  else if (item.paintwear_float != null) w.tag(7, WIRE.VARINT).varint(floatToBits(item.paintwear_float));
  put(8, item.paintseed);
  if (item.stattrak || item.killeatervalue != null) {
    w.tag(9, WIRE.VARINT).varint(item.killeaterscoretype ?? 0);
    w.tag(10, WIRE.VARINT).varint(item.killeatervalue ?? 0);
  }
  for (const s of item.stickers || []) { const b = writeSticker(s); w.tag(12, WIRE.LEN).len(b); }
  put(13, item.inventory); put(14, item.origin);
  for (const k of item.keychains || []) { const b = writeSticker(k); w.tag(20, WIRE.LEN).len(b); }
  return w.bytes();
}

// --- cert (un)wrap ----------------------------------------------------------

const hexToBytes = (hex) => { const out = new Uint8Array(hex.length / 2); for (let i = 0; i < out.length; i++) out[i] = parseInt(hex.substr(i * 2, 2), 16); return out; };
const bytesToHex = (b) => Array.from(b, (x) => x.toString(16).padStart(2, "0")).join("");

// hex cert string -> decoded item. Throws on malformed input.
export function decodeCert(hex) {
  const raw = hexToBytes(hex);
  if (raw.length < 6) throw new Error("cert too short");
  const key = raw[0];
  for (let i = 0; i < raw.length; i++) raw[i] ^= key; // XOR-deobfuscate (key is first byte)
  const payload = raw.subarray(1, raw.length - 4);    // drop key + 4-byte CRC
  return decodeItemBlock(payload);
}

// item -> hex cert string. CRC is not verified on decode, so we emit zero bytes for it.
// xorKey defaults to a non-zero value to exercise the deobfuscation path.
export function encodeCert(item, xorKey = 0xa5) {
  const payload = encodeItemBlock(item);
  const raw = new Uint8Array(1 + payload.length + 4);
  raw[0] = 0; raw.set(payload, 1); // CRC bytes left 0
  for (let i = 0; i < raw.length; i++) raw[i] ^= xorKey;
  return bytesToHex(raw).toUpperCase();
}

export const _internals = { Reader, Writer, bitsToFloat, floatToBits };
