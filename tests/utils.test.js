/**
 * Tests for shared utility functions and common patterns
 */

describe('URL and String Utilities', () => {
  test('should encode and decode URLs correctly', () => {
    const testUrl = "steam://rungame/730/76561202255233023/+csgo_econ_action_preview S76561198123456789A12345D67890";
    const encoded = encodeURIComponent(testUrl);
    const decoded = decodeURIComponent(encoded);

    expect(decoded).toBe(testUrl);
    expect(encoded).toContain('%20');
    expect(encoded).toContain('%2F');
  });

  test('should handle URLSearchParams correctly', () => {
    const params = new URLSearchParams({
      url: "steam://test",
      s: "76561198123456789",
      a: "12345",
      d: "67890",
      m: "0"
    });

    expect(params.toString()).toContain('url=steam%3A%2F%2Ftest');
    expect(params.get('s')).toBe('76561198123456789');
    expect(params.get('a')).toBe('12345');
  });

  test('should validate inspect URL patterns', () => {
    const patterns = {
      numeric: /^[SM]\d+A\d+D\d+$/,
      hex: /^[0-9A-F]+$/
    };

    expect(patterns.numeric.test('S76561198123456789A12345D67890')).toBe(true);
    expect(patterns.numeric.test('M1A12345D67890')).toBe(true);
    expect(patterns.hex.test('ABCDEF123456')).toBe(true);
    expect(patterns.numeric.test('invalid')).toBe(false);
    expect(patterns.hex.test('GHIJKL')).toBe(false);
  });
});

describe('Hash and URL Management', () => {
  beforeEach(() => {
    window.location.hash = '';
  });

  test('should handle hash updates correctly', () => {
    const steamId = '76561198123456789';
    window.location.hash = steamId;

    expect(window.location.hash).toBe(steamId);
    // In mock environment, hash doesn't have # prefix, so no need to substring
    expect(decodeURIComponent(window.location.hash)).toBe(steamId);
  });

  test('should handle URL encoded hash values', () => {
    const profileUrl = 'https://steamcommunity.com/profiles/76561198123456789';
    window.location.hash = '#' + encodeURIComponent(profileUrl);

    // Hash should contain the encoded URL with # prefix
    expect(window.location.hash).toBe('#https%3A%2F%2Fsteamcommunity.com%2Fprofiles%2F76561198123456789');

    const decoded = decodeURIComponent(window.location.hash.substring(1));
    expect(decoded).toBe(profileUrl);
  });
});

describe('Data Type Conversions', () => {
  test('should convert between different number formats', () => {
    // Test uint32 to float32 conversion logic
    const buffer = new ArrayBuffer(4);
    const view = new DataView(buffer);

    view.setUint32(0, 1065353216);
    const float = view.getFloat32(0);
    expect(float).toBeCloseTo(1.0);

    view.setUint32(0, 1056964608);
    const float2 = view.getFloat32(0);
    expect(float2).toBeCloseTo(0.5);
  });

  test('should handle string to number conversions', () => {
    expect(parseInt('123', 10)).toBe(123);
    expect(parseFloat('1.5')).toBe(1.5);
    expect(parseFloat('invalid')).toBeNaN();
    expect(parseInt('', 10)).toBeNaN();
  });

  test('should validate numeric ranges', () => {
    function isValidFloat(value, min = 0, max = 1) {
      const num = parseFloat(value);
      return !isNaN(num) && num >= min && num <= max;
    }

    expect(isValidFloat('0.5', 0, 1)).toBe(true);
    expect(isValidFloat('1.5', 0, 1)).toBe(false);
    expect(isValidFloat('-0.1', 0, 1)).toBe(false);
    expect(isValidFloat('invalid', 0, 1)).toBe(false);
  });
});

describe('Error Handling', () => {
  test('should handle network errors gracefully', async () => {
    fetch.mockRejectedValueOnce(new Error('Network error'));

    try {
      await fetch('/api/test');
    } catch (error) {
      expect(error.message).toBe('Network error');
    }
  });

  test('should handle JSON parsing errors', () => {
    expect(() => JSON.parse('invalid json')).toThrow();

    function safeJsonParse(str) {
      try {
        return JSON.parse(str);
      } catch {
        return null;
      }
    }

    expect(safeJsonParse('{"valid": true}')).toEqual({valid: true});
    expect(safeJsonParse('invalid json')).toBeNull();
  });

  test('should handle missing DOM elements gracefully', () => {
    function getElementSafely(id) {
      try {
        return document.getElementById(id);
      } catch {
        return null;
      }
    }

    expect(getElementSafely('nonexistent')).toBeNull();
  });
});

describe('Array and Object Manipulation', () => {
  test('should sort arrays correctly', () => {
    const numbers = [3, 1, 4, 1, 5, 9, 2, 6];
    const sorted = [...numbers].sort((a, b) => a - b);

    expect(sorted).toEqual([1, 1, 2, 3, 4, 5, 6, 9]);
    expect(numbers).toEqual([3, 1, 4, 1, 5, 9, 2, 6]); // Original unchanged
  });

  test('should filter arrays correctly', () => {
    const items = [
      { name: 'Item 1', value: 10 },
      { name: 'Item 2', value: 20 },
      { name: 'Item 3', value: 5 }
    ];

    const filtered = items.filter(item => item.value > 10);
    expect(filtered).toHaveLength(1);
    expect(filtered[0].name).toBe('Item 2');
  });

  test('should map arrays correctly', () => {
    const items = [
      { name: 'Item 1', value: 10 },
      { name: 'Item 2', value: 20 }
    ];

    const names = items.map(item => item.name);
    expect(names).toEqual(['Item 1', 'Item 2']);
  });

  test('should handle array reduce operations', () => {
    const numbers = [1, 2, 3, 4, 5];
    const sum = numbers.reduce((acc, curr) => acc + curr, 0);
    const max = numbers.reduce((acc, curr) => Math.max(acc, curr), -Infinity);

    expect(sum).toBe(15);
    expect(max).toBe(5);
  });
});

describe('DOM Manipulation Helpers', () => {
  beforeEach(() => {
    document.body.innerHTML = '';
  });

  test('should create and append elements correctly', () => {
    const div = document.createElement('div');
    div.id = 'test';
    div.textContent = 'Test content';
    document.body.appendChild(div);

    const found = document.getElementById('test');
    expect(found).toBeTruthy();
    expect(found.textContent).toBe('Test content');
  });

  test('should handle class manipulation', () => {
    const div = document.createElement('div');
    div.classList.add('class1', 'class2');

    expect(div.classList.contains('class1')).toBe(true);
    expect(div.classList.contains('class2')).toBe(true);

    div.classList.remove('class1');
    expect(div.classList.contains('class1')).toBe(false);

    div.classList.toggle('class3');
    expect(div.classList.contains('class3')).toBe(true);
  });

  test('should handle event listeners', () => {
    const button = document.createElement('button');
    const handler = jest.fn();

    button.addEventListener('click', handler);
    button.click();

    expect(handler).toHaveBeenCalledTimes(1);
  });
});

describe('Timing and Performance', () => {
  test('should measure timing correctly', () => {
    const start = performance.now();
    // Simulate some work
    const end = performance.now();
    const duration = end - start;

    expect(duration).toBeGreaterThanOrEqual(0);
    expect(typeof duration).toBe('number');
  });

  test('should handle delays and timeouts', (done) => {
    const start = Date.now();
    setTimeout(() => {
      const elapsed = Date.now() - start;
      expect(elapsed).toBeGreaterThanOrEqual(10);
      done();
    }, 10);
  });
});

describe('Local Storage and Session Management', () => {
  beforeEach(() => {
    // Mock localStorage
    Object.defineProperty(window, 'localStorage', {
      value: {
        getItem: jest.fn(),
        setItem: jest.fn(),
        removeItem: jest.fn(),
        clear: jest.fn(),
      },
      writable: true
    });
  });

  test('should handle localStorage operations', () => {
    window.localStorage.setItem('test', 'value');
    window.localStorage.getItem.mockReturnValue('value');

    expect(window.localStorage.setItem).toHaveBeenCalledWith('test', 'value');
    expect(window.localStorage.getItem('test')).toBe('value');
  });

  test('should handle localStorage errors gracefully', () => {
    window.localStorage.setItem.mockImplementation(() => {
      throw new Error('Storage quota exceeded');
    });

    function safeSetItem(key, value) {
      try {
        window.localStorage.setItem(key, value);
        return true;
      } catch {
        return false;
      }
    }

    expect(safeSetItem('test', 'value')).toBe(false);
  });
});