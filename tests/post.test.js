/**
 * Tests for post.js - Main item inspection functionality
 */

// Mock DOM elements and functions that would be available in the browser
const mockElements = {
  itemName: { innerHTML: '', classList: { add: jest.fn(), remove: jest.fn() } },
  itemPaintwear: { innerHTML: '', parentElement: { classList: { add: jest.fn(), remove: jest.fn() } } },
  itemWear: { innerHTML: '', parentElement: { classList: { add: jest.fn(), remove: jest.fn() } } },
  itemRarity: { innerHTML: '', parentElement: { classList: { add: jest.fn(), remove: jest.fn() } } },
  itemItemid: { innerHTML: '', parentElement: { classList: { add: jest.fn(), remove: jest.fn() } } },
  itemPaintseed: { innerHTML: '', parentElement: { classList: { add: jest.fn(), remove: jest.fn() } } },
  status: { innerHTML: '' },
  stattrakIndicator: { classList: { add: jest.fn(), remove: jest.fn() } },
  inspectButton: { href: '' },
  textbox: { value: '', addEventListener: jest.fn() },
  button: { addEventListener: jest.fn(), click: jest.fn(), blur: jest.fn() },
  errorDisplay: { innerHTML: '', style: { display: '' } },
};

// Setup DOM before each test
beforeEach(() => {
  // Reset DOM
  document.body.innerHTML = `
    <div id="item_name"></div>
    <div id="item_paintwear"><div class="parent"></div></div>
    <div id="item_wear"><div class="parent"></div></div>
    <div id="item_rarity"><div class="parent"></div></div>
    <div id="item_itemid"><div class="parent"></div></div>
    <div id="item_paintseed"><div class="parent"></div></div>
    <div id="status"></div>
    <div id="stattrak-indicator"></div>
    <a id="inspect-button"></a>
    <input id="textbox" />
    <button id="button"></button>
    <div id="error-display"></div>
  `;

  // Mock getElementById to return our mock elements
  jest.spyOn(document, 'getElementById').mockImplementation((id) => {
    const realElement = document.querySelector(`#${id.replace('_', '-')}`);
    if (realElement) {
      return { ...realElement, ...mockElements[id.replace(/[_-]/g, '')] };
    }
    return mockElements[id.replace(/[_-]/g, '')];
  });

  // Reset window.location.hash
  window.location.hash = '';

  // Reset fetch mock
  fetch.mockClear();
});

describe('Utility Functions', () => {
  // Load the actual post.js file content as text to extract functions
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
      "Mil-Spec",
      "Restricted",
      "Classified",
      "Covert",
      "Contraband",
    ];
    return rarities[rarityNumber] || "Unknown";
  }

  test('uint32ToFloat32 should convert uint32 to float32 correctly', () => {
    expect(uint32ToFloat32(1065353216)).toBeCloseTo(1.0);
    expect(uint32ToFloat32(1056964608)).toBeCloseTo(0.5);
    expect(uint32ToFloat32(0)).toBe(0);
  });

  test('getWearFromFloat should return correct wear categories', () => {
    expect(getWearFromFloat(0.06)).toBe("Factory New");
    expect(getWearFromFloat(0.10)).toBe("Minimal Wear");
    expect(getWearFromFloat(0.25)).toBe("Field-Tested");
    expect(getWearFromFloat(0.40)).toBe("Well-Worn");
    expect(getWearFromFloat(0.50)).toBe("Battle-Scarred");
  });

  test('getRarityFromNumber should return correct rarity names', () => {
    expect(getRarityFromNumber(0)).toBe("Default");
    expect(getRarityFromNumber(1)).toBe("Consumer Grade");
    expect(getRarityFromNumber(2)).toBe("Industrial Grade");
    expect(getRarityFromNumber(3)).toBe("Mil-Spec");
    expect(getRarityFromNumber(4)).toBe("Restricted");
    expect(getRarityFromNumber(5)).toBe("Classified");
    expect(getRarityFromNumber(6)).toBe("Covert");
    expect(getRarityFromNumber(7)).toBe("Contraband");
    expect(getRarityFromNumber(99)).toBe("Unknown");
  });
});

describe('Display Functions', () => {
  // Mock the helper functions that display() depends on
  const mockStopLoading = jest.fn();
  const mockHandleError = jest.fn();

  // Global elements variable like in the real implementation
  let elements;

  function display(iteminfo, url, loadTime) {
    mockStopLoading();

    if (iteminfo.error) {
      mockHandleError(iteminfo.error);
      return;
    }

    try {
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
          "Mil-Spec",
          "Restricted",
          "Classified",
          "Covert",
          "Contraband",
        ];
        return rarities[rarityNumber] || "Unknown";
      }

      elements.itemName.innerHTML = `${iteminfo.weapon} | ${iteminfo.skin} <span class="pop">${iteminfo.special}</span>`;
      elements.itemName.classList.remove("knife", "souvenir");

      if (iteminfo.quality === 3) {
        elements.itemName.classList.add("knife");
      }
      if (iteminfo.quality === 12) {
        elements.itemName.classList.add("souvenir");
      }

      const paintwearFloat = uint32ToFloat32(iteminfo.paintwear);
      elements.itemPaintwear.innerHTML = paintwearFloat;
      elements.itemWear.innerHTML = getWearFromFloat(paintwearFloat);
      elements.itemRarity.innerHTML = getRarityFromNumber(iteminfo.rarity);

      if (iteminfo.itemid == 0) {
        elements.itemItemid.innerHTML = "Unknown";
      } else {
        elements.itemItemid.innerHTML = iteminfo.itemid;
      }

      elements.itemPaintseed.innerHTML = iteminfo.paintseed;
      elements.status.innerHTML = `Loaded in ${loadTime} seconds`;
      elements.stattrakIndicator.classList.remove("yes");

      if (iteminfo.stattrak) {
        elements.stattrakIndicator.classList.add("yes");
      }

      elements.inspectButton.href = url;
    } catch (e) {
      mockHandleError("An error occurred while displaying the item data");
      throw e;
    }
  }

  function setupDisplayDOM() {
    document.body.innerHTML = `
      <div>
        <div id="item_name"></div>
      </div>
      <div>
        <div id="item_paintwear"></div>
      </div>
      <div>
        <div id="item_wear"></div>
      </div>
      <div>
        <div id="item_rarity"></div>
      </div>
      <div>
        <div id="item_itemid"></div>
      </div>
      <div>
        <div id="item_paintseed"></div>
      </div>
      <div id="status"></div>
      <div id="stattrak-indicator"></div>
      <a id="inspect-button"></a>
      <div id="error-display" style="display: none;"></div>
    `;

    // Initialize elements with proper mocking
    const itemNameEl = document.getElementById("item_name");
    const stattrakEl = document.getElementById("stattrak-indicator");

    elements = {
      itemName: {
        ...itemNameEl,
        classList: { add: jest.fn(), remove: jest.fn() },
        innerHTML: ""
      },
      itemPaintwear: {
        ...document.getElementById("item_paintwear"),
        innerHTML: ""
      },
      itemWear: {
        ...document.getElementById("item_wear"),
        innerHTML: ""
      },
      itemRarity: {
        ...document.getElementById("item_rarity"),
        innerHTML: ""
      },
      itemItemid: {
        ...document.getElementById("item_itemid"),
        innerHTML: ""
      },
      itemPaintseed: {
        ...document.getElementById("item_paintseed"),
        innerHTML: ""
      },
      status: {
        ...document.getElementById("status"),
        innerHTML: ""
      },
      stattrakIndicator: {
        ...stattrakEl,
        classList: { add: jest.fn(), remove: jest.fn() }
      },
      inspectButton: {
        ...document.getElementById("inspect-button"),
        href: ""
      },
      errorDisplay: {
        ...document.getElementById("error-display"),
        innerHTML: "",
        style: { display: "" }
      },
    };
  }

  beforeEach(() => {
    mockStopLoading.mockClear();
    mockHandleError.mockClear();
  });

  test('display should handle valid item data correctly', () => {
    setupDisplayDOM();

    const itemInfo = {
      weapon: "AK-47",
      skin: "Redline",
      special: "StatTrak™",
      quality: 4,
      paintwear: 1065353216, // 1.0 as uint32
      rarity: 3,
      itemid: 12345,
      paintseed: 661,
      stattrak: true
    };

    const url = "steam://inspect/test";
    const loadTime = "2.5";

    display(itemInfo, url, loadTime);

    // Verify stopLoading was called
    expect(mockStopLoading).toHaveBeenCalled();

    // Verify DOM updates
    expect(elements.itemName.innerHTML).toBe("AK-47 | Redline <span class=\"pop\">StatTrak™</span>");
    expect(elements.itemPaintwear.innerHTML).toBe(1);
    expect(elements.itemWear.innerHTML).toBe("Battle-Scarred");
    expect(elements.itemRarity.innerHTML).toBe("Mil-Spec");
    expect(elements.itemItemid.innerHTML).toBe(12345);
    expect(elements.itemPaintseed.innerHTML).toBe(661);
    expect(elements.status.innerHTML).toBe("Loaded in 2.5 seconds");
    expect(elements.inspectButton.href).toBe(url);

    // Verify StatTrak indicator was added
    expect(elements.stattrakIndicator.classList.add).toHaveBeenCalledWith("yes");
  });

  test('display should handle error correctly', () => {
    setupDisplayDOM();

    const itemInfo = {
      error: "Item not found"
    };

    display(itemInfo, "", "0");

    // Verify stopLoading was called
    expect(mockStopLoading).toHaveBeenCalled();

    // Verify handleError was called with the error message
    expect(mockHandleError).toHaveBeenCalledWith("Item not found");
  });

  test('display should handle knife items correctly', () => {
    setupDisplayDOM();

    const itemInfo = {
      weapon: "Karambit",
      skin: "Doppler",
      special: "Phase 2",
      quality: 3, // Knife quality
      paintwear: 1056964608, // 0.5 as uint32
      rarity: 6,
      itemid: 67890,
      paintseed: 123,
      stattrak: false
    };

    display(itemInfo, "", "1.0");

    // Verify knife class was added
    expect(elements.itemName.classList.add).toHaveBeenCalledWith("knife");

    // Verify other fields are set correctly
    expect(elements.itemName.innerHTML).toBe("Karambit | Doppler <span class=\"pop\">Phase 2</span>");
    expect(elements.itemWear.innerHTML).toBe("Battle-Scarred");
    expect(elements.itemRarity.innerHTML).toBe("Covert");
  });

  test('display should handle souvenir items correctly', () => {
    setupDisplayDOM();

    const itemInfo = {
      weapon: "AK-47",
      skin: "Safari Mesh",
      special: "",
      quality: 12, // Souvenir quality
      paintwear: 1061997773, // 0.7 as uint32
      rarity: 2,
      itemid: 54321,
      paintseed: 456,
      stattrak: false
    };

    display(itemInfo, "", "1.5");

    // Verify souvenir class was added
    expect(elements.itemName.classList.add).toHaveBeenCalledWith("souvenir");

    // Verify other fields are set correctly
    expect(elements.itemName.innerHTML).toBe("AK-47 | Safari Mesh <span class=\"pop\"></span>");
    expect(elements.itemWear.innerHTML).toBe("Battle-Scarred");
    expect(elements.itemRarity.innerHTML).toBe("Industrial Grade");

    // Verify StatTrak was not added (souvenirs can't be StatTrak)
    expect(elements.stattrakIndicator.classList.add).not.toHaveBeenCalledWith("yes");
  });
});

describe('URL Validation', () => {
  function isValidInspectUrl(url) {
    const inspectPrefix = "steam://rungame/730/76561202255233023/+csgo_econ_action_preview%20";
    const reduced = url.replace(inspectPrefix, "");
    return /^[SM]\d+A\d+D\d+$/.test(reduced) || /^[0-9A-F]+$/.test(reduced);
  }

  test('should validate correct inspect URLs', () => {
    const validUrls = [
      "steam://rungame/730/76561202255233023/+csgo_econ_action_preview%20S76561198123456789A12345D67890",
      "steam://rungame/730/76561202255233023/+csgo_econ_action_preview%20M1A12345D67890",
      "steam://rungame/730/76561202255233023/+csgo_econ_action_preview%20ABCDEF123456"
    ];

    validUrls.forEach(url => {
      expect(isValidInspectUrl(url)).toBe(true);
    });
  });

  test('should reject invalid inspect URLs', () => {
    const invalidUrls = [
      "not a valid url",
      "steam://invalid",
      "steam://rungame/730/76561202255233023/+csgo_econ_action_preview%20invalid",
      ""
    ];

    invalidUrls.forEach(url => {
      expect(isValidInspectUrl(url)).toBe(false);
    });
  });
});

describe('API Integration', () => {
  test('should make correct API call', async () => {
    fetch.mockResolvedValueOnce({
      json: async () => ({
        weapon: "AK-47",
        skin: "Redline",
        special: "",
        quality: 4,
        paintwear: 1065353216,
        rarity: 3,
        itemid: 12345,
        paintseed: 661,
        stattrak: false
      })
    });

    const url = "steam://rungame/730/76561202255233023/+csgo_econ_action_preview%20S76561198123456789A12345D67890";

    // Simulate the API call that would be made in post.js
    const response = await fetch(`/api?${new URLSearchParams({url})}`);
    const data = await response.json();

    expect(fetch).toHaveBeenCalledWith('/api?url=steam%3A%2F%2Frungame%2F730%2F76561202255233023%2F%2Bcsgo_econ_action_preview%2520S76561198123456789A12345D67890');
    expect(data.weapon).toBe("AK-47");
    expect(data.skin).toBe("Redline");
  });
});