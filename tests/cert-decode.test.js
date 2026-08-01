/**
 * Equivalence tests for wwwroot/cert-decode.js against the .NET decoder.
 *
 * tests/fixtures/cert-fixtures.json is NOT hand-written. Each entry's `hex` was produced by
 * protobuf-net serialising a real SteamKit2 `CEconItemPreviewDataBlock` (same framing as
 * csgoSkinStatistics.Tests/InspectCert.cs, extended with a non-zero XOR key), and each `server`
 * block is the verbatim JSON the running app returned from `GET /api?url=<that link>` - i.e. the
 * output of the real `InspectLink.ParseInspectUrl` + `ItemResponse.CreateResponse`. The volatile
 * `price` object and the long Steam CDN image URLs were stripped; nothing else was touched.
 *
 * So "the client agrees with the server" here means literally: the same bytes, through two
 * independent decoders, produce the same values.
 */

const fixtures = require('./fixtures/cert-fixtures.json');
const {
  decodeCert,
  decodeCertToItemInfo,
  certToItemInfo,
  hexToBytes,
  looksLikeHexCert,
  paintwearToFloat,
  wearNameFromFloat,
  parseItemBlock,
  diffAgainstServer,
  CLIENT_FIELDS,
  MAX_HEX_CHARS,
} = require('../wwwroot/cert-decode.js');

const byName = (name) => fixtures.find((f) => f.name === name);

// Every field the client claims it can produce, checked against the server's own value.
const SCALARS = [
  'itemid', 'defindex', 'paintindex', 'rarity', 'quality', 'paintwear', 'paintseed',
  'inventory', 'origin', 'stattrak', 'stattrak_kills', 'paintwear_float',
  'souvenir', 'is_knife_or_glove', 'wear_name',
];

describe('cert-decode vs the .NET decoder (fixture equivalence)', () => {
  test('the fixture set covers the cases we care about', () => {
    expect(fixtures).toHaveLength(9);
    expect(fixtures.map((f) => f.name)).toEqual(expect.arrayContaining([
      'plain_skin', 'plain_skin_xor', 'plain_skin_xor_ff', 'stattrak', 'stattrak_zero_kills',
      'stickers', 'keychain_slab', 'zero_itemid', 'customname',
    ]));
  });

  test.each(fixtures.map((f) => [f.name, f]))('%s: every client-decodable scalar matches', (_name, fixture) => {
    const result = decodeCertToItemInfo(fixture.hex);
    expect(result.ok).toBe(true);
    for (const field of SCALARS) {
      expect({ [field]: result.item[field] }).toEqual({ [field]: fixture.server[field] });
    }
  });

  test.each(fixtures.map((f) => [f.name, f]))('%s: sticker and keychain arrays match field for field', (_name, fixture) => {
    const { item } = decodeCertToItemInfo(fixture.hex);

    expect(item.stickers).toHaveLength(fixture.server.stickers.length);
    item.stickers.forEach((s, i) => {
      const expected = fixture.server.stickers[i];
      // name/image are catalog lookups the client cannot do, so they are simply absent here -
      // never blanked, which is what would read as "this sticker has no name".
      expect(s).toEqual({
        sticker_id: expected.sticker_id,
        wear: expected.wear,
        rotation: expected.rotation,
        offset_x: expected.offset_x,
        offset_y: expected.offset_y,
      });
      expect(s).not.toHaveProperty('name');
    });

    expect(item.keychains).toHaveLength(fixture.server.keychains.length);
    item.keychains.forEach((k, i) => {
      const expected = fixture.server.keychains[i];
      expect(k).toEqual({
        sticker_id: expected.sticker_id,
        wear: expected.wear,
        offset_x: expected.offset_x,
        offset_y: expected.offset_y,
        pattern: expected.pattern,
        slab: expected.slab,
        wrapped_sticker: expected.wrapped_sticker,
      });
    });
  });

  test.each(fixtures.map((f) => [f.name, f]))('%s: nothing the client emits disagrees with the server', (_name, fixture) => {
    const { item } = decodeCertToItemInfo(fixture.hex);
    expect(diffAgainstServer(item, fixture.server)).toEqual([]);
  });

  test('the client emits only fields it can actually derive', () => {
    const { item } = decodeCertToItemInfo(byName('plain_skin').hex);
    expect(Object.keys(item).sort()).toEqual([...CLIENT_FIELDS].sort());
  });
});

describe('the specific cases that are easy to get wrong', () => {
  test('a non-zero XOR key decodes to the same item as the legacy 0x00 form', () => {
    const legacy = decodeCertToItemInfo(byName('plain_skin').hex).item;
    const xored = decodeCertToItemInfo(byName('plain_skin_xor').hex).item;
    const xoredFf = decodeCertToItemInfo(byName('plain_skin_xor_ff').hex).item;
    expect(byName('plain_skin_xor').hex).not.toBe(byName('plain_skin').hex); // genuinely different bytes
    expect(xored).toEqual(legacy);
    expect(xoredFf).toEqual(legacy);
  });

  test('StatTrak with zero kills is StatTrak, not "no StatTrak"', () => {
    const { item } = decodeCertToItemInfo(byName('stattrak_zero_kills').hex);
    expect(item.stattrak).toBe(true);
    expect(item.stattrak_kills).toBe(0);
    expect(item.stattrak_kills).not.toBeNull();
  });

  test('a non-StatTrak item reports null kills, never 0', () => {
    const { item } = decodeCertToItemInfo(byName('plain_skin').hex);
    expect(item.stattrak).toBe(false);
    expect(item.stattrak_kills).toBeNull();
  });

  test('a Sticker Slab keeps the sealed sticker id from unmodelled proto field 12', () => {
    const { item } = decodeCertToItemInfo(byName('keychain_slab').hex);
    expect(item.keychains[0]).toMatchObject({ sticker_id: 60, pattern: 44073, slab: false, wrapped_sticker: 0 });
    expect(item.keychains[1]).toMatchObject({ sticker_id: 37, slab: true, wrapped_sticker: 4352 });
  });

  test('stacked stickers in one slot stay positional and are not collapsed', () => {
    const { block } = decodeCertToItemInfo(byName('stickers').hex);
    expect(block.stickers.map((s) => s.slot)).toEqual([0, 3, 3]);
    expect(block.stickers.map((s) => s.sticker_id)).toEqual([4515, 1052, 4516]);
  });

  test('absent sticker sub-fields are null, but absent wear is 0 (matching MakeStickerDto)', () => {
    const { item } = decodeCertToItemInfo(byName('stickers').hex);
    expect(item.stickers[2]).toEqual({
      sticker_id: 4516, wear: 0, rotation: null, offset_x: null, offset_y: null,
    });
  });

  test('itemid 0 items (music kits, graffiti) decode fully', () => {
    const { item } = decodeCertToItemInfo(byName('zero_itemid').hex);
    expect(item.itemid).toBe(0);
    expect(item.defindex).toBe(1314);
    expect(item.paintindex).toBe(0);
  });

  test('customname is decoded but deliberately kept out of the rendered projection', () => {
    const result = decodeCertToItemInfo(byName('customname').hex);
    expect(result.block.customname).toBe('<img src=x onerror=alert(1)>');
    expect(result.item).not.toHaveProperty('customname');
    // The server has no customname field either, so there is nothing to reconcile against.
    expect(byName('customname').server).not.toHaveProperty('customname');
  });

  test('paintwear is a uint32 bit pattern reinterpreted as float32', () => {
    expect(paintwearToFloat(1043574843)).toBeCloseTo(0.17547695338726044, 12);
    expect(paintwearToFloat(0)).toBe(0);
    // 0x3F800000 == 1.0f
    expect(paintwearToFloat(0x3f800000)).toBe(1);
  });

  test('a 64-bit itemid beyond Number precision degrades to an exact string', () => {
    // itemid = 2^63 + 1, hand-encoded: field 2, varint.
    const bytes = [0x00, 0x10, 0x81, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01, 0, 0, 0, 0];
    const hex = bytes.map((b) => b.toString(16).padStart(2, '0')).join('').toUpperCase();
    const { block } = decodeCert(hex);
    expect(block.itemid).toBe('9223372036854775809');
  });
});

describe('wear bands', () => {
  test.each([
    [0, 'Factory New'],
    [0.0699, 'Factory New'],
    [0.07, 'Minimal Wear'],
    [0.15, 'Field-Tested'],
    [0.38, 'Well-Worn'],
    [0.45, 'Battle-Scarred'],
    [1, 'Battle-Scarred'],
  ])('%f -> %s', (f, name) => {
    expect(wearNameFromFloat(f)).toBe(name);
  });

  test('matches the server on every fixture that has a paint', () => {
    for (const f of fixtures) {
      const { item } = decodeCertToItemInfo(f.hex);
      expect(item.wear_name).toBe(f.server.wear_name);
    }
  });
});

describe('rejections (mirroring the server guards)', () => {
  test('rejects a hex payload longer than the server cap', () => {
    expect(hexToBytes('A'.repeat(MAX_HEX_CHARS + 2))).toBeNull();
    expect(decodeCert('A'.repeat(MAX_HEX_CHARS + 2))).toEqual({ ok: false, reason: 'malformed' });
  });

  test('rejects odd-length hex', () => {
    expect(hexToBytes('0010BCB1ABF')).toBeNull();
  });

  test('rejects fewer than 6 decoded bytes', () => {
    expect(hexToBytes('0011223344')).toBeNull(); // 5 bytes
    expect(hexToBytes('001122334455')).not.toBeNull(); // 6 bytes
  });

  test('rejects non-hex input', () => {
    expect(hexToBytes('zzzzzzzzzzzz')).toBeNull();
    expect(hexToBytes('')).toBeNull();
    expect(hexToBytes(null)).toBeNull();
  });

  test('valid hex that is not a protobuf is reported, not thrown', () => {
    // 0xFF as a tag byte is wire type 7, which does not exist.
    const result = decodeCert('00FFFFFFFFFFFFFFFF00000000');
    expect(result.ok).toBe(false);
    expect(result.reason).toBe('not-a-cert');
  });

  test('a truncated varint is reported, not thrown', () => {
    // field 2 varint with the continuation bit set and nothing after it.
    expect(decodeCert('001080808080808080808000000000').ok).toBe(false);
  });

  test('a length-delimited field claiming more bytes than remain is rejected', () => {
    // field 12 (stickers), wire type 2, length 0x7F, but only a couple of bytes follow.
    expect(decodeCert('0062 7F 01 02 00000000'.replace(/ /g, '')).ok).toBe(false);
  });
});

describe('unknown fields', () => {
  test('an unmodelled scalar field is skipped without desynchronising the stream', () => {
    // field 21 (style, varint 3) sits between defindex and paintindex; both must still read.
    // 0x18 = field 3 varint, 0xA8 0x01 = field 21 varint, 0x20 = field 4 varint.
    const hex = '00' + '1807' + 'A801 03'.replace(/ /g, '') + '20B005' + '00000000';
    const { block } = decodeCert(hex);
    expect(block.defindex).toBe(7);
    expect(block.paintindex).toBe(688);
  });

  test('an unmodelled length-delimited field is skipped by its length', () => {
    // field 22 (variations), wire type 2 => 0xB2 0x01, length 3, three junk bytes.
    const hex = '00' + '1807' + 'B201' + '03' + 'AABBCC' + '20B005' + '00000000';
    const { block } = decodeCert(hex);
    expect(block.defindex).toBe(7);
    expect(block.paintindex).toBe(688);
  });
});

describe('looksLikeHexCert (the lazy-load trigger)', () => {
  test('accepts a real cert', () => {
    expect(looksLikeHexCert(byName('plain_skin').hex)).toBe(true);
  });

  test('rejects the legacy S/A/D link body, a vanity name and an id64', () => {
    expect(looksLikeHexCert('S76561198084749846A698323590D7935523998312483177')).toBe(false);
    expect(looksLikeHexCert('mattrb')).toBe(false);
    expect(looksLikeHexCert('76561198261551396')).toBe(false); // 17 digits: hex-ish but too short
  });

  test('rejects odd-length and over-long input without touching the decoder', () => {
    expect(looksLikeHexCert('A'.repeat(35))).toBe(false);
    expect(looksLikeHexCert('A'.repeat(MAX_HEX_CHARS + 2))).toBe(false);
  });

  test('accepts lowercase (post.js uppercases before sending, but the gate should not care)', () => {
    expect(looksLikeHexCert(byName('plain_skin').hex.toLowerCase())).toBe(true);
  });
});

describe('parseItemBlock / certToItemInfo directly', () => {
  test('an empty body yields an item with everything at its protobuf default', () => {
    const block = parseItemBlock(new Uint8Array(0));
    const item = certToItemInfo(block);
    expect(item.itemid).toBe(0);
    expect(item.stattrak).toBe(false);
    expect(item.stattrak_kills).toBeNull();
    expect(item.paintwear_float).toBe(0);
    expect(item.wear_name).toBe('Factory New'); // 0.0 is genuinely Factory New; see the doc's caveats
    expect(item.stickers).toEqual([]);
  });

  test('knife and glove defindex ranges', () => {
    expect(certToItemInfo(parseItemBlock(new Uint8Array([0x18, 0xf4, 0x03]))).is_knife_or_glove).toBe(true);  // 500
    expect(certToItemInfo(parseItemBlock(new Uint8Array([0x18, 0xdb, 0x04]))).is_knife_or_glove).toBe(false); // 603
    expect(certToItemInfo(parseItemBlock(new Uint8Array([0x18, 0xb6, 0x27]))).is_knife_or_glove).toBe(true);  // 5046
    expect(certToItemInfo(parseItemBlock(new Uint8Array([0x18, 0x07]))).is_knife_or_glove).toBe(false);       // 7
  });

  test('souvenir is quality 12', () => {
    expect(certToItemInfo(parseItemBlock(new Uint8Array([0x30, 0x0c]))).souvenir).toBe(true);
    expect(certToItemInfo(parseItemBlock(new Uint8Array([0x30, 0x09]))).souvenir).toBe(false);
  });
});

describe('diffAgainstServer (the reconciliation check)', () => {
  const { item } = decodeCertToItemInfo(byName('stattrak').hex);

  test('agrees with the server on the real fixture', () => {
    expect(diffAgainstServer(item, byName('stattrak').server)).toEqual([]);
  });

  test('a drifting kill count is NOT a disagreement', () => {
    const stale = { ...byName('stattrak').server, stattrak_kills: 4000 };
    expect(diffAgainstServer(item, stale)).toEqual([]);
  });

  test('a moved inventory slot is NOT a disagreement', () => {
    const moved = { ...byName('stattrak').server, inventory: 999 };
    expect(diffAgainstServer(item, moved)).toEqual([]);
  });

  test('a differing immutable field IS a disagreement', () => {
    const wrong = { ...byName('stattrak').server, paintseed: 1, paintwear: 5 };
    expect(diffAgainstServer(item, wrong).sort()).toEqual(['paintseed', 'paintwear']);
  });

  test('a missing side is not reported as a disagreement', () => {
    expect(diffAgainstServer(null, byName('stattrak').server)).toEqual([]);
    expect(diffAgainstServer(item, null)).toEqual([]);
  });
});
