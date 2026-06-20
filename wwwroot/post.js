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

  q(".card-inspect").href = url;
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

window.addEventListener("load", function () {
  controls = {
    cardOuter: document.getElementById("item-card-outer"),
    template: document.getElementById("item-card-template"),
    textbox: document.getElementById("textbox"),
    button: document.getElementById("button"),
  };

  controls.textbox.addEventListener("keydown", function (event) {
    if (event.code === "Enter") {
      event.preventDefault();
      controls.button.click();
    }
  });

  controls.button.addEventListener("click", function (element) {
    element.target.blur();

    const input = controls.textbox.value;
    // Strip either the legacy (rungame/<steamid>) or new (run/730//) prefix.
    const reduced = input
      .replace(/^.*csgo_econ_action_preview(%20|\s+)/i, "")
      .trim();
    if (/^[SM]\d+A\d+D\d+$/.test(reduced) || /^[0-9A-F]+$/.test(reduced)) {
      window.location.hash = reduced;
      post(inspectPrefix + reduced, reduced);
      controls.textbox.value = ""; // clear on search, like the inventory page
    } else {
      controls.textbox.value = "Not a valid inspect link";
    }
  });

  if (window.location.hash) {
    const hashURL = window.location.hash.substring(1);
    controls.textbox.value = hashURL;
    controls.button.click();
  }
});

// Each search adds a card above the previous results rather than replacing them. A loading
// card goes up immediately; the response fills it in, or turns it into an error placeholder.
// Repeat searches resurface the existing card (result or error) with no network request.
function post(url, key) {
  const seen = cardsByInput.get(key);
  if (seen && seen.isConnected) {
    resurface(seen);
    return;
  }

  const card = controls.template.content.firstElementChild.cloneNode(true);
  // s/a/d/m links can need a Game Coordinator lookup (seconds), while hex cert links decode
  // locally (instant). Set expectations when a slow lookup is possible.
  if (/^[SM]\d+A\d+D\d+$/.test(key)) {
    card.querySelector(".card-name").textContent = "Looking up in-game…";
  }
  controls.cardOuter.prepend(card);
  cardsByInput.set(key, card);

  const start = performance.now();
  fetch(`/api?${new URLSearchParams({url})}`)
    .then((response) => response.json())
    .then((iteminfo) => {
      if (iteminfo.error) {
        renderErrorCard(card, iteminfo.error);
        return;
      }

      // Same item reached via a different link form: drop the fresh card and resurface the
      // one already on the page, pointing this search at it too.
      const assetId = String(iteminfo.a || iteminfo.itemid || "");
      const twin = assetId && cardsByAssetId.get(assetId);
      if (twin && twin !== card && twin.isConnected) {
        card.remove();
        cardsByInput.set(key, twin);
        resurface(twin);
        return;
      }

      const loadTime = ((performance.now() - start) / 1000).toFixed(2);
      try {
        populateCard(card, iteminfo, url, loadTime);
        if (assetId) cardsByAssetId.set(assetId, card);
      } catch (e) {
        renderErrorCard(card, "An error occurred while displaying the item data");
        throw e;
      }
    })
    .catch(() => {
      renderErrorCard(card, "Failed to load item details");
    });
}
