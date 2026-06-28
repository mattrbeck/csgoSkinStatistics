/* eslint-disable no-unused-vars -- every function here is a global consumed by post.js and
   inventory.js; eslint lints each file alone and so can't see the cross-file use. */

// Shared rendering used by both the inventory cards (inventory.js) and the single-item
// page (post.js): the applied-decal row, the float bar, and the wear pill. Loaded as a
// plain script before either page's script, so these are globals.
//
// The server resolves each decal to {name, image} (and, for charms, a `slab` flag and the
// sealed sticker id), so these builders render straight from the item response.

// The hover/focus tooltip on a chip or float bar is a CSS pseudo-element centered over the
// element, so one near a screen edge would overflow. On hover/focus we measure where the
// centered tooltip would land and set --tt-shift to nudge it back inside the viewport; the
// CSS transform reads that variable. Works in light DOM and shadow DOM (we use viewport
// coordinates and set the variable on the element itself).
let tooltipMeasureCtx;
function measureTooltipWidth(text) {
  if (!tooltipMeasureCtx) {
    tooltipMeasureCtx = document.createElement('canvas').getContext('2d');
  }
  // Match the tooltip font; add horizontal padding (8px each side) and border (1px each).
  tooltipMeasureCtx.font = '600 11px Lato, sans-serif';
  return tooltipMeasureCtx.measureText(text).width + 18;
}

function enableTooltip(el) {
  if (el.dataset.ttEnabled) return; // applyFloatRange can re-run on the same bar
  el.dataset.ttEnabled = '1';
  const clamp = () => {
    const text = el.dataset.label || el.dataset.range || '';
    const rect = el.getBoundingClientRect();
    const half = measureTooltipWidth(text) / 2;
    const center = rect.left + rect.width / 2;
    const margin = 8;
    let shift = 0;
    if (center - half < margin) shift = margin - (center - half);
    else if (center + half > window.innerWidth - margin) shift = (window.innerWidth - margin) - (center + half);
    el.style.setProperty('--tt-shift', `${Math.round(shift)}px`);
  };
  el.addEventListener('pointerenter', clamp);
  el.addEventListener('focus', clamp);
}

// Long-press a link to copy its href to the clipboard, with a brief "copied" state on the
// element. iOS Safari's native long-press menu doesn't offer "Copy" for custom-scheme links
// (steam://...), so the inspect buttons aren't copyable there; this provides it consistently.
// Pair with `-webkit-touch-callout: none` on the element so Safari's own menu doesn't fight it.
//
// The hold is detected by a timer (which arms the copy and shows a cue), but the clipboard write
// itself runs in the `touchend` handler. That split matters: iOS Safari only allows clipboard
// writes during a live user gesture, and a timer callback no longer counts as one — writing from
// the timer is silently rejected there, so the copy must wait for release. A tap (release before
// the threshold) still follows the link. Touch-only, so desktop clicks and right-click "Copy
// Link" are untouched.
function enableLongPressCopy(el) {
  if (el.dataset.copyBound) return;
  el.dataset.copyBound = '1';

  let timer = null;
  let armed = false;  // the hold passed the threshold; releasing now copies instead of navigating
  let copied = false; // a copy just happened; the click it precedes must not navigate
  const HOLD_MS = 450;

  const disarm = () => {
    if (timer) {
      clearTimeout(timer);
      timer = null;
    }
    if (armed) {
      armed = false;
      el.classList.remove('arming');
    }
  };

  el.addEventListener('touchstart', () => {
    copied = false;
    disarm();
    timer = setTimeout(() => {
      timer = null;
      const link = el.getAttribute('href');
      if (!link || link === '#' || !navigator.clipboard) return; // no link / no API: stay a link
      armed = true; // release will copy; cue the hold now since iOS gives no haptic of its own
      el.classList.add('arming');
      if (navigator.vibrate) navigator.vibrate(10);
    }, HOLD_MS);
  }, { passive: true });

  el.addEventListener('touchmove', disarm, { passive: true }); // a scroll isn't a hold
  el.addEventListener('touchcancel', disarm);

  el.addEventListener('touchend', () => {
    if (timer) { // released before the threshold: a tap, let it follow the link
      clearTimeout(timer);
      timer = null;
      return;
    }
    if (!armed) return;
    armed = false;
    el.classList.remove('arming');
    const link = el.getAttribute('href');
    if (!link || link === '#' || !navigator.clipboard) return;
    copied = true; // set synchronously so the following click is suppressed without a race
    // Called inside the touchend gesture so iOS Safari permits the write.
    navigator.clipboard.writeText(link).then(() => {
      el.classList.add('copied');
      setTimeout(() => el.classList.remove('copied'), 1200);
    }).catch(() => { /* clipboard blocked; nothing copied, navigation already suppressed */ });
  });

  el.addEventListener('click', (e) => {
    if (copied) {
      e.preventDefault(); // don't launch Steam right after copying
      copied = false;
    }
  });
}

// A Sticker Slab is a charm that seals a sticker inside it; the server sends the sealed
// sticker's name/image and flags it, so we show the sticker but mark it.
//
// opts.showLabels renders each decal's name inline (icon + name pill) instead of an icon-only
// chip with a hover tooltip. The single-item page passes it (room for the full name); the dense
// inventory grid leaves it off and keeps the compact icons + tooltip.
function buildStickerChips(stickers, keychains, opts = {}) {
  const showLabels = !!opts.showLabels;
  const decals = [
    ...(stickers || []).map(s => ({ s, charm: false })),
    ...(keychains || []).map(s => ({ s, charm: true })),
  ];

  const frag = document.createDocumentFragment();
  for (const { s, charm } of decals) {
    const slab = !!s.slab;
    const name = s.name || `${charm && !slab ? 'Charm' : 'Sticker'} #${slab ? s.wrapped_sticker : s.sticker_id}`;

    const chip = document.createElement('span');
    chip.className = charm ? 'sticker-chip charm' : 'sticker-chip';
    if (slab) chip.classList.add('slab');
    chip.tabIndex = 0;

    // Stickers scrape (wear 0 = pristine .. 1 = nearly gone); charms and slabs don't.
    // Surface the scrape level in the label and fade the thumbnail toward it, but never
    // below legibility.
    let label = slab ? `${name} · Slab` : name;
    const wear = Number(s.wear) || 0;
    const worn = !charm && wear > 0;
    if (worn) label += ` · ${Math.round(wear * 100)}% worn`;
    chip.setAttribute('aria-label', label);
    // Labeled chips print the name, so they don't need the hover tooltip (it would just repeat
    // the visible text); icon-only chips keep it as their only way to surface the name.
    if (!showLabels) {
      chip.dataset.label = label;
      enableTooltip(chip);
    }

    if (s.image) {
      const img = document.createElement('img');
      img.src = s.image;
      img.alt = name;
      img.loading = 'lazy';
      // Fade only the thumbnail toward its scraped state - not the chip, or the tooltip
      // text (rendered via the chip's ::after) would dim along with it.
      if (worn) img.style.opacity = String(1 - 0.6 * wear);
      chip.appendChild(img);
    } else {
      // Unknown/new id the catalog predates: keep a placeholder so the decal still shows and
      // its name stays reachable (via the tooltip, or the inline label when showing labels).
      chip.classList.add('placeholder');
      if (showLabels) {
        const ph = document.createElement('span');
        ph.className = 'sticker-ph';
        ph.textContent = '?';
        chip.appendChild(ph);
      } else {
        chip.textContent = '?';
      }
    }

    if (showLabels) {
      chip.classList.add('labeled');
      const nameEl = document.createElement('span');
      nameEl.className = 'sticker-name';
      nameEl.textContent = slab ? `${name} · Slab` : name;
      chip.appendChild(nameEl);
      if (worn) {
        const wearEl = document.createElement('span');
        wearEl.className = 'sticker-wear';
        wearEl.textContent = `${Math.round(wear * 100)}%`;
        chip.appendChild(wearEl);
      }
    }

    frag.appendChild(chip);
  }
  return frag;
}

// 0-1 wear scale with the five wear zones and a marker for where this item sits. The paint
// kit's reachable range (floatRanges[paintIndex]) dims the unrollable ends, and the range
// shows as a tooltip. Returns the bar element.
function buildFloatBar(paintwearFloat, paintIndex, floatRanges) {
  const bar = document.createElement('span');
  bar.className = 'float-bar';

  const left = document.createElement('span');
  left.className = 'float-bar-dim';
  const right = document.createElement('span');
  right.className = 'float-bar-dim';
  const marker = document.createElement('span');
  marker.className = 'float-marker';
  marker.style.left = `${Math.min(100, Math.max(0, paintwearFloat * 100))}%`;
  bar.append(left, right, marker);

  const range = floatRanges && paintIndex != null ? floatRanges[paintIndex] : null;
  if (range) {
    const [min, max] = range;
    left.hidden = !(min > 0);
    left.style.width = `${min * 100}%`;
    right.hidden = !(max < 1);
    right.style.width = `${(1 - max) * 100}%`;
    // Focusable so the tooltip is reachable by tap and keyboard, not just hover.
    bar.dataset.range = `Range: ${min}-${max}`;
    bar.tabIndex = 0;
    enableTooltip(bar);
  } else {
    left.hidden = true;
    right.hidden = true;
  }
  return bar;
}

const RARITY_COLORS = {
  // Standard weapon skin rarities (CS2/CS:GO)
  'Consumer Grade': '#B0C3D9',       // Light Gray/White
  'Industrial Grade': '#5E98D9',     // Light Blue
  'Mil-Spec Grade': '#4B69FF',       // Blue
  'Restricted': '#8847FF',           // Purple
  'Classified': '#D32CE6',           // Pink/Magenta
  'Covert': '#EB4B4B',               // Red (weapons)
  'Extraordinary': '#EB4B4B',        // Red (knives/gloves) - same tier as Covert; Steam tags it eb4b4b
  'Contraband': '#E4AE39',           // Gold/Orange (e.g. M4A4 Howl) - the only gold rarity

  // Agent rarities (based on Operation rewards)
  'Base Grade': '#B0C3D9',           // Light Gray/White
  'Distinguished': '#4B69FF',        // Blue (28 stars)
  'Exceptional': '#8847FF',          // Purple (52 stars)
  'Superior': '#D32CE6',             // Pink (76 stars)
  'Master': '#EB4B4B',               // Red (89 stars)

  // Stickers, charms, graffiti, etc. (same rarity-value colors as above)
  'High Grade': '#4B69FF',           // Blue (value 3, == Mil-Spec/Distinguished)
  'Remarkable': '#8847FF',           // Purple (value 4, == Restricted/Exceptional)
  'Exotic': '#D32CE6',               // Pink (value 5, == Classified/Superior)

  // Default weapon (no skin)
  'Stock': '#DED6CC'                 // Off-white/gray (Steam Rarity_Default_Weapon)
};

function rarityColorOf(rarity) {
  return RARITY_COLORS[rarity] || '#B0C3D9'; // Default to light gray if not found
}

const DECAL_WEAR_ABBREVIATIONS = [
  [0.07, 'fn', 'FN'],
  [0.15, 'mw', 'MW'],
  [0.38, 'ft', 'FT'],
  [0.45, 'ww', 'WW'],
  [Infinity, 'bs', 'BS'],
];

// Compact wear badge (FN/MW/FT/WW/BS), colored to match the float bar zones.
function buildWearPill(paintwearFloat) {
  const [, cls, abbr] = DECAL_WEAR_ABBREVIATIONS.find(([max]) => paintwearFloat < max);
  const pill = document.createElement('span');
  pill.className = `wear-pill ${cls}`;
  pill.textContent = abbr;
  return pill;
}

// Exposed for unit tests under Node/CommonJS. The browser has no `module`, so this is skipped
// there and the functions stay ordinary globals loaded via <script>.
if (typeof module !== 'undefined' && module.exports) {
  module.exports = {
    rarityColorOf, buildWearPill, buildStickerChips, buildFloatBar, RARITY_COLORS,
    DECAL_WEAR_ABBREVIATIONS
  };
}
