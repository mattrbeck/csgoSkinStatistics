#!/usr/bin/env node
// Refreshes the skin data we derive from the CS2 game files, using ByMykel/CSGO-API
// (community-maintained JSON regenerated from items_game.txt + localization files):
//
//   - const.json "skins"  (paint index -> skin name) - adds new paint kits, fixes renames
//   - const.json "items"  (defindex   -> weapon name) - same, for weapons that have skins
//   - wwwroot/float-ranges.json (paint index -> [min_float, max_float]) - the wear range
//     each paint kit can roll; the inventory page dims the unreachable parts of its
//     float bars with this.
//
// Run from the repo root: node scripts/update-skin-data.mjs
// The script prints every change it makes; review the diff before committing.

import { readFile, writeFile } from 'node:fs/promises';

const SOURCE = 'https://raw.githubusercontent.com/ByMykel/CSGO-API/main/public/api/en/skins.json';
const CONST_PATH = 'const.json';
const RANGES_PATH = 'wwwroot/float-ranges.json';

const response = await fetch(SOURCE);
if (!response.ok) {
  console.error(`Failed to fetch ${SOURCE}: HTTP ${response.status}`);
  process.exit(1);
}
const sourceSkins = await response.json();
console.log(`Fetched ${sourceSkins.length} weapon-skin entries`);

const constData = JSON.parse(await readFile(CONST_PATH, 'utf8'));
constData.skins ??= {};
constData.items ??= {};

// Merge a key -> name map, reporting additions and renames. Game data wins on
// conflict: const.json drift (typos, renamed weapons) gets corrected.
function merge(map, key, name, label) {
  if (!(key in map)) {
    console.log(`  + ${label} ${key}: ${name}`);
    map[key] = name;
  } else if (map[key] !== name) {
    console.log(`  ~ ${label} ${key}: ${map[key]} -> ${name}`);
    map[key] = name;
  }
}

const floatRanges = {};
const conflicts = [];

for (const skin of sourceSkins) {
  const paintIndex = skin.paint_index;
  if (paintIndex) {
    if (skin.pattern?.name) {
      merge(constData.skins, paintIndex, skin.pattern.name, 'skin');
    }
    const range = [skin.min_float ?? 0, skin.max_float ?? 1];
    const existing = floatRanges[paintIndex];
    if (existing && (existing[0] !== range[0] || existing[1] !== range[1])) {
      conflicts.push(`${paintIndex}: [${existing}] vs [${range}]`);
    }
    floatRanges[paintIndex] = range;
  }
  if (skin.weapon?.weapon_id != null && skin.weapon.name) {
    merge(constData.items, String(skin.weapon.weapon_id), skin.weapon.name, 'item');
  }
}

if (conflicts.length > 0) {
  // Has never happened in practice; a paint kit has one wear range by definition.
  console.warn(`WARNING: conflicting float ranges for paint indexes:\n  ${conflicts.join('\n  ')}`);
}

await writeFile(CONST_PATH, JSON.stringify(constData, null, 2) + '\n');
await writeFile(RANGES_PATH, JSON.stringify(floatRanges) + '\n');
console.log(`Wrote ${Object.keys(constData.skins).length} skins / ${Object.keys(constData.items).length} items to ${CONST_PATH}`);
console.log(`Wrote ${Object.keys(floatRanges).length} float ranges to ${RANGES_PATH}`);
