/**
 * Tests for post.js - exercises the real renderName exported by the module.
 */

const { renderName } = require('../wwwroot/post.js');

function makeNameEl() {
  const el = document.createElement('span');
  el.className = 'card-name';
  return el;
}

describe('renderName', () => {
  test('renders "Weapon | Skin" as the base text', () => {
    const el = makeNameEl();
    renderName(el, { weapon: 'AK-47', skin: 'Redline', quality: 4 });
    expect(el.textContent).toBe('AK-47 | Redline');
    expect(el.querySelector('.stattrak-badge')).toBeNull();
    expect(el.querySelector('.item-special')).toBeNull();
  });

  test('adds the knife class for knives and gloves', () => {
    const el = makeNameEl();
    renderName(el, { weapon: 'Karambit', skin: 'Doppler', is_knife_or_glove: true, quality: 0 });
    expect(el.classList.contains('knife')).toBe(true);
  });

  test.each([
    [1, 'genuine'],
    [2, 'vintage'],
    [6, 'valve'],
    [7, 'selfmade']
  ])('maps quality %i to the %s class', (quality, cls) => {
    const el = makeNameEl();
    renderName(el, { weapon: 'AK-47', skin: 'Redline', quality });
    expect(el.classList.contains(cls)).toBe(true);
  });

  test('adds the souvenir class for souvenir items', () => {
    const el = makeNameEl();
    renderName(el, { weapon: 'AWP', skin: 'Safari Mesh', quality: 0, souvenir: true });
    expect(el.classList.contains('souvenir')).toBe(true);
  });

  test('appends the special-pattern note when present', () => {
    const el = makeNameEl();
    renderName(el, { weapon: 'Karambit', skin: 'Doppler', quality: 0, special: 'Ruby' });
    expect(el.querySelector('.item-special').textContent).toBe('Ruby');
  });

  test('renders a StatTrak badge with the kill count slide-out', () => {
    const el = makeNameEl();
    renderName(el, { weapon: 'AK-47', skin: 'Redline', quality: 9, stattrak: true, stattrak_kills: 1234 });
    const badge = el.querySelector('.stattrak-badge');
    expect(badge).not.toBeNull();
    expect(badge.textContent).toContain('ST');
    const detail = badge.querySelector('.st-detail');
    expect(detail.textContent).toBe(': 1,234 Kills');
    expect(badge.tabIndex).toBe(0);
  });

  test('omits the kill count when the count is unknown', () => {
    const el = makeNameEl();
    renderName(el, { weapon: 'AK-47', skin: 'Redline', quality: 9, stattrak: true });
    const badge = el.querySelector('.stattrak-badge');
    expect(badge).not.toBeNull();
    expect(badge.querySelector('.st-detail')).toBeNull();
  });
});

describe('Inspect URL acceptance', () => {
  // Mirrors the contract post.js enforces on the search box: bare S/A/D/M or hex certs only.
  function isAcceptedInspect(reduced) {
    return /^[SM]\d+A\d+D\d+$/.test(reduced) || /^[0-9A-F]+$/.test(reduced);
  }

  test('accepts S/A/D/M and hex forms', () => {
    expect(isAcceptedInspect('S76561198123456789A12345D67890')).toBe(true);
    expect(isAcceptedInspect('M1A12345D67890')).toBe(true);
    expect(isAcceptedInspect('ABCDEF123456')).toBe(true);
  });

  test('rejects anything else', () => {
    expect(isAcceptedInspect('not valid')).toBe(false);
    expect(isAcceptedInspect('GHIJKL')).toBe(false);
    expect(isAcceptedInspect('')).toBe(false);
  });
});
