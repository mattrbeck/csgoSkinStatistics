// Wires the static prototype together: decode a hex cert in the browser, lazily pull only
// the catalog shards the item needs, render the card, and show the network breakdown.

import { decodeCert } from "./proto.js";
import { resolveItem, needsSpecialShard } from "./resolve.js";
import { DataLoader } from "./loader.js";

const loader = new DataLoader();
let core = null;

const $ = (id) => document.getElementById(id);
const status = (msg, isErr = false) => { const s = $("status"); s.textContent = msg; s.classList.toggle("err", isErr); };

// Pull the hex out of whatever the user pasted (full steam:// link or bare hex).
function extractHex(input) {
  const m = input.replace(/%20/gi, " ").match(/csgo_econ_action_preview\s+([0-9A-Fa-f]+)/);
  const hex = (m ? m[1] : input.trim());
  return /^[0-9A-Fa-f]+$/.test(hex) && hex.length >= 12 ? hex : null;
}

// Is this the legacy S/A/D/M form (needs the Game Coordinator, impossible client-side)?
function isMaskedForm(input) {
  return /csgo_econ_action_preview\s*[SM]\d+A\d+D\d+/i.test(input.replace(/%20/gi, " "));
}

async function handle(input) {
  if (isMaskedForm(input)) {
    status("That is the S/A/D/M link form — it carries no item data, only a Game Coordinator lookup token. A static client can't resolve it (the server's logged-in Steam bot does). Use a hex 'cert' link.", true);
    return;
  }
  const hex = extractHex(input);
  if (!hex) { status("Not a hex inspect link.", true); return; }

  status("Decoding in browser…");
  const before = loader.log.length;
  let item;
  try { item = decodeCert(hex); }
  catch { status("Could not decode that cert payload.", true); return; }

  // Names/special: const-core is already loaded; pull const-special only if this item needs it.
  let special = null;
  if (needsSpecialShard(item, core)) special = await loader.constSpecial();
  const info = resolveItem(item, core, special);

  // Decals + image: each resolves from a single bucket shard, fetched on demand.
  const stickers = await Promise.all((item.stickers || []).map(async (s) => ({
    ...s, ...((await loader.sticker(s.sticker_id)) ?? { name: "", image: "" }),
  })));
  const keychains = await Promise.all((item.keychains || []).map(async (k) =>
    ({ ...k, ...(await loader.keychain(k.sticker_id, k.wrapped_sticker)) })));
  const image = Number(item.paintindex) > 0 ? await loader.image(item.defindex, item.paintindex) : "";

  renderCard({ item, info, stickers, keychains, image, netLog: loader.log.slice(before) });
  status("");
  $("session-stats").textContent =
    `session: ${(loader.downloadedBytes() / 1024).toFixed(1)} KB (uncompressed) across ${loader._cache.size} shards (+ manifest) — roughly a quarter of this over the wire with gzip`;
}

function renderCard({ item, info, stickers, keychains, image, netLog }) {
  const card = $("card-template").content.firstElementChild.cloneNode(true);
  const q = (sel) => card.querySelector(sel);

  const name = q(".card-name");
  name.textContent = `${info.weapon || "?"} | ${info.skin || "?"}`;
  if (info.is_knife_or_glove) name.classList.add("knife");
  if (info.special) { const sp = document.createElement("span"); sp.className = "special"; sp.textContent = info.special; name.appendChild(sp); }
  if (item.stattrak) { const b = document.createElement("span"); b.className = "st"; b.textContent = item.killeatervalue != null ? `ST · ${item.killeatervalue.toLocaleString()} kills` : "ST"; name.appendChild(b); }

  q(".card-rarity").textContent = info.rarity_name || "";

  const hasSkin = Number(item.paintindex) > 0;
  q(".card-float").textContent = hasSkin && item.paintwear_float != null ? item.paintwear_float.toFixed(8) : "—";
  q(".card-wear").textContent = info.wear_name || "—";
  q(".card-seed").textContent = hasSkin ? `seed ${item.paintseed}` : "—";
  q(".card-quality").textContent = info.quality_name || "—";
  q(".card-origin").textContent = info.origin_name || "—";
  q(".card-itemid").textContent = item.itemid === "0" ? "Unknown" : item.itemid;

  const img = q(".card-image");
  if (image) { img.src = image; img.alt = name.textContent; } else { img.classList.add("placeholder"); }

  const decals = [...stickers.map((s) => ({ ...s, kind: "sticker" })), ...keychains.map((k) => ({ ...k, kind: k.slab ? "slab" : "charm" }))];
  if (decals.length) {
    const wrap = q(".card-stickers");
    for (const d of decals) {
      const chip = document.createElement("span");
      chip.className = `chip chip-${d.kind}`;
      if (d.image) { const di = document.createElement("img"); di.src = d.image; di.alt = ""; chip.appendChild(di); }
      const label = document.createElement("span");
      label.textContent = (d.name || `#${d.sticker_id}`) + (d.kind === "slab" ? " (slab)" : "");
      chip.appendChild(label);
      wrap.appendChild(chip);
    }
  }

  q(".card-market").textContent = info.market_hash_name;

  // Per-item network breakdown.
  const fresh = netLog.filter((e) => !e.reused);
  const reused = netLog.filter((e) => e.reused);
  q(".card-net-sum").textContent =
    `${fresh.length} shard${fresh.length === 1 ? "" : "s"} fetched` +
    (reused.length ? `, ${reused.length} reused from cache` : "");
  const table = q(".card-net-table");
  for (const e of netLog) {
    const tr = document.createElement("tr");
    tr.className = e.reused ? "reused" : "";
    tr.innerHTML = `<td>${e.reused ? "cache" : "fetch"}</td><td>${e.name}</td><td>${e.reused ? "—" : (e.bytes / 1024).toFixed(1) + " KB"}</td>`;
    table.appendChild(tr);
  }

  $("cards").prepend(card);
}

// Warm the rest of the catalog during browser idle time, before the user types anything.
// Each shard is immutable + cache-forever, so this populates the HTTP cache too: a query
// later resolves from memory with zero network. One shard per idle tick keeps the main
// thread responsive and lets a real query barge in (its fetch just joins the cache).
function startIdlePrefetch() {
  const todo = loader.allShards(); // const-core is already cached; prefetch() skips it

  const idle = window.requestIdleCallback || ((cb) => setTimeout(() => cb({ timeRemaining: () => 16 }), 200));
  let i = 0, warmed = 0;
  const tick = (deadline) => {
    while (i < todo.length && (!deadline || deadline.timeRemaining() > 4)) {
      if (loader.prefetch(todo[i])) warmed++;
      i++;
    }
    const pct = Math.round((i / todo.length) * 100);
    $("prefetch").textContent = i < todo.length
      ? `prefetching catalog in the background… ${pct}%`
      : `catalog fully prefetched (${warmed} shards warm) — queries now resolve with no network`;
    if (i < todo.length) idle(tick);
  };
  idle(tick);
}

async function boot() {
  status("Loading manifest + core catalog…");
  await loader.init();
  core = await loader.constCore(); // needed to name every item; loaded once
  status("");

  $("button").addEventListener("click", () => handle($("textbox").value));
  $("textbox").addEventListener("keydown", (e) => { if (e.key === "Enter") handle($("textbox").value); });

  // Deep link: #<hex|link> auto-decodes one item, mirroring the existing app's /#<hex>.
  // The linked item is resolved FIRST, then the idle prefetcher starts — so a shared link
  // paints its item without the background warm competing for bandwidth.
  if (location.hash.length > 1) {
    const h = decodeURIComponent(location.hash.slice(1));
    $("textbox").value = h;
    await handle(h);
    window.__cardMs = performance.now(); // benchmark hook: nav-start → deep-linked card rendered
  }
  startIdlePrefetch();

  // Sample buttons (real cert links minted by the build from real catalog ids).
  try {
    const samples = await (await fetch("samples.json", { cache: "no-store" })).json();
    const host = $("sample-buttons");
    for (const s of samples) {
      const b = document.createElement("button");
      b.className = "sample";
      b.textContent = s.label;
      b.title = s.note;
      b.addEventListener("click", () => { $("textbox").value = s.link; handle(s.link); });
      host.appendChild(b);
    }
  } catch { /* samples are optional */ }
}

boot();
