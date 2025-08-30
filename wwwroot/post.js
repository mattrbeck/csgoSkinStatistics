let elements;
const inspectPrefix =
  "steam://rungame/730/76561202255233023/+csgo_econ_action_preview%20";

function uint32ToFloat32(uint32Value) {
  const buffer = new ArrayBuffer(4);
  const view = new DataView(buffer);
  view.setUint32(0, uint32Value);
  return view.getFloat32(0);
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
    "Mil-Spec",
    "Restricted",
    "Classified",
    "Covert",
    "Contraband",
  ];
  return rarities[rarityNumber] || "Unknown";
}

function display(iteminfo, url, loadTime) {
  stopLoading();

  if (iteminfo.error) {
    handleError(iteminfo.error);
    return;
  }

  try {
    elements.itemName.innerHTML = `${iteminfo.weapon} | ${iteminfo.skin} <span class="pop">${iteminfo.special}</span>`;
    elements.itemName.classList.remove("knife", "souvenir");
    if (iteminfo.quality === 3) {
      elements.itemName.classList.add("knife");
    }
    if (iteminfo.quality === 12) {
      elements.itemName.classList.add("souvenir");
    }
    const paintwearFloat = typeof iteminfo.paintwear === 'number' && iteminfo.paintwear > 1 ? 
      uint32ToFloat32(iteminfo.paintwear) : parseFloat(iteminfo.paintwear);
    elements.itemPaintwear.innerHTML = paintwearFloat;
    elements.itemWear.innerHTML = getWearFromFloat(paintwearFloat);
    elements.itemRarity.innerHTML = getRarityFromNumber(iteminfo.rarity);
    if (iteminfo.itemid == 0) {
      elements.itemItemid.innerHTML = "Unknown";
    } else {
      elements.itemItemid.innerHTML = iteminfo.itemid;
    }
    elements.itemPaintseed.innerHTML = iteminfo.paintseed;
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
  elements.itemName.classList.remove("knife", "souvenir");
  elements.itemPaintwear.innerHTML = "-";
  elements.itemWear.innerHTML = "-";
  elements.itemRarity.innerHTML = "-";
  elements.itemItemid.innerHTML = "-";
  elements.itemPaintseed.innerHTML = "-";
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
}

function stopLoading() {
  elements.itemName.parentElement.classList.remove("loading");
  elements.itemPaintwear.parentElement.classList.remove("loading");
  elements.itemWear.parentElement.classList.remove("loading");
  elements.itemRarity.parentElement.classList.remove("loading");
  elements.itemItemid.parentElement.classList.remove("loading");
  elements.itemPaintseed.parentElement.classList.remove("loading");
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
