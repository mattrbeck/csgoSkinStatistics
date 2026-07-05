let controls;
// We post the full steam:// link: the server matches on the command within it,
// and the "Inspect In Game" button reuses the same link to hand off to Steam.
// This is the run/730// form Steam's own site now emits, which drops the old
// dummy steamid the legacy rungame/<steamid> form required.
const inspectPrefix = "steam://run/730//+csgo_econ_action_preview%20";

// Paint-kit wear ranges (paint index -> [min, max]) used to dim the float bar's
// unreachable ends. Fetched once; buildFloatBar tolerates it being null (no dimming).
let floatRanges = null;
fetch("float-ranges.json")
  .then((r) => (r.ok ? r.json() : null))
  .then((data) => { floatRanges = data; })
  .catch(() => { /* cosmetic; the bar simply stays undimmed */ });

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
    document.body.classList.remove("pre-search"); // glide the search up out of its centered landing
    window.location.hash = cert;
    post(inspectPrefix + cert, cert);
    controls.textbox.value = ""; // clear on search, like the inventory page
  } else if (isProfile) {
    // The inventory analyzer is a separate page; it reads the profile from the hash on load.
    window.location.href = `/inventory#${encodeURIComponent(input)}`;
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

  if (window.location.hash) {
    const hashURL = window.location.hash.substring(1);
    controls.textbox.value = hashURL;
    submitSearch(hashURL);
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
