// A CDN-like static server for the prototype: gzips responses (so transfers match what a real
// CDN serves) and marks content-hashed shards immutable. Used for fair benchmarking — Python's
// http.server sends everything uncompressed, which unfairly inflates the prototype's transfer
// sizes versus the existing app's gzipped responses.
//
// Run: node static-prototype/build/static-gzip.mjs   (serves site/ on :8780)

import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { gzipSync } from "node:zlib";
import { fileURLToPath } from "node:url";
import { dirname, join, extname, normalize } from "node:path";

const root = join(dirname(fileURLToPath(import.meta.url)), "..", "site");
const PORT = 8780;
const TYPES = { ".html": "text/html", ".js": "text/javascript", ".json": "application/json", ".css": "text/css", ".png": "image/png", ".svg": "image/svg+xml" };

createServer(async (req, res) => {
  let path = decodeURIComponent(new URL(req.url, `http://x`).pathname);
  if (path === "/") path = "/index.html";
  const file = normalize(join(root, path));
  if (!file.startsWith(root)) { res.writeHead(403).end(); return; }
  try {
    const body = await readFile(file);
    const type = TYPES[extname(file)] || "application/octet-stream";
    // Hashed shards (name.<hash>.json) are immutable; manifest is no-store.
    const cache = /\.[0-9a-f]{8}\.json$/.test(file) ? "public, max-age=31536000, immutable"
      : path.endsWith("manifest.json") ? "no-store" : "no-cache";
    const headers = { "content-type": type, "cache-control": cache };
    if ((req.headers["accept-encoding"] || "").includes("gzip") && body.length > 256) {
      const gz = gzipSync(body);
      res.writeHead(200, { ...headers, "content-encoding": "gzip", "content-length": gz.length });
      res.end(gz);
    } else {
      res.writeHead(200, { ...headers, "content-length": body.length });
      res.end(body);
    }
  } catch {
    res.writeHead(404, { "content-type": "application/json" }).end('{"error":"not found"}');
  }
}).listen(PORT, () => console.log(`gzip static server on http://localhost:${PORT}`));
