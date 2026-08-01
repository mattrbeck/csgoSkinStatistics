/**
 * Regression test for the client-side inventory cache accepting a failure body.
 *
 * analyzeInventory used to decide "this was a success" by sniffing the parsed body for `.error`.
 * That trusted the server to describe every failure in one shape. Any other error body - MVC's
 * RFC-9110 ProblemDetails for a missing/blank `steamid`, a JSON error page from a proxy - carries
 * no `.error`, so it slipped past the check and was written into sessionStorage as though it were
 * an inventory, then replayed on the next reload (and reported as "No CS2 items found").
 *
 * The status is now what decides, so the client is correct whatever shape the body takes. This
 * drives the real analyzeInventory (exported for tests) with an injected `elements` and a fetch
 * mock, and asserts on sessionStorage directly.
 */

const inventory = require('../wwwroot/inventory.js');

function fakeEl() {
  return {
    style: {},
    textContent: '',
    value: '',
    classList: { add() {}, remove() {}, toggle() {}, contains() { return false; } },
    setAttribute() {},
  };
}

function makeElements() {
  const el = {};
  for (const k of [
    'button', 'cancelButton', 'errorDisplay', 'status', 'inventoryStatus',
    'inventoryContainer', 'inventorySummary', 'loadingMessage', 'progressFill',
    'progressText', 'textbox',
  ]) {
    el[k] = fakeEl();
  }
  return el;
}

// Answers /api/profile as a non-success (so the profile handler no-ops and needs no DOM) and
// /api/inventory with the given status/body.
function mockFetch(status, body) {
  global.fetch = jest.fn((url) => {
    if (url.includes('/api/profile')) {
      return Promise.resolve({ ok: true, status: 200, json: () => Promise.resolve({ success: 0 }) });
    }
    if (url.includes('/api/inventory')) {
      return Promise.resolve({ ok: status >= 200 && status < 300, status, json: () => Promise.resolve(body) });
    }
    return Promise.resolve({ ok: false, status: 404, json: () => Promise.resolve(null) });
  });
}

const cachedKeys = () =>
  Object.keys(sessionStorage).filter(k => k.startsWith('inv:'));

describe('inventory cache only accepts genuine successes', () => {
  let elements;

  beforeEach(() => {
    sessionStorage.clear();
    elements = makeElements();
    inventory.__setElements(elements);
  });

  test('a ProblemDetails 400 is never written to the cache', async () => {
    // Exactly what MVC returns for a missing/blank `steamid`: no `error` field anywhere.
    mockFetch(400, {
      type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
      title: 'One or more validation errors occurred.',
      status: 400,
      errors: { steamid: ['The steamid field is required.'] },
      traceId: '00-abc-def-00',
    });

    await inventory.analyzeInventory('someone');

    expect(cachedKeys()).toEqual([]);
    // And the user is told the request failed, not "No CS2 items found in inventory".
    expect(elements.errorDisplay.textContent).toBe('Request failed (400)');
  });

  test("the API's own { error } body is surfaced and not cached", async () => {
    mockFetch(400, { error: 'Inventory is private or user does not exist' });

    await inventory.analyzeInventory('someone-else');

    expect(cachedKeys()).toEqual([]);
    expect(elements.errorDisplay.textContent).toBe('Inventory is private or user does not exist');
  });

  test('a 200 that is not an inventory payload is not cached either', async () => {
    // e.g. an interstitial or login page that happens to parse as JSON.
    mockFetch(200, { some: 'other json' });

    await inventory.analyzeInventory('third');

    expect(cachedKeys()).toEqual([]);
    expect(elements.errorDisplay.textContent).toBe('Invalid response from server');
  });

  test('a genuine success is still cached', async () => {
    // An empty-but-valid inventory: enough to reach (and pass) the cache write, while stopping
    // short of the render path. The "no items" message comes after the write.
    mockFetch(200, { success: 1, total: 0, steamid: '76561198000000001', csgo_items: [] });

    await inventory.analyzeInventory('fourth');

    expect(cachedKeys()).toEqual(['inv:fourth']);
    const entry = JSON.parse(sessionStorage.getItem('inv:fourth'));
    expect(entry.data.csgo_items).toEqual([]);
    expect(entry.data.steamid).toBe('76561198000000001');
  });
});
