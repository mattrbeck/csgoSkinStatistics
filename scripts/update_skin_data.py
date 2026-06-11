#!/usr/bin/env python3
# Refreshes the skin data we derive from the CS2 game files, using ByMykel/CSGO-API
# (community-maintained JSON regenerated from items_game.txt + localization files):
#
#   - const.json "skins"  (paint index -> skin name) - adds new paint kits, fixes renames
#   - const.json "items"  (defindex   -> item name) - weapons that have skins, plus
#     collectibles (medals, coins, pins), crates/capsules, and keys, which all flow
#     through the same defindex -> name lookup when an inventory is decoded
#   - wwwroot/float-ranges.json (paint index -> [min_float, max_float]) - the wear range
#     each paint kit can roll; the inventory page dims the unreachable parts of its
#     float bars with this.
#
# Run from the repo root: python3 scripts/update_skin_data.py
# The script prints every change it makes; review the diff before committing.

import json
import sys
import urllib.error
import urllib.request

SOURCE = 'https://raw.githubusercontent.com/ByMykel/CSGO-API/main/public/api/en/skins.json'
# Non-weapon items whose names the server also resolves by defindex. Each entry's id
# has the form "<kind>-<defindex>" (e.g. "collectible-5270", "crate-4001").
EXTRA_ITEM_SOURCES = [
    'https://raw.githubusercontent.com/ByMykel/CSGO-API/main/public/api/en/collectibles.json',
    'https://raw.githubusercontent.com/ByMykel/CSGO-API/main/public/api/en/crates.json',
    'https://raw.githubusercontent.com/ByMykel/CSGO-API/main/public/api/en/keys.json',
]
CONST_PATH = 'const.json'
RANGES_PATH = 'wwwroot/float-ranges.json'


# Merge a key -> name map, reporting additions and renames. Game data wins on
# conflict: const.json drift (typos, renamed weapons) gets corrected.
def merge(mapping, key, name, label):
    if key not in mapping:
        print(f'  + {label} {key}: {name}')
        mapping[key] = name
    elif mapping[key] != name:
        print(f'  ~ {label} {key}: {mapping[key]} -> {name}')
        mapping[key] = name


# Integer-like keys ascending first, then the rest in insertion order - the
# ordering JS objects impose, which is what previously wrote these files.
def numeric_key_order(mapping):
    return dict(sorted(mapping.items(),
                       key=lambda kv: (0, int(kv[0])) if kv[0].isdigit() else (1, 0)))


def fetch_json(url):
    try:
        with urllib.request.urlopen(url) as response:
            return json.load(response)
    except urllib.error.HTTPError as error:
        print(f'Failed to fetch {url}: HTTP {error.code}', file=sys.stderr)
        sys.exit(1)


source_skins = fetch_json(SOURCE)
print(f'Fetched {len(source_skins)} weapon-skin entries')

with open(CONST_PATH, encoding='utf-8') as f:
    const_data = json.load(f)
const_data.setdefault('skins', {})
const_data.setdefault('items', {})

float_ranges = {}
conflicts = []

for skin in source_skins:
    paint_index = skin.get('paint_index')
    if paint_index:
        paint_index = str(paint_index)
        pattern_name = (skin.get('pattern') or {}).get('name')
        if pattern_name:
            merge(const_data['skins'], paint_index, pattern_name, 'skin')
        min_float = skin.get('min_float')
        max_float = skin.get('max_float')
        range_ = [0 if min_float is None else min_float,
                  1 if max_float is None else max_float]
        existing = float_ranges.get(paint_index)
        if existing is not None and existing != range_:
            conflicts.append(f'{paint_index}: {existing} vs {range_}')
        float_ranges[paint_index] = range_
    weapon = skin.get('weapon') or {}
    if weapon.get('weapon_id') is not None and weapon.get('name'):
        merge(const_data['items'], str(weapon['weapon_id']), weapon['name'], 'item')

if conflicts:
    # Has never happened in practice; a paint kit has one wear range by definition.
    print('WARNING: conflicting float ranges for paint indexes:\n  '
          + '\n  '.join(conflicts), file=sys.stderr)

for source in EXTRA_ITEM_SOURCES:
    entries = fetch_json(source)
    print(f'Fetched {len(entries)} entries from {source.rsplit("/", 1)[-1]}')
    for entry in entries:
        defindex = (entry.get('id') or '').rsplit('-', 1)[-1]
        if defindex.isdigit() and entry.get('name'):
            merge(const_data['items'], defindex, entry['name'], 'item')

const_data['skins'] = numeric_key_order(const_data['skins'])
const_data['items'] = numeric_key_order(const_data['items'])

with open(CONST_PATH, 'w', encoding='utf-8') as f:
    f.write(json.dumps(const_data, ensure_ascii=False, indent=2) + '\n')
with open(RANGES_PATH, 'w', encoding='utf-8') as f:
    f.write(json.dumps(numeric_key_order(float_ranges),
                       ensure_ascii=False, separators=(',', ':')) + '\n')
print(f'Wrote {len(const_data["skins"])} skins / {len(const_data["items"])} items to {CONST_PATH}')
print(f'Wrote {len(float_ranges)} float ranges to {RANGES_PATH}')
