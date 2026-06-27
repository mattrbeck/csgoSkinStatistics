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

// Balanced bucketing targets (raw bytes per shard, ~4x smaller gzipped). The build picks
// contiguous id ranges so each shard lands near these. Kept generous so a typical item
// still pulls a single shard for all its decals.
const STICKER_TARGET = 130 * 1024;
const IMAGE_TARGET = 130 * 1024;

// Old fixed-bucket sizes, retained only to print the before/after comparison.
const STICKER_BUCKET = 1000;
const IMAGE_BUCKET = 1000;

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

// --- bucketing strategies ---------------------------------------------------
// `bucketOf` maps a map key to the numeric value it's bucketed on (the key itself for
// sticker ids; the paintindex part for "<def>_<paint>" image keys).

// FIXED: floor(value / size). Simple, but lopsided when ids cluster (image bucket 0 held
// every low paintindex and ballooned). Kept only to report the comparison.
function planFixed(map, bucketSize, bucketOf) {
  const buckets = new Map();
  for (const [id, val] of Object.entries(map)) {
    const b = Math.floor(bucketOf(id) / bucketSize);
    (buckets.get(b) ?? buckets.set(b, {}).get(b))[id] = val;
  }
  return [...buckets.values()].map((contents) => ({ contents }));
}

// BALANCED: keep ids contiguous (so a multi-decal item still tends to hit one shard) but
// choose the cut points so each shard is ~targetBytes. Greedily fill a shard in id order;
// start a new one when adding the next entry would overflow. Returns shards in id order,
// each tagged with the smallest id it contains so the client can binary-search.
function planBalanced(map, targetBytes, bucketOf) {
  const entries = Object.entries(map).sort((a, b) => bucketOf(a[0]) - bucketOf(b[0]));
  const shards = [];
  let cur = null, curBytes = 0;
  for (const [id, val] of entries) {
    const size = id.length + JSON.stringify(val).length + 8; // ~bytes this entry adds
    if (!cur || (curBytes + size > targetBytes && Object.keys(cur).length)) {
      cur = {}; curBytes = 0;
      shards.push({ min: bucketOf(id), contents: cur });
    }
    cur[id] = val; curBytes += size;
  }
  return shards;
}

// Write a plan's shards to disk and return manifest-ready descriptors. Balanced shards
// carry their `min`; fixed shards are emitted under a logical -N suffix.
function emitShards(prefix, plan) {
  return plan.map((s, i) => {
    const file = emit(`${prefix}-${s.min ?? i}`, s.contents);
    return s.min != null ? { min: s.min, file } : { file };
  });
}

// Summarise a plan's shard-size distribution (gzip-equivalent ratio aside, raw bytes).
function sizeStats(plan) {
  const sizes = plan.map((s) => JSON.stringify(s.contents).length).sort((a, b) => a - b);
  const kb = (n) => +(n / 1024).toFixed(1);
  return {
    shards: sizes.length, max: kb(sizes.at(-1)), min: kb(sizes[0]),
    avg: kb(sizes.reduce((n, x) => n + x, 0) / sizes.length),
    p95: kb(sizes[Math.min(sizes.length - 1, Math.floor(sizes.length * 0.95))]),
  };
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
const stickerId = (k) => Number(k);
const imageId = (k) => Number(k.split("_")[1]); // bucket images on paintindex

const stickerPlan = planBalanced(stickers.stickers, STICKER_TARGET, stickerId);
const imagePlan = planBalanced(skinImages, IMAGE_TARGET, imageId);
const stickerShards = emitShards("stickers", stickerPlan);
const imageShards = emitShards("images", imagePlan);
const keychainsFile = emit("keychains", stickers.keychains); // only ~78 entries, ship whole
const floatFile = emit("float-ranges", floatRanges);

// Compare the two strategies on the same data (sizes only; fixed plan isn't emitted).
const compare = (label, map, idOf, target, bucket) => {
  const fixed = sizeStats(planFixed(map, bucket, idOf));
  const balanced = sizeStats(planBalanced(map, target, idOf));
  return { label, fixed, balanced };
};
const comparisons = [
  compare("stickers", stickers.stickers, stickerId, STICKER_TARGET, STICKER_BUCKET),
  compare("images", skinImages, imageId, IMAGE_TARGET, IMAGE_BUCKET),
];

// --- manifest ---------------------------------------------------------------
// `version` is a hash of every shard filename, so any data change yields a new version
// string the client can surface ("data updated, reloading"). The manifest itself is the
// ONLY file served with no-cache; every shard it points at is immutable + cache-forever.
const manifest = {
  version: createHash("sha256").update(written.map((w) => w.name).sort().join()).digest("hex").slice(0, 12),
  builtFrom: { strategy: "balanced", stickerTarget: STICKER_TARGET, imageTarget: IMAGE_TARGET },
  shards: {
    constCore: constCoreFile,
    constSpecial: constSpecialFile,
    keychains: keychainsFile,
    floatRanges: floatFile,
    // Balanced shards: each entry is { min, file } in ascending `min` order. The client
    // binary-searches for the largest min <= id. The bucket size is no longer uniform.
    stickers: { strategy: "balanced", shards: stickerShards },
    images: { strategy: "balanced", shards: imageShards },
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
const kb = (name) => (written.find((w) => w.name === name).bytes / 1024).toFixed(1);
console.log(`manifest version ${manifest.version}`);
console.log(`wrote ${written.length} shards, ${(total / 1024).toFixed(0)} KB total (uncompressed)`);
console.log(`  const-core      ${kb(constCoreFile)} KB (loaded once, every item)`);
console.log(`  const-special   ${kb(constSpecialFile)} KB (only fade/fire&ice)`);
console.log("\nbucketing comparison (raw KB per shard):");
console.log("  catalog    strategy   shards   max     min     avg     p95");
for (const c of comparisons) {
  for (const [strat, s] of [["fixed/1000", c.fixed], ["balanced", c.balanced]]) {
    console.log(
      `  ${c.label.padEnd(9)}  ${strat.padEnd(10)} ${String(s.shards).padStart(4)}   ` +
      `${String(s.max).padStart(6)}  ${String(s.min).padStart(6)}  ${String(s.avg).padStart(6)}  ${String(s.p95).padStart(6)}`,
    );
  }
}
console.log(`\nwrote ${sampleOut.length} sample links to site/samples.json`);
