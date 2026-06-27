// Build the static site's data layer from the repo's source catalogs.
//
// What this demonstrates:
//   1. RANGED SHARDS — instead of shipping the whole 2.9 MB stickers.json (or doing an
//      N+1 round-trip per id), we split id-keyed catalogs into fixed-size buckets. The
//      client computes bucket = floor(id / BUCKET) and fetches only the one shard the
//      item actually needs. This is the middle ground the prior investigation
//      (docs/client-side-cert-decode-findings.md) never measured.
//   2. CONTENT-HASHED, IMMUTABLE FILES — every shard's filename embeds a hash of its
//      bytes. The bytes can't change under a fixed name, so every shard is safe to cache
//      forever (immutable). A regenerated catalog produces new filenames.
//   3. A NO-CACHE MANIFEST — the only file fetched fresh on every load. It maps logical
//      names ("stickers bucket 5") to the current content-hashed URLs. Swapping the
//      manifest atomically cuts the whole site over to a new data version; nothing else
//      needs cache invalidation. This is the answer to "what happens when caches go stale".
//
// Run: node static-prototype/build/build.mjs
// (uses only Node built-ins + ../site/proto.js)

import { createHash } from "node:crypto";
import { readFileSync, writeFileSync, rmSync, mkdirSync, readdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { encodeCert } from "../site/proto.js";

const __dirname = dirname(fileURLToPath(import.meta.url));
const repo = join(__dirname, "..", "..");
const outDir = join(__dirname, "..", "site", "data");

const STICKER_BUCKET = 1000; // sticker_id span per shard
const IMAGE_BUCKET = 1000;   // paintindex span per image shard

const readJson = (p) => JSON.parse(readFileSync(join(repo, p), "utf8"));

// --- inputs -----------------------------------------------------------------
const constData = readJson("const.json");
const stickers = readJson("stickers.json");          // { stickers:{id:{name,image}}, keychains:{...} }
const skinImages = readJson("skin-images.json");      // { "<def>_<paint>": url }
const floatRanges = readJson("wwwroot/float-ranges.json");

// --- shard writer with content hashing --------------------------------------
rmSync(outDir, { recursive: true, force: true });
mkdirSync(outDir, { recursive: true });

const written = [];
function emit(logicalBase, obj) {
  const body = JSON.stringify(obj);
  const hash = createHash("sha256").update(body).digest("hex").slice(0, 8);
  const name = `${logicalBase}.${hash}.json`;
  writeFileSync(join(outDir, name), body);
  written.push({ name, bytes: body.length });
  return name;
}

// Split an id-keyed map into fixed-size buckets. `bucketOf` maps a map key to the numeric
// value the bucket is computed from (the key itself for sticker ids; the paintindex part
// for "<def>_<paint>" image keys). Returns { bucketIndex: filename }.
function shardByBucket(prefix, map, bucketSize, bucketOf = (k) => Number(k)) {
  const buckets = {};
  for (const [id, val] of Object.entries(map)) {
    const b = Math.floor(bucketOf(id) / bucketSize);
    (buckets[b] ??= {})[id] = val;
  }
  const files = {};
  for (const [b, contents] of Object.entries(buckets)) {
    files[b] = emit(`${prefix}-${b}`, contents);
  }
  return files;
}

// --- const split: core (always needed) vs special (only for fade%/fire&ice) -
// The two 1001-element pattern tables are only consulted when an item is actually a
// Fade / Marble Fade, so they ship as a separate lazily-loaded shard. Everything needed
// to name *any* item (weapon, pattern, rarity/quality/origin, doppler/kimono labels)
// stays in the small core shard.
const { fade_order, fireice_order, ...constCore } = constData;
const constCoreFile = emit("const-core", constCore);
const constSpecialFile = emit("const-special", { fade_order, fireice_order });

// --- sticker / keychain / image / float shards ------------------------------
const stickerFiles = shardByBucket("stickers", stickers.stickers, STICKER_BUCKET);
const keychainsFile = emit("keychains", stickers.keychains); // only ~78 entries, ship whole
const imageFiles = shardByBucket("images", skinImages, IMAGE_BUCKET, (k) => Number(k.split("_")[1]));
const floatFile = emit("float-ranges", floatRanges);

// --- manifest ---------------------------------------------------------------
// `version` is a hash of every shard filename, so any data change yields a new version
// string the client can surface ("data updated, reloading"). The manifest itself is the
// ONLY file served with no-cache; every shard it points at is immutable + cache-forever.
const manifest = {
  version: createHash("sha256").update(written.map((w) => w.name).sort().join()).digest("hex").slice(0, 12),
  builtFrom: { stickerBucket: STICKER_BUCKET, imageBucket: IMAGE_BUCKET },
  shards: {
    constCore: constCoreFile,
    constSpecial: constSpecialFile,
    keychains: keychainsFile,
    floatRanges: floatFile,
    stickers: { bucket: STICKER_BUCKET, files: stickerFiles },
    images: { bucket: IMAGE_BUCKET, files: imageFiles },
  },
};
writeFileSync(join(outDir, "manifest.json"), JSON.stringify(manifest, null, 2));

// --- sample inspect links (real catalog ids → real cert hex) ----------------
// Minted with the same encoder the prototype decodes, so the demo renders real names
// and images and exercises every code path (special patterns, stickers, slab, StatTrak).
const wear = (f) => f; // readability
const samples = [
  {
    label: "M4A1-S | Atomic Alloy (StatTrak) + 3 stickers",
    note: "Core shard only for names; one sticker shard (bucket 0); one image shard.",
    item: {
      itemid: "5800000001", defindex: 60, paintindex: 301, rarity: 5, quality: 9,
      paintwear_float: 0.069, paintseed: 412, stattrak: true, killeatervalue: 1337,
      origin: 8,
      stickers: [
        { slot: 0, sticker_id: 1, wear: 0.0 },
        { slot: 1, sticker_id: 5, wear: 0.0 },
        { slot: 2, sticker_id: 7, wear: 0.2 },
      ],
    },
  },
  {
    label: "Karambit | Fade (100%)",
    note: "Needs const-special (fade_order) to compute the fade %. Karambit's fade is reversed, so this high % comes from a low seed.",
    item: { itemid: "5800000002", defindex: 507, paintindex: 38, rarity: 6, quality: 3, paintwear_float: 0.012, paintseed: 412, origin: 8 },
  },
  {
    label: "Karambit | Marble Fade (Fire & Ice, 1st Max)",
    note: "Needs const-special (fireice_order) for the tier.",
    item: { itemid: "5800000003", defindex: 507, paintindex: 413, rarity: 6, quality: 3, paintwear_float: 0.03, paintseed: 412, origin: 8 },
  },
  {
    label: "Karambit | Doppler (Ruby)",
    note: "Phase label comes from the core shard's doppler map — no special shard needed.",
    item: { itemid: "5800000004", defindex: 507, paintindex: 415, rarity: 6, quality: 3, paintwear_float: 0.01, paintseed: 4, origin: 8 },
  },
  {
    label: "Karambit | Doppler (Ruby) + Sticker Slab charm",
    note: "Pulls a keychain shard; the slab's sealed sticker resolves from a sticker shard.",
    item: {
      itemid: "5800000005", defindex: 507, paintindex: 415, rarity: 6, quality: 3, paintwear_float: 0.01, paintseed: 4, origin: 8,
      keychains: [{ slot: 0, sticker_id: 30, wear: 0.0, wrapped_sticker: 4352 }],
    },
  },
];

const sampleOut = samples.map((s) => ({
  label: s.label,
  note: s.note,
  link: "steam://run/730//+csgo_econ_action_preview%20" + encodeCert(s.item),
}));
writeFileSync(join(__dirname, "..", "site", "samples.json"), JSON.stringify(sampleOut, null, 2));

// --- report -----------------------------------------------------------------
const total = written.reduce((n, w) => n + w.bytes, 0);
const stickerShards = Object.keys(stickerFiles).length;
const imageShards = Object.keys(imageFiles).length;
console.log(`manifest version ${manifest.version}`);
console.log(`wrote ${written.length} shards, ${(total / 1024).toFixed(0)} KB total (uncompressed)`);
console.log(`  const-core      ${(written.find((w) => w.name === constCoreFile).bytes / 1024).toFixed(1)} KB (loaded once, every item)`);
console.log(`  const-special   ${(written.find((w) => w.name === constSpecialFile).bytes / 1024).toFixed(1)} KB (only fade/fire&ice)`);
console.log(`  stickers        ${stickerShards} shards, avg ${(Object.values(stickerFiles).reduce((n, f) => n + written.find((w) => w.name === f).bytes, 0) / stickerShards / 1024).toFixed(1)} KB each`);
console.log(`  images          ${imageShards} shards`);
console.log(`wrote ${sampleOut.length} sample links to site/samples.json`);
