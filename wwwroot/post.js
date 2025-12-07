let elements;
const inspectPrefix =
  "steam://rungame/730/76561202255233023/+csgo_econ_action_preview%20";

const conversionBuffer = new ArrayBuffer(4);
const conversionView = new DataView(conversionBuffer);

function uint32ToFloat32(uint32Value) {
  conversionView.setUint32(0, uint32Value);
  return conversionView.getFloat32(0);
}

function getWearFromFloat(float) {
  if (float < 0.07) return "Factory New";
  if (float < 0.15) return "Minimal Wear";
  if (float < 0.38) return "Field-Tested";
  if (float < 0.45) return "Well-Worn";
  return "Battle-Scarred";
}

function getRarityFromNumber(rarityNumber) {
  const rarities = [
    "Default",
    "Consumer Grade",
    "Industrial Grade",
    "Mil-Spec Grade",
    "Restricted",
    "Classified",
    "Covert",
    "Contraband",
  ];
  return rarities[rarityNumber] || "Unknown";
}

// Quality defines the category of existence / provenance state of the item
// This is distinct from wear (Factory New, etc.) which is determined by float value
function getQualityFromNumber(qualityNumber) {
  const qualities = {
    0: "Normal",       // Stock/Vanilla items, default weapons
    1: "Genuine",      // Promotional items from real-world events
    2: "Vintage",      // Legacy items predating economy updates
    3: "★",            // Unusual - Knives and Gloves (star prefix)
    4: "Unique",       // Standard drops from cases (no prefix)
    5: "Community",    // Reserved/Deprecated
    6: "Valve",        // Developer items with Flying Bits effect
    7: "Self-Made",    // Workshop creator items with Community Sparkle
    9: "StatTrak™",    // Items with kill counter (Strange)
    12: "Souvenir",    // Tournament drops with pre-applied stickers
  };
  return qualities[qualityNumber] || null;
}

function getOriginFromNumber(originNumber) {
  const origins = {
    0: "Timed Drop",      // Random drop at end of match
    1: "Achievement",     // Granted via achievement (mostly obsolete)
    2: "Purchased",       // Bought from in-game store
    3: "Traded",          // Obtained via trade
    4: "Crafted",         // Created via Trade Up Contract
    8: "Unboxed",         // Found in Crate (case opening)
    24: "Tournament Drop" // Dropped during a Major (Souvenir Packages)
  };
  return origins[originNumber] || "Unknown";
}

// Check if defindex belongs to knife/glove category (500+ for knives, 5000+ for gloves)
function isKnifeOrGlove(defindex) {
  // Knives typically have defindex 500-600
  // Gloves typically have defindex 5000+
  return (defindex >= 500 && defindex < 600) || defindex >= 5000;
}

function display(iteminfo, url, loadTime) {
  stopLoading();

  if (iteminfo.error) {
    handleError(iteminfo.error);
    return;
  }

  try {
    elements.itemName.innerHTML = `${iteminfo.weapon} | ${iteminfo.skin} <span class="pop">${iteminfo.special}</span>`;
    elements.itemName.classList.remove("knife", "souvenir", "genuine", "vintage", "valve", "selfmade");
    // Check for knife/glove using defindex (500-600 for knives, 5000+ for gloves)
    // This is more reliable than quality === 3, since StatTrak knives have quality 9
    if (isKnifeOrGlove(iteminfo.defindex)) {
      elements.itemName.classList.add("knife");
    }
    // Handle special qualities
    if (iteminfo.quality === 1) {
      elements.itemName.classList.add("genuine");
    } else if (iteminfo.quality === 2) {
      elements.itemName.classList.add("vintage");
    } else if (iteminfo.quality === 6) {
      elements.itemName.classList.add("valve");
    } else if (iteminfo.quality === 7) {
      elements.itemName.classList.add("selfmade");
    } else if (iteminfo.quality === 12) {
      elements.itemName.classList.add("souvenir");
    }
    const paintwearFloat = uint32ToFloat32(iteminfo.paintwear);
    elements.itemPaintwear.innerHTML = paintwearFloat;
    elements.itemWear.innerHTML = getWearFromFloat(paintwearFloat);
    elements.itemRarity.innerHTML = getRarityFromNumber(iteminfo.rarity);
    if (iteminfo.itemid == 0) {
      elements.itemItemid.innerHTML = "Unknown";
    } else {
      elements.itemItemid.innerHTML = iteminfo.itemid;
    }
    elements.itemPaintseed.innerHTML = iteminfo.paintseed;
    elements.itemOrigin.innerHTML = getOriginFromNumber(iteminfo.origin);
    const qualityName = getQualityFromNumber(iteminfo.quality);
    elements.itemQuality.innerHTML = qualityName || "Unique";
    elements.status.innerHTML = `Loaded in ${loadTime} seconds`;
    elements.stattrakIndicator.classList.remove("yes");
    if (iteminfo.stattrak) {
      elements.stattrakIndicator.classList.add("yes");
    }
    elements.inspectButton.href = url;
  } catch (e) {
    handleError("An error occurred while displaying the item data");
    throw e;
  }
}

function resetFields() {
  elements.itemName.innerHTML = "-";
  elements.itemName.classList.remove("knife", "souvenir", "genuine", "vintage", "valve", "selfmade");
  elements.itemPaintwear.innerHTML = "-";
  elements.itemWear.innerHTML = "-";
  elements.itemRarity.innerHTML = "-";
  elements.itemItemid.innerHTML = "-";
  elements.itemPaintseed.innerHTML = "-";
  elements.itemOrigin.innerHTML = "-";
  elements.itemQuality.innerHTML = "-";
  elements.status.innerHTML = "";
  elements.stattrakIndicator.classList.remove("yes");
  elements.inspectButton.href = "#";
  elements.errorDisplay.style.display = "none";
}

function startLoading() {
  elements.itemName.parentElement.classList.add("loading");
  elements.itemPaintwear.parentElement.classList.add("loading");
  elements.itemWear.parentElement.classList.add("loading");
  elements.itemRarity.parentElement.classList.add("loading");
  elements.itemItemid.parentElement.classList.add("loading");
  elements.itemPaintseed.parentElement.classList.add("loading");
  elements.itemOrigin.parentElement.classList.add("loading");
  elements.itemQuality.parentElement.classList.add("loading");
}

function stopLoading() {
  elements.itemName.parentElement.classList.remove("loading");
  elements.itemPaintwear.parentElement.classList.remove("loading");
  elements.itemWear.parentElement.classList.remove("loading");
  elements.itemRarity.parentElement.classList.remove("loading");
  elements.itemItemid.parentElement.classList.remove("loading");
  elements.itemPaintseed.parentElement.classList.remove("loading");
  elements.itemOrigin.parentElement.classList.remove("loading");
  elements.itemQuality.parentElement.classList.remove("loading");
}

function handleError(errorMessage) {
  resetFields();
  elements.errorDisplay.innerHTML = errorMessage;
  elements.errorDisplay.style.display = "block";
}

window.addEventListener("load", function () {
  elements = {
    itemName: document.getElementById("item_name"),
    itemPaintwear: document.getElementById("item_paintwear"),
    itemWear: document.getElementById("item_wear"),
    itemRarity: document.getElementById("item_rarity"),
    itemItemid: document.getElementById("item_itemid"),
    itemPaintseed: document.getElementById("item_paintseed"),
    itemOrigin: document.getElementById("item_origin"),
    itemQuality: document.getElementById("item_quality"),
    status: document.getElementById("status"),
    stattrakIndicator: document.getElementById("stattrak-indicator"),
    inspectButton: document.getElementById("inspect_button"),
    textbox: document.getElementById("textbox"),
    button: document.getElementById("button"),
    errorDisplay: document.getElementById("error-display"),
  };

  elements.textbox.addEventListener("keydown", function (event) {
    if (event.code === "Enter") {
      event.preventDefault();
      elements.button.click();
    }
  });

  elements.button.addEventListener("click", function (element) {
    element.target.blur();

    const input = elements.textbox.value;
    const reduced = input.replace(inspectPrefix, "");
    if (/^[SM]\d+A\d+D\d+$/.test(reduced) || /^[0-9A-F]+$/.test(reduced)) {
      elements.textbox.value = reduced;
      window.location.hash = reduced;
      resetFields();
      post(inspectPrefix + reduced);
    } else {
      elements.textbox.value = "Not a valid inspect link";
    }
  });

  if (window.location.hash) {
    const hashURL = window.location.hash.substring(1);
    elements.textbox.value = hashURL;
    elements.button.click();
  }
});

function post(url) {
  startLoading();
  const start = performance.now();
  fetch(`/api?${new URLSearchParams({url})}`)
    .then((response) => response.json())
    .then((iteminfo) =>
      display(iteminfo, url, ((performance.now() - start) / 1000).toFixed(2))
    );
}
