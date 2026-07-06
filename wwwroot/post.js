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

// Make `card` the ONLY card on the item page. A new search replaces the previous item rather than
// stacking beneath it — back/forward and "Recent lookups" are how you revisit now. Replaced cards
// are only detached from the DOM (their refs live on in cardsByInput / cardsByAssetId), so
// re-searching one, or navigating back to it, re-attaches the rendered node with no refetch.
function showOnlyCard(card) {
  controls.cardOuter.replaceChildren(card);
}

// Re-show an already-rendered card as the sole item, with a brief flash.
function resurface(card) {
  showOnlyCard(card);
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
// Dedupe key: items by visible identity (name+float); profiles by their SteamId64, which never
// changes — persona name and vanity URL both can — so a re-query updates the row in place (with the
// fresh name) instead of adding a duplicate. Falls back to the share hash for older stored entries.
function recentKey(e) {
  if (e.type === "profile") return `profile:${e.steamId || e.value}`;
  return `item:${e.name}|${e.float}`;
}

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
    // Rare-pattern attribute + pattern name so the row can redraw the same chip the card shows.
    special: iteminfo.special || "",
    skin: iteminfo.skin || "",
    rarityColor: (typeof rarityColorOf === "function" ? rarityColorOf(iteminfo.rarity_name) : "") || "",
  });
}

// Record a resolved Steam profile. Called by inventory.js once /api/profile comes back.
// eslint-disable-next-line no-unused-vars -- called by inventory.js (a separate <script>)
function addRecentProfile(profile) {
  if (!profile || !profile.hash) return;
  upsertRecent({
    type: "profile",
    steamId: profile.steamid || null,  // stable identity for dedupe (persona + vanity can change)
    value: profile.hash,               // vanity or SteamId64 — re-runs the lookup on click
    name: profile.persona_name || profile.hash,
    avatar: profile.avatar || "",
    sinceYear: profile.since_year || null,
  });
}

// Build one recent row (a button that re-runs the lookup). DOM nodes only — names are remote data.
function recentRow(entry) {
  const isProfile = entry.type === "profile";
  const row = document.createElement("button");
  row.type = "button";
  row.className = isProfile ? "recent-row recent-profile" : "recent-row";
  row.title = entry.name;
  if (entry.rarityColor) row.style.borderLeftColor = entry.rarityColor;
  row.addEventListener("click", () => { controls.textbox.value = entry.value; submitSearch(entry.value); });

  const thumb = document.createElement("span");
  thumb.className = isProfile ? "recent-thumb avatar" : "recent-thumb";
  const src = isProfile ? entry.avatar : entry.image;
  if (src) {
    const img = document.createElement("img");
    img.src = src; img.alt = ""; img.loading = "lazy";
    thumb.appendChild(img);
  }
  row.appendChild(thumb);

  const nameBlock = document.createElement("span");
  nameBlock.className = "recent-nameblock";
  const name = document.createElement("span");
  name.className = "recent-name";
  name.textContent = entry.name;
  nameBlock.appendChild(name);
  // Rare-pattern chip (fade %, Ruby, blue gem, tier, ...) right after the name, so recents scan
  // the same way the item/inventory cards do.
  if (!isProfile && entry.special) {
    const chip = buildSpecialChip(entry.special, entry.skin);
    if (chip) nameBlock.appendChild(chip);
  }
  row.appendChild(nameBlock);

  const meta = document.createElement("span");
  meta.className = "recent-meta";
  if (isProfile) {
    const tag = document.createElement("span");
    tag.className = "recent-tag";
    tag.textContent = entry.sinceYear ? `Since ${entry.sinceYear}` : "Inventory";
    meta.appendChild(tag);
    row.appendChild(meta);
  } else if (entry.float) {
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
  // Items and profiles both show now; ignore any malformed / unknown-type entries.
  const list = loadRecents().filter((e) => e && (e.type === "item" || e.type === "profile"));
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

  // Rare-pattern chip (fade %, Ruby, blue gem, tier, ...) sits right after the seed, between
  // "Pattern: <n>" and the rarity label. Its dot color signals which kind of pattern it is.
  const patternLine = q(".card-pattern-line");
  patternLine.querySelector(".special-chip")?.remove();
  if (hasSkin && iteminfo.special) {
    const chip = buildSpecialChip(iteminfo.special, iteminfo.skin);
    if (chip) q(".card-seed").after(chip);
  }

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

// "Weapon | Skin", with a StatTrak badge and the quality/knife styling. The special-pattern
// note (fade %, Ruby, tier, ...) is no longer appended here - it renders as a chip after the
// Pattern seed instead (see the .card-pattern-line chip above).
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
// Record the active search in the URL as ?q=<value> (shareable; back/forward work via the popstate
// handler in initSearch). Every distinct search PUSHES a new history entry — including the first one
// from the empty landing — so Back walks the full trail the user actually visited (landing → item →
// inventory → …) and Forward retraces it, matching what the arrows do everywhere else on the web.
// (inventory.js separately replaceState's the canonical vanity into the current entry.)
function setQuery(q) {
  const current = new URLSearchParams(location.search).get("q");
  if (current === q) return; // same query — no duplicate entry
  history.pushState({ q }, "", location.pathname + "?q=" + encodeURIComponent(q));
}

// Return to the empty landing (e.g. the user hit Back past the first search).
function showLanding() {
  document.body.classList.remove("mode-item", "mode-inventory");
  document.body.classList.add("pre-search");
  controls.textbox.value = "";
}

// fromHistory = true when replaying a URL (initial load or back/forward): render, but don't push a
// new history entry.
function submitSearch(rawInput, fromHistory) {
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
    if (!fromHistory) setQuery(cert);
    // Show the item view (hide any prior inventory) and glide out of the centered landing.
    document.body.classList.remove("pre-search", "mode-inventory");
    document.body.classList.add("mode-item");
    post(inspectPrefix + cert, cert);
    controls.textbox.value = ""; // clear on search
  } else if (isProfile) {
    if (!fromHistory) setQuery(input); // inventory.js later replaceState's the canonical vanity
    // Inventory analysis renders INLINE on this same page (no navigation).
    document.body.classList.remove("pre-search", "mode-item");
    document.body.classList.add("mode-inventory");
    controls.textbox.value = ""; // clear immediately, matching the item flow
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

  // Back/forward: re-render whatever ?q the URL now holds (or the empty landing).
  window.addEventListener("popstate", () => {
    const q = new URLSearchParams(location.search).get("q");
    if (q) submitSearch(q, true);
    else showLanding();
  });

  // The search lives in ?q=<value>. Fall back to the legacy #<value> for old links, migrating it to
  // ?q= (via replaceState) so back/forward and sharing behave from here on.
  let initialQuery = new URLSearchParams(location.search).get("q");
  if (!initialQuery && location.hash.length > 1) {
    initialQuery = decodeURIComponent(location.hash.substring(1));
    history.replaceState({ q: initialQuery }, "", location.pathname + "?q=" + encodeURIComponent(initialQuery));
  }
  if (initialQuery) {
    submitSearch(initialQuery, true); // URL already carries the query — render without pushing again
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

// Re-record a resurfaced card's item in Recent lookups so re-viewing a cached item moves it back
// to the top, matching a fresh fetch (which records via addRecentItem) and the profile flow. Error
// cards carry no _iteminfo and are skipped, so they never enter recents.
function recordRecentFromCard(card, key) {
  if (card && card._iteminfo) addRecentItem(card._iteminfo, key);
}

// Each search shows a single card, replacing whatever item was there before. The card starts
// invisible until there's something to show - the result, or, if the lookup runs long, a loading
// state. Repeat searches (including back/forward) resurface the cached card - result or error -
// with no network request, even after it was detached by a later search.
function post(url, key) {
  const seen = cardsByInput.get(key);
  if (seen) {
    resurface(seen);
    recordRecentFromCard(seen, key); // a re-view moves the item up in Recent lookups
    return;
  }

  const card = controls.template.content.firstElementChild.cloneNode(true);
  card.classList.add("pending"); // invisible until revealed below
  // s/a/d/m links can need a Game Coordinator lookup (seconds), while hex cert links decode
  // locally (instant). Set expectations when a slow lookup is possible.
  if (/^[SM]\d+A\d+D\d+$/.test(key)) {
    card.querySelector(".card-name").textContent = "Looking up in-game…";
  }
  showOnlyCard(card);
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
      if (twin && twin !== card) {
        settled = true;
        clearTimeout(loadingTimer);
        card.remove();
        cardsByInput.set(key, twin);
        resurface(twin);
        recordRecentFromCard(twin, key);
        return;
      }

      const loadTime = ((performance.now() - start) / 1000).toFixed(2);
      reveal();
      try {
        populateCard(card, iteminfo, url, loadTime);
        if (assetId) cardsByAssetId.set(assetId, card);
        card._iteminfo = iteminfo; // stash so a later re-view (from cache) can re-record it
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
