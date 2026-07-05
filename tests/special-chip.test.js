/**
 * Tests for special-chip.js - the rare-pattern chip shared by the item card and inventory grid.
 */

const { classifySpecial, buildSpecialChip } = require('../wwwroot/special-chip.js');

describe('classifySpecial', () => {
  test('returns null when there is no attribute', () => {
    expect(classifySpecial('', 'Fade')).toBeNull();
    expect(classifySpecial(null, 'Doppler')).toBeNull();
  });

  test.each([
    ['Ruby', 'Doppler', 'ruby'],
    ['Sapphire', 'Doppler', 'sapphire'],
    ['Emerald', 'Gamma Doppler', 'emerald'],
    ['Black Pearl', 'Doppler', 'black-pearl'],
    ['Phase 1', 'Doppler', 'phase'],
    ['Phase 4', 'Gamma Doppler', 'phase'],
    ['Tier 1.5', 'Crimson Kimono', 'tier'],
    ['Optimal', 'Crimson Kimono', 'tier'],
    ['1st Max', 'Marble Fade', 'fire-ice'],
    ['FFI', 'Marble Fade', 'fire-ice'],
  ])('classifies %s as %s', (special, pattern, kind) => {
    expect(classifySpecial(special, pattern)).toBe(kind);
  });

  test('splits fade percentages by pattern name', () => {
    expect(classifySpecial('96.4%', 'Fade')).toBe('fade');
    expect(classifySpecial('88.5%', 'Amber Fade')).toBe('amber-fade');
  });

  test('blue gem is matched before the fade percentage rule', () => {
    // Both "Blue Gem 92%" and a fade "%" end in '%'; blue gem must win.
    expect(classifySpecial('Blue Gem 92%', 'Case Hardened')).toBe('blue-gem');
    expect(classifySpecial('Blue Gem 92% / 45% mag', 'Case Hardened')).toBe('blue-gem');
  });

  test('unknown attributes fall back to the generic kind', () => {
    expect(classifySpecial('Something New', 'Weird Pattern')).toBe('generic');
  });
});

describe('buildSpecialChip', () => {
  test('returns null when there is no attribute', () => {
    expect(buildSpecialChip('', 'Fade')).toBeNull();
  });

  test('builds a chip with a dot and the attribute text, tagged by kind', () => {
    const chip = buildSpecialChip('Ruby', 'Doppler');
    expect(chip.classList.contains('special-chip')).toBe(true);
    expect(chip.dataset.kind).toBe('ruby');
    expect(chip.querySelector('.special-chip__dot')).not.toBeNull();
    expect(chip.querySelector('.special-chip__text').textContent).toBe('Ruby');
  });

  test('gives each Doppler phase its own dot color', () => {
    const p1 = buildSpecialChip('Phase 1', 'Doppler').querySelector('.special-chip__dot');
    const p3 = buildSpecialChip('Phase 3', 'Doppler').querySelector('.special-chip__dot');
    expect(p1.style.background).not.toBe('');
    expect(p1.style.background).not.toBe(p3.style.background);
  });
});
