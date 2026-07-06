#!/usr/bin/env node
/*
 * Harvest the Fade / Amber Fade / Acid Fade percentages and write fade.json
 * (pattern -> weapon -> [percentage per paint seed 0..1000]), so the app can label a decoded
 * fade skin with its exact percentage.
 *
 *   Source: chescos/csgo-fade-percentage-calculator `generated/*.json`
 *   (https://github.com/chescos/csgo-fade-percentage-calculator), MIT-licensed (Copyright (c) 2022
 *   chescos). That project ports Valve's pattern RNG (via Step7750's implementation) to derive each
 *   seed's rotation, then ranks rotations into the 80-100% fade band - the market-standard method
 *   CSFloat / Skinport / CSGOSKINS.GG use. It ships the fully precomputed per-seed tables, so we
 *   consume those directly (no Node at runtime, no algorithm to re-derive).
 *   See THIRD_PARTY_NOTICES.md for the license + attribution.
 *
 * This replaces the old shared `fade_order` rank table + per-weapon `reversed` flag in const.json,
 * which linearly mapped one global ranking onto every weapon and (mis)reused that table for Amber
 * Fade. The real tables are per-weapon and rotation-accurate, and add Acid Fade (SSG 08).
 *
 * Output (weapon names match const.json GetWeaponName; pattern names match GetPatternName):
 *   { "Fade": { "AWP": [95.1, 95.1, 86.2, ...1001 entries], ... },
 *     "Amber Fade": { "AUG": [...], ... },
 *     "Acid Fade": { "SSG 08": [...] } }
 *   Percentages are rounded to 1 decimal (the display precision); index = paint seed.
 * Re-run to refresh: `node scripts/update_fade.js`
 */
'use strict';
const https = require('https');
const fs = require('fs');
const path = require('path');

const BASE = 'https://raw.githubusercontent.com/chescos/csgo-fade-percentage-calculator/master/generated';
// Generated file -> the pattern name the app uses (const.json "skins", GetPatternName).
const SOURCES = [
  ['fade-percentages.json', 'Fade'],
  ['amber-fade-percentages.json', 'Amber Fade'],
  ['acid-fade-percentages.json', 'Acid Fade'],
];
const OUT_PATH = path.join(__dirname, '..', 'fade.json');
const CONST = require(path.join(__dirname, '..', 'const.json')); // items: defindex -> weapon name

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
  console.log('Harvesting fade percentages from chescos/csgo-fade-percentage-calculator (MIT) …');
  const knownWeapons = new Set(Object.values(CONST.items || {}));

  const out = {};
  let totalWeapons = 0;
  for (const [file, pattern] of SOURCES) {
    const weapons = JSON.parse(await get(`${BASE}/${file}`));
    const byWeapon = {};
    for (const { weapon, percentages } of weapons) {
      // A weapon the const.json catalog doesn't know can never be matched at decode time, so it
      // would just be dead data - skip it loudly rather than silently ship an unusable table.
      if (!knownWeapons.has(weapon)) {
        console.warn(`  ! ${pattern}: "${weapon}" not in const.json items - skipped`);
        continue;
      }
      // Size the table to the weapon's actual seed coverage: knives run 0..999, guns 0..1000. That
      // way an out-of-range seed (e.g. 1000 on a knife) trips the backend's length guard and falls
      // through unlabelled instead of reading a zero-filled hole as "0%". Round to the 1-decimal
      // display precision (the backend just appends "%").
      const maxSeed = Math.max(...percentages.map((p) => p.seed));
      const table = new Array(maxSeed + 1);
      for (const { seed, percentage } of percentages) {
        table[seed] = Math.round(percentage * 10) / 10;
      }
      // No CS2 fade has an interior seed gap; a hole would serialize as null and misread downstream.
      const holes = [];
      for (let i = 0; i < table.length; i++) if (table[i] === undefined) holes.push(i);
      if (holes.length) {
        console.warn(`  ! ${pattern}: "${weapon}" has ${holes.length} seed gap(s) (e.g. ${holes[0]})`);
      }
      byWeapon[weapon] = table;
      totalWeapons++;
    }
    out[pattern] = byWeapon;
  }

  fs.writeFileSync(OUT_PATH, JSON.stringify(out));
  console.log(`Wrote ${path.relative(process.cwd(), OUT_PATH)} — ${totalWeapons} weapons across ` +
    `${SOURCES.map(([, p]) => p).join(', ')}.`);
}

main().catch((e) => { console.error('FAILED:', e.message); process.exit(1); });
