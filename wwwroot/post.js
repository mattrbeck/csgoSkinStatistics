let controls;
// We post the full steam:// link: the server matches on the command within it,
// and the "Inspect In Game" button reuses the same link to hand off to Steam.
// This is the run/730// form Steam's own site now emits, which drops the old
// dummy steamid the legacy rungame/<steamid> form required.
const inspectPrefix = "steam://run/730//+csgo_econ_action_preview%20";

// floatRanges (paint index -> [min,max], for dimming the float bar) is declared + fetched by
// inventory.js, which loads before post.js on this shared page; both this file and inventory-item.js
// read that one global.

// Session caches so a repeat search resurfaces an existing card instead of re-fetching:
// by the exact search string (caught before any network call), and by the resolved asset
// id (catches the same item reached via a different link form).
const cardsByInput = new Map();
const cardsByAssetId = new Map();

// Move an already-rendered card back to the top of the stack, with a brief flash.
function resurface(card) {
  controls.cardOuter.prepend(card);
  card.classList.remove("resurfaced");
  void card.offsetWidth; // restart the flash animation if it's already played
  card.classList.add("resurfaced");
}

// ---------------------------------------------------------------------------
// Recent lookups (localStorage-backed) — a short history shown on the empty landing so a repeat
// visit can re-open a past item in one click. Item lookups only for now (profiles open on the
// separate inventory page).
// ---------------------------------------------------------------------------
const RECENTS_KEY = "skinstats:recents:v1";
const RECENTS_MAX = 12;        // how many are kept in storage
const RECENTS_COLLAPSED = 5;   // how many show before "Show more" is clicked
let recentsExpanded = false;

// Float -> wear zone (exclusive upper bounds), so a recent row draws without the item response.
const RECENT_WEAR_ZONES = [
  [0.07, "fn", "FN"], [0.15, "mw", "MW"], [0.38, "ft", "FT"], [0.45, "ww", "WW"], [Infinity, "bs", "BS"],
];

function loadRecents() {
  try {
    const list = JSON.parse(localStorage.getItem(RECENTS_KEY) || "[]");
    return Array.isArray(list) ? list : [];
  } catch { return []; } // private mode / corrupt value: recents are a nicety, never required
}

function saveRecents(list) {
  try { localStorage.setItem(RECENTS_KEY, JSON.stringify(list.slice(0, RECENTS_MAX))); } catch { /* blocked */ }
}

// Dedupe key: the same item (name + float) collapses to one row regardless of link encoding.
function recentKey(e) { return `item:${e.name}|${e.float}`; }

// Insert newest-first, dedupe (a repeat lookup moves to the front), cap, re-render.
function upsertRecent(entry) {
  if (!entry || !entry.value) return;
  const key = recentKey(entry);
  const list = loadRecents().filter((e) => recentKey(e) !== key);
  list.unshift(entry);
  saveRecents(list);
  renderRecents();
}

// Capture just enough from a rendered item to redraw a rich row (thumb, name, float + wear zone).
function addRecentItem(iteminfo, reducedLink) {
  if (!iteminfo || !iteminfo.weapon) return;
  let float = "", wearClass = "", wearAbbr = "";
  if (Number(iteminfo.paintindex) > 0 && iteminfo.paintwear_float != null) {
    const f = Number(iteminfo.paintwear_float);
    const zone = RECENT_WEAR_ZONES.find(([max]) => f < max);
    float = f.toFixed(4); wearClass = zone[1]; wearAbbr = zone[2];
  }
  upsertRecent({
    type: "item",
    value: reducedLink,
    name: (iteminfo.is_knife_or_glove ? "★ " : "") + `${iteminfo.weapon} | ${iteminfo.skin}`,
    image: iteminfo.image || "",
    float, wearAbbr, wearClass,
    rarityColor: (typeof rarityColorOf === "function" ? rarityColorOf(iteminfo.rarity_name) : "") || "",
  });
}

// Build one recent row (a button that re-runs the lookup). DOM nodes only — names are remote data.
function recentRow(entry) {
  const row = document.createElement("button");
  row.type = "button";
  row.className = "recent-row";
  row.title = entry.name;
  if (entry.rarityColor) row.style.borderLeftColor = entry.rarityColor;
  row.addEventListener("click", () => { controls.textbox.value = entry.value; submitSearch(entry.value); });

  const thumb = document.createElement("span");
  thumb.className = "recent-thumb";
  if (entry.image) {
    const img = document.createElement("img");
    img.src = entry.image; img.alt = ""; img.loading = "lazy";
    thumb.appendChild(img);
  }
  row.appendChild(thumb);

  const name = document.createElement("span");
  name.className = "recent-name";
  name.textContent = entry.name;
  row.appendChild(name);

  if (entry.float) {
    const meta = document.createElement("span");
    meta.className = "recent-meta";
    const wear = document.createElement("span");
    wear.className = "recent-wear";
    wear.textContent = entry.wearAbbr;
    const fl = document.createElement("span");
    fl.className = `recent-float wear-${entry.wearClass}`;
    fl.textContent = entry.float;
    meta.append(wear, fl);
    row.appendChild(meta);
  }
  return row;
}

function renderRecents() {
  const host = document.getElementById("recent-lookups");
  if (!host) return;
  // Item lookups only on this page; ignore any foreign entries (e.g. profile rows written by a
  // different build sharing the storage key).
  const list = loadRecents().filter((e) => e && e.type === "item");
  if (!list.length) { host.hidden = true; host.replaceChildren(); return; }
  const head = document.createElement("div");
  head.className = "recents-head";
  head.textContent = "Recent lookups";
  const rows = document.createElement("div");
  rows.className = "recents-list";
  // Show only the first few until the user asks for more.
  const shown = recentsExpanded ? list : list.slice(0, RECENTS_COLLAPSED);
  shown.forEach((e) => rows.appendChild(recentRow(e)));
  const children = [head, rows];
  if (list.length > RECENTS_COLLAPSED) {
    const toggle = document.createElement("button");
    toggle.type = "button";
    toggle.className = "recents-toggle";
    toggle.textContent = recentsExpanded ? "Show less" : `Show ${list.length - RECENTS_COLLAPSED} more`;
    toggle.addEventListener("click", () => { recentsExpanded = !recentsExpanded; renderRecents(); });
    children.push(toggle);
  }
  host.replaceChildren(...children);
  host.hidden = false;
}

// Fill a freshly-cloned card with one item's data. All lookups are scoped to the card so
// many results can coexist on the page. Built as DOM nodes, never innerHTML: the names are
// remote data.
function populateCard(card, iteminfo, url, loadTime) {
  card.classList.remove("loading");
  const q = (sel) => card.querySelector(sel);

  renderName(q(".card-name"), iteminfo);

  // Color the card's left edge and the rarity label by Steam rarity tier.
  const rarityColor = rarityColorOf(iteminfo.rarity_name);
  card.style.borderLeftColor = rarityColor;
  const rarity = q(".card-rarity");
  rarity.textContent = iteminfo.rarity_name || "";
  rarity.style.color = rarityColor;

  // Paint-less items (medals, music kits, vanilla knives, ...) have no meaningful float or
  // pattern seed, so drop those rows.
  const hasSkin = Number(iteminfo.paintindex) > 0;
  if (hasSkin && iteminfo.paintwear_float != null) {
    const paintwear = Number(iteminfo.paintwear_float);
    q(".card-float").textContent = iteminfo.paintwear_float;
    q(".card-wear-pill").appendChild(buildWearPill(paintwear));
    q(".card-float-bar-slot").appendChild(buildFloatBar(paintwear, Number(iteminfo.paintindex), floatRanges));
  } else {
    q(".card-float-line").style.display = "none";
  }
  q(".card-seed").style.display = hasSkin ? "" : "none";
  q(".card-paintseed").textContent = iteminfo.paintseed;

  // Secondary fields, always shown.
  q(".card-wear").textContent = iteminfo.wear_name || "-";
  q(".card-quality").textContent = iteminfo.quality_name || "-";
  q(".card-origin").textContent = iteminfo.origin_name || "-";
  q(".card-itemid").textContent = iteminfo.itemid == 0 ? "Unknown" : iteminfo.itemid;

  // Skin image (server-resolved; "" for vanilla/unknown combos). With no src the CSS shows
  // the placeholder glyph in the frame instead of an empty box.
  const img = q(".card-image");
  if (iteminfo.image) {
    img.src = iteminfo.image;
    img.alt = `${iteminfo.weapon} | ${iteminfo.skin}`;
  } else {
    img.removeAttribute("src");
  }

  // Applied stickers / charms / slabs (shared with the inventory card).
  const hasDecals = (iteminfo.stickers || []).length > 0 || (iteminfo.keychains || []).length > 0;
  const stickers = q(".item-stickers");
  stickers.replaceChildren(hasDecals ? buildStickerChips(iteminfo.stickers, iteminfo.keychains) : "");
  stickers.style.display = hasDecals ? "" : "none";

  const inspect = q(".card-inspect");
  inspect.href = url;
  enableLongPressCopy(inspect); // long-press to copy the steam:// link (iOS Safari)
  q(".card-loadtime").textContent = `Loaded in ${loadTime} seconds`;
}

// "Weapon | Skin", with the special-pattern note (fade %, Ruby, tier, ...), a StatTrak
// badge, and the quality/knife styling.
function renderName(nameEl, iteminfo) {
  nameEl.className = "card-name";
  nameEl.textContent = `${iteminfo.weapon} | ${iteminfo.skin}`;

  if (iteminfo.is_knife_or_glove) {
    nameEl.classList.add("knife");
  }
  if (iteminfo.quality === 1) {
    nameEl.classList.add("genuine");
  } else if (iteminfo.quality === 2) {
    nameEl.classList.add("vintage");
  } else if (iteminfo.quality === 6) {
    nameEl.classList.add("valve");
  } else if (iteminfo.quality === 7) {
    nameEl.classList.add("selfmade");
  } else if (iteminfo.souvenir) {
    nameEl.classList.add("souvenir");
  }

  if (iteminfo.special) {
    const special = document.createElement("span");
    special.className = "item-special";
    special.textContent = iteminfo.special;
    nameEl.appendChild(special);
  }
  if (iteminfo.stattrak) {
    const badge = document.createElement("span");
    badge.className = "stattrak-badge";
    badge.textContent = "ST";
    // Kill count slides out of the badge on hover/focus (see .st-detail), matching the
    // inventory card. The count rides along in the cert/GC response for free.
    const kills = iteminfo.stattrak_kills;
    if (kills != null) {
      const detail = document.createElement("span");
      detail.className = "st-detail";
      detail.textContent = `: ${kills.toLocaleString()} Kills`;
      badge.appendChild(detail);
      badge.tabIndex = 0;
    }
    nameEl.appendChild(badge);
  }
}

// Turn an in-flight card into a placeholder for an item that couldn't be loaded: red edge,
// the image-placeholder glyph (no src), and the reason. It stays in the stack like a result.
function renderErrorCard(card, message) {
  card.classList.remove("loading");
  card.classList.add("error");
  card.querySelector(".card-name").textContent = "Item unavailable";
  // textContent, not innerHTML: the message can echo server-provided strings.
  card.querySelector(".card-submessage").textContent = message;
}

// The real search. Parses an inspect link (or the bare reduced form) and kicks off the lookup.
// Called by the early-input shim in index.html the moment the app is ready, and by the
// deep-link / queued-input paths below.
function submitSearch(rawInput) {
  controls.button.blur();
  const input = (rawInput || "").trim();
  // Strip either the legacy (rungame/<steamid>) or new (run/730//) inspect-link prefix.
  const reduced = input
    .replace(/^.*csgo_econ_action_preview(%20|\s+)/i, "")
    .trim();

  // One box, two kinds of input — that's what makes the search "universal". A CS2 inspect link
  // (an S/A/D or M/A/D command, or a long hex cert) is looked up as an item here; a Steam profile
  // (a 17-digit id64, a steamcommunity URL, or a bare vanity) is handed to the inventory page.
  // Order matters: a bare id64 is all digits (would match the hex test) so it's tested first, and
  // the length>=34 guard keeps a short vanity from being mistaken for a hex cert.
  const isItem = /^[SM]\d+A\d+D\d+$/i.test(reduced)
    || (/^[0-9a-fA-F]+$/.test(reduced) && reduced.length >= 34);
  const isProfile = !isItem && (
    /^7656119\d{10}$/.test(input)
    || /steamcommunity\.com/i.test(input)
    || /^[A-Za-z0-9_-]{2,32}$/.test(input)
  );

  if (isItem) {
    const cert = reduced.toUpperCase();
    // Show the item view (hide any prior inventory) and glide out of the centered landing.
    document.body.classList.remove("pre-search", "mode-inventory");
    document.body.classList.add("mode-item");
    window.location.hash = cert;
    post(inspectPrefix + cert, cert);
    controls.textbox.value = ""; // clear on search, like the inventory page
  } else if (isProfile) {
    // Inventory analysis renders INLINE on this same page now (no navigation). inventory.js owns
    // the inventory view + sets the hash from the resolved profile.
    document.body.classList.remove("pre-search", "mode-item");
    document.body.classList.add("mode-inventory");
    if (window.SkinInventory) window.SkinInventory.run(input);
    else window.__pendingSearch = input; // inventory.js not up yet — its init replays this
  } else {
    controls.textbox.value = "Not a valid inspect link or profile";
  }
}

function initSearch() {
  const textbox = document.getElementById("textbox");
  const button = document.getElementById("button");
  if (!textbox || !button) return; // not the search page (or a test importing this module)
  controls = {
    cardOuter: document.getElementById("item-card-outer"),
    template: document.getElementById("item-card-template"),
    textbox,
    button,
  };

  // Hand the real search to the early-input shim, which owns the Enter/click listeners so input
  // is captured before this script loads. If the shim isn't present (e.g. post.js used alone),
  // wire the listeners here as a fallback.
  window.__submitSearch = submitSearch;
  if (!window.__shimReady) {
    const form = document.getElementById("input");
    if (form) form.addEventListener("submit", (e) => { e.preventDefault(); submitSearch(controls.textbox.value); });
    controls.button.addEventListener("click", () => submitSearch(controls.textbox.value));
  }

  // "Try an example" tiles on the landing pre-fill the box and run the lookup.
  document.querySelectorAll(".example-item[data-value]").forEach((el) => {
    el.addEventListener("click", () => {
      controls.textbox.value = el.dataset.value;
      submitSearch(el.dataset.value);
    });
  });

  renderRecents(); // draw any stored history into the landing

  if (window.location.hash) {
    const hashURL = decodeURIComponent(window.location.hash.substring(1));
    controls.textbox.value = hashURL;
    submitSearch(hashURL); // classifies item vs profile and shows the right view
  } else if (window.__pendingSearch) {
    // A paste+search that arrived while this script was still downloading — replay it now.
    const queued = window.__pendingSearch;
    window.__pendingSearch = null;
    submitSearch(queued);
  } else {
    // Empty landing: put the cursor in the search box (the shim already did this; harmless).
    controls.textbox.focus({ preventScroll: true });
  }
}

// Wait this long before revealing the loading shimmer. Cert/cache lookups usually return
// faster than this, so they fill the card in directly and never flash a loading state; only
// a genuinely slow lookup (e.g. a Game Coordinator round trip) shows it.
const LOADING_DELAY_MS = 200;

// Each search adds a card above the previous results rather than replacing them. The card
// holds its spot at the top but stays invisible until there's something to show - the result,
// or, if the lookup runs long, a loading state. Repeat searches resurface the existing card
// (result or error) with no network request.
function post(url, key) {
  const seen = cardsByInput.get(key);
  if (seen && seen.isConnected) {
    resurface(seen);
    return;
  }

  const card = controls.template.content.firstElementChild.cloneNode(true);
  card.classList.add("pending"); // invisible until revealed below
  // s/a/d/m links can need a Game Coordinator lookup (seconds), while hex cert links decode
  // locally (instant). Set expectations when a slow lookup is possible.
  if (/^[SM]\d+A\d+D\d+$/.test(key)) {
    card.querySelector(".card-name").textContent = "Looking up in-game…";
  }
  controls.cardOuter.prepend(card);
  cardsByInput.set(key, card);

  let settled = false;
  const loadingTimer = setTimeout(() => {
    if (!settled) card.classList.remove("pending"); // slow lookup: show the shimmer
  }, LOADING_DELAY_MS);
  const reveal = () => {
    settled = true;
    clearTimeout(loadingTimer);
    card.classList.remove("pending");
  };

  const start = performance.now();
  fetch(`/api?${new URLSearchParams({url})}`)
    .then((response) => response.json())
    .then((iteminfo) => {
      if (iteminfo.error) {
        reveal();
        renderErrorCard(card, iteminfo.error);
        return;
      }

      // Same item reached via a different link form: drop the fresh card and resurface the
      // one already on the page, pointing this search at it too.
      const assetId = String(iteminfo.a || iteminfo.itemid || "");
      const twin = assetId && cardsByAssetId.get(assetId);
      if (twin && twin !== card && twin.isConnected) {
        settled = true;
        clearTimeout(loadingTimer);
        card.remove();
        cardsByInput.set(key, twin);
        resurface(twin);
        return;
      }

      const loadTime = ((performance.now() - start) / 1000).toFixed(2);
      reveal();
      try {
        populateCard(card, iteminfo, url, loadTime);
        if (assetId) cardsByAssetId.set(assetId, card);
        addRecentItem(iteminfo, key);
      } catch (e) {
        renderErrorCard(card, "An error occurred while displaying the item data");
        throw e;
      }
    })
    .catch(() => {
      reveal();
      renderErrorCard(card, "Failed to load item details");
    });
}

// Exposed for unit tests under Node/CommonJS. The browser has no `module`, so this is skipped
// there and the functions stay ordinary globals loaded via <script>.
if (typeof module !== 'undefined' && module.exports) {
  module.exports = { renderName, renderErrorCard };
}

// Bootstrap last, so everything this module defines (post(), LOADING_DELAY_MS, …) is
// initialized before initSearch can replay a queued/deep-linked search. Deferred script ⇒ the
// DOM is already parsed, so init right away rather than waiting for `load` (which on a slow link
// trails behind fonts/images, needlessly delaying the handlers); fall back to DOMContentLoaded
// if this is ever loaded without `defer`.
if (typeof document !== 'undefined') {
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", initSearch);
  } else {
    initSearch();
  }
}
