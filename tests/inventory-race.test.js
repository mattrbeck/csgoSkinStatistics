/**
 * Regression test for the rapid re-search race (audit H4).
 *
 * When a second inventory search starts while the first is still in flight, the first run must not
 * touch shared UI/controller state on its way out - it should notice it's been superseded and bow
 * out silently. Before the fix, analyzeInventory read the *module-level* analysisController in its
 * catch/finally, so the aborted first run would null out the second run's live controller and paint
 * its "Analysis was cancelled" banner over the second run's fresh UI.
 *
 * This drives the real analyzeInventory (exported for the test) with a lightweight injected
 * `elements` and a fetch mock whose /api/inventory promise rejects on abort - exactly how a real
 * fetch behaves when its AbortSignal fires.
 */

const inventory = require('../wwwroot/inventory.js');

// A stand-in DOM node: enough surface for the pre-fetch path, setAnalyzeButtons, updateProgress,
// and the catch/finally to run without a real document.
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

describe('rapid re-search race (H4)', () => {
  let elements;

  beforeEach(() => {
    elements = makeElements();
    inventory.__setElements(elements);
    // /api/profile resolves as a non-success (so the profile handler no-ops and needs no DOM);
    // /api/inventory stays pending but rejects with an AbortError when its signal aborts.
    global.fetch = jest.fn((url, opts = {}) => {
      if (url.includes('/api/profile')) {
        return Promise.resolve({ json: () => Promise.resolve({ success: 0 }) });
      }
      if (url.includes('/api/inventory')) {
        return new Promise((resolve, reject) => {
          const signal = opts.signal;
          const abort = () => reject(new DOMException('The operation was aborted', 'AbortError'));
          if (signal) {
            if (signal.aborted) return abort();
            signal.addEventListener('abort', abort);
          }
        });
      }
      return Promise.resolve({ ok: false, json: () => Promise.resolve(null) });
    });
  });

  const flush = () => new Promise((r) => setTimeout(r, 0));

  test('a superseded first run does not stomp the second run', async () => {
    // Run A starts and parks on its /api/inventory fetch.
    const runA = inventory.analyzeInventory('profileA');
    await flush();
    const controllerA = inventory.__getAnalysisController();
    expect(controllerA).not.toBeNull();

    // A second search takes over: cancel the in-flight run (mirrors resetInterface), then start B.
    inventory.cancelAnalysis(); // aborts controllerA -> A's inventory fetch rejects with AbortError
    const runB = inventory.analyzeInventory('profileB');
    const controllerB = inventory.__getAnalysisController();

    // B installed its own controller, distinct from A's, and hasn't been aborted.
    expect(controllerB).not.toBe(controllerA);
    expect(controllerB.signal.aborted).toBe(false);

    // Let A's rejection propagate through its catch/finally.
    await runA;
    await flush();

    // The heart of the fix: A recognised it was superseded and left B's state untouched.
    expect(inventory.__getAnalysisController()).toBe(controllerB); // A did NOT null it
    expect(controllerB.signal.aborted).toBe(false);
    // A did NOT restore the Analyze button / hide the Cancel button that B is showing...
    expect(elements.cancelButton.style.display).toBe('flex');
    expect(elements.button.style.display).toBe('none');
    // ...and did NOT paint its cancellation banner over B's UI.
    expect(elements.errorDisplay.textContent).not.toBe('Analysis was cancelled');
    expect(elements.errorDisplay.style.display).not.toBe('block');

    // Tidy up the still-pending run B so it settles.
    controllerB.abort();
    await runB.catch(() => {});
  });
});
