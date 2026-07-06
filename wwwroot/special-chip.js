// Special-attribute ("rare pattern") chip: a small colored dot + the attribute text,
// shown after the Pattern seed on both the single-item card (post.js) and the inventory
// grid rows (inventory-item.js). The dot's color signals WHICH kind of rare pattern this is
// (ruby-red, sapphire-blue, blue-gem light-blue, fade gradient, ...).
//
// The server sends the attribute as a flat string (`special`, e.g. "Ruby", "Tier 1.5",
// "96.4%", "Blue Gem 92% / 45% mag") plus the pattern name (`skin`, e.g. "Fade",
// "Amber Fade", "Marble Fade"). We classify that pair into a themed "kind" here so the two
// render sites - and the demo page - all draw the chip identically. Loaded before
// inventory-item.js/post.js (all `defer`), so `buildSpecialChip` is a plain global.

// A "kind" -> dot fill. Solid gems get a flat color; multi-hued patterns (fades, fire & ice,
// black pearl) get a gradient that mimics the skin. `accent` is a single representative color
// used to tint the chip's border/background so it reads as belonging to the same family.
const SPECIAL_CHIP_STYLES = {
  ruby:          { dot: '#e0224e', accent: '#e0224e' },  // Doppler Ruby - ruby red
  sapphire:      { dot: '#1a5fd0', accent: '#1a5fd0' },  // Doppler Sapphire - sapphire blue
  emerald:       { dot: '#12b886', accent: '#12b886' },  // Gamma Doppler Emerald - emerald green
  'black-pearl': { dot: 'radial-gradient(circle at 32% 28%, #8b97a8, #232838 68%)', accent: '#6b7688' },
  'blue-gem':    { dot: '#4aa8ff', accent: '#4aa8ff' },  // Case Hardened blue gem - light blue
  fade:          { dot: 'linear-gradient(135deg, #ffe14d 0%, #ff8a3d 48%, #b04bd6 100%)', accent: '#f0993d' },
  'amber-fade':  { dot: 'linear-gradient(135deg, #ffdf6b 0%, #ff8a1e 55%, #d1531a 100%)', accent: '#ff8a1e' },
  'acid-fade':   { dot: 'linear-gradient(135deg, #dbe64f 0%, #7fae2b 52%, #3f6f1f 100%)', accent: '#8fbf3a' },
  'fire-ice':    { dot: 'linear-gradient(120deg, #ff5a3c 0%, #ff5a3c 42%, #4aa8ff 58%, #4aa8ff 100%)', accent: '#9a7fd0' },
  tier:          { dot: '#dc2637', accent: '#dc2637' },  // Crimson Kimono glove tier - crimson
  generic:       { dot: '#2ecc71', accent: '#2ecc71' },  // fallback (the old accent green)
};

// Doppler phases each get their own hue, roughly tracking the phase's dominant color.
const PHASE_COLORS = { 1: '#e35fb0', 2: '#ff4d88', 3: '#4f83ff', 4: '#2bd4c4' };

// Classify a (special, pattern) pair into a chip kind, or null when there's nothing to show.
// Order matters: "Blue Gem 92%" and a fade "%" both end in '%', so blue gem is checked first.
function classifySpecial(special, pattern) {
  if (!special) return null;
  const p = pattern || '';

  if (/^Blue Gem/i.test(special)) return 'blue-gem';
  if (special === 'Ruby') return 'ruby';
  if (special === 'Sapphire') return 'sapphire';
  if (special === 'Emerald') return 'emerald';
  if (special === 'Black Pearl') return 'black-pearl';
  if (/^Phase\s*\d/i.test(special)) return 'phase';
  // Fades read as a percentage; the pattern name picks the amber / acid palette off the classic one.
  if (/%$/.test(special)) {
    if (p === 'Amber Fade') return 'amber-fade';
    if (p === 'Acid Fade') return 'acid-fade';
    return 'fade';
  }
  // Marble Fade "Fire & Ice" tiers: "1st Max" ... "10th Max", "FFI".
  if (p === 'Marble Fade' || /\bMax$/.test(special) || special === 'FFI') return 'fire-ice';
  // Glove finish tiers (Crimson Kimono etc.): "Tier 1.5", "Optimal".
  if (/^Tier\b/i.test(special) || special === 'Optimal') return 'tier';
  return 'generic';
}

function hexToRgba(hex, alpha) {
  const n = parseInt(hex.slice(1), 16);
  return `rgba(${(n >> 16) & 255}, ${(n >> 8) & 255}, ${n & 255}, ${alpha})`;
}

// Build the chip element for a (special, pattern) pair, or null when there's no attribute.
// The dot fill / accent come entirely from the tables above so every render site matches.
function buildSpecialChip(special, pattern) {
  const kind = classifySpecial(special, pattern);
  if (!kind) return null;

  const style = SPECIAL_CHIP_STYLES[kind] || SPECIAL_CHIP_STYLES.generic;
  let dotFill = style.dot;
  let accent = style.accent;
  if (kind === 'phase') {
    const phase = PHASE_COLORS[(special.match(/\d/) || [])[0]] || '#8a6cff';
    dotFill = phase;
    accent = phase;
  }

  const chip = document.createElement('span');
  chip.className = 'special-chip';
  chip.dataset.kind = kind;
  chip.style.borderColor = hexToRgba(accent, 0.45);
  chip.style.background = hexToRgba(accent, 0.12);

  const dot = document.createElement('span');
  dot.className = 'special-chip__dot';
  dot.style.background = dotFill;
  dot.style.boxShadow = `0 0 5px ${hexToRgba(accent, 0.8)}`;

  const text = document.createElement('span');
  text.className = 'special-chip__text';
  text.textContent = special;

  chip.append(dot, text);
  return chip;
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = { classifySpecial, buildSpecialChip };
}
