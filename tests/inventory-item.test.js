/**
 * Tests for inventory-item.js - the <inventory-item> custom element that renders every card
 * in the inventory grid.
 *
 * Three things make this file unusual to set up, and all three are handled in the harness below:
 *
 *  1. It is a custom element with (until now) no exports. Requiring the module runs
 *     customElements.define, so the tests build cards the same way inventory.js does:
 *     document.createElement('inventory-item').
 *
 *  2. It renders by cloning <template id="inventory-item-template">, which lives in index.html.
 *     Rather than keep a second copy of that markup here (which would silently drift), the
 *     template is read out of wwwroot/index.html and installed into the document.
 *
 *  3. It reads globals that a *different* <script> defines (inventory.js and decals.js) - the
 *     ones carrying the `read by inventory-item.js (a separate <script>)` eslint comments.
 *     Under jest every file is its own CommonJS module, so nothing is shared implicitly; the
 *     harness puts them on `global` explicitly. Where the real function is exported it is used
 *     as-is; where it is not, the stub is a jest.fn so the call itself can be asserted.
 */

const fs = require('fs');
const path = require('path');

const decals = require('../wwwroot/decals.js');
const { buildSpecialChip } = require('../wwwroot/special-chip.js');
const inventory = require('../wwwroot/inventory.js');

// --- cross-script globals -------------------------------------------------------------------
// Real implementations, so a regression in the shared helpers fails here too.
global.rarityColorOf = decals.rarityColorOf;
global.buildStickerChips = decals.buildStickerChips;
global.buildSpecialChip = buildSpecialChip;
global.uint32ToFloat32 = inventory.uint32ToFloat32;
global.getWearFromFloat = inventory.getWearFromFloat;
global.getRarityFromNumber = inventory.getRarityFromNumber;
global.isKnifeOrGlove = inventory.isKnifeOrGlove;

// Not exported by their own modules, so these are stubs.
// formatPriceCents lives in inventory.js and is not on its export list; a sentinel return keeps
// this test about what inventory-item.js contributes (the "~" prefix, the .has-price gate)
// without duplicating - and silently drifting from - inventory.js's formatting.
global.formatPriceCents = jest.fn(cents => `FMT:${cents}`);
// describePriceBasis also lives in inventory.js and is also unexported. Same sentinel treatment:
// the wording of the hover text is inventory.js's business, and duplicating it here would only
// give it somewhere else to drift. What this file owns is that the card asks for the text and
// hangs it on the tag.
global.describePriceBasis = jest.fn(price => `BASIS:${price.basis}`);
global.WEAR_ABBREVIATIONS = {
  'Factory New': 'FN',
  'Minimal Wear': 'MW',
  'Field-Tested': 'FT',
  'Well-Worn': 'WW',
  'Battle-Scarred': 'BS'
};
// Behavioural wiring rather than rendering; asserted by call, not by effect.
global.enableTooltip = jest.fn();
global.enableLongPressCopy = jest.fn();

// Mutable page state owned by inventory.js. Bare reads of an undeclared identifier throw, so
// these must exist (as null) even when a test does not care about them.
global.floatRanges = null;
global.currentOwnerSteamId = null;

// Requiring registers <inventory-item>; must come after the globals above only for tidiness -
// nothing outside the class body reads them.
require('../wwwroot/inventory-item.js');

// --- the real template, lifted out of index.html --------------------------------------------
const INDEX_HTML = fs.readFileSync(
  path.join(__dirname, '..', 'wwwroot', 'index.html'), 'utf8');
const TEMPLATE_HTML = (
  INDEX_HTML.match(/<template id="inventory-item-template">[\s\S]*?<\/template>/) || [])[0];

// A float with plenty of precision: uint32 1047540363 -> 0.2345678061246872 (Field-Tested).
const PAINTWEAR_FT = 1047540363;
const FLOAT_FT = '0.2345678061246872';

/** Build a card the way inventory.js's createItemElement does, and connect it. */
function makeCard(item = {}, index = 0) {
  const el = document.createElement('inventory-item');
  el.setItemData(item, index);
  document.body.appendChild(el);
  return el;
}

/** Shorthand for the template's data-field hooks inside the shadow root. */
function field(el, name) {
  return el.shadowRoot.querySelector(`[data-field="${name}"]`);
}

beforeEach(() => {
  document.body.innerHTML = TEMPLATE_HTML;
  global.floatRanges = null;
  global.currentOwnerSteamId = null;
});

describe('test harness', () => {
  test('the card template was found in index.html', () => {
    // If index.html moves or renames the template, every test below would fail obscurely.
    expect(TEMPLATE_HTML).toBeDefined();
    expect(TEMPLATE_HTML).toContain('data-field="name"');
  });
});

describe('basic render', () => {
  test('clones the template into a shadow root and shows the Steam name', () => {
    const el = makeCard({ name: 'AK-47 | Redline', rarity: 'Classified' });
    expect(el.shadowRoot).not.toBeNull();
    expect(field(el, 'name').textContent).toBe('AK-47 | Redline');
    expect(field(el, 'image')).not.toBeNull();
  });

  test('the shadow DOM is built once, so re-connecting a card does not duplicate it', () => {
    // Filter/sort moves detach and re-attach the same element; connectedCallback guards on this.
    const el = makeCard({ name: 'AWP | Asiimov' });
    const first = el.shadowRoot.childElementCount;
    el.remove();
    document.body.appendChild(el);
    expect(el.shadowRoot.childElementCount).toBe(first);
    expect(el.shadowRoot.querySelectorAll('[data-field="name"]')).toHaveLength(1);
  });

  test('data set after connection updates the card immediately', () => {
    const el = document.createElement('inventory-item');
    document.body.appendChild(el);
    el.setItemData({ name: 'Glock-18 | Fade' }, 3);
    expect(field(el, 'name').textContent).toBe('Glock-18 | Fade');
    expect(el.itemIndex).toBe(3);
  });

  test('falls back to market_name, then to "Unknown Item"', () => {
    expect(field(makeCard({ market_name: 'Souvenir AWP | Dragon Lore' }), 'name').textContent)
      .toBe('Souvenir AWP | Dragon Lore');
    expect(field(makeCard({}), 'name').textContent).toBe('Unknown Item');
  });

  test('strips the leading star and marks knives, which re-add it via CSS', () => {
    const el = makeCard({ name: '★ Karambit | Doppler', type: '★ Knife' });
    expect(field(el, 'name').textContent).toBe('Karambit | Doppler');
    expect(field(el, 'name').classList.contains('knife')).toBe(true);
  });

  test('marks souvenir items from the Steam quality', () => {
    const el = makeCard({ name: 'AWP | Dragon Lore', quality: 'Souvenir' });
    expect(field(el, 'name').classList.contains('souvenir')).toBe(true);
  });

  test('marks the card as loading until analysis lands', () => {
    expect(makeCard({ name: 'P90 | Asiimov' }).classList.contains('loading')).toBe(true);
  });
});

describe('XSS safety', () => {
  // The file carries a deliberate "Built as DOM nodes, never innerHTML: the Steam name is
  // remote data" comment. Steam display names are attacker-influenceable (name tags, and
  // market_hash_name for community items), so they must reach the DOM as text.
  const HOSTILE = '<img src=x onerror="alert(1)"> AK-47 <script>alert(2)</script>';

  test('the item name is written as text, never parsed as HTML', () => {
    const el = makeCard({ name: HOSTILE });
    const name = field(el, 'name');
    expect(name.textContent).toBe(HOSTILE);
    expect(name.querySelector('img')).toBeNull();
    expect(name.querySelector('script')).toBeNull();
    expect(name.childElementCount).toBe(0);
    expect(name.innerHTML).toContain('&lt;img');
  });

  test('a hostile market_name is escaped too', () => {
    const name = field(makeCard({ market_name: HOSTILE }), 'name');
    expect(name.textContent).toBe(HOSTILE);
    expect(name.childElementCount).toBe(0);
  });

  test('the detailed rename keeps weapon and skin as text', () => {
    const el = makeCard({ name: 'X' });
    el.updateWithDetails({
      weapon: '<b>AK-47</b>',
      skin: '<img src=x onerror=alert(1)>',
      paintindex: 44,
      paintwear: PAINTWEAR_FT
    }, null);
    const name = field(el, 'name');
    expect(name.textContent).toBe('<b>AK-47</b> | <img src=x onerror=alert(1)>');
    expect(name.querySelector('b')).toBeNull();
    expect(name.querySelector('img')).toBeNull();
  });

  test('the skinless name path also falls back to text, not markup', () => {
    // paintindex 0 -> no skin -> the card keeps Steam's own name for the item.
    const el = makeCard({ name: HOSTILE });
    el.updateWithDetails({ weapon: 'Music Kit', skin: 'x', paintindex: 0 }, null);
    const name = field(el, 'name');
    expect(name.textContent).toBe(HOSTILE);
    expect(name.childElementCount).toBe(0);
  });

  test('the float innerHTML only ever carries a number, even for junk input', () => {
    // updateWithDetails builds the float display with innerHTML. The value is not remote text:
    // it is uint32ToFloat32's numeric output, and a non-numeric paintwear degrades to 0.
    const el = makeCard({ name: 'X' });
    el.updateWithDetails({
      weapon: 'AK-47', skin: 'Redline', paintindex: 44,
      paintwear: '<img src=x onerror=alert(1)>'
    }, null);
    const float = field(el, 'float');
    expect(float.querySelector('img')).toBeNull();
    expect(float.textContent).toBe('0');
    expect(float.querySelectorAll('span')).toHaveLength(2);
  });

  test('the pattern seed is written as text', () => {
    const el = makeCard({ name: 'X' });
    el.updateWithDetails({
      weapon: 'AK-47', skin: 'Redline', paintindex: 44,
      paintwear: PAINTWEAR_FT, paintseed: '<b>661</b>'
    }, null);
    expect(field(el, 'pattern').textContent).toBe('<b>661</b>');
    expect(field(el, 'pattern').childElementCount).toBe(0);
  });
});

describe('rarity', () => {
  test('shows the rarity and colours it, including the card edge', () => {
    const el = makeCard({ name: 'AK-47 | Redline', rarity: 'Classified' });
    const rarity = field(el, 'rarity');
    expect(rarity.textContent).toBe('Classified');
    expect(rarity.style.color).toBe('rgb(211, 44, 230)'); // #D32CE6
    expect(el.style.getPropertyValue('border-left-color')).toBe('#D32CE6');
  });

  test('"Unknown" renders as no rarity at all', () => {
    const el = makeCard({ name: 'Sticker', rarity: 'Unknown' });
    expect(field(el, 'rarity').textContent).toBe('');
  });

  test('getRarityColor falls back to the default grey', () => {
    expect(makeCard({}).getRarityColor('Nonexistent')).toBe('#B0C3D9');
  });

  test('the numeric GC rarity is only used when Steam gave us nothing', () => {
    const el = makeCard({ name: 'X' }); // no Steam rarity
    el.updateWithDetails({ weapon: 'AK-47', skin: 'Redline', paintindex: 44, rarity: 6 }, null);
    expect(field(el, 'rarity').textContent).toBe('Covert');
  });

  test('Steam rarity wins over the numeric GC ladder', () => {
    // getRarityFromNumber is a weapon-only ladder and mislabels medals/stickers/agents.
    const el = makeCard({ name: 'Sticker', rarity: 'High Grade' });
    el.updateWithDetails({ weapon: 'Sticker', skin: 'x', paintindex: 0, rarity: 6 }, null);
    expect(field(el, 'rarity').textContent).toBe('High Grade');
  });
});

describe('wear pill', () => {
  test.each([
    ['Factory New', 'FN'],
    ['Minimal Wear', 'MW'],
    ['Field-Tested', 'FT'],
    ['Well-Worn', 'WW'],
    ['Battle-Scarred', 'BS']
  ])('%s renders the %s pill', (wear, abbr) => {
    const pill = field(makeCard({ name: 'X', wear }), 'wear-pill');
    expect(pill.textContent).toBe(abbr);
    expect(pill.className).toBe(`wear-pill ${abbr.toLowerCase()}`);
    expect(pill.title).toBe(wear);
    expect(pill.hidden).toBe(false);
  });

  test('an unrecognised or absent wear hides the pill', () => {
    expect(field(makeCard({ name: 'X' }), 'wear-pill').hidden).toBe(true);
    expect(field(makeCard({ name: 'X', wear: 'Pristine' }), 'wear-pill').hidden).toBe(true);
  });

  test('analysis re-derives the pill from the true float', () => {
    const el = makeCard({ name: 'X', wear: 'Factory New' }); // Steam's coarse wear
    el.updateWithDetails({
      weapon: 'AK-47', skin: 'Redline', paintindex: 44, paintwear: PAINTWEAR_FT
    }, null);
    expect(field(el, 'wear-pill').textContent).toBe('FT');
  });
});

// The card renders `value` - the server's best estimate of what the item SELLS for, which is a
// median of completed sales where we have one and only falls back to an asking price. `suggested`
// is still in the payload as listing detail, but it is deliberately not what the card shows: it
// runs about 25% above the sale median, so displaying it overstated every inventory.
describe('price tag', () => {
  test('formats the value and reveals the tag', () => {
    const el = makeCard({ name: 'X', price: { value: 4288, basis: 'sale' } });
    const tag = field(el, 'price');
    expect(global.formatPriceCents).toHaveBeenCalledWith(4288);
    expect(tag.textContent).toBe('FMT:4288');
    expect(tag.classList.contains('has-price')).toBe(true);
  });

  test('the value wins over the listing detail alongside it', () => {
    // Both fields are present on every priced item, so which one the card reaches for is the
    // whole point rather than an implementation detail.
    const tag = field(makeCard({ name: 'X', price: { value: 4288, suggested: 5360, basis: 'sale' } }), 'price');
    expect(tag.textContent).toBe('FMT:4288');
    expect(global.formatPriceCents).not.toHaveBeenCalledWith(5360);
  });

  test('an approximate price is prefixed with ~', () => {
    const tag = field(makeCard({ name: 'X', price: { value: 4288, approximate: true } }), 'price');
    expect(tag.textContent).toBe('~FMT:4288');
    expect(tag.classList.contains('has-price')).toBe(true);
  });

  test('an exact price carries no ~', () => {
    const tag = field(makeCard({ name: 'X', price: { value: 4288, approximate: false } }), 'price');
    expect(tag.textContent).toBe('FMT:4288');
    expect(tag.textContent.startsWith('~')).toBe(false);
  });

  test('a free item still shows a price - 0 cents is a price, not a missing one', () => {
    const tag = field(makeCard({ name: 'X', price: { value: 0 } }), 'price');
    expect(tag.textContent).toBe('FMT:0');
    expect(tag.classList.contains('has-price')).toBe(true);
  });

  test('the basis is explained on hover, not on the card', () => {
    // A measured sale and a borrowed asking price are very different claims for the same number,
    // and the card has no room to say which - so the distinction lives in the title.
    const price = { value: 4288, basis: 'nearest-wear-listing', approximate: true };
    const tag = field(makeCard({ name: 'X', price }), 'price');
    expect(global.describePriceBasis).toHaveBeenCalledWith(price);
    expect(tag.title).toBe('BASIS:nearest-wear-listing');
    expect(tag.textContent).toBe('~FMT:4288'); // and the card itself still says only the number
  });

  test('clearing a price drops its hover text too', () => {
    const el = makeCard({ name: 'X', price: { value: 4288, basis: 'sale' } });
    el.setItemData({ name: 'X' }, 0);
    expect(field(el, 'price').hasAttribute('title')).toBe(false);
  });

  test('no price leaves the tag empty and hidden', () => {
    expect(field(makeCard({ name: 'X' }), 'price').classList.contains('has-price')).toBe(false);
    expect(field(makeCard({ name: 'X', price: null }), 'price').textContent).toBe('');
    expect(field(makeCard({ name: 'X', price: {} }), 'price').classList.contains('has-price'))
      .toBe(false);
  });

  test('re-setting an item without a price clears a previously shown one', () => {
    const el = makeCard({ name: 'X', price: { value: 4288 } });
    el.setItemData({ name: 'X' }, 0);
    const tag = field(el, 'price');
    expect(tag.textContent).toBe('');
    expect(tag.classList.contains('has-price')).toBe(false);
  });
});

describe('links and image', () => {
  test('sets the inspect link and wires long-press copy', () => {
    const el = makeCard({ name: 'X', inspect_link: 'steam://rungame/730/1/+csgo_econ_action_preview%20S1A2D3' });
    expect(field(el, 'inspect-link').getAttribute('href')).toContain('csgo_econ_action_preview');
    expect(global.enableLongPressCopy).toHaveBeenCalledWith(field(el, 'inspect-link'));
  });

  test('an item with no inspect link gets an inert href', () => {
    expect(field(makeCard({ name: 'X' }), 'inspect-link').getAttribute('href')).toBe('#');
  });

  test('the Steam deep link uses the resolved owner and the assetid', () => {
    global.currentOwnerSteamId = '76561198000000000';
    const link = field(makeCard({ name: 'X', assetid: '12345' }), 'steam-link');
    expect(link.getAttribute('href'))
      .toBe('https://steamcommunity.com/profiles/76561198000000000/inventory#730_2_12345');
    expect(link.style.display).toBe('');
  });

  test('falls back to the ids embedded in a classic S...A... inspect link', () => {
    const link = field(makeCard({
      name: 'X',
      inspect_link: 'steam://rungame/730/1/+csgo_econ_action_preview S76561198000000000A987654321D555'
    }), 'steam-link');
    expect(link.getAttribute('href'))
      .toBe('https://steamcommunity.com/profiles/76561198000000000/inventory#730_2_987654321');
  });

  test('the deep link is hidden when neither source yields an owner', () => {
    expect(field(makeCard({ name: 'X', assetid: '12345' }), 'steam-link').style.display)
      .toBe('none');
  });

  test('prefers the large economy image, then the small one, then a placeholder', () => {
    expect(field(makeCard({ name: 'X', icon_url_large: 'BIG', icon_url: 'SMALL' }), 'image').src)
      .toBe('https://community.cloudflare.steamstatic.com/economy/image/BIG');
    expect(field(makeCard({ name: 'X', icon_url: 'SMALL' }), 'image').src)
      .toBe('https://community.cloudflare.steamstatic.com/economy/image/SMALL');
    expect(field(makeCard({ name: 'X' }), 'image').src).toMatch(/^data:image\/svg\+xml;base64,/);
  });

  test('the image alt carries the item name, or a generic fallback', () => {
    expect(field(makeCard({ name: 'AK-47 | Redline' }), 'image').alt).toBe('AK-47 | Redline');
    expect(field(makeCard({}), 'image').alt).toBe('CS2 Item');
  });
});

describe('applyFloatRange', () => {
  // Public surface: inventory.js calls el.applyFloatRange() directly after float-ranges.json
  // arrives, to catch cards that were rendered before the ranges loaded.
  function analysed(paintindex = 44) {
    const el = makeCard({ name: 'X' });
    el.updateWithDetails({
      weapon: 'AK-47', skin: 'Redline', paintindex, paintwear: PAINTWEAR_FT
    }, null);
    return el;
  }

  test('dims both unreachable ends and labels the range', () => {
    global.floatRanges = { 44: [0.06, 0.8] };
    const el = analysed();
    el.applyFloatRange();
    const left = field(el, 'float-dim-left');
    const right = field(el, 'float-dim-right');
    expect(left.hidden).toBe(false);
    expect(parseFloat(left.style.width)).toBeCloseTo(6);
    expect(right.hidden).toBe(false);
    expect(parseFloat(right.style.width)).toBeCloseTo(20);
    expect(field(el, 'float-bar').dataset.range).toBe('Range: 0.06-0.8');
    expect(field(el, 'float-bar').tabIndex).toBe(0);
    expect(global.enableTooltip).toHaveBeenCalledWith(field(el, 'float-bar'));
  });

  test('a full 0-1 range dims nothing', () => {
    global.floatRanges = { 44: [0, 1] };
    const el = analysed();
    el.applyFloatRange();
    expect(field(el, 'float-dim-left').hidden).toBe(true);
    expect(field(el, 'float-dim-right').hidden).toBe(true);
  });

  test('is a no-op before float-ranges.json has loaded', () => {
    global.floatRanges = null;
    const el = analysed();
    el.applyFloatRange();
    expect(field(el, 'float-bar').dataset.range).toBeUndefined();
  });

  test('is a no-op for a paint kit with no published range', () => {
    global.floatRanges = { 99: [0.1, 0.9] };
    const el = analysed(44);
    el.applyFloatRange();
    expect(field(el, 'float-bar').dataset.range).toBeUndefined();
  });

  test('is a no-op on a card that has not been analysed yet', () => {
    global.floatRanges = { 44: [0.06, 0.8] };
    const el = makeCard({ name: 'X' }); // paintIndex never set
    expect(() => el.applyFloatRange()).not.toThrow();
    expect(field(el, 'float-bar').dataset.range).toBeUndefined();
  });
});

describe('float display', () => {
  function analysed(extra = {}) {
    const el = makeCard({ name: 'X' });
    el.updateWithDetails({
      weapon: 'AK-47', skin: 'Redline', paintindex: 44, paintwear: PAINTWEAR_FT, ...extra
    }, null);
    return el;
  }

  test('splits the float into six shown decimals plus the rest', () => {
    const float = field(analysed(), 'float');
    expect(float.textContent).toBe(FLOAT_FT);
    expect(float.querySelector('.float-short').textContent).toBe('0.234567');
    expect(float.querySelector('.float-rest').textContent).toBe('8061246872');
    expect(float.classList.contains('float-value')).toBe(true);
    expect(float.classList.contains('loading-placeholder')).toBe(false);
  });

  test('truncates rather than rounds, so the two halves concatenate to the real float', () => {
    const float = field(analysed(), 'float');
    expect(float.querySelector('.float-short').textContent +
      float.querySelector('.float-rest').textContent).toBe(FLOAT_FT);
  });

  test('drops the "Float:" label and reveals the bar with the marker positioned', () => {
    const el = analysed();
    expect(field(el, 'float-label').style.display).toBe('none');
    expect(field(el, 'float-bar').hidden).toBe(false);
    expect(parseFloat(field(el, 'float-marker').style.left)).toBeCloseTo(23.45678, 3);
  });

  test('the marker is clamped into the bar for out-of-range floats', () => {
    // 1.0 as a uint32 bit pattern; nothing should ever exceed 100%.
    const el = analysed({ paintwear: 1065353216 });
    expect(field(el, 'float-marker').style.left).toBe('100%');
  });

  test('exposes the float as a copy button for keyboard and screen-reader users', () => {
    const float = field(analysed(), 'float');
    expect(float.tabIndex).toBe(0);
    expect(float.getAttribute('role')).toBe('button');
    expect(float.getAttribute('aria-label')).toBe(`Copy float value ${FLOAT_FT}`);
    expect(float.style.cursor).toBe('copy');
  });
});

describe('float copy to clipboard', () => {
  let writeText;

  beforeEach(() => {
    writeText = jest.fn(() => Promise.resolve());
    Object.defineProperty(global.navigator, 'clipboard', {
      value: { writeText }, configurable: true, writable: true
    });
  });

  function analysed() {
    const el = makeCard({ name: 'X' });
    el.updateWithDetails({
      weapon: 'AK-47', skin: 'Redline', paintindex: 44, paintwear: PAINTWEAR_FT
    }, null);
    return el;
  }

  test('clicking copies the full float and confirms, then restores', async () => {
    jest.useFakeTimers();
    try {
      const float = field(analysed(), 'float');
      float.click();
      expect(writeText).toHaveBeenCalledWith(FLOAT_FT);
      await Promise.resolve();
      expect(float.textContent).toBe('Copied!');
      jest.advanceTimersByTime(1000);
      expect(float.textContent).toBe(FLOAT_FT);
      expect(float.querySelector('.float-rest')).not.toBeNull();
    } finally {
      jest.useRealTimers();
    }
  });

  test('Enter and Space copy too; other keys do not', () => {
    const float = field(analysed(), 'float');
    float.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    float.dispatchEvent(new KeyboardEvent('keydown', { key: ' ', bubbles: true }));
    float.dispatchEvent(new KeyboardEvent('keydown', { key: 'a', bubbles: true }));
    expect(writeText).toHaveBeenCalledTimes(2);
  });

  test('a blocked clipboard leaves the float value untouched', async () => {
    writeText.mockReturnValue(Promise.reject(new Error('denied')));
    const float = field(analysed(), 'float');
    float.click();
    await Promise.resolve();
    await Promise.resolve();
    expect(float.textContent).toBe(FLOAT_FT);
  });
});

describe('StatTrak and special qualities', () => {
  function analysed(extra) {
    const el = makeCard(extra.steam || { name: 'X' });
    el.updateWithDetails({
      weapon: 'AK-47', skin: 'Redline', paintindex: 44, paintwear: PAINTWEAR_FT, ...extra.gc
    }, null);
    return el;
  }

  test('adds an ST badge with the kill count', () => {
    const el = analysed({
      steam: { name: 'X', stattrak_kills: 1234 },
      gc: { stattrak: true }
    });
    const badge = field(el, 'name').querySelector('.stattrak-badge');
    expect(badge.textContent).toBe('ST: 1,234 Kills');
    expect(badge.querySelector('.st-detail').textContent).toBe(': 1,234 Kills');
    expect(badge.tabIndex).toBe(0); // focusable so the count slides out on tap
  });

  test('a StatTrak item with no kill count shows the bare badge', () => {
    const el = analysed({ gc: { stattrak: true } });
    const badge = field(el, 'name').querySelector('.stattrak-badge');
    expect(badge.textContent).toBe('ST');
    expect(badge.querySelector('.st-detail')).toBeNull();
  });

  test('a non-StatTrak item has no badge', () => {
    expect(field(analysed({ gc: {} }), 'name').querySelector('.stattrak-badge')).toBeNull();
  });

  test('a knife defindex marks the name, even at StatTrak quality', () => {
    const el = analysed({ gc: { defindex: 507, quality: 9 } });
    expect(field(el, 'name').classList.contains('knife')).toBe(true);
  });

  test.each([
    [1, 'genuine'],
    [2, 'vintage'],
    [6, 'valve'],
    [7, 'selfmade'],
    [12, 'souvenir']
  ])('GC quality %i renders the %s prefix class', (quality, cls) => {
    expect(field(analysed({ gc: { quality } }), 'name').classList.contains(cls)).toBe(true);
  });

  test('an ordinary quality adds no prefix class', () => {
    const name = field(analysed({ gc: { quality: 4 } }), 'name');
    expect(name.className).toBe('item-name');
  });
});

describe('items with no paint kit', () => {
  // Medals, coins, pins, music kits, graffiti, vanilla knives: paintindex 0. "Float: 0",
  // "Pattern: 0" and a wear pill are all meaningless for them.
  test('drops the float row and the pattern value', () => {
    const el = makeCard({ name: 'Berlin 2019 Champion', rarity: 'Extraordinary', wear: 'Factory New' });
    el.updateWithDetails({ weapon: 'Medal', skin: 'x', paintindex: 0 }, null);
    expect(field(el, 'float-line').style.display).toBe('none');
    expect(field(el, 'pattern').style.display).toBe('none');
    expect(field(el, 'wear-pill').hidden).toBe(true);
  });

  test('keeps the pattern row when there is still a rarity to show', () => {
    const el = makeCard({ name: 'Medal', rarity: 'Extraordinary' });
    el.updateWithDetails({ weapon: 'Medal', skin: 'x', paintindex: 0 }, null);
    expect(field(el, 'pattern-line').style.display).toBe('');
    expect(field(el, 'rarity').textContent).toBe('Extraordinary');
  });

  test('hides the pattern row entirely when there is no rarity either', () => {
    const el = makeCard({ name: 'Mystery' });
    el.updateWithDetails({ weapon: 'Thing', skin: 'x', paintindex: 0 }, null);
    expect(field(el, 'pattern-line').style.display).toBe('none');
  });

  test('a skinned item keeps both rows', () => {
    const el = makeCard({ name: 'X' });
    el.updateWithDetails({
      weapon: 'AK-47', skin: 'Redline', paintindex: 44, paintwear: PAINTWEAR_FT, paintseed: 661
    }, null);
    expect(field(el, 'float-line').style.display).toBe('');
    expect(field(el, 'pattern').textContent).toBe('661');
    expect(field(el, 'pattern').classList.contains('loading-placeholder')).toBe(false);
  });
});

describe('rare-pattern chip', () => {
  function analysed(gc) {
    const el = makeCard({ name: 'X' });
    el.updateWithDetails({
      weapon: 'AK-47', skin: 'Fade', paintindex: 38, paintwear: PAINTWEAR_FT, paintseed: 661, ...gc
    }, null);
    return el;
  }

  test('renders the chip straight after the pattern seed', () => {
    const el = analysed({ special: '96.4%' });
    const chip = field(el, 'pattern-line').querySelector('.special-chip');
    expect(chip).not.toBeNull();
    expect(chip.dataset.kind).toBe('fade');
    expect(chip.textContent).toBe('96.4%');
    expect(field(el, 'pattern').nextElementSibling).toBe(chip);
  });

  test('a re-render replaces the chip instead of stacking a second one', () => {
    // Sort/filter re-runs updateWithDetails on the same card.
    const el = analysed({ special: '96.4%' });
    el.updateWithDetails({
      weapon: 'AK-47', skin: 'Fade', paintindex: 38, paintwear: PAINTWEAR_FT,
      paintseed: 661, special: '99.1%'
    }, null);
    const chips = field(el, 'pattern-line').querySelectorAll('.special-chip');
    expect(chips).toHaveLength(1);
    expect(chips[0].textContent).toBe('99.1%');
  });

  test('an item with no special attribute gets no chip', () => {
    expect(field(analysed({}), 'pattern-line').querySelector('.special-chip')).toBeNull();
  });

  test('a paintless item never gets a chip, even if one is supplied', () => {
    const el = makeCard({ name: 'Medal' });
    el.updateWithDetails({ weapon: 'Medal', skin: 'x', paintindex: 0, special: '96.4%' }, null);
    expect(field(el, 'pattern-line').querySelector('.special-chip')).toBeNull();
  });
});

describe('applied decals', () => {
  function analysed(gc) {
    const el = makeCard({ name: 'X' });
    el.updateWithDetails({
      weapon: 'AK-47', skin: 'Redline', paintindex: 44, paintwear: PAINTWEAR_FT, ...gc
    }, null);
    return el;
  }

  test('renders a chip per sticker and marks the charm', () => {
    const el = analysed({
      stickers: [{ name: 'Katowice 2014', image: 'kat.png' }, { name: 'Titan', image: 't.png' }],
      keychains: [{ name: 'Lil Squirt', image: 'sq.png' }]
    });
    const chips = field(el, 'stickers').querySelectorAll('.sticker-chip');
    expect(chips).toHaveLength(3);
    expect(chips[0].dataset.label).toBe('Katowice 2014');
    expect(chips[2].classList.contains('charm')).toBe(true);
    expect(field(el, 'stickers').hidden).toBe(false);
  });

  test('a Sticker Slab reads as neither a plain sticker nor a plain charm', () => {
    const el = analysed({ keychains: [{ name: 'Sealed Titan', image: 's.png', slab: true }] });
    const chip = field(el, 'stickers').querySelector('.sticker-chip');
    expect(chip.classList.contains('charm')).toBe(true);
    expect(chip.classList.contains('slab')).toBe(true);
    expect(chip.dataset.label).toBe('Sealed Titan · Slab');
  });

  test('an id the catalog does not know falls back to a labelled ? placeholder', () => {
    const el = analysed({ stickers: [{ sticker_id: 987654 }] });
    const chip = field(el, 'stickers').querySelector('.sticker-chip');
    expect(chip.classList.contains('placeholder')).toBe(true);
    expect(chip.textContent).toBe('?');
    expect(chip.dataset.label).toBe('Sticker #987654');
  });

  test('sticker scrape is surfaced in the label', () => {
    const el = analysed({ stickers: [{ name: 'Titan', image: 't.png', wear: 0.5 }] });
    expect(field(el, 'stickers').querySelector('.sticker-chip').dataset.label)
      .toBe('Titan · 50% worn');
  });

  test('an item with no decals hides the row entirely', () => {
    const el = analysed({ stickers: [], keychains: [] });
    expect(field(el, 'stickers').hidden).toBe(true);
    expect(field(el, 'stickers').childElementCount).toBe(0);
  });

  test('a re-render rebuilds the row rather than appending to it', () => {
    const el = analysed({ stickers: [{ name: 'A', image: 'a.png' }] });
    el.updateWithDetails({
      weapon: 'AK-47', skin: 'Redline', paintindex: 44, paintwear: PAINTWEAR_FT,
      stickers: [{ name: 'B', image: 'b.png' }]
    }, null);
    const chips = field(el, 'stickers').querySelectorAll('.sticker-chip');
    expect(chips).toHaveLength(1);
    expect(chips[0].dataset.label).toBe('B');
  });

  test('decals that were there are cleared when a later render has none', () => {
    const el = analysed({ stickers: [{ name: 'A', image: 'a.png' }] });
    el.updateWithDetails({
      weapon: 'AK-47', skin: 'Redline', paintindex: 44, paintwear: PAINTWEAR_FT
    }, null);
    expect(field(el, 'stickers').hidden).toBe(true);
    expect(field(el, 'stickers').childElementCount).toBe(0);
  });
});

describe('failed analysis', () => {
  test('keeps the basic info and marks the analysed fields as failed', () => {
    const el = makeCard({ name: 'AK-47 | Redline', rarity: 'Classified' });
    el.updateWithDetails({ error: 'timed out' }, null);

    expect(el.classList.contains('error')).toBe(true);
    expect(el.classList.contains('loading')).toBe(false);
    expect(field(el, 'name').textContent).toBe('AK-47 | Redline'); // basic info survives
    expect(field(el, 'float').textContent).toBe('Analysis Failed');
    expect(field(el, 'float').classList.contains('error-message')).toBe(true);
    expect(field(el, 'float').classList.contains('loading-placeholder')).toBe(false);
    expect(field(el, 'pattern').textContent).toBe('Analysis Failed');
    expect(el.shadowRoot.querySelector('.error-message-small').textContent)
      .toBe('Detailed analysis failed');
  });

  // The red left edge for a failed analysis: what jsdom can and cannot see.
  //
  // Checked empirically before these two tests were written, not assumed:
  //   * jsdom (20.x, via jest-environment-jsdom 29) has no adoptedStyleSheets, so the component
  //     takes its <style> fallback path - the text read below really is the stylesheet the card
  //     installs.
  //   * getComputedStyle() on a shadow host returns an empty string for anything a :host() rule
  //     sets; jsdom does not apply shadow stylesheets to their host at all.
  //   * jsdom does not populate .sheet on a <style> inside a shadow root either, so the rule
  //     cannot be reached through the CSSOM.
  //   * jsdom's cascade also gets !important backwards in the plain document: an author
  //     !important declaration loses to an inline style there, the opposite of the real rule.
  //
  // So no jest test in this repo can assert that the edge actually renders red - only a browser
  // can. The two tests below check the two halves that ARE checkable and are named for exactly
  // that: the behavioural half (an errored card still carries a conflicting inline rarity
  // colour, which is *why* the rule needs !important) and a source-level guard on the single
  // token that decides the conflict.
  test('an errored card still carries its inline rarity colour, which the error edge must outrank', () => {
    const el = makeCard({ name: 'AK-47 | Redline', rarity: 'Classified' });
    el.updateWithDetails({ error: 'timed out' }, null);
    expect(el.classList.contains('error')).toBe(true);
    expect(el.style.getPropertyValue('border-left-color')).toBe('#D32CE6');
  });

  test('the installed stylesheet declares the error edge !important (source check, not a render check)', () => {
    const el = makeCard({ name: 'AK-47 | Redline', rarity: 'Classified' });
    const css = el.shadowRoot.querySelector('style').textContent;
    const errorRule = (css.match(/:host\(\.error\)\s*\{([^}]*)\}/) || [])[1];
    expect(errorRule).toBeDefined();
    // Without the !important the inline colour asserted above wins and this edge never renders,
    // which is how it sat dead in the stylesheet from 8d45891 until 2026-08-01.
    expect(errorRule).toMatch(/border-left-color\s*:[^;]*!important/);
  });

  test('a retry that fails again does not stack a second error line', () => {
    const el = makeCard({ name: 'X' });
    el.updateWithDetails({ error: 'timed out' }, null);
    el.updateWithDetails({ error: 'timed out' }, null);
    expect(el.shadowRoot.querySelectorAll('.error-message-small')).toHaveLength(1);
  });

  test('a successful analysis swaps loading for loaded', () => {
    const el = makeCard({ name: 'X' });
    el.updateWithDetails({ weapon: 'AK-47', skin: 'Redline', paintindex: 44 }, null);
    expect(el.classList.contains('loading')).toBe(false);
    expect(el.classList.contains('loaded')).toBe(true);
    expect(el.classList.contains('error')).toBe(false);
  });

  test('analysis can supply the inspect link the basic render lacked', () => {
    const el = makeCard({ name: 'X' });
    el.updateWithDetails({ weapon: 'AK-47', skin: 'Redline', paintindex: 44 }, 'steam://inspect/1');
    expect(field(el, 'inspect-link').getAttribute('href')).toBe('steam://inspect/1');
  });
});
