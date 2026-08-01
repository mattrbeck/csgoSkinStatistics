# 3D skin rendering in the browser — feasibility study + prototype findings

Prototype: `docs/prototypes/skin-3d-demo.html` — a fully self-contained page (no external
requests, works from `file://`) that renders an **AK-47 | Case Hardened** in raw WebGL2 with
the **actual game mesh, the actual pattern texture, and the actual seed→placement maths**,
driven by four per-item numbers: `(defindex, paintindex, paintseed, paintwear)`.

Companion documents: `README.md` (start here / what Valve-derived material is included),
`skin-3d-implementation.md` (how it works, precisely enough to reimplement),
`skin-3d-asset-pipeline.md` (how to regenerate every asset).

## Headline result

**The per-item incremental payload is 16 bytes** (four 32-bit values; 10 bytes packed:
u16 defindex + u16 paintindex + u16 seed + f32 wear). Everything else — mesh, five textures,
paint-kit parameters, shader — is shared per *weapon+finish* and cacheable forever. The demo
displays this figure live. This is the number that makes the feature shippable: a browse grid
of 50 Case Hardened AKs costs 50 × 16 bytes beyond one shared ~2 MB (demo) / ~600 KB
(realistic production) cache entry.

## How the finish actually works (precise enough to reimplement)

Everything below was taken from primary sources: the leaked `cstrike15_src` engine code
(mirror: `perilouswithadollarsign/cstrike15_src`), CS:GO/CS2 `items_game.txt`
(SteamTracking/GameTracking-CS2), and assets extracted from an archived CS:GO depot.

### Parameters (paint kit 44, `aq_oiled`, AK-47)

From `items_game.txt` (legacy CS:GO file; CS2 keeps the kit with `use_legacy_model 1`):

```
style 8 (ANTIQUED / "Patina")            pattern "oiled"  (paints/antiqued/oiled.vtf)
pattern_scale 1.0                        pattern_offset_x/y ∈ [0,1]
pattern_rotate ∈ [0,360]                 wear_remap 0..1 (identity)
color0/1 = 120 111 101   color2 = 102 96 89   color3 = 30 34 47
phongexponent 25         phongalbedoboost 80  (spec is albedo-tinted → blue "glow")
```

Per-weapon paint data (items_game `paint_data`): AK-47 = `Name rif_ak47, OrigMat ak47,
ViewmodelDim 2048, WorldDim 512, WeaponLength 37.7462, UVScale 0.549`.

### Seed → placement (the crucial mapping)

`CCSWeaponVisualsDataProcessor::SetVisualsData` (leaked source) does, in this exact order:

```
RandomSeed(paintseed)                                  // global Valve stream
patternOffsetX = RandomFloat(0, 1)
patternOffsetY = RandomFloat(0, 1)
patternRot     = RandomFloat(0, 360)
wearScale  = RandomFloat(1.6, 1.8); wearOffX = RF(0,1); wearOffY = RF(0,1); wearRot = RF(0,360)
grungeScale= RandomFloat(1.6, 1.8); grungeOffX= RF(0,1); grungeOffY= RF(0,1); grungeRot= RF(0,360)
```

The RNG is Valve's `CUniformRandomStream` — Numerical Recipes **ran1** (IA=16807,
IM=2^31−1, IQ=127773, IR=2836, NTAB=32, warm-up NTAB+7..0, AM=1/IM, RNMX=1−1.2e−7),
`RandomFloat(lo,hi) = fl*(hi−lo)+lo` with `fl = float(AM*gen())` clamped to RNMX.
The demo ports it exactly (with `Math.fround` at the float32 points). Cross-validated
against `chescos/csgo-fade-percentage-calculator` (the market-standard Fade% tool, MIT):
**identical generator, identical call order**. A known quirk falls out for free: ran1
maps seed 0 and seed 1 to the same stream, and indeed community data lists identical
blue percentages for seeds 0 and 1 — the demo reproduces this (`seed0eq1: true`).

The values are then formatted into a VMT string **with `%.2f`** — i.e. the game itself
quantises offsets to 0.01 and rotation to 0.01° — and parsed by
`CreateMatrixMaterialVarFromKeyValue` (cmaterial.cpp). Working through Source's matrix
conventions (`MatrixBuildRotateZ` = CCW for column vectors; the parser's
`translate(t−0.5) · scale · rotate · translate(R⁻¹(0.5/s))` chain), the 0.5-center terms
cancel exactly and the whole thing collapses to:

```
patternUV = S · R(rot) · baseUV + (tx, ty)        // rotation about UV origin, wrap-sampled
```

with `S = pattern_scale × UVScale` in the composite path. UV space is DirectX convention
(v down); base UVs are used as authored in the model.

The **wear** blend (style 8 = ANTIQUED) from `customweapon_ps20b.fxc`, diffuse path,
with `wearAmt = paintwear` remapped through `wear_remap_min/max` (identity for CH):

```
grunge   = lerp(1, grungeTex, pow(1−cavity,4)*0.25 + 0.75*wearAmt)
patina   = smoothstep(.1,.2, wearTex.g * ao * cavity² * wearAmt)
oilRub   = smoothstep(0,.15, saturate(cavity*ao − wearAmt*.1) − grungeTex.r*g*b + .08)
cPatina  = lerp( lerp(col1,col3,√wearAmt), lerp(col1,col2,wearAmt), oilRub ) * pattern.rgb
cPatina  = lerp( cPatina, col0 * luma(pattern), patina )        // worn = grey scratches
cPaint   = saturate(cPatina*grunge + aoTex.b*(…)*2) * ao
albedo   = lerp(cPaint, baseDiffuse, 1 − masks.r)               // masks.r = painted area
```

So Case Hardened's blue **comes from the pattern texture's own RGB**, tinted by the kit
colors; wear greys it out via the shared scratch mask. The float slider in the demo drives
exactly this and reproduces the FN→BS progression (grey, scratched steel at high floats).

### The one open question: effective pattern scale (CS:GO vs CS2)

- The leaked **composite** path gives `S = 1.0 × 0.549` (single UVScale).
- The **preview/inspect/icon** path additionally multiplies the transform by
  `$previewweaponuvscale` (see `SetVertexShaderTextureScaledTransform` +
  `customweapon_dx9_helper`), i.e. UVScale can be applied **twice** → `S ≈ 0.302`.
- Empirically, **Valve's own default-generated icon for AK CH (rendered at the kit's
  preview seed 29)** matches `S = 0.302` with unscaled offsets, feature for feature
  (mag: silver top → purple-red middle → gold tip; pale receiver with pink-purple front
  accents; gold gas block; gold stock socket). At `S = 0.549` it does not.

The demo therefore ships `S = q2(1.0×0.549) × 0.549 = 0.302` and prints it in the UI.
This is documented rather than hidden because it is the piece I could not verify against
the *current* game binary (no CS2 install available here — see Fidelity).

## Assets: what's needed, from where, and their sizes

| asset | source file | raw size | in demo (PNG, downscaled) | scope |
|---|---|---|---|---|
| mesh + UVs | Valve workshop kit OBJ (officially published by Valve for finish authors; byte-identical geometry to `sticker_preview_rif_ak47.mdl`) | 977 KB OBJ | 111 KB (quantised u16 pos/uv + zlib) | per weapon |
| pattern | `paints/antiqued/oiled.vtf` 512² DXT1 | 175 KB | 291 KB (512² RGB PNG) | per finish (shared by every CH weapon) |
| base diffuse + paint mask | `v_models/rif_ak47/ak47.vtf` 2048² DXT5 + `rif_ak47_masks.vtf` 512² | 5.6 MB + 175 KB | 373 KB (512² RGBA, mask in alpha) | per weapon |
| AO / cavity / modulation / paint-blend | `rif_ak47_ao.vtf` 1024² DXT5 | 1.4 MB | 357 KB (512² RGBA) | per weapon |
| wear (scratches) | `shared/paint_wear.vtf` 2048² (only .g used) | 2.8 MB | 195 KB (512² grey) | **global** (all weapons) |
| grunge | `shared/gun_grunge.vtf` 1024² | 1.4 MB | 143 KB (256² RGB) | **global** |
| paint-kit params | items_game.txt | — | ~200 B JSON | per finish |
| **per item** | — | — | **16 B (10 packed)** | per item |

Demo totals: **1.47 MB raw / 1.91 MB as base64** (the artifact constraint forces base64;
a served version uses raw binaries). The page is 1.98 MB all-in, including the renderer
(~15 KB of hand-written JS/GLSL — no three.js; raw WebGL2 keeps dependency bytes at zero).

Production sizing: with KTX2/BasisU or WebP and sane resolutions (pattern 512, weapon maps
512, shared masks 512/256, mesh ~24k-tri LOD as here), one weapon+finish bundle is
realistically **500–700 KB**, of which the two shared masks (~340 KB) and the pattern
(~300 KB) amortise across *all* weapons / all CH items respectively. The marginal cost of
the second CH AK listing is 16 bytes; of a CH Five-SeveN, ~450 KB (its weapon maps).

Extraction pipeline used here (no CS2 install on this machine): Valve's official workshop
zips (`workbench_materials.zip`, 12 MB — OBJs + UV sheets for every weapon) plus targeted
HTTP range-requests into an archive.org mirror of the CS:GO depot's VPKs — a 15 MB
`pak01_dir.vpk` index, then only the ~7 needed files (~11 MB), CRC-verified, decoded by a
~120-line VTF/DXT decoder and a ~150-line MDL/VVD/VTX parser. Those scratch scripts are not
in the repo, but **`skin-3d-asset-pipeline.md` documents the whole path — formats, header
offsets, the two classic decode bugs, and the exact packing choices — precisely enough to
rewrite them**, and it doubles as the spec for a real build step.

## Browse-grid strategy (50 items on a page)

Measured on this machine (Apple Silicon, Chrome): warm draw ~0.1–0.5 ms per frame
(13,069 tris, 5 texture fetches/pixel), 60 fps vsync-locked while orbiting; texture GPU
memory ~6 MB per weapon+finish; page init (mesh inflate + 5 texture decodes + upload)
well under 1 s.

- **Detail page: trivially fine.** One canvas, one draw call, <1 ms GPU.
- **Grid of 50: do not run 50 live canvases.** Browsers cap WebGL contexts (~8–16).
  The right pattern is one shared offscreen WebGL context that **composites each item's
  albedo once** (seed+wear are per-item constants, so the result is a static texture),
  renders a posed thumbnail to a bitmap, and hands it to 50 plain `<img>`/`drawImage`
  tiles — ~1–2 ms per item, ~100 ms for the whole grid, done once and cached
  (key: weapon+finish+seed+wearBucket). Live 3D spins up only on hover/click, reusing
  the already-loaded shared assets. 50 items on a page is plausible with this shape;
  50 always-spinning viewers is not, and isn't needed.
- Same trick server-side if we ever want `<img>`-only clients: headless GL compositing
  into a CDN-cached JPEG per (weapon, finish, seed, wearBucket) — but client-side makes
  the payload 16 B/item instead of ~40 KB/item, which is the whole point.

## Fidelity, edge cases and open problems — honest assessment

What is faithful:
- Seed → (offsets, rotation): exact (proven RNG + order, quantisation included).
- Patina/antiqued colour maths, wear behaviour, masks: transcribed line-for-line from the
  game's pixel shader; real pattern/AO/masks/grunge/wear textures; real base diffuse.
- Mesh + UVs: Valve's own published AK model (identical to the in-game sticker-preview
  model; the viewmodel differs slightly in tri count but its receiver/mag UVs coincide).
- Verified against Valve's own icon render at the kit preview seed (29): pattern feature
  placement matches at S=0.302.

What is approximate or unresolved:
- **Lighting/specular are simplified** (derivative normals, generic phong with albedo
  tint standing in for the exponent-texture + phongalbedoboost pipeline). The owner
  explicitly deprioritised this. Consequence: colours read flatter/darker than in-game
  screenshots; blue-gem "glow" at grazing angles is only hinted.
- **CS2 parity is not proven.** CS2 re-implemented the compositor in Source 2. My renders
  match Valve's CS:GO-era icon, but do not pixel-match CS2-era community screenshots
  (pattern.wiki) for e.g. seed 661, and per-seed blue% from csbluegem
  (`blue-gem.json` in this repo) correlates only weakly (Spearman ~0.2–0.3) with every
  measurement protocol I tried (side/top/mag views, texture-space, multiple classifiers,
  every transform-convention variant — a full sweep, all documented in session scripts).
  Two candidate explanations, both actionable: (a) CS2's compositor transforms differ
  (community did report pattern shifts at CS2 launch), (b) csbluegem's measurement
  protocol is simply not what I approximated. Resolving it needs a CS2 install: extract
  `csgo_weapon_composite.vfx` + the CH composite inputs via VRF/Source2Viewer and A/B a
  handful of seeds in-game. That is the single most important next step for correctness,
  and it's a bounded one — the demo's architecture doesn't change, only the constants.
- **Pattern scale is ambiguous and I picked a side.** The leaked composite code implies
  `UVScale` is applied once → `S = 0.549`. Valve's own generated icon for this kit at its
  preview seed (29) matches `UVScale` applied **twice** → `S = 0.302`, and that is what the
  demo ships and prints. Both readings are defensible from the sources; only one matches the
  reference render I had. If CS2 ground-truthing (below) contradicts it, this is a
  one-constant change. Derivation in `skin-3d-implementation.md` §3.
- **Only style 8 (ANTIQUED / Patina) is implemented.** Case Hardened and Heat Treated use it;
  every other finish family — Solid, Hydrographic, Spray, Anodized (Multicoloured/Airbrushed),
  Custom Paint Job, Antiqued variants, Gunsmith — is a different branch of the same pixel
  shader and is **unbuilt**. Fade/Doppler are the easy ones (no pattern placement at all, so
  the seed→UV machinery is irrelevant); Gunsmith needs more per-weapon textures. The
  architecture is shared, but each style is real work.
- **The demo is one weapon, one finish, hard-coded.** `defindex 7 / paintindex 44` is baked
  in, as are the mesh and the five textures. There is no asset loader, no manifest, no
  per-weapon `UVScale` table. Generalising is the point of the "asset pipeline" production
  step, not a small edit to the demo.
- **The demo shows its own measured "albedo blue %"** next to csbluegem's numbers with an
  explicit warning that the protocols differ. Do not read the two columns as comparable.

### Smaller edge cases worth knowing before you touch it

- **Seed 0 and seed 1 are identical.** A consequence of ran1's seeding negation, confirmed
  independently by community data. It is a real game behaviour, not a bug — but it will look
  like one, so it is a useful regression test rather than something to "fix".
- **`%.2f` quantisation is load-bearing.** The game rounds offsets to 0.01 and rotations to
  0.01° when it formats the VMT string. Skip it and placement drifts subtly.
- **Float32 rounding inside the RNG is load-bearing** for the same reason. `Math.fround` at
  three specific points; see the implementation doc.
- **UVs are not in `[0,1]`** for this model (roughly `−0.12 … 3.41`), and v is flipped to the
  DirectX convention at build time. Textures wrap with `REPEAT`; the pattern is expected to
  tile.
- **Wear is continuous, not five buckets.** Any per-item render cache must key on a *bucketed*
  float, or the cache never hits.
- **`DecompressionStream('deflate')`** is used to inflate the mesh. Fine on modern engines;
  it would need a fallback for older Safari.
- **Draw time is measured, GPU time is not.** The HUD's "draw N ms" is CPU-side wall clock
  around the submit; real GPU cost is lower and the number moves with window size and DPR.

## Licensing — decided for this prototype, open for production

- The **maths** (RNG, transforms, shader logic) reimplemented from reference is the safe
  part; the chescos calculator (MIT) demonstrates community precedent.
- The **OBJ mesh + UV sheets** come from Valve's own workshop-resources zip, which Valve
  publishes for finish authors to download and use. Distribution intent is clear, though
  the zip carries no explicit licence.
- The **textures** (pattern, AO, masks, base diffuse, wear/grunge) are Valve game assets
  extracted from game files, downscaled to ≤512² and inlined in the demo page. Local
  prototyping is normal community practice; **serving them from our CDN to browsers is
  redistribution of Valve IP.** Mitigations short of "don't ship": significant downscaling
  (we already ship ≤512², far below game quality), the fact that every major skin site
  (cs.money, Skinport 3D viewers) ships equivalent extracted assets, and Valve's long
  tolerance of that ecosystem.
- **Decision on the record (owner, at publication of this branch):** proceed — Valve has no
  precedent of pursuing people for storing or redistributing its textures and models, so
  this branch carries the extracted assets openly rather than stripping them. That covers
  *this prototype*. It is precedent rather than permission, so the production question —
  serving these assets at scale from our own CDN, under our own brand — is still worth a
  conscious second look rather than being treated as settled by this decision.
  `README.md` states exactly which Valve-derived files are included and where each came from.
- Prior art reused: Valve workshop kit (assets), leaked-source *reference* (read, not
  copied — the demo contains a clean-room reimplementation of ~40 lines of maths),
  chescos fade calculator (cross-validation only), csbluegem/pattern.wiki (validation
  imagery), SteamTracking items_game (parameters).

## What production would take

1. **Asset pipeline** (build-time, per weapon+finish): VPK/VTF extractor (~300 lines; spec in
   `skin-3d-asset-pipeline.md`) or VRF for CS2 assets → KTX2/WebP + quantised mesh bundles →
   CDN with immutable cache headers. One-off per weapon; CH covers 23 weapons/knives.
2. **CS2 ground-truthing** (the correctness step above): one CS2 install + VRF, verify
   scale/offset conventions per weapon against in-game screenshots for ~5 seeds each.
3. **Viewer component**: the demo's renderer as an Angular component (~500 lines total),
   one shared WebGL context, composite-to-thumbnail for grids, live orbit on detail pages.
   Inputs: the four numbers we already have on every listing (we decode inspect links).
4. **Scope control**: start with Case Hardened / Heat Treated (highest pattern-premium
   finishes, biggest buyer value), then Fade/Doppler (trivial — no pattern placement),
   then Gunsmith/Custom styles (same shader, more textures).

What I would **not** do: 50 live WebGL contexts in a grid; chase full lighting parity
with the game; ship 2048² viewmodel textures; build a Source-2 runtime asset loader in
the browser; or block the feature on the CS2-parity question — ship behind a "3D preview
(beta)" affordance on detail pages first, where "same pattern family, faithful maths,
slightly different tone than an in-game screenshot" is already far beyond what any
competitor shows inline.

## Verdict

**Viable.** The hard requirement — per-item payload — lands at 16 bytes against a
shared, cacheable ~0.5–2 MB per weapon+finish. Client render cost is negligible for a
detail page and manageable for grids via composite-once thumbnails. The pattern maths is
reproduced from engine source and validated against Valve's own renders at the CS:GO
level; the remaining CS2-exactness question is bounded, does not change the
architecture, and has a concrete resolution path. The genuine business risk is
licensing of the textures, which must be decided consciously, not assumed.
