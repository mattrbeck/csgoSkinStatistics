let elements;
// We post the full steam:// link: the server matches on the command within it,
// and the "Inspect In Game" button reuses the same link to hand off to Steam.
// This is the run/730// form Steam's own site now emits, which drops the old
// dummy steamid the legacy rungame/<steamid> form required.
const inspectPrefix = "steam://run/730//+csgo_econ_action_preview%20";

function display(iteminfo, url, loadTime) {
  stopLoading();

  if (iteminfo.error) {
    handleError(iteminfo.error);
    return;
  }

  try {
    // Built as DOM nodes, never innerHTML: these strings are remote data.
    elements.itemName.textContent = `${iteminfo.weapon} | ${iteminfo.skin} `;
    if (iteminfo.special) {
      const special = document.createElement("span");
      special.className = "pop";
      special.textContent = iteminfo.special;
      elements.itemName.appendChild(special);
    }
    elements.itemName.classList.remove("knife", "souvenir", "genuine", "vintage", "valve", "selfmade");
    
    if (iteminfo.is_knife_or_glove) {
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
    } else if (iteminfo.souvenir) {
      elements.itemName.classList.add("souvenir");
    }
    
    elements.itemPaintwear.textContent = iteminfo.paintwear_float;
    elements.itemWear.textContent = iteminfo.wear_name;
    elements.itemRarity.textContent = iteminfo.rarity_name;
    if (iteminfo.itemid == 0) {
      elements.itemItemid.textContent = "Unknown";
    } else {
      elements.itemItemid.textContent = iteminfo.itemid;
    }
    elements.itemPaintseed.textContent = iteminfo.paintseed;
    elements.itemOrigin.textContent = iteminfo.origin_name;
    elements.itemQuality.textContent = iteminfo.quality_name;
    elements.status.textContent = `Loaded in ${loadTime} seconds`;
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
  elements.itemName.textContent = "-";
  elements.itemName.classList.remove("knife", "souvenir", "genuine", "vintage", "valve", "selfmade");
  elements.itemPaintwear.textContent = "-";
  elements.itemWear.textContent = "-";
  elements.itemRarity.textContent = "-";
  elements.itemItemid.textContent = "-";
  elements.itemPaintseed.textContent = "-";
  elements.itemOrigin.textContent = "-";
  elements.itemQuality.textContent = "-";
  elements.status.textContent = "";
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
  // textContent, not innerHTML: the message can echo server-provided strings
  elements.errorDisplay.textContent = errorMessage;
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
    inspectButton: document.getElementById("inspect-button"),
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
    // Strip either the legacy (rungame/<steamid>) or new (run/730//) prefix.
    const reduced = input
      .replace(/^.*csgo_econ_action_preview(%20|\s+)/i, "")
      .trim();
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
