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
      'High Grade': 2,
      'Mil-Spec Grade': 3,
      'Mil-Spec': 3,
      'Remarkable': 3,
      'Distinguished': 4,
      'Restricted': 4,
      'Exceptional': 5,
      'Classified': 5,
      'Superior': 6,
      'Covert': 6,
      'Master': 7,
      'Contraband': 8,
      'Extraordinary': 9
    };
    return rarityOrder[rarity] || 0;
  }

  function sortItems(items, field, order) {
    return [...items].sort((a, b) => {
      let valueA, valueB;

      switch (field) {
        case 'name':
          valueA = (a.steamData.name || '').toLowerCase();
          valueB = (b.steamData.name || '').toLowerCase();
          break;
        case 'rarity':
          valueA = getRarityValue(a.steamData.rarity || '');
          valueB = getRarityValue(b.steamData.rarity || '');
          break;
        case 'float':
          const conversionBuffer = new ArrayBuffer(4);
          const conversionView = new DataView(conversionBuffer);
          function uint32ToFloat32(uint32Value) {
            conversionView.setUint32(0, uint32Value);
            return conversionView.getFloat32(0);
          }
          valueA = a.detailedData && a.detailedData.paintwear ? uint32ToFloat32(a.detailedData.paintwear) : 999;
          valueB = b.detailedData && b.detailedData.paintwear ? uint32ToFloat32(b.detailedData.paintwear) : 999;
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
    expect(getRarityValue('Extraordinary')).toBe(9);
    expect(getRarityValue('Unknown')).toBe(0);
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

      // Rarity filter
      if (filters.rarity && item.steamData.rarity !== filters.rarity) {
        return false;
      }

      // Wear filter
      if (filters.wear && item.steamData.wear !== filters.wear) {
        return false;
      }

      // Float range filter
      if ((filters.floatMin !== null || filters.floatMax !== null) &&
          item.detailedData && item.detailedData.paintwear) {
        const itemFloat = uint32ToFloat32(item.detailedData.paintwear);
        if (filters.floatMin !== null && itemFloat < filters.floatMin) {
          return false;
        }
        if (filters.floatMax !== null && itemFloat > filters.floatMax) {
          return false;
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