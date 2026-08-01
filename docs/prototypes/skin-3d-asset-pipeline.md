# Regenerating the demo's assets

The assets inlined in `skin-3d-demo.html` were produced by four throwaway Python scripts
(~450 lines total, numpy + zlib only, no third-party image or model libraries). Those
scripts lived in a session scratchpad and are **not** in this repo. This document records
the path precisely enough to rewrite them, and is also the spec for a real build step if
this ever ships.

There was no CS2 install on the machine that built this, which shaped the whole approach:
everything came from Valve's public workshop zip plus an archived **CS:GO** depot. That is
also the root of the CS2-parity caveat in `skin-3d-findings.md` — the pipeline below
reproduces the CS:GO-era assets exactly, and CS2's may differ.

## What you need to end up with

| output | from | final form in the demo |
|---|---|---|
| `mesh.bin.z` + `mesh.json` | `ak-47.obj` (workshop kit) | zlib'd u16 pos/uv/index + ranges, 151 KB |
| `pattern.png` | `paints/antiqued/oiled.vtf` | 512² RGB, 291 KB |
| `ao.png` | `rif_ak47_ao.vtf` | 512² RGBA (r cavity, g ao, b modulation, a paint-blend), 357 KB |
| `base.png` | `ak47.vtf` + `rif_ak47_masks.vtf` | 512² RGBA — base diffuse in rgb, `masks.r` in alpha, 373 KB |
| `wear.png` | `shared/paint_wear.vtf` | 512² grey (the `.g` channel only), 195 KB |
| `grunge.png` | `shared/gun_grunge.vtf` | 256² RGB, 143 KB |

Total 1.47 MB raw, 1.91 MB as base64. Base64 is only needed because the demo must be a
single self-contained file; a served version ships the binaries.

## Step 1 — geometry, from Valve's workshop resources

Valve publishes `workbench_materials.zip` (~12 MB) for finish authors: OBJs and UV sheets for
every weapon, plus finish examples. Take `OBJs/ak-47.obj` (977 KB).

Why this rather than the game model: it is officially published for exactly this purpose,
and its geometry is byte-identical to `sticker_preview_rif_ak47.mdl`. The viewmodel
(`v_rif_ak47`) differs slightly in triangle count, but its receiver and magazine UVs
coincide — which is what matters, since the pattern is placed in UV space.

Build step:

1. Parse `v`, `vt`, `f` lines; triangulate fans (`f a b c d` → `(a,b,c), (a,c,d)`).
2. Deduplicate `(position index, uv index)` pairs into a single vertex list — an OBJ's
   position and UV indices are independent, GL's are not. The AK yields 9,926 unique pairs
   and 13,069 triangles, so `u16` indices suffice (assert `< 65536`).
3. **Flip `v`: `uv.y = 1 − uv.y`.** Source uses the DirectX convention. Do this here so the
   renderer can upload textures with `UNPACK_FLIP_Y_WEBGL = false`.
4. Quantise positions and UVs to `u16` over their own min/max bounding ranges; record
   `pmin/pmax/umin/umax` in `mesh.json`. Note the UV range is *not* `[0,1]` — this model's
   UVs run roughly `(−0.117, −0.004)` to `(2.010, 3.410)`, so clamp nothing.
5. Concatenate `positions | uvs | indices` as little-endian `u16` and `zlib.compress(level=9)`.

### If you need the actual game model instead

A minimal Source 1 MDL v49 reader is ~150 lines and needs three files
(`<name>.mdl`, `.vvd`, `.dx90.vtx`):

- **VVD** — header `IDSV`; read `numFixups, fixupTableStart, vertexDataStart,
  tangentDataStart` at offset 48. Vertices are 48 bytes each; position is `float3` at byte
  16, UV is `float2` at byte 40. If `numFixups`, walk the 12-byte fixup records
  (`lod, srcID, numVertexes`), skipping `lod < 0`, and concatenate the referenced ranges — the
  raw vertex array is *not* usable directly.
- **MDL** — `numtextures/textureindex` at offset 204 (64-byte texture records, name is a
  relative string offset), `numbodyparts/bodypartindex` at 232. Body parts are 16 bytes;
  models 148 bytes with `nummeshes/meshindex` at +72 and `numvertices/vertexindex` at +80
  (divide `vertexindex` by 48 for a vertex base); meshes are 116 bytes with `material`,
  `numvertices` and `vertexoffset` in the first four ints.
- **VTX** (`.dx90.vtx`, version 7) — gives you the index buffer. Walk body parts (8 bytes) →
  models (8 bytes) → **LOD 0** → meshes (**9 bytes**, packed: two ints + one byte) → strip
  groups. In CS:GO's mdl v49 the strip-group header is **33 bytes** (25 + 8 topology fields);
  getting this stride wrong is the usual failure and produces garbage indices. The strip
  group's vertex table is 9 bytes per entry with `origMeshVertID` as a `u16` at offset 4;
  map indices through it, then add `model.vert_base + mesh.vertexoffset` to reach the VVD.

VTX meshes come out in MDL declaration order, so you can zip them together to recover
per-material submeshes.

## Step 2 — textures, from an archived CS:GO depot

Seven files are needed (all paths relative to `csgo/`):

```
materials/models/weapons/customization/paints/antiqued/oiled.vtf
materials/models/weapons/customization/shared/gun_grunge.vtf
materials/models/weapons/customization/shared/paint_wear.vtf
materials/models/weapons/customization/rif_ak47/rif_ak47_ao.vtf
materials/models/weapons/customization/rif_ak47/rif_ak47_masks.vtf
materials/models/weapons/v_models/rif_ak47/ak47.vtf
materials/models/weapons/customization/paints/vmts/aq_oiled.vmt      (parameters, for reference)
```

If you have CS:GO or CS2 installed, pull them from the local VPKs (CS2: use VRF /
Source2Viewer instead — Source 2 repacked these). The build machine had neither, so it used
an archive.org mirror of the depot:

```
https://archive.org/download/csgo_demo_viewer/app_730/depot_731/csgo/
```

Download only `pak01_dir.vpk` (~15 MB) — the index — then **HTTP range-request** just the byte
spans you need out of `pak01_NNN.vpk`. That was ~11 MB of actual payload rather than a
multi-gigabyte depot download.

**VPK v2 directory format.** Header: `u32 signature = 0x55AA1234`, `i32 version`,
`u32 treeSize`; for v2 four more `u32`s (`fileDataSectionSize`, `archiveMD5SectionSize`,
`otherMD5SectionSize`, `signatureSectionSize`) — tree starts at 28 (v1: 12). The tree is
three nested levels of NUL-terminated strings — extension, then directory path, then file
name — each level terminated by an empty string. After each file name comes an 18-byte
record: `u32 crc, u16 preloadBytes, u16 archiveIndex, u32 entryOffset, u32 entryLength,
u16 terminator (0xFFFF)`, followed by `preloadBytes` of inline data. A path of `" "` (single
space) means the root.

File bytes are `preload || body`. If `archiveIndex == 0x7FFF` the body lives in the dir VPK
itself at `endOfTree + entryOffset`; otherwise it is at `entryOffset` in
`pak01_{archiveIndex:03d}.vpk`. **Verify `zlib.crc32(preload || body) == crc`** — this is
free and catches a mis-parsed tree immediately.

## Step 3 — VTF decode

A ~120-line decoder covers what is needed. VTF header layout (7.x):

```
0  signature "VTF\0"      4  version (2×i32)     12 headerSize (u32)
16 width (u16)            18 height (u16)        20 flags (u32)
24 frames (u16)           26 firstFrame (u16)    28 pad
32 reflectivity (3×f32)   44 pad                 48 bumpScale (f32)
52 highResImageFormat(u32) 56 mipCount (u8)      57 lowResImageFormat (u32)
61 lowResWidth (u8)       62 lowResHeight (u8)
```

Image data starts at `headerSize`. If `lowResImageFormat != 0xFFFFFFFF`, skip the low-res
thumbnail (DXT1-sized) first. **Mips are stored smallest-first**, so to reach mip 0 skip
every mip from `mipCount−1` down to 1, times `frames`.

Format sizes: DXT1 (13) = `ceil(w/4)·ceil(h/4)·8`; DXT3 (14) and DXT5 (15) = `·16`;
BGRA8888 (12) = `w·h·4`; RGB888 (2) / BGR888 (3) = `w·h·3`; RGBA8888 (0) = `w·h·4`.

DXT1 colour: per 8-byte block, two RGB565 endpoints then sixteen 2-bit indices (row-major,
little-endian within each byte, four pixels per byte). If `c0 > c1` the two interpolated
entries are `(2p0+p1)/3` and `(p0+2p1)/3`, else `(p0+p1)/2` and transparent black. **Inside a
DXT5 block the colour half always uses the 4-colour rule regardless of endpoint order** — this
is the second classic bug after the mip ordering.

DXT5 alpha: two `u8` endpoints then 48 bits of 3-bit indices. If `a0 > a1`, six interpolants
at `((7−i)·a0 + i·a1)/7`; else four at `((5−i)·a0 + i·a1)/5` plus explicit 0 and 255.

Vectorise per-block with numpy (`unpackbits(..., bitorder='little')`, then gather from a
per-block palette array) — a per-pixel Python loop over a 2048² texture is unusably slow.

## Step 4 — pack and downscale

Box-filter downsample by integer factors (`reshape` to `(h/f, f, w/f, f, c)` and mean over
axes 1 and 3), then:

- `pattern.png` = `oiled` RGB at native 512² (alpha is unused in the style-8 diffuse path).
- `ao.png` = `rif_ak47_ao` 1024² → 512², all four channels kept as-is.
- `base.png` = `ak47.vtf` 2048² → 512² RGB, with `rif_ak47_masks.vtf`'s **red** channel
  dropped into alpha. (`ak47.vtf`'s own alpha is a specular mask, unused by the demo's
  simplified lighting — if you restore real specular you will need it back.)
- `wear.png` = `paint_wear` **green channel only**, 2048² → 512², written as 8-bit greyscale.
  The shader reads `.r`; the packing moves `.g` there.
- `grunge.png` = `gun_grunge` RGB 1024² → 256².

Write PNGs by hand (`zlib` + the four chunks) trying filter 0 and filter 2 (Up) per image and
keeping whichever compresses smaller — it is ~15 lines and avoids a Pillow dependency. Then
base64 each into the `ASSETS` object in the HTML.

**Resolutions were chosen by eye against visible quality, not measured.** They are also a
deliberate step away from game-quality assets — see the licensing note in `README.md`.

## For production

Do not keep this shape. The right build step is: extract per weapon+finish once (VRF for
CS2 assets), emit **KTX2/BasisU or WebP** rather than PNG, emit the quantised mesh as a plain
binary, and serve both from a CDN with immutable cache headers. Realistic bundle size is
500–700 KB per weapon+finish, of which the two globally shared masks (~340 KB) and the
per-finish pattern (~300 KB) amortise across everything else. The marginal cost of the
second Case Hardened AK is 16 bytes.
