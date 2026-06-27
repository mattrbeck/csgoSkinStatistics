// Static inventory page. Mirrors the server's GetInventoryData stitching
// (Controllers/SkinController.cs) — but the cert decode + enrichment that the C# does with
// SteamKit2 + the GC + SQLite all runs here in the browser, reusing the single-item code.

import { decodeCert } from "./proto.js";
import { resolveItem, needsSpecialShard } from "./resolve.js";
import { DataLoader } from "./loader.js";

const IMG_BASE = "https://community.akamai.steamstatic.com/economy/image/";

// Public CORS proxies, tried in order until one returns the inventory JSON. These are the
// "for now" stand-in for a real CORS shim (build/cors-proxy.mjs) — convenient for a demo,
// but they rate-limit, can go down, and see the SteamID64 you look up. A production static
// app would run its own ~40-line shim or a serverless edge function instead.
const STEAM_URL = (sid) => `https://steamcommunity.com/inventory/${sid}/730/2?l=english&count=2000`;
const PUBLIC_PROXIES = [
  (sid) => `https://proxy.cors.sh/${STEAM_URL(sid)}`,
  (sid) => `https://api.allorigins.win/raw?url=${encodeURIComponent(STEAM_URL(sid))}`,
  (sid) => `https://api.codetabs.com/v1/proxy/?quest=${encodeURIComponent(STEAM_URL(sid))}`,
];

const loader = new DataLoader();
let core = null;

const $ = (id) => document.getElementById(id);
const status = (m, err = false) => { const s = $("status"); s.textContent = m; s.classList.toggle("err", err); };

async function getInventory(steamid, source) {
  if (source === "proxy") {
    if (!/^7656\d{13}$/.test(steamid)) throw new Error("Enter a SteamID64 to fetch a live inventory.");
    let lastErr = "";
    for (const make of PUBLIC_PROXIES) {
      const url = make(steamid);
      const host = new URL(url).host;
      try {
        status(`Fetching via ${host}…`);
        const r = await fetch(url, { cache: "no-store" });
        if (!r.ok) { lastErr = `${host} → ${r.status}`; continue; }
        const data = await r.json();
        if (data && data.assets) return data;
        lastErr = `${host} → no inventory in response (private/empty?)`;
      } catch (e) { lastErr = `${host} → ${e.message || e}`; }
    }
    throw new Error(`All public proxies failed (${lastErr}). They rate-limit and go down — run \`node build/cors-proxy.mjs\` for a reliable local shim, or use the bundled fixture.`);
  }
  // Bundled real inventory (so the demo runs with no network/proxy at all).
  const r = await fetch("fixtures/inventory-sample.json", { cache: "no-store" });
  return r.json();
}

// Stitch the three parallel arrays the same way the C# controller does, then decode each
// item's certificate locally. propid 6 is the XOR cert; it decodes with zero GC traffic.
async function process(inv) {
  const propsByAsset = new Map();
  for (const ap of inv.asset_properties ?? []) {
    const byId = {};
    for (const p of ap.asset_properties ?? []) byId[p.propertyid] = p.string_value ?? p.int_value ?? p.float_value;
    propsByAsset.set(ap.assetid, byId);
  }
  const descByKey = new Map();
  for (const d of inv.descriptions ?? []) descByKey.set(`${d.classid}_${d.instanceid}`, d);

  const items = [];
  for (const asset of inv.assets ?? []) {
    const desc = descByKey.get(`${asset.classid}_${asset.instanceid}`);
    if (!desc) continue;
    const cert = propsByAsset.get(asset.assetid)?.[6];
    items.push({ asset, desc, cert });
  }
  return items;
}

async function enrich({ asset, desc, cert }) {
  const steamImg = desc.icon_url ? IMG_BASE + desc.icon_url : "";
  if (!cert) {
    // Non-inspectable (cases, tools, capsules, agents): render Steam's own data. No cert, so
    // nothing for us to decode — the old backend skipped these too.
    return { decoded: false, name: desc.market_name || desc.name, image: steamImg, type: desc.type };
  }
  const item = decodeCert(cert);
  let special = null;
  if (needsSpecialShard(item, core)) special = await loader.constSpecial();
  const info = resolveItem(item, core, special);
  const stickers = await Promise.all((item.stickers || []).map(async (s) =>
    ({ ...s, ...((await loader.sticker(s.sticker_id)) ?? { name: "", image: "" }) })));
  const keychains = await Promise.all((item.keychains || []).map(async (k) =>
    ({ ...k, ...(await loader.keychain(k.sticker_id, k.wrapped_sticker)) })));
  // Most items name themselves from the cert (weapon | skin). A few defindexes aren't in
  // const.json (agents, some passes); fall back to Steam's own name for those.
  const name = info.weapon ? `${info.weapon} | ${info.skin}` : (desc.market_name || desc.name);
  return {
    decoded: true, item, info, name,
    image: steamImg, // Steam's per-item thumbnail is in-response and comprehensive; free.
    stickers, keychains,
  };
}

function tile(e) {
  const t = $("tile-template").content.firstElementChild.cloneNode(true);
  const q = (s) => t.querySelector(s);
  const img = q(".tile-img img");
  if (e.image) img.src = e.image; else q(".tile-img").classList.add("ph");

  q(".tile-name").textContent = e.name || "Unknown";
  if (e.decoded && e.info.is_knife_or_glove) q(".tile-name").classList.add("knife");

  if (e.decoded && Number(e.item.paintindex) > 0 && e.item.paintwear_float != null) {
    q(".tile-float").textContent = `${e.info.wear_name} · ${e.item.paintwear_float.toFixed(6)} · seed ${e.item.paintseed}`;
  } else {
    q(".tile-float").textContent = e.decoded ? "" : (e.type || "");
  }

  const badges = q(".tile-badges");
  const badge = (txt, cls) => { const b = document.createElement("span"); b.className = `b ${cls}`; b.textContent = txt; badges.appendChild(b); };
  if (e.decoded) {
    if (e.info.special) badge(e.info.special, "sp");
    if (e.item.stattrak) badge(e.item.killeatervalue != null ? `ST ${e.item.killeatervalue.toLocaleString()}` : "ST", "st");
    if (e.info.quality_name && !["Unique", "Unknown"].includes(e.info.quality_name) && !e.item.stattrak) badge(e.info.quality_name, "q");
  } else {
    badge("not inspectable", "muted");
  }

  if (e.decoded) {
    const decals = [...(e.stickers || []), ...(e.keychains || [])];
    const wrap = q(".tile-stickers");
    for (const d of decals) {
      const chip = document.createElement("span");
      chip.className = "schip" + (d.slab ? " slab" : "");
      if (d.image) { const di = document.createElement("img"); di.src = d.image; chip.appendChild(di); }
      chip.title = (d.name || `#${d.sticker_id}`) + (d.slab ? " (slab)" : "");
      wrap.appendChild(chip);
    }
  }

  q(".tile-src").textContent = e.decoded ? "decoded from cert (client-side)" : "from Steam description";
  q(".tile-src").classList.add(e.decoded ? "ok" : "muted");
  return t;
}

async function run() {
  const steamid = $("steamid").value.trim();
  const source = document.querySelector('input[name="source"]:checked').value;
  $("grid").replaceChildren();
  $("summary").textContent = "";
  status("Fetching inventory…");

  let inv;
  try { inv = await getInventory(steamid, source); }
  catch (e) { status(String(e.message || e), true); return; }
  if (!inv.assets) { status(inv.error || "Inventory empty or private.", true); return; }

  status(`Decoding ${inv.assets.length} items in the browser…`);
  const t0 = performance.now();
  const stitched = await process(inv);
  const enriched = [];
  for (const s of stitched) { try { enriched.push(await enrich(s)); } catch { enriched.push({ decoded: false, name: s.desc?.name || "?", image: "" }); } }
  const ms = Math.round(performance.now() - t0);

  // Decoded first (the interesting items), then the rest.
  enriched.sort((a, b) => Number(b.decoded) - Number(a.decoded));
  const frag = document.createDocumentFragment();
  for (const e of enriched) frag.appendChild(tile(e));
  $("grid").appendChild(frag);

  const decoded = enriched.filter((e) => e.decoded).length;
  status("");
  $("summary").innerHTML =
    `<strong>${enriched.length}</strong> items · <strong>${decoded}</strong> decoded from embedded certificates ` +
    `<em>entirely client-side</em> in <strong>${ms} ms</strong> — that's ${decoded} Game Coordinator round-trips and ` +
    `${decoded} database rows the old backend needed, now <strong>zero</strong>. ` +
    `Catalog data: ${(loader.downloadedBytes() / 1024).toFixed(0)} KB across ${loader._cache.size} shards.`;
}

async function boot() {
  status("Loading manifest + core catalog…");
  await loader.init();
  core = await loader.constCore();
  status("");
  $("load").addEventListener("click", run);
  $("steamid").addEventListener("keydown", (e) => { if (e.key === "Enter") run(); });
}

boot();
