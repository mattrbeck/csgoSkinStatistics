/**
 * Tests for inventory.js - Inventory analysis functionality
 */

// Mock custom elements
class MockInventoryItem {
  constructor() {
    this.shadowRoot = { appendChild: jest.fn(), querySelector: jest.fn() };
    this.classList = { add: jest.fn(), remove: jest.fn() };
    this.itemData = {};
    this.itemIndex = 0;
    this.needsUpdate = false;
  }

  connectedCallback() {
    this.render();
  }

  render() {
    // Mock render
  }

  setItemData(item, index) {
    this.itemData = item;
    this.itemIndex = index;
  }

  updateWithDetails(itemData, inspectLink) {
    this.classList.remove('loading');
    this.classList.add('loaded');
  }
}

// Mock custom elements define
global.customElements = {
  define: jest.fn()
};

describe('Inventory Utility Functions', () => {
  const conversionBuffer = new ArrayBuffer(4);
  const conversionView = new DataView(conversionBuffer);

  function uint32ToFloat32(uint32Value) {
    conversionView.setUint32(0, uint32Value);
    return conversionView.getFloat32(0);
  }

  function getWearFromFloat(float) {
    if (float < 0.07) return "Factory New";
    if (float < 0.15) return "Minimal Wear";
    if (float < 0.38) return "Field-Tested";
    if (float < 0.45) return "Well-Worn";
    return "Battle-Scarred";
  }

  function getRarityFromNumber(rarityNumber) {
    const rarities = [
      "Default",
      "Consumer Grade",
      "Industrial Grade",
      "Mil-Spec Grade",
      "Restricted",
      "Classified",
      "Covert",
      "Contraband",
    ];
    return rarities[rarityNumber] || "Unknown";
  }

  test('uint32ToFloat32 should convert correctly', () => {
    expect(uint32ToFloat32(1065353216)).toBeCloseTo(1.0);
    expect(uint32ToFloat32(1056964608)).toBeCloseTo(0.5);
  });

  test('getWearFromFloat should categorize wear correctly', () => {
    expect(getWearFromFloat(0.06)).toBe("Factory New");
    expect(getWearFromFloat(0.10)).toBe("Minimal Wear");
    expect(getWearFromFloat(0.25)).toBe("Field-Tested");
    expect(getWearFromFloat(0.40)).toBe("Well-Worn");
    expect(getWearFromFloat(0.50)).toBe("Battle-Scarred");
  });

  test('getRarityFromNumber should return correct rarities', () => {
    expect(getRarityFromNumber(1)).toBe("Consumer Grade");
    expect(getRarityFromNumber(4)).toBe("Restricted");
    expect(getRarityFromNumber(6)).toBe("Covert");
    expect(getRarityFromNumber(99)).toBe("Unknown");
  });
});

describe('Inventory Item Sorting', () => {
  function getRarityValue(rarity) {
    const rarityOrder = {
      'Consumer Grade': 1,
      'Base Grade': 1,
      'Industrial Grade': 2,
      'Mil-Spec Grade': 3,
      'High Grade': 3,
      'Distinguished': 3,
      'Restricted': 4,
      'Remarkable': 4,
      'Exceptional': 4,
      'Classified': 5,
      'Exotic': 5,
      'Superior': 5,
      'Covert': 6,
      'Master': 6,
      'Contraband': 7,
      'Extraordinary': 8
    };
    return rarityOrder[rarity] || 0;
  }

  function nameSortKey(steamData) {
    return (steamData.name || '')
      .replace(/^★\s*/, '')
      .replace(/^StatTrak™\s*/, '')
      .replace(/^Souvenir\s+/, '')
      .toLowerCase();
  }

  function getItemFloat(item) {
    const conversionBuffer = new ArrayBuffer(4);
    const conversionView = new DataView(conversionBuffer);
    if (!item.detailedData || item.detailedData.paintwear == null ||
        Number(item.detailedData.paintindex) === 0) {
      return null;
    }
    conversionView.setUint32(0, item.detailedData.paintwear);
    return conversionView.getFloat32(0);
  }

  function sortItems(items, field, order) {
    return [...items].sort((a, b) => {
      let valueA, valueB;

      switch (field) {
        case 'name':
          if (a.nameSortKey === undefined) a.nameSortKey = nameSortKey(a.steamData);
          if (b.nameSortKey === undefined) b.nameSortKey = nameSortKey(b.steamData);
          valueA = a.nameSortKey;
          valueB = b.nameSortKey;
          break;
        case 'rarity':
          valueA = getRarityValue(a.steamData.rarity || '');
          valueB = getRarityValue(b.steamData.rarity || '');
          break;
        case 'float':
          valueA = getItemFloat(a);
          valueB = getItemFloat(b);
          // Items without a float sink to the end in both directions
          if (valueA === null || valueB === null) {
            if (valueA === valueB) return 0;
            return valueA === null ? 1 : -1;
          }
          break;
        case 'date':
        default:
          valueA = a.originalIndex;
          valueB = b.originalIndex;
          break;
      }

      if (valueA < valueB) return order === 'asc' ? -1 : 1;
      if (valueA > valueB) return order === 'asc' ? 1 : -1;
      return 0;
    });
  }

  test('getRarityValue should return correct numeric values', () => {
    expect(getRarityValue('Consumer Grade')).toBe(1);
    expect(getRarityValue('Restricted')).toBe(4);
    expect(getRarityValue('Covert')).toBe(6);
    expect(getRarityValue('Extraordinary')).toBe(8);
    expect(getRarityValue('Unknown')).toBe(0);
  });

  test('getRarityValue should rank sticker/music/agent tiers by their color', () => {
    // High Grade is the blue tier: above Industrial, equal to Mil-Spec
    expect(getRarityValue('High Grade')).toBeGreaterThan(getRarityValue('Industrial Grade'));
    expect(getRarityValue('High Grade')).toBe(getRarityValue('Mil-Spec Grade'));
    expect(getRarityValue('Remarkable')).toBe(getRarityValue('Restricted'));
    expect(getRarityValue('Exotic')).toBe(getRarityValue('Classified'));
    expect(getRarityValue('Master')).toBe(getRarityValue('Covert'));
  });

  test('sortItems should sort by rarity correctly', () => {
    const items = [
      {
        originalIndex: 0,
        steamData: { name: 'Item A', rarity: 'Consumer Grade', wear: 'Factory New' },
        detailedData: null
      },
      {
        originalIndex: 1,
        steamData: { name: 'Item B', rarity: 'Covert', wear: 'Field-Tested' },
        detailedData: null
      },
      {
        originalIndex: 2,
        steamData: { name: 'Item C', rarity: 'Restricted', wear: 'Minimal Wear' },
        detailedData: null
      }
    ];

    const sorted = sortItems(items, 'rarity', 'desc');

    expect(sorted[0].steamData.rarity).toBe('Covert');
    expect(sorted[1].steamData.rarity).toBe('Restricted');
    expect(sorted[2].steamData.rarity).toBe('Consumer Grade');
  });

  test('sortItems should sort by name correctly', () => {
    const items = [
      {
        originalIndex: 0,
        steamData: { name: 'Zebra Gun', rarity: 'Consumer Grade', wear: 'Factory New' },
        detailedData: null
      },
      {
        originalIndex: 1,
        steamData: { name: 'Alpha Gun', rarity: 'Covert', wear: 'Field-Tested' },
        detailedData: null
      },
      {
        originalIndex: 2,
        steamData: { name: 'Beta Gun', rarity: 'Restricted', wear: 'Minimal Wear' },
        detailedData: null
      }
    ];

    const sorted = sortItems(items, 'name', 'asc');

    expect(sorted[0].steamData.name).toBe('Alpha Gun');
    expect(sorted[1].steamData.name).toBe('Beta Gun');
    expect(sorted[2].steamData.name).toBe('Zebra Gun');
  });

  test('name sort should ignore ★/StatTrak™/Souvenir prefixes', () => {
    const items = [
      { originalIndex: 0, steamData: { name: 'StatTrak™ AK-47 | Redline' }, detailedData: null },
      { originalIndex: 1, steamData: { name: 'M4A1-S | Hyper Beast' }, detailedData: null },
      { originalIndex: 2, steamData: { name: '★ Bayonet | Fade' }, detailedData: null },
      { originalIndex: 3, steamData: { name: 'Souvenir AWP | Safari Mesh' }, detailedData: null }
    ];

    const sorted = sortItems(items, 'name', 'asc');

    expect(sorted.map(i => i.steamData.name)).toEqual([
      'StatTrak™ AK-47 | Redline', // A
      'Souvenir AWP | Safari Mesh', // A
      '★ Bayonet | Fade',           // B
      'M4A1-S | Hyper Beast'        // M
    ]);
  });

  test('float sort should sink items without a float in both directions', () => {
    const items = [
      { originalIndex: 0, steamData: { name: 'Mid' }, detailedData: { paintindex: 44, paintwear: 1036831949 } },  // 0.1
      { originalIndex: 1, steamData: { name: 'Unanalyzed' }, detailedData: null },
      { originalIndex: 2, steamData: { name: 'High' }, detailedData: { paintindex: 44, paintwear: 1060320051 } }, // 0.7
      { originalIndex: 3, steamData: { name: 'Medal' }, detailedData: { paintindex: 0, paintwear: 0 } },
      { originalIndex: 4, steamData: { name: 'Zero' }, detailedData: { paintindex: 44, paintwear: 0 } }           // true 0.0
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
  function filterItems(items, filters) {
    const conversionBuffer = new ArrayBuffer(4);
    const conversionView = new DataView(conversionBuffer);
    function uint32ToFloat32(uint32Value) {
      conversionView.setUint32(0, uint32Value);
      return conversionView.getFloat32(0);
    }

    return items.filter(item => {
      // Hide commemorative items filter (paintindex 0)
      if (filters.hideCommemorative &&
          item.detailedData &&
          item.detailedData.paintindex === 0) {
        return false;
      }

      // Attribute chips (independent AND filters)
      function isStarItem(steamData) {
        return ((steamData && steamData.name) || '').includes('★') ||
               ((steamData && steamData.type) || '').includes('★');
      }
      if (filters.star && !isStarItem(item.steamData)) {
        return false;
      }
      if (filters.stattrak &&
          !(item.steamData.name || '').includes('StatTrak™') &&
          !(item.detailedData && item.detailedData.stattrak)) {
        return false;
      }
      if (filters.souvenir && item.steamData.quality !== 'Souvenir') {
        return false;
      }
      if (filters.special && !(item.detailedData && item.detailedData.special)) {
        return false;
      }

      // Item type filter (Steam's "Type" tag)
      if (filters.type && (item.steamData.item_type || 'Other') !== filters.type) {
        return false;
      }

      // Rarity filter
      if (filters.rarity && item.steamData.rarity !== filters.rarity) {
        return false;
      }

      // Wear filter
      if (filters.wear && item.steamData.wear !== filters.wear) {
        return false;
      }

      // Float range filter. Items without a float (unanalyzed, paint-less) pass through.
      if (filters.floatMin !== null || filters.floatMax !== null) {
        const hasFloat = item.detailedData && item.detailedData.paintwear != null &&
                         Number(item.detailedData.paintindex) !== 0;
        const itemFloat = hasFloat ? uint32ToFloat32(item.detailedData.paintwear) : null;
        if (itemFloat !== null) {
          if (filters.floatMin !== null && itemFloat < filters.floatMin) {
            return false;
          }
          if (filters.floatMax !== null && itemFloat > filters.floatMax) {
            return false;
          }
        }
      }

      return true;
    });
  }

  test('should filter by rarity correctly', () => {
    const items = [
      {
        steamData: { rarity: 'Consumer Grade', wear: 'Factory New' },
        detailedData: null
      },
      {
        steamData: { rarity: 'Covert', wear: 'Field-Tested' },
        detailedData: null
      },
      {
        steamData: { rarity: 'Consumer Grade', wear: 'Minimal Wear' },
        detailedData: null
      }
    ];

    const filters = { rarity: 'Consumer Grade', wear: '', floatMin: null, floatMax: null, hideCommemorative: false };
    const filtered = filterItems(items, filters);

    expect(filtered).toHaveLength(2);
    expect(filtered.every(item => item.steamData.rarity === 'Consumer Grade')).toBe(true);
  });

  test('should filter by wear correctly', () => {
    const items = [
      {
        steamData: { rarity: 'Consumer Grade', wear: 'Factory New' },
        detailedData: null
      },
      {
        steamData: { rarity: 'Covert', wear: 'Field-Tested' },
        detailedData: null
      },
      {
        steamData: { rarity: 'Consumer Grade', wear: 'Factory New' },
        detailedData: null
      }
    ];

    const filters = { rarity: '', wear: 'Factory New', floatMin: null, floatMax: null, hideCommemorative: false };
    const filtered = filterItems(items, filters);

    expect(filtered).toHaveLength(2);
    expect(filtered.every(item => item.steamData.wear === 'Factory New')).toBe(true);
  });

  test('should filter by float range correctly', () => {
    const items = [
      {
        steamData: { rarity: 'Consumer Grade', wear: 'Factory New' },
        detailedData: { paintwear: 1036831949 } // 0.1
      },
      {
        steamData: { rarity: 'Covert', wear: 'Field-Tested' },
        detailedData: { paintwear: 1060320051 } // 0.7
      },
      {
        steamData: { rarity: 'Consumer Grade', wear: 'Minimal Wear' },
        detailedData: { paintwear: 1028443341 } // 0.05
      }
    ];

    const filters = { rarity: '', wear: '', floatMin: 0.08, floatMax: 0.5, hideCommemorative: false };
    const filtered = filterItems(items, filters);

    expect(filtered).toHaveLength(1);
    expect(filtered[0].steamData.wear).toBe('Factory New');
  });

  test('attribute chips should AND together (StatTrak knives match ★ + ST)', () => {
    const items = [
      {
        steamData: { name: '★ StatTrak™ Karambit | Doppler', item_type: 'Knife' },
        detailedData: null
      },
      {
        steamData: { name: '★ Karambit | Fade', item_type: 'Knife' },
        detailedData: null
      },
      {
        steamData: { name: 'StatTrak™ AK-47 | Redline', item_type: 'Rifle' },
        detailedData: null
      }
    ];

    const base = { rarity: '', wear: '', floatMin: null, floatMax: null, hideCommemorative: false, type: '' };

    const starAndSt = filterItems(items, { ...base, star: true, stattrak: true });
    expect(starAndSt).toHaveLength(1);
    expect(starAndSt[0].steamData.name).toBe('★ StatTrak™ Karambit | Doppler');

    // GC-detected StatTrak counts even when the Steam name lacks the prefix
    const gcStattrak = filterItems(
      [{ steamData: { name: 'Music Kit | Some Artist' }, detailedData: { stattrak: true } }],
      { ...base, stattrak: true });
    expect(gcStattrak).toHaveLength(1);
  });

  test('should filter by item type', () => {
    const items = [
      { steamData: { name: 'AK-47 | Redline', item_type: 'Rifle' }, detailedData: null },
      { steamData: { name: 'Sticker | Crown (Foil)', item_type: 'Sticker' }, detailedData: null },
      { steamData: { name: 'Mystery Item' }, detailedData: null } // no item_type -> Other
    ];

    const base = { rarity: '', wear: '', floatMin: null, floatMax: null, hideCommemorative: false };

    expect(filterItems(items, { ...base, type: 'Rifle' })).toHaveLength(1);
    expect(filterItems(items, { ...base, type: 'Other' })).toHaveLength(1);
    expect(filterItems(items, { ...base, type: '' })).toHaveLength(3);
  });

  test('should hide commemorative items correctly', () => {
    const items = [
      {
        steamData: { rarity: 'Consumer Grade', wear: 'Factory New' },
        detailedData: { paintindex: 0 } // Commemorative item
      },
      {
        steamData: { rarity: 'Covert', wear: 'Field-Tested' },
        detailedData: { paintindex: 179 } // Regular skin
      }
    ];

    const filters = { rarity: '', wear: '', floatMin: null, floatMax: null, hideCommemorative: true };
    const filtered = filterItems(items, filters);

    expect(filtered).toHaveLength(1);
    expect(filtered[0].detailedData.paintindex).toBe(179);
  });
});

describe('Steam ID Validation', () => {
  function validateSteamId(steamId) {
    const steamId64Regex = /^7656119\d{10}$/;
    return steamId64Regex.test(steamId);
  }

  function extractSteamIdFromInput(input) {
    // Check if it's already a valid SteamId64
    if (validateSteamId(input)) {
      return input;
    }

    // Try to extract from Steam profile URL
    const profileMatch = input.match(/steamcommunity\.com\/profiles\/(\d+)/);
    if (profileMatch && validateSteamId(profileMatch[1])) {
      return profileMatch[1];
    }

    // Return null for custom URLs or invalid input - let the server handle it
    return null;
  }

  test('should validate correct Steam IDs', () => {
    const validIds = [
      '76561198123456789',
      '76561199000000000',
      '76561198000000001'
    ];

    validIds.forEach(id => {
      expect(validateSteamId(id)).toBe(true);
    });
  });

  test('should reject invalid Steam IDs', () => {
    const invalidIds = [
      '123456789',
      '76561187123456789', // Wrong prefix
      '765611981234567890', // Too long
      '7656119812345678', // Too short
      'invalid',
      ''
    ];

    invalidIds.forEach(id => {
      expect(validateSteamId(id)).toBe(false);
    });
  });

  test('should extract Steam ID from profile URLs', () => {
    const testCases = [
      ['https://steamcommunity.com/profiles/76561198123456789', '76561198123456789'],
      ['steamcommunity.com/profiles/76561198123456789', '76561198123456789'],
      ['76561198123456789', '76561198123456789'],
      ['https://steamcommunity.com/id/customurl', null],
      ['invalid', null]
    ];

    testCases.forEach(([input, expected]) => {
      expect(extractSteamIdFromInput(input)).toBe(expected);
    });
  });
});

describe('InventoryItem Web Component', () => {
  test('should create inventory item correctly', () => {
    const item = {
      name: 'AK-47 | Redline',
      wear: 'Field-Tested',
      rarity: 'Classified',
      inspect_link: 'steam://test'
    };

    const inventoryItem = new MockInventoryItem();
    inventoryItem.setItemData(item, 0);

    expect(inventoryItem.itemData).toEqual(item);
    expect(inventoryItem.itemIndex).toBe(0);
  });

  test('should update item with details correctly', () => {
    const inventoryItem = new MockInventoryItem();
    const itemData = {
      weapon: 'AK-47',
      skin: 'Redline',
      stattrak: true,
      paintwear: 1061997773
    };

    inventoryItem.updateWithDetails(itemData, 'steam://test');

    expect(inventoryItem.classList.remove).toHaveBeenCalledWith('loading');
    expect(inventoryItem.classList.add).toHaveBeenCalledWith('loaded');
  });
});

describe('Progress Tracking', () => {
  test('should calculate progress correctly', () => {
    function updateProgress(completed, total) {
      const percentage = total > 0 ? (completed / total) * 100 : 0;
      return {
        percentage,
        text: `${completed} / ${total} detailed analyses complete`
      };
    }

    expect(updateProgress(5, 10)).toEqual({
      percentage: 50,
      text: '5 / 10 detailed analyses complete'
    });

    expect(updateProgress(0, 0)).toEqual({
      percentage: 0,
      text: '0 / 0 detailed analyses complete'
    });

    expect(updateProgress(10, 10)).toEqual({
      percentage: 100,
      text: '10 / 10 detailed analyses complete'
    });
  });
});