/* eslint-disable no-unused-vars -- these are globals consumed by post.js and inventory.js;
   eslint lints each file alone and so can't see the cross-file use. */

// Shared front-end helpers used by BOTH pages (item + inventory), loaded as a plain script
// before either page's own script so these are ordinary globals:
//
//   1. classifyInput()  - decide whether a pasted string is an inspect link (item lookup) or a
//                         Steam profile (inventory lookup), so a single search bar can route to
//                         the right page. Both pages call this in submitSearch.
//   2. recent searches  - a small localStorage-backed list of past lookups (items + profiles),
//                         shared across the two pages and rendered as clickable chips on the
//                         empty landing of each.

// ---------------------------------------------------------------------------
// Input classification
// ---------------------------------------------------------------------------

// Strip either inspect-link prefix (legacy rungame/<steamid> or the new run/730// form) down to
// the bare command payload (the S..A..D.. / M..A..D.. form, or a hex cert).
function reduceInspect(raw) {
  return (raw || "").replace(/^.*csgo_econ_action_preview(?:%20|\s+)/i, "").trim();
}

// Returns { kind: 'item' | 'profile' | null, value }. `value` is the normalized form the caller
// acts on: the reduced inspect payload for items, or the raw profile identifier for profiles.
//
// Disambiguation order matters: a 17-digit SteamID64 is all digits and so would also match the
// hex-cert test, and a long hex cert would match nothing else - so we check the specific shapes
// (S/M inspect form, then SteamID64, then profile URL) before the looser hex / vanity catch-alls.
function classifyInput(raw) {
  const input = (raw || "").trim();
  if (!input) return { kind: null, value: "" };

  const reduced = reduceInspect(input);

  // Classic inspect form: S<owner>A<asset>D<d> (inventory) or M<market>A<asset>D<d> (market).
  if (/^[SM]\d+A\d+D\d+$/i.test(reduced)) return { kind: "item", value: reduced.toUpperCase() };

  // 17-digit SteamID64 in the individual-account block (76561197960265728+). All digits, so it
  // must be caught before the hex test below.
  if (/^7656119\d{10}$/.test(input)) return { kind: "profile", value: input };

  // Any steamcommunity.com profile URL (/id/<vanity> or /profiles/<id64>).
  if (/steamcommunity\.com/i.test(input)) return { kind: "profile", value: input };

  // Self-contained hex cert payload (a few hundred hex chars); the length floor keeps it from
  // swallowing short vanity names that happen to be all hex digits (e.g. "dad", "beef").
  if (/^[0-9a-fA-F]+$/.test(reduced) && reduced.length >= 34) {
    return { kind: "item", value: reduced.toUpperCase() };
  }

  // Bare Steam vanity name (letters, digits, _ and -). Matches the server's IsValidVanity.
  if (/^[A-Za-z0-9_-]{2,32}$/.test(input)) return { kind: "profile", value: input };

  return { kind: null, value: input };
}

// ---------------------------------------------------------------------------
// Recent searches
// ---------------------------------------------------------------------------

const RECENTS_KEY = "skinstats:recents:v1";
const RECENTS_MAX = 12;

function loadRecents() {
  try {
    const raw = localStorage.getItem(RECENTS_KEY);
    const list = raw ? JSON.parse(raw) : [];
    return Array.isArray(list) ? list : [];
  } catch {
    return []; // private mode / corrupt value: recents are a nicety, never a hard dependency
  }
}

function saveRecents(list) {
  try {
    localStorage.setItem(RECENTS_KEY, JSON.stringify(list.slice(0, RECENTS_MAX)));
  } catch {
    /* storage full or blocked: silently skip */
  }
}

// Record a lookup. `entry` is { type:'item'|'profile', value, label, sub?, avatar? }. Dedupes on
// type+value (a repeat search just moves back to the front) and caps the list.
function addRecent(entry) {
  if (!entry || !entry.value || !entry.label) return;
  const list = loadRecents().filter(
    (e) => !(e.type === entry.type && e.value === entry.value)
  );
  list.unshift({ ...entry, ts: Date.now() });
  saveRecents(list);
}

function removeRecent(type, value) {
  saveRecents(loadRecents().filter((e) => !(e.type === type && e.value === value)));
}

function clearRecents() {
  saveRecents([]);
}

// Small inline icons for the chip's leading glyph when there's no avatar.
function recentIcon(type) {
  const ns = "http://www.w3.org/2000/svg";
  const svg = document.createElementNS(ns, "svg");
  svg.setAttribute("viewBox", "0 0 24 24");
  svg.setAttribute("class", "recent-chip-icon");
  svg.setAttribute("aria-hidden", "true");
  const p = document.createElementNS(ns, "path");
  if (type === "profile") {
    // person glyph
    p.setAttribute("d", "M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8Zm-7 8a7 7 0 0 1 14 0");
  } else {
    // tag / item glyph
    p.setAttribute("d", "M4 13V5a1 1 0 0 1 1-1h8l7 7-9 9-7-7Zm4-6.5a1 1 0 1 0 0 2 1 1 0 0 0 0-2Z");
  }
  p.setAttribute("fill", "none");
  p.setAttribute("stroke", "currentColor");
  p.setAttribute("stroke-width", "1.7");
  p.setAttribute("stroke-linecap", "round");
  p.setAttribute("stroke-linejoin", "round");
  svg.appendChild(p);
  return svg;
}

// Render the recents panel into `container`. `onPick(value, entry)` runs the search for a chip.
// Re-renders in place on remove/clear, so the panel stays live without a page reload. The panel
// hides itself when there's nothing to show.
function renderRecents(container, onPick) {
  if (!container) return;
  const list = loadRecents();
  container.replaceChildren();
  if (!list.length) {
    container.hidden = true;
    return;
  }
  container.hidden = false;

  const head = document.createElement("div");
  head.className = "recent-head";
  const title = document.createElement("span");
  title.className = "recent-title";
  title.textContent = "Recent";
  const clear = document.createElement("button");
  clear.type = "button";
  clear.className = "recent-clear";
  clear.textContent = "Clear";
  clear.addEventListener("click", () => {
    clearRecents();
    renderRecents(container, onPick);
  });
  head.append(title, clear);

  const row = document.createElement("div");
  row.className = "recent-row";

  for (const e of list) {
    const chip = document.createElement("button");
    chip.type = "button";
    chip.className = `recent-chip recent-${e.type}`;
    chip.title = e.sub ? `${e.label} · ${e.sub}` : e.label;

    if (e.type === "profile" && e.avatar) {
      const img = document.createElement("img");
      img.className = "recent-chip-avatar";
      img.src = e.avatar;
      img.alt = "";
      img.loading = "lazy";
      chip.appendChild(img);
    } else {
      chip.appendChild(recentIcon(e.type));
    }

    const label = document.createElement("span");
    label.className = "recent-chip-label";
    label.textContent = e.label;
    chip.appendChild(label);

    chip.addEventListener("click", () => onPick(e.value, e));

    const del = document.createElement("span");
    del.className = "recent-chip-remove";
    del.setAttribute("role", "button");
    del.setAttribute("aria-label", `Remove ${e.label} from recent searches`);
    del.tabIndex = 0;
    del.textContent = "×";
    const doRemove = (ev) => {
      ev.stopPropagation();
      ev.preventDefault();
      removeRecent(e.type, e.value);
      renderRecents(container, onPick);
    };
    del.addEventListener("click", doRemove);
    del.addEventListener("keydown", (ev) => {
      if (ev.key === "Enter" || ev.key === " ") doRemove(ev);
    });
    chip.appendChild(del);

    row.appendChild(chip);
  }

  container.append(head, row);
}

// Exposed for unit tests under Node/CommonJS; in the browser these stay ordinary globals.
if (typeof module !== "undefined" && module.exports) {
  module.exports = { classifyInput, reduceInspect, loadRecents, addRecent, removeRecent, clearRecents };
}
