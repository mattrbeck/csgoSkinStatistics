// Client port of the server's ConstDataService — the "identify special information"
// code the prompt asked about, running entirely in the browser. Pure functions over the
// loaded catalog tables; no network here (the loader fetches shards, this interprets them).
//
// `core`    = const-core shard (items, skins, rarities, qualities, origins, doppler,
//             kimonos, fades, amberfades, fireice).
// `special` = const-special shard (fade_order, fireice_order) — only consulted for
//             Fade / Amber Fade / Marble Fade, so the caller can skip loading it otherwise.

const FIRE_ICE_NAMES = ["", "1st Max", "2nd Max", "3rd Max", "4th Max", "5th Max", "6th Max", "7th Max", "8th Max", "9th Max", "10th Max", "FFI"];

const getWeapon = (core, defindex) => core.items?.[defindex] ?? "";
const getPattern = (core, paintindex) => core.skins?.[paintindex] ?? "";

export function wearFromFloat(f) {
  if (f < 0.07) return "Factory New";
  if (f < 0.15) return "Minimal Wear";
  if (f < 0.38) return "Field-Tested";
  if (f < 0.45) return "Well-Worn";
  return "Battle-Scarred";
}

const rarityName = (core, r) => (core.rarities && r < core.rarities.length ? core.rarities[r] : "Unknown");
const qualityName = (core, q) => core.qualities?.[q] ?? "Unknown";
const originName = (core, o) => core.origins?.[o] ?? "Unknown";
const isSouvenir = (q) => q === 12;
const isKnifeOrGlove = (defindex) => (defindex >= 500 && defindex < 600) || defindex >= 5000;

function fadePercent(paintseed, reversed, fadeOrder) {
  const MIN = 80;
  if (paintseed < 0 || paintseed >= fadeOrder.length) return 0;
  let idx = fadeOrder[paintseed];
  if (reversed) idx = 1000 - idx;
  const actual = idx / 1001;
  return Math.round((MIN + actual * (100 - MIN)) * 10) / 10;
}

// Does this item's pattern require the const-special shard (fade_order / fireice_order)?
// Marble Fade only needs it for the fire&ice weapons; Fade / Amber Fade always do.
export function needsSpecialShard(item, core) {
  const weapon = getWeapon(core, item.defindex);
  const pattern = getPattern(core, item.paintindex);
  if (pattern === "Fade") return core.fades?.[weapon] != null;
  if (pattern === "Amber Fade") return core.amberfades?.[weapon] != null;
  if (pattern === "Marble Fade") return Array.isArray(core.fireice) && core.fireice.includes(weapon);
  return false;
}

function specialLabel(item, core, special) {
  const weapon = getWeapon(core, item.defindex);
  const pattern = getPattern(core, item.paintindex);
  const seed = item.paintseed | 0;
  const paint = String(item.paintindex);

  if (pattern === "Marble Fade" && core.fireice?.includes(weapon) && special?.fireice_order) {
    const i = special.fireice_order[seed];
    if (i >= 0 && i < FIRE_ICE_NAMES.length) return FIRE_ICE_NAMES[i];
  } else if (pattern === "Fade" && core.fades?.[weapon] != null && special?.fade_order) {
    return fadePercent(seed, core.fades[weapon], special.fade_order) + "%";
  } else if (pattern === "Amber Fade" && core.amberfades?.[weapon] != null && special?.fade_order) {
    return fadePercent(seed, core.amberfades[weapon], special.fade_order) + "%";
  } else if ((pattern === "Doppler" || pattern === "Gamma Doppler") && core.doppler?.[paint] != null) {
    return core.doppler[paint];
  } else if (pattern === "Crimson Kimono" && core.kimonos?.[String(seed)] != null) {
    return core.kimonos[String(seed)];
  }
  return "";
}

function marketHashName(core, weapon, pattern, wear, knifeOrGlove, souvenir, stattrak) {
  let s = "";
  if (knifeOrGlove) s += qualityName(core, 3) + " ";       // ★
  if (souvenir) s += qualityName(core, 12) + " ";          // Souvenir
  else if (stattrak) s += qualityName(core, 9) + " ";      // StatTrak™
  s += weapon;
  if (pattern !== getPattern(core, 0)) s += ` | ${pattern} (${wear})`;
  return s;
}

// Build the same shape the server's CreateResponse returns, minus the sticker/image
// resolution the caller layers on after fetching those shards.
export function resolveItem(item, core, special) {
  const weapon = getWeapon(core, item.defindex);
  const pattern = getPattern(core, item.paintindex);
  const wear = wearFromFloat(item.paintwear_float ?? 0);
  const knifeOrGlove = isKnifeOrGlove(item.defindex);
  const souvenir = isSouvenir(item.quality);
  return {
    weapon, skin: pattern,
    wear_name: wear,
    rarity_name: rarityName(core, item.rarity),
    quality_name: qualityName(core, item.quality),
    origin_name: originName(core, item.origin),
    special: specialLabel(item, core, special),
    is_knife_or_glove: knifeOrGlove,
    souvenir,
    market_hash_name: marketHashName(core, weapon, pattern, wear, knifeOrGlove, souvenir, item.stattrak),
    paintwear_float: item.paintwear_float,
  };
}
