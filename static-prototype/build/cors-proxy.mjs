// The one piece of server that a static inventory page genuinely cannot avoid: a CORS
// shim. Steam's community inventory endpoint returns no Access-Control-Allow-Origin, so a
// browser on another origin can't read it (verified: `fetch` throws "Failed to fetch").
//
// This is a ~40-line dumb pass-through — NO business logic, NO Game Coordinator, NO
// database, NO Steam login. It forwards one whitelisted URL shape and stamps CORS headers.
// Everything that made the real C# backend non-trivial (cert decode, enrichment, GC, DB,
// warm queue) moves to the browser. Contrast: Controllers/SkinController.cs is ~600 lines.
//
// Run:  node static-prototype/build/cors-proxy.mjs   (listens on :8788)
// The inventory page falls back to a bundled fixture when this isn't running.

import { createServer } from "node:http";

const PORT = 8788;

const send = (res, status, body, type = "application/json") => {
  res.writeHead(status, {
    "content-type": type,
    "access-control-allow-origin": "*",
    "cache-control": "no-store",
  });
  res.end(body);
};

createServer(async (req, res) => {
  const url = new URL(req.url, `http://localhost:${PORT}`);
  if (url.pathname !== "/inventory") return send(res, 404, JSON.stringify({ error: "not found" }));

  // Only a SteamID64 is allowed through — never an arbitrary URL (no open proxy / SSRF).
  const steamid = url.searchParams.get("steamid") || "";
  if (!/^7656\d{13}$/.test(steamid)) return send(res, 400, JSON.stringify({ error: "bad steamid" }));

  const target = `https://steamcommunity.com/inventory/${steamid}/730/2?l=english&count=2000`;
  try {
    const upstream = await fetch(target, { headers: { "user-agent": "static-prototype/0" } });
    const text = await upstream.text();
    console.log(`${steamid} -> ${upstream.status} (${text.length} bytes)`);
    send(res, upstream.status, text);
  } catch (e) {
    send(res, 502, JSON.stringify({ error: "upstream fetch failed", detail: String(e) }));
  }
}).listen(PORT, () => console.log(`CORS shim on http://localhost:${PORT}/inventory?steamid=...`));
