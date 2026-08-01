# Prototypes

Self-contained experiments that are not wired into the app. Each one is a single page you
can open directly from disk; none of them make network requests.

## `skin-3d-demo.html` — browser 3D rendering of a seed-accurate Case Hardened AK-47

Open `docs/prototypes/skin-3d-demo.html` in any WebGL2 browser (`file://` works — double
click it). Drag to orbit, wheel to zoom, pick a seed, drag the float slider.

**Start here if you are picking this up cold:**

1. **`skin-3d-findings.md`** — the feasibility study. What it costs, whether it is viable,
   what is unproven, what production would take. Read this first; it is the decision
   document.
2. **`skin-3d-implementation.md`** — how the renderer actually works: the `ran1` RNG port,
   the seed → UV-transform derivation, the Patina (style 8) blend, the shader inputs.
   Precise enough to reimplement from scratch without reading the demo source.
3. **`skin-3d-asset-pipeline.md`** — where every byte of geometry and texture came from and
   how to regenerate it. The scratch scripts that produced the current assets are not in the
   repo; this document describes the file formats and steps precisely enough to rewrite them.
4. **`skin-3d-demo.html`** — the demo itself. The renderer is the last ~370 lines of the
   file; everything above it is base64 asset data on two very long lines.

### The one-line summary

Four numbers per item — `(defindex, paintindex, paintseed, paintwear)`, **16 bytes** — plus a
per-weapon+finish bundle that is shared and cacheable forever, are enough to render the
correct pattern for a specific skin in the browser. The demo proves it end to end for
AK-47 | Case Hardened.

### Contents of the demo file

| region | size | what |
|---|---|---|
| `ASSETS` (line 55) | ~1.85 MB | five base64 PNG data URIs: pattern, AO, base+mask, wear, grunge |
| `MESH_B64` (line 56) | ~151 KB | zlib-compressed quantised mesh (u16 positions + u16 UVs + u16 indices) |
| `MESH_META`, `PRESETS`, `KIT` | ~1 KB | dequantisation ranges, seed presets w/ csbluegem figures, paint-kit params |
| renderer | ~15 KB | RNG, transform derivation, WebGL2 setup, GLSL, orbit UI, blue% measurement |

No three.js, no dependencies, no network. `window._dbg` exposes `setSeed`, `setWear`,
`setView(yaw, pitch, dist)` and `vis(seed)` for scripted A/B captures.

### Valve-derived material — stated plainly

This branch **contains Valve-derived game assets**, inlined as base64 in the demo file:

- the **AK-47 mesh and UVs**, from Valve's officially published workshop resources zip
  (`workbench_materials.zip` → `OBJs/ak-47.obj`), which Valve distributes for finish authors;
- the **`aq_oiled` pattern texture**, the **AK-47 AO/cavity/modulation map**, the **AK-47
  paint masks**, the **AK-47 base diffuse**, and the **shared `paint_wear` / `gun_grunge`
  masks**, all extracted from an archived CS:GO depot (see `skin-3d-asset-pipeline.md`),
  decoded from VTF/DXT, downscaled to ≤512² and re-encoded as PNG.

The repository owner considered the licensing question before publishing and decided to
proceed, on the basis that Valve has no precedent of pursuing people for storing or
redistributing its textures and models, and that every major skin site ships equivalent
extracted assets. That is a deliberate decision, recorded here so it is explicit rather
than implicit. It is a decision about *this prototype branch*; the separate question of
serving these assets from a production CDN is discussed in `skin-3d-findings.md` under
*Licensing* and still deserves its own look before shipping.

The **maths** (RNG, transform derivation, shader logic) is a clean-room reimplementation
from reference sources, not copied code.
