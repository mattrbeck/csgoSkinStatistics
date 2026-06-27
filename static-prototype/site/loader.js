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

  // Find the balanced shard whose contiguous id range contains `id`: the one with the
  // largest `min` that is <= id (binary search over ascending-min shards).
  static _shardFor(shards, id) {
    let lo = 0, hi = shards.length - 1, file = null;
    while (lo <= hi) {
      const mid = (lo + hi) >> 1;
      if (shards[mid].min <= id) { file = shards[mid].file; lo = mid + 1; }
      else hi = mid - 1;
    }
    return file;
  }

  // Resolve one sticker id → {name, image}, loading only the shard that covers it.
  async sticker(id) {
    const file = DataLoader._shardFor(this.manifest.shards.stickers.shards, id);
    if (!file) return null;
    return (await this._shard(file))[id] ?? null;
  }

  // Resolve a skin image by defindex+paintindex, loading only the shard covering paintindex.
  async image(defindex, paintindex) {
    const file = DataLoader._shardFor(this.manifest.shards.images.shards, paintindex);
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

  // Every shard filename the manifest references, in a sensible warm order: the small
  // always/often-needed shards first, then the big sticker/image ranges. Used by the idle
  // prefetcher to fill the cache before the user asks for anything.
  allShards() {
    const s = this.manifest.shards;
    return [
      s.constCore, s.constSpecial, s.keychains, s.floatRanges,
      ...s.images.shards.map((x) => x.file),
      ...s.stickers.shards.map((x) => x.file),
    ];
  }

  // Warm one shard if not already cached/in-flight. Returns false if it was already known
  // (so the prefetcher can skip the work).
  prefetch(name) {
    if (this._cache.has(name)) return false;
    this._shard(name);
    return true;
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
