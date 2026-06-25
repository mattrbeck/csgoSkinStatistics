/**
 * Tests for decals.js - the rendering helpers shared by the inventory and item pages.
 */

const { rarityColorOf, buildWearPill, buildStickerChips, buildFloatBar } =
  require('../wwwroot/decals.js');

describe('rarityColorOf', () => {
  test('maps known rarities to their Steam colors', () => {
    expect(rarityColorOf('Mil-Spec Grade')).toBe('#4B69FF');
    expect(rarityColorOf('Covert')).toBe('#EB4B4B');
    expect(rarityColorOf('Contraband')).toBe('#E4AE39');
  });

  test('falls back to light gray for unknown rarities', () => {
    expect(rarityColorOf('Nonexistent')).toBe('#B0C3D9');
    expect(rarityColorOf(undefined)).toBe('#B0C3D9');
  });
});

describe('buildWearPill', () => {
  test.each([
    [0.06, 'fn', 'FN'],
    [0.10, 'mw', 'MW'],
    [0.25, 'ft', 'FT'],
    [0.40, 'ww', 'WW'],
    [0.50, 'bs', 'BS']
  ])('float %f -> %s pill', (float, cls, abbr) => {
    const pill = buildWearPill(float);
    expect(pill.className).toBe(`wear-pill ${cls}`);
    expect(pill.textContent).toBe(abbr);
  });

  test('thresholds are exclusive upper bounds', () => {
    expect(buildWearPill(0.07).textContent).toBe('MW');
    expect(buildWearPill(0.15).textContent).toBe('FT');
    expect(buildWearPill(0.45).textContent).toBe('BS');
  });
});

describe('buildStickerChips', () => {
  test('renders a chip per sticker and keychain', () => {
    const frag = buildStickerChips(
      [{ name: 'Sticker A', image: 'a.png', wear: 0 }],
      [{ name: 'Charm B', image: 'b.png' }]
    );
    const chips = frag.querySelectorAll('.sticker-chip');
    expect(chips).toHaveLength(2);
    expect(chips[1].classList.contains('charm')).toBe(true);
  });

  test('marks slab charms and shows a placeholder when the image is missing', () => {
    const frag = buildStickerChips([], [{ slab: true, wrapped_sticker: 5 }]);
    const chip = frag.querySelector('.sticker-chip');
    expect(chip.classList.contains('slab')).toBe(true);
    expect(chip.classList.contains('placeholder')).toBe(true);
    expect(chip.textContent).toBe('?');
    expect(chip.getAttribute('aria-label')).toContain('Slab');
  });

  test('surfaces sticker scrape in the label', () => {
    const frag = buildStickerChips([{ name: 'Worn', image: 'w.png', wear: 0.5 }], []);
    expect(frag.querySelector('.sticker-chip').getAttribute('aria-label')).toBe('Worn · 50% worn');
  });
});

describe('buildFloatBar', () => {
  test('positions the marker at the float percentage', () => {
    const bar = buildFloatBar(0.25, 44, null);
    expect(bar.classList.contains('float-bar')).toBe(true);
    expect(bar.querySelector('.float-marker').style.left).toBe('25%');
  });

  test('dims the unreachable ends when a paint-kit range is known', () => {
    const bar = buildFloatBar(0.25, 44, { 44: [0.1, 0.8] });
    const [left, right] = bar.querySelectorAll('.float-bar-dim');
    expect(parseFloat(left.style.width)).toBeCloseTo(10);  // min * 100
    expect(parseFloat(right.style.width)).toBeCloseTo(20);  // (1 - max) * 100
    expect(bar.dataset.range).toBe('Range: 0.1-0.8');
  });
});
