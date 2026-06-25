/**
 * Tests for inventory.js - exercises the real functions exported by the module (not copies),
 * so a regression in wwwroot/inventory.js fails the suite.
 */

const {
  uint32ToFloat32,
  getWearFromFloat,
  getRarityFromNumber,
  getRarityValue,
  sortItems,
  filterItems,
  validateSteamId,
  extractSteamIdFromInput
} = require('../wwwroot/inventory.js');

describe('Inventory Utility Functions', () => {
  test('uint32ToFloat32 should convert correctly', () => {
    expect(uint32ToFloat32(1065353216)).toBeCloseTo(1.0);
    expect(uint32ToFloat32(1056964608)).toBeCloseTo(0.5);
  });

  test('getWearFromFloat should categorize wear correctly', () => {
    expect(getWearFromFloat(0.06)).toBe('Factory New');
    expect(getWearFromFloat(0.10)).toBe('Minimal Wear');
    expect(getWearFromFloat(0.25)).toBe('Field-Tested');
    expect(getWearFromFloat(0.40)).toBe('Well-Worn');
    expect(getWearFromFloat(0.50)).toBe('Battle-Scarred');
  });

  test('getWearFromFloat boundaries are exclusive upper bounds', () => {
    expect(getWearFromFloat(0.07)).toBe('Minimal Wear');
    expect(getWearFromFloat(0.15)).toBe('Field-Tested');
    expect(getWearFromFloat(0.38)).toBe('Well-Worn');
    expect(getWearFromFloat(0.45)).toBe('Battle-Scarred');
  });

  test('getRarityFromNumber should return correct rarities', () => {
    expect(getRarityFromNumber(1)).toBe('Consumer Grade');
    expect(getRarityFromNumber(3)).toBe('Mil-Spec Grade');
    expect(getRarityFromNumber(4)).toBe('Restricted');
    expect(getRarityFromNumber(6)).toBe('Covert');
    expect(getRarityFromNumber(99)).toBe('Unknown');
  });
});

describe('Inventory Item Sorting', () => {
  test('getRarityValue should return correct numeric values', () => {
    expect(getRarityValue('Consumer Grade')).toBe(1);
    expect(getRarityValue('Restricted')).toBe(4);
    expect(getRarityValue('Covert')).toBe(6);
    expect(getRarityValue('Extraordinary')).toBe(8);
    expect(getRarityValue('Unknown')).toBe(0);
  });

  test('getRarityValue should rank sticker/music/agent tiers by their color', () => {
    expect(getRarityValue('High Grade')).toBeGreaterThan(getRarityValue('Industrial Grade'));
    expect(getRarityValue('High Grade')).toBe(getRarityValue('Mil-Spec Grade'));
    expect(getRarityValue('Remarkable')).toBe(getRarityValue('Restricted'));
    expect(getRarityValue('Exotic')).toBe(getRarityValue('Classified'));
    expect(getRarityValue('Master')).toBe(getRarityValue('Covert'));
  });

  test('sortItems should sort by rarity correctly', () => {
    const items = [
      { originalIndex: 0, steamData: { name: 'Item A', rarity: 'Consumer Grade' }, detailedData: null },
      { originalIndex: 1, steamData: { name: 'Item B', rarity: 'Covert' }, detailedData: null },
      { originalIndex: 2, steamData: { name: 'Item C', rarity: 'Restricted' }, detailedData: null }
    ];

    const sorted = sortItems(items, 'rarity', 'desc');

    expect(sorted[0].steamData.rarity).toBe('Covert');
    expect(sorted[1].steamData.rarity).toBe('Restricted');
    expect(sorted[2].steamData.rarity).toBe('Consumer Grade');
  });

  test('sortItems should sort by name correctly', () => {
    const items = [
      { originalIndex: 0, steamData: { name: 'Zebra Gun' }, detailedData: null },
      { originalIndex: 1, steamData: { name: 'Alpha Gun' }, detailedData: null },
      { originalIndex: 2, steamData: { name: 'Beta Gun' }, detailedData: null }
    ];

    const sorted = sortItems(items, 'name', 'asc');

    expect(sorted.map(i => i.steamData.name)).toEqual(['Alpha Gun', 'Beta Gun', 'Zebra Gun']);
  });

  test('name sort should ignore the leading provenance prefixes', () => {
    const items = [
      { originalIndex: 0, steamData: { name: 'StatTrak™ AK-47 | Redline' }, detailedData: null },
      { originalIndex: 1, steamData: { name: 'M4A1-S | Hyper Beast' }, detailedData: null },
      { originalIndex: 2, steamData: { name: '★ Bayonet | Fade' }, detailedData: null },
      { originalIndex: 3, steamData: { name: 'Souvenir AWP | Safari Mesh' }, detailedData: null }
    ];

    const sorted = sortItems(items, 'name', 'asc');

    expect(sorted.map(i => i.steamData.name)).toEqual([
      'StatTrak™ AK-47 | Redline', // ak
      'Souvenir AWP | Safari Mesh', // awp
      '★ Bayonet | Fade',           // bayonet
      'M4A1-S | Hyper Beast'        // m4
    ]);
  });

  test('float sort should sink items without a float in both directions', () => {
    const items = [
      { originalIndex: 0, steamData: { name: 'Mid' }, detailedData: { paintindex: 44, paintwear: 1036831949 } },  // 0.1
      { originalIndex: 1, steamData: { name: 'Unanalyzed' }, detailedData: null },
      { originalIndex: 2, steamData: { name: 'High' }, detailedData: { paintindex: 44, paintwear: 1060320051 } }, // 0.7
      { originalIndex: 3, steamData: { name: 'Medal' }, detailedData: { paintindex: 0, paintwear: 0 } },
      { originalIndex: 4, steamData: { name: 'Zero' }, detailedData: { paintindex: 44, paintwear: 0 } }           // 0.0
    ];

    const asc = sortItems(items, 'float', 'asc').map(i => i.steamData.name);
    expect(asc.slice(0, 3)).toEqual(['Zero', 'Mid', 'High']);
    expect(asc.slice(3).sort()).toEqual(['Medal', 'Unanalyzed']);

    const desc = sortItems(items, 'float', 'desc').map(i => i.steamData.name);
    expect(desc.slice(0, 3)).toEqual(['High', 'Mid', 'Zero']);
    expect(desc.slice(3).sort()).toEqual(['Medal', 'Unanalyzed']);
  });
});

describe('Inventory Filtering', () => {
  const base = {
    rarity: '', wear: '', floatMin: null, floatMax: null, hideCommemorative: false,
    search: '', type: '', star: false, stattrak: false, souvenir: false, special: false
  };

  test('should filter by rarity correctly', () => {
    const items = [
      { steamData: { rarity: 'Consumer Grade' }, detailedData: null },
      { steamData: { rarity: 'Covert' }, detailedData: null },
      { steamData: { rarity: 'Consumer Grade' }, detailedData: null }
    ];

    const filtered = filterItems(items, { ...base, rarity: 'Consumer Grade' });

    expect(filtered).toHaveLength(2);
    expect(filtered.every(item => item.steamData.rarity === 'Consumer Grade')).toBe(true);
  });

  test('should filter by wear correctly', () => {
    const items = [
      { steamData: { wear: 'Factory New' }, detailedData: null },
      { steamData: { wear: 'Field-Tested' }, detailedData: null },
      { steamData: { wear: 'Factory New' }, detailedData: null }
    ];

    const filtered = filterItems(items, { ...base, wear: 'Factory New' });

    expect(filtered).toHaveLength(2);
    expect(filtered.every(item => item.steamData.wear === 'Factory New')).toBe(true);
  });

  test('should filter by float range correctly', () => {
    const items = [
      { steamData: { wear: 'Factory New' }, detailedData: { paintindex: 44, paintwear: 1036831949 } }, // 0.1
      { steamData: { wear: 'Field-Tested' }, detailedData: { paintindex: 44, paintwear: 1060320051 } }, // 0.7
      { steamData: { wear: 'Minimal Wear' }, detailedData: { paintindex: 44, paintwear: 1028443341 } }  // 0.05
    ];

    const filtered = filterItems(items, { ...base, floatMin: 0.08, floatMax: 0.5 });

    expect(filtered).toHaveLength(1);
    expect(filtered[0].steamData.wear).toBe('Factory New');
  });

  test('attribute chips should AND together (StatTrak knives match the star and ST chips)', () => {
    const items = [
      { steamData: { name: '★ StatTrak™ Karambit | Doppler', item_type: 'Knife' }, detailedData: null },
      { steamData: { name: '★ Karambit | Fade', item_type: 'Knife' }, detailedData: null },
      { steamData: { name: 'StatTrak™ AK-47 | Redline', item_type: 'Rifle' }, detailedData: null }
    ];

    const starAndSt = filterItems(items, { ...base, star: true, stattrak: true });
    expect(starAndSt).toHaveLength(1);
    expect(starAndSt[0].steamData.name).toBe('★ StatTrak™ Karambit | Doppler');

    // GC-detected StatTrak counts even when the Steam name lacks the prefix
    const gcStattrak = filterItems(
      [{ steamData: { name: 'Music Kit | Some Artist' }, detailedData: { stattrak: true } }],
      { ...base, stattrak: true });
    expect(gcStattrak).toHaveLength(1);
  });

  test('text search matches across normalized tokens, order-independent', () => {
    const items = [
      { steamData: { name: '★ Specialist Gloves | Crimson Kimono' }, detailedData: null },
      { steamData: { name: 'AK-47 | Redline' }, detailedData: null }
    ];

    expect(filterItems(items, { ...base, search: 'gloves crimson' })).toHaveLength(1);
    expect(filterItems(items, { ...base, search: 'redline' })).toHaveLength(1);
    expect(filterItems(items, { ...base, search: 'nonexistent' })).toHaveLength(0);
  });

  test('should filter by item type', () => {
    const items = [
      { steamData: { name: 'AK-47 | Redline', item_type: 'Rifle' }, detailedData: null },
      { steamData: { name: 'Sticker | Crown (Foil)', item_type: 'Sticker' }, detailedData: null },
      { steamData: { name: 'Mystery Item' }, detailedData: null } // no item_type -> Other
    ];

    expect(filterItems(items, { ...base, type: 'Rifle' })).toHaveLength(1);
    expect(filterItems(items, { ...base, type: 'Other' })).toHaveLength(1);
    expect(filterItems(items, { ...base, type: '' })).toHaveLength(3);
  });

  test('should hide commemorative items correctly', () => {
    const items = [
      { steamData: { rarity: 'Consumer Grade' }, detailedData: { paintindex: 0 } },  // commemorative
      { steamData: { rarity: 'Covert' }, detailedData: { paintindex: 179 } }          // real skin
    ];

    const filtered = filterItems(items, { ...base, hideCommemorative: true });

    expect(filtered).toHaveLength(1);
    expect(filtered[0].detailedData.paintindex).toBe(179);
  });
});

describe('Steam ID Validation', () => {
  test('should validate correct Steam IDs', () => {
    ['76561198123456789', '76561199000000000', '76561198000000001']
      .forEach(id => expect(validateSteamId(id)).toBe(true));
  });

  test('should reject invalid Steam IDs', () => {
    [
      '123456789',
      '76561187123456789', // wrong prefix
      '765611981234567890', // too long
      '7656119812345678', // too short
      'invalid',
      ''
    ].forEach(id => expect(validateSteamId(id)).toBe(false));
  });

  test('should extract Steam ID from profile URLs', () => {
    const cases = [
      ['https://steamcommunity.com/profiles/76561198123456789', '76561198123456789'],
      ['steamcommunity.com/profiles/76561198123456789', '76561198123456789'],
      ['76561198123456789', '76561198123456789'],
      ['https://steamcommunity.com/id/customurl', null],
      ['invalid', null]
    ];

    cases.forEach(([input, expected]) => expect(extractSteamIdFromInput(input)).toBe(expected));
  });
});
