/**
 * post.js's optimistic cert path: the lazy decoder load, the loading states, reconciliation, and
 * the offline path.
 *
 * These drive the real `post()` against the same fixtures tests/cert-decode.test.js uses, so the
 * "server response" here is literally what the running .NET app returned for that link.
 */

const fixtures = require('./fixtures/cert-fixtures.json');
const decals = require('../wwwroot/decals.js');
const specialChip = require('../wwwroot/special-chip.js');

const PLAIN = fixtures.find((f) => f.name === 'plain_skin');
const STICKERED = fixtures.find((f) => f.name === 'stickers');
const CERT_URL = 'steam://run/730//+csgo_econ_action_preview ' + PLAIN.hex;

// post.js reads these as plain globals (decals.js / special-chip.js / inventory.js each load as an
// ordinary <script> in the browser, before post.js).
beforeAll(() => {
  global.buildStickerChips = decals.buildStickerChips;
  global.buildFloatBar = decals.buildFloatBar;
  global.buildWearPill = decals.buildWearPill;
  global.rarityColorOf = decals.rarityColorOf;
  global.enableTooltip = () => {};
  global.enableLongPressCopy = () => {};
  global.buildSpecialChip = specialChip.buildSpecialChip;
  global.floatRanges = null; // fetched by inventory.js in the browser; absent here
});

const CARD_TEMPLATE = `
  <template id="item-card-template">
    <div class="item-card loading">
      <div class="card-image-frame">
        <img class="card-image" alt="" />
        <svg class="card-image-placeholder"></svg>
      </div>
      <div class="card-body">
        <div class="card-name">Loading…</div>
        <div class="card-submessage"></div>
        <div class="card-line card-float-line">
          <span class="card-wear-pill"></span>
          <span class="card-float">-</span>
          <span class="card-float-bar-slot"></span>
        </div>
        <div class="card-line card-pattern-line">
          <span class="card-seed"><span class="card-paintseed">-</span></span>
          <span class="card-rarity"></span>
        </div>
        <div class="item-stickers"></div>
        <dl class="card-meta">
          <div><dt>Wear</dt><dd class="card-wear">-</dd></div>
          <div><dt>Quality</dt><dd class="card-quality">-</dd></div>
          <div><dt>Origin</dt><dd class="card-origin">-</dd></div>
          <div><dt>ID</dt><dd class="card-itemid">-</dd></div>
        </dl>
        <div class="card-loadtime"></div>
      </div>
      <a class="card-inspect" href="#"></a>
    </div>
  </template>
  <div id="item-card-outer"></div>
  <form id="input"><input id="textbox" /><button id="button"></button></form>
`;

// Fresh module instance per test: post.js keeps session caches (cardsByInput) and runs initSearch
// at load, so it has to be re-evaluated against a fresh DOM each time.
function loadPost() {
  document.body.innerHTML = CARD_TEMPLATE;
  let mod;
  jest.isolateModules(() => { mod = require('../wwwroot/post.js'); });
  return mod;
}

const card = () => document.querySelector('.item-card');
const text = (sel) => card().querySelector(sel).textContent;
// Let the dynamic import() and any queued promise callbacks run.
const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

afterEach(() => {
  localStorage.clear();
});

describe('isHexCertKey (the gate that decides whether to download the decoder at all)', () => {
  const { isHexCertKey } = require('../wwwroot/post.js');

  test('accepts every fixture cert', () => {
    for (const f of fixtures) expect(isHexCertKey(f.hex)).toBe(true);
  });

  test('rejects S/A/D links, vanity names and id64s', () => {
    expect(isHexCertKey('S76561198084749846A698323590D7935523998312483177')).toBe(false);
    expect(isHexCertKey('mattrb')).toBe(false);
    expect(isHexCertKey('76561198261551396')).toBe(false);
    expect(isHexCertKey('')).toBe(false);
    expect(isHexCertKey(null)).toBe(false);
  });

  test('rejects odd-length and over-cap input, matching the server guards', () => {
    expect(isHexCertKey('A'.repeat(35))).toBe(false);
    expect(isHexCertKey('A'.repeat(2050))).toBe(false);
  });

  test('agrees with the decoder module it is a stand-in for', () => {
    const { looksLikeHexCert } = require('../wwwroot/cert-decode.js');
    const samples = [
      PLAIN.hex, PLAIN.hex.toLowerCase(), 'mattrb', '76561198261551396',
      'S1A2D3', 'A'.repeat(35), 'A'.repeat(34), 'A'.repeat(2050),
    ];
    for (const s of samples) expect(isHexCertKey(s)).toBe(looksLikeHexCert(s));
  });
});

describe('optimistic render while the server is still thinking', () => {
  let mod;
  beforeEach(() => {
    global.fetch = jest.fn(() => new Promise(() => {})); // never settles
    mod = loadPost();
    mod.post(CERT_URL, PLAIN.hex);
  });

  test('the card is revealed and filled from the link alone', async () => {
    await flush();
    // .pending is display:none. The optimistic decode removed it without waiting for /api, which
    // is the whole point: the card is on screen while the request is still in flight.
    expect(card().classList.contains('pending')).toBe(false);
    expect(global.fetch).toHaveBeenCalledTimes(1); // ...and the request did go out
    expect(global.fetch.mock.results[0].value).toBeInstanceOf(Promise); // still unresolved
    expect(card().dataset.source).toBe('cert');
    expect(text('.card-paintseed')).toBe('185');
    expect(text('.card-itemid')).toBe('13942577340');
    expect(text('.card-float')).toBe(String(PLAIN.server.paintwear_float));
    expect(text('.card-loadtime')).toBe('Decoded from the link');
  });

  test('wear band is computed locally and matches the server', async () => {
    await flush();
    expect(text('.card-wear')).toBe(PLAIN.server.wear_name);
    expect(card().querySelector('.wear-pill').textContent).toBe('FT');
  });

  test('server-only fields shimmer rather than showing a dash or a zero', async () => {
    await flush();
    for (const sel of ['.card-name', '.card-rarity', '.card-quality', '.card-origin']) {
      const el = card().querySelector(sel);
      expect(el.querySelector('.field-pending')).not.toBeNull();
      // The important part: it must not read as an answer.
      expect(el.textContent.trim()).not.toBe('-');
      expect(el.textContent.trim()).not.toBe('0');
    }
  });

  test('the image frame shimmers instead of showing the "no image" glyph', async () => {
    await flush();
    expect(card().querySelector('.card-image-frame').classList.contains('media-pending')).toBe(true);
    expect(card().querySelector('.card-image').hasAttribute('src')).toBe(false);
  });

  test('a skin gets a pending rare-pattern chip, so a fade cannot read as ordinary', async () => {
    await flush();
    const chip = card().querySelector('.special-chip');
    expect(chip).not.toBeNull();
    expect(chip.classList.contains('pending')).toBe(true);
  });

  test('nothing decoded locally is written to localStorage', async () => {
    await flush();
    expect(localStorage.getItem('skinstats:recents:v1')).toBeNull();
    expect(localStorage.length).toBe(0);
  });
});

describe('decals decoded from the cert', () => {
  test('sticker ids render immediately, flagged as still loading their names', async () => {
    global.fetch = jest.fn(() => new Promise(() => {}));
    const mod = loadPost();
    mod.post('steam://x ' + STICKERED.hex, STICKERED.hex);
    await flush();
    const chips = card().querySelectorAll('.sticker-chip');
    expect(chips).toHaveLength(3);
    chips.forEach((chip) => expect(chip.classList.contains('pending')).toBe(true));
    // The id is known, so the chip is labelled - it is not an anonymous blank.
    expect(chips[0].getAttribute('aria-label')).toContain('#4515');
  });
});

describe('a non-cert link never triggers the decoder', () => {
  test('an S/A/D key renders the in-game wait, with no pending-field affordances', async () => {
    global.fetch = jest.fn(() => new Promise(() => {}));
    const mod = loadPost();
    mod.post('steam://x', 'S1A2D3');
    await flush();
    expect(text('.card-name')).toBe('Looking up in-game…');
    expect(card().querySelectorAll('.field-pending')).toHaveLength(0);
    expect(card().dataset.source).toBeUndefined();
  });
});

describe('reconciliation when the server answers', () => {
  test('the server overwrites every optimistic value and clears the loading states', async () => {
    let settle;
    global.fetch = jest.fn(() => new Promise((r) => { settle = r; }));
    const mod = loadPost();
    mod.post(CERT_URL, PLAIN.hex);
    await flush();
    expect(card().querySelectorAll('.field-pending').length).toBeGreaterThan(0);

    settle({ json: () => Promise.resolve(PLAIN.server) });
    await flush();

    expect(card().querySelectorAll('.field-pending')).toHaveLength(0);
    expect(card().querySelector('.card-name').textContent).toBe('AK-47 | Exposure');
    expect(text('.card-quality')).toBe('Unique');
    expect(text('.card-origin')).toBe('Unboxed');
    expect(text('.card-rarity')).toBe('Classified');
    expect(card().dataset.source).toBeUndefined();
    expect(card().querySelector('.card-image-frame').classList.contains('media-pending')).toBe(false);
    expect(card().dataset.certMismatch).toBeUndefined();
  });

  test('the optimistic render is replaced, not stacked on top of', async () => {
    let settle;
    global.fetch = jest.fn(() => new Promise((r) => { settle = r; }));
    const mod = loadPost();
    mod.post(CERT_URL, PLAIN.hex);
    await flush();
    settle({ json: () => Promise.resolve(PLAIN.server) });
    await flush();
    expect(card().querySelectorAll('.wear-pill')).toHaveLength(1);
    expect(card().querySelectorAll('.float-bar')).toHaveLength(1);
    expect(card().querySelectorAll('.special-chip')).toHaveLength(0); // this item has no rare pattern
  });

  test('a drifting StatTrak kill count is NOT flagged as a disagreement', async () => {
    const st = fixtures.find((f) => f.name === 'stattrak');
    let settle;
    global.fetch = jest.fn(() => new Promise((r) => { settle = r; }));
    const mod = loadPost();
    mod.post('steam://x ' + st.hex, st.hex);
    await flush();
    // A cached server row that trails the live count.
    settle({ json: () => Promise.resolve({ ...st.server, stattrak_kills: 4000, inventory: 999 }) });
    await flush();
    expect(card().dataset.certMismatch).toBeUndefined();
    expect(card().querySelector('.st-detail').textContent).toBe(': 4,000 Kills'); // server's value shown
  });

  test('an immutable-field disagreement is surfaced, not swallowed', async () => {
    let settle;
    global.fetch = jest.fn(() => new Promise((r) => { settle = r; }));
    const mod = loadPost();
    mod.post(CERT_URL, PLAIN.hex);
    await flush();
    settle({ json: () => Promise.resolve({ ...PLAIN.server, paintseed: 999 }) });
    await flush();
    expect(card().dataset.certMismatch).toBe('paintseed');
    expect(text('.card-submessage')).toContain('paintseed');
    // The server's value is still what's displayed.
    expect(text('.card-paintseed')).toBe('999');
  });
});

describe('the server never answers', () => {
  test('the locally decoded data is kept and the missing fields say so', async () => {
    let fail;
    global.fetch = jest.fn(() => new Promise((_r, rej) => { fail = rej; }));
    const mod = loadPost();
    mod.post(CERT_URL, PLAIN.hex);
    await flush();

    fail(new TypeError('Failed to fetch'));
    await flush();

    expect(text('.card-paintseed')).toBe('185');      // still there
    expect(text('.card-wear')).toBe('Field-Tested');   // still there
    expect(card().querySelectorAll('.field-pending')).toHaveLength(0);
    const unavailable = card().querySelectorAll('.field-unavailable');
    expect(unavailable.length).toBeGreaterThan(0);
    unavailable.forEach((el) => expect(el.textContent).toBe('unavailable'));
    expect(text('.card-submessage')).toContain('Offline');
    expect(text('.card-name')).not.toBe('Item unavailable'); // NOT the error card
    expect(localStorage.length).toBe(0);
  });

  test('a server-reported error still keeps the link-derived data', async () => {
    let settle;
    global.fetch = jest.fn(() => new Promise((r) => { settle = r; }));
    const mod = loadPost();
    mod.post(CERT_URL, PLAIN.hex);
    await flush();
    settle({ json: () => Promise.resolve({ error: 'Item not found' }) });
    await flush();
    expect(text('.card-paintseed')).toBe('185');
    expect(text('.card-submessage')).toContain('Item not found');
    expect(card().querySelectorAll('.field-unavailable').length).toBeGreaterThan(0);
  });

  test('with no local decode, a failure still renders the ordinary error card', async () => {
    let fail;
    global.fetch = jest.fn(() => new Promise((_r, rej) => { fail = rej; }));
    const mod = loadPost();
    mod.post('steam://x', 'S1A2D3');
    await flush();
    fail(new TypeError('Failed to fetch'));
    await flush();
    expect(text('.card-name')).toBe('Item unavailable');
    expect(text('.card-submessage')).toBe('Failed to load item details');
  });
});

describe('markServerFieldsUnavailable', () => {
  test('replaces every shimmer and leaves nothing pending behind', () => {
    document.body.innerHTML = CARD_TEMPLATE;
    const mod = require('../wwwroot/post.js');
    const el = document.getElementById('item-card-template').content.firstElementChild.cloneNode(true);
    document.body.appendChild(el);
    mod.populateCardOptimistic(el, {
      itemid: 1, defindex: 7, paintindex: 0, rarity: 5, quality: 4, paintwear: 0, paintseed: 3,
      inventory: 0, origin: 8, stattrak: false, stattrak_kills: null, paintwear_float: 0,
      souvenir: false, is_knife_or_glove: false, wear_name: 'Factory New',
      stickers: [], keychains: [],
    }, '#');
    expect(el.querySelectorAll('.field-pending').length).toBeGreaterThan(0);
    // A paint-less item hides the float line. Wear still reads "Factory New" (paintwear 0 -> 0.0),
    // which is what the server-rendered card shows for the same item - misleading, but identical on
    // both sides, so the value never changes under the user. See caveat 8 in the doc.
    expect(el.querySelector('.card-float-line').style.display).toBe('none');
    expect(el.querySelector('.card-wear').textContent).toBe('Factory New');
    mod.markServerFieldsUnavailable(el);
    expect(el.querySelectorAll('.field-pending')).toHaveLength(0);
    expect(el.querySelectorAll('.field-unavailable').length).toBeGreaterThan(0);
  });
});
