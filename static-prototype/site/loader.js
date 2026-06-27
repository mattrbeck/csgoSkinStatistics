// Manifest-driven lazy loader. The contract that makes caching safe:
//
//   manifest.json  — fetched with cache: "no-store". Tiny. Maps logical names to the
//                    current content-hashed shard URLs. This is the ONLY mutable URL.
//   data/*.json    — every shard's name embeds a hash of its bytes, so the bytes can never
//                    change under a given URL. Fetched with cache: "force-cache" and safe to
//                    mark immutable / max-age=31536000 at the CDN.
//
// Result: a data update ships new shard files + a new manifest. Old shards keep serving any
// client mid-session; new loads pick up the new manifest and only re-download the shards
// whose hashes actually changed. No cache busting, no stale-data risk on the hashed files.
//
// Every fetch is recorded so the UI can show exactly which ranges an item pulled.

export class DataLoader {
  constructor(base = "data") {
    this.base = base;
    this.manifest = null;
    this._cache = new Map();   // filename -> Promise<json>
    this.log = [];             // { name, bytes, ms, reused }
  }

  async init() {
    const t = performance.now();
    // no-store: never trust a cached manifest, so a freshly deployed version is seen at once.
    const res = await fetch(`${this.base}/manifest.json`, { cache: "no-store" });
    this.manifest = await res.json();
    this.log.push({ name: "manifest.json", bytes: res.headers.get("content-length") | 0, ms: +(performance.now() - t).toFixed(1), reused: false });
    return this.manifest;
  }

  // Fetch one shard by filename, memoised. Immutable hashed URL → force-cache.
  async _shard(name) {
    if (this._cache.has(name)) {
      this.log.push({ name, bytes: 0, ms: 0, reused: true });
      return this._cache.get(name);
    }
    const p = (async () => {
      const t = performance.now();
      const res = await fetch(`${this.base}/${name}`, { cache: "force-cache" });
      const text = await res.text();
      this.log.push({ name, bytes: text.length, ms: +(performance.now() - t).toFixed(1), reused: false });
      return JSON.parse(text);
    })();
    this._cache.set(name, p);
    return p;
  }

  constCore() { return this._shard(this.manifest.shards.constCore); }
  constSpecial() { return this._shard(this.manifest.shards.constSpecial); }
  keychains() { return this._shard(this.manifest.shards.keychains); }
  floatRanges() { return this._shard(this.manifest.shards.floatRanges); }

  // Resolve one sticker id → {name, image}, loading only its bucket shard.
  async sticker(id) {
    const { bucket, files } = this.manifest.shards.stickers;
    const file = files[Math.floor(id / bucket)];
    if (!file) return null;
    return (await this._shard(file))[id] ?? null;
  }

  // Resolve a skin image by defindex+paintindex, loading only the paintindex bucket shard.
  async image(defindex, paintindex) {
    const { bucket, files } = this.manifest.shards.images;
    const file = files[Math.floor(paintindex / bucket)];
    if (!file) return "";
    return (await this._shard(file))[`${defindex}_${paintindex}`] ?? "";
  }

  // Resolve a keychain id, transparently following a Sticker Slab to its sealed sticker.
  async keychain(id, wrappedStickerId) {
    if (wrappedStickerId) {
      const sealed = await this.sticker(wrappedStickerId);
      return { ...(sealed ?? { name: "", image: "" }), slab: true, wrapped_sticker: wrappedStickerId };
    }
    const kit = (await this.keychains())[id] ?? null;
    return { ...(kit ?? { name: "", image: "" }), slab: false, wrapped_sticker: 0 };
  }

  // Bytes actually downloaded this session (reused shards counted once).
  downloadedBytes() {
    const seen = new Set();
    let total = 0;
    for (const e of this.log) {
      if (e.reused || seen.has(e.name)) continue;
      seen.add(e.name); total += e.bytes;
    }
    return total;
  }
}
