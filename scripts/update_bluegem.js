#!/usr/bin/env node
/*
 * Harvest the Case Hardened / Heat Treated "blue gem" percentages and write blue-gem.json
 * (pattern -> weapon -> paint seed -> { playside %, backside % blue }), so the app can label a
 * decoded item with its blue coverage.
 *
 *   Source: csfloat/extension `data/bluegem.json` (https://github.com/csfloat/extension), MIT-licensed
 *   (Copyright (c) 2026 CSFloat Inc.). That file is CSFloat's published snapshot of the CSBlueGem
 *   (bluegem.app) numbers — its generator (tools/generate_bluegem_json.ts) states it "fetches the
 *   data from the bluegem.app API." So the percentages are the market-standard ones (AK-47 seed 661
 *   = 76.13% playside). We consume the MIT-licensed GitHub file — NOT csfloat.com's live service.
 *   See THIRD_PARTY_NOTICES.md for the license + attribution (CSFloat + CSBlueGem).
 *
 * We take only the BLUE percentages (playside/backside). The upstream file also carries purple/gold
 * coverage and contour pixel counts, which we ignore for now (future: gold/purple gems). No tier or
 * rank is derived — tiers were intentionally dropped for now.
 *
 * Output (pattern + weapon names match const.json, i.e. GetPatternName / GetWeaponName):
 *   { "Case Hardened": { "AK-47": { "661": { "pb": 76.13, "bb": 7.46 }, ... }, ... },
 *     "Heat Treated":  { "Desert Eagle": { ... } } }
 *   pb = playside blue %, bb = backside blue % (raw, unrounded; backend rounds for display).
 * Re-run to refresh: `node scripts/update_bluegem.js`
 */
'use strict';
const https = require('https');
const fs = require('fs');
const path = require('path');

const SRC = 'https://raw.githubusercontent.com/csfloat/extension/master/data/bluegem.json';
const OUT_PATH = path.join(__dirname, '..', 'blue-gem.json');
const CONST = require(path.join(__dirname, '..', 'const.json')); // items: defindex->weapon, skins: paintindex->pattern

// Keep a seed if EITHER face is at least this blue — the backend decides how to present it (knives
// use playside; the AK-47 also surfaces a blue "magazine"/backside face). The % reads the same as on
// CSBlueGem/CSFloat. AK Case Hardened tops out ~76% playside, knives ~95%.
const GEM_MIN = 30;

function get(url) {
  return new Promise((resolve, reject) => {
    https.get(url, { headers: { 'User-Agent': 'Mozilla/5.0' } }, (r) => {
      if (r.statusCode >= 300 && r.statusCode < 400 && r.headers.location) {
        return get(new URL(r.headers.location, url).href).then(resolve, reject);
      }
      if (r.statusCode !== 200) { r.resume(); return reject(new Error(`${r.statusCode} for ${url}`)); }
      let d = ''; r.on('data', (c) => (d += c)); r.on('end', () => resolve(d));
    }).on('error', reject);
  });
}

async function main() {
  console.log('Harvesting blue-gem percentages from csfloat/extension (MIT; CSBlueGem numbers) …');
  const bg = JSON.parse(await get(SRC));
  // csfloat file is keyed data[defIndex][paintIndex][paintSeed] = { playside_blue, backside_blue, ... }
  const data = bg.data || bg;

  const out = {};
  let total = 0;
  for (const [defIndex, byPaint] of Object.entries(data)) {
    const weapon = CONST.items?.[defIndex];
    if (!weapon) continue;
    const isGun = Number(defIndex) < 500; // guns are defindex 1/3/7/17; knives are 500+
    for (const [paintIndex, seeds] of Object.entries(byPaint)) {
      const pattern = CONST.skins?.[paintIndex]; // "Case Hardened" | "Heat Treated"
      if (!pattern) continue;
      for (const [seed, v] of Object.entries(seeds)) {
        const ps = v.playside_blue;
        const bs = typeof v.backside_blue === 'number' ? v.backside_blue : null;
        if (typeof ps !== 'number') continue;
        // Keep if the playside qualifies; also keep backside-only gems for GUNS (the AK-47's blue
        // "magazine"), but not for knives, which we judge on the playside alone.
        if (ps < GEM_MIN && !(isGun && bs != null && bs >= GEM_MIN)) continue;
        // Store RAW %s (unrounded) so backend thresholds like the AK-47's 38.4% playside cutoff apply
        // precisely; the backend rounds for display.
        ((out[pattern] ||= {})[weapon] ||= {})[seed] = { pb: ps, bb: bs };
        total++;
      }
    }
  }

  fs.writeFileSync(OUT_PATH, JSON.stringify(out));
  const weapons = new Set();
  for (const w of Object.values(out)) for (const k of Object.keys(w)) weapons.add(k);
  console.log(`Wrote ${path.relative(process.cwd(), OUT_PATH)} — ${total} gem seeds; ` +
    `patterns: ${Object.keys(out).join(', ')}; ${weapons.size} weapons.`);
}

main().catch((e) => { console.error('FAILED:', e.message); process.exit(1); });
