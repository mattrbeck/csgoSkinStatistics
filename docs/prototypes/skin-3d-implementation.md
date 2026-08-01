# How the seed-accurate renderer works

Reference for reimplementing `skin-3d-demo.html` from scratch. Everything here was derived
from primary sources — the leaked `cstrike15_src` engine tree (mirror:
`perilouswithadollarsign/cstrike15_src`), CS:GO/CS2 `items_game.txt`
(SteamTracking / GameTracking-CS2), and assets extracted from an archived CS:GO depot.

The pipeline is four stages:

```
paintseed ──▶ ran1 RNG ──▶ 11 floats ──▶ %.2f quantise ──▶ 3 UV transforms
                                                              │
defindex/paintindex ──▶ paint-kit params (colours, style) ────┼──▶ style-8 blend ──▶ albedo
paintwear ────────────────────────────────────────────────────┘
```

---

## 1. The RNG: Valve's `CUniformRandomStream`

`vstdlib/random.cpp`. It is Numerical Recipes **ran1** — a Lehmer generator with a
Bays–Durham shuffle table. Constants:

```
IA  = 16807          IM   = 2147483647 (2^31 − 1)
IQ  = 127773         IR   = 2836
NTAB= 32             NDIV = 1 + (IM − 1) / NTAB      (integer division)
AM  = 1.0 / IM       RNMX = 1.0 − 1.2e−7
```

Seeding is `idum = (seed < 0) ? seed : −seed; iy = 0`. On the first `gen()` after seeding
(`idum <= 0 || iy == 0`) the table is warmed: `idum = max(−idum, 1)`, then **`j` from
`NTAB+7` down to `0`** — 40 iterations — applying the Schrage step each time and storing
`iv[j] = idum` for `j < NTAB`; finally `iy = iv[0]`.

The Schrage step, used both in warm-up and in the steady state:

```
k    = idum / IQ                    // integer division, truncating toward zero
idum = IA*(idum − k*IQ) − IR*k
if (idum < 0) idum += IM
```

Steady state then shuffles: `j = iy / NDIV; iy = iv[j]; iv[j] = idum; return iy`.

```
RandomFloat(lo, hi):
    fl = float32(AM * gen())
    if (fl > RNMX) fl = RNMX
    return fl * (hi − lo) + lo          // computed in float32
```

**Port notes.** In JavaScript, do the integer arithmetic in doubles (all intermediates fit
in 2^53 comfortably — `IA * idum` peaks around 2^45) but round the *float* steps with
`Math.fround`, at three points: after `AM * gen()`, on the `RNMX` clamp, and on the
multiply-add. Getting this wrong shifts offsets in the third decimal, which survives the
`%.2f` quantisation below only occasionally — so it will look almost right and be subtly
wrong. Use integer truncation (`|0`) for `k` and `j`, not `Math.floor`.

**Known quirk, useful as a self-test:** because seeding negates, `seed 0` and `seed 1`
produce the identical stream. Community pattern data lists identical blue percentages for
seeds 0 and 1, which is independent confirmation the port is right. The demo asserts this
(`window._dbg.vis(0)` deep-equals `window._dbg.vis(1)`).

Cross-validated against `chescos/csgo-fade-percentage-calculator` (the market-standard
Fade% tool, MIT licensed): identical generator, identical call order.

## 2. Seed → the eleven random values

`CCSWeaponVisualsDataProcessor::SetVisualsData` draws in exactly this order, from a single
stream seeded once with `paintseed`. Order matters: any extra or missing draw desynchronises
everything after it.

```
RandomSeed(paintseed)
patternOffsetX = RandomFloat(pattern_offset_x_min, pattern_offset_x_max)   // 0..1 for kit 44
patternOffsetY = RandomFloat(pattern_offset_y_min, pattern_offset_y_max)   // 0..1
patternRot     = RandomFloat(pattern_rotate_min,  pattern_rotate_max)      // 0..360
wearScale      = RandomFloat(1.6, 1.8)
wearOffsetX    = RandomFloat(0, 1)
wearOffsetY    = RandomFloat(0, 1)
wearRot        = RandomFloat(0, 360)
grungeScale    = RandomFloat(1.6, 1.8)
grungeOffsetX  = RandomFloat(0, 1)
grungeOffsetY  = RandomFloat(0, 1)
grungeRot      = RandomFloat(0, 360)
```

The wear/grunge min-max (1.6, 1.8) are hard-coded in the engine, not kit parameters.

**The quantisation is load-bearing.** The engine formats these into a VMT keyvalue string
with `%.2f`, so the values the material system actually sees are rounded to 0.01 (offsets)
and 0.01° (rotation). Reproduce it (`Math.round(x*100)/100`) or your pattern placement will
drift by up to half a texel against the game's.

## 3. The UV transform derivation

The formatted string is parsed by `CreateMatrixMaterialVarFromKeyValue` in
`materialsystem/cmaterial.cpp`, which builds

```
M = translate(center + translate') · rotate(angle) · scale(scale) · translate(−center)
```

with `center = (0.5, 0.5)`. Working the chain through Source's matrix conventions
(`MatrixBuildRotateZ` is CCW for column vectors, and the parser's own
`translate(t − 0.5) · scale · rotate · translate(R⁻¹(0.5/s))` ordering), **the 0.5-centre
terms cancel exactly**. The whole thing collapses to a plain scale-rotate-about-origin plus
a translation:

```
patternUV = S · R(θ) · baseUV + (tx, ty)
```

which as a 2×3 is

```
[ S·cosθ  −S·sinθ   tx ]
[ S·sinθ   S·cosθ   ty ]
```

Sampled with `GL_REPEAT` on both axes. **UV space is DirectX convention (v increasing
downward)** — if you are loading an OBJ, flip `v` (`v = 1 − v`) at build time, as the asset
script does, and then do *not* flip textures on upload
(`UNPACK_FLIP_Y_WEBGL = false`). Base UVs are used exactly as authored in the model; there
is no per-weapon UV remap beyond the scale below.

Three transforms are built per item, from the eleven values: pattern
`(S_pattern, patternRot, patternOffsetX, patternOffsetY)`, wear
`(q2(wearScale·UVScale), wearRot, wearOffsetX, wearOffsetY)` and grunge likewise. Offsets are
**not** multiplied by the scale.

### `S_pattern` — the pattern-scale ambiguity

`UVScale` is a per-weapon constant from `items_game.txt`'s `paint_data` block
(AK-47: `Name rif_ak47, OrigMat ak47, ViewmodelDim 2048, WorldDim 512,
WeaponLength 37.7462, UVScale 0.549`).

There are two defensible answers and the demo ships the second:

- The leaked **composite** path applies it once: `S = pattern_scale × UVScale = 0.549`.
- The **preview / inspect / icon** path additionally multiplies the transform by
  `$previewweaponuvscale` (`SetVertexShaderTextureScaledTransform` in
  `customweapon_dx9_helper`), i.e. UVScale applied **twice**:
  `S = q2(1.0 × 0.549) × 0.549 = 0.55 × 0.549 = 0.302`.

Valve's own default-generated icon for AK-47 | Case Hardened — rendered at the kit's preview
seed 29 — matches `S = 0.302` feature for feature (mag: silver top → purple-red middle → gold
tip; pale receiver with pink-purple front accents; gold gas block; gold stock socket). At
`S = 0.549` it plainly does not. So **0.302 is what ships**, and the UI prints it.

This is an unresolved discrepancy, not a settled fact — see *Edge cases and open problems* in
`skin-3d-findings.md`.

## 4. Paint-kit parameters

Paint kit 44 (`aq_oiled`, "Case Hardened"), from `items_game.txt`. CS2 keeps the kit with
`use_legacy_model 1`; the legacy CS:GO file has the fuller parameter set.

```
style 8 (ANTIQUED / "Patina")     pattern "oiled"   → paints/antiqued/oiled.vtf
pattern_scale 1.0                 pattern_offset_x/y ∈ [0,1]
pattern_rotate ∈ [0,360]          wear_remap_min/max = 0/1 (identity)
color0 = 120 111 101              color1 = 120 111 101
color2 = 102  96  89              color3 =  30  34  47
phongexponent 25                  phongalbedoboost 80
```

`wearAmt` is the item's `paintwear` float remapped through `wear_remap_min/max` — identity
for this kit, but not for every kit, so implement the remap.

## 5. The style-8 (Patina) blend

Transcribed from `customweapon_ps20b.fxc`, `PAINTSTYLE 8`, diffuse path. Texture inputs, and
which channels are actually used:

| sampler | source | channels used |
|---|---|---|
| `tAo` | `rif_ak47_ao.vtf` | `r` cavity, `g` AO, `b` modulation, `a` paint-blend |
| `tBase` | `ak47.vtf` rgb + `rif_ak47_masks.vtf` `.r` packed into alpha | `rgb` base diffuse, `a` = `masks.r` (painted-area mask) |
| `tPat` | `paints/antiqued/oiled.vtf` | `rgb` |
| `tWear` | `shared/paint_wear.vtf` | one channel (game uses `.g`; the demo packs that into a grey texture and reads `.r`) |
| `tGrg` | `shared/gun_grunge.vtf` | `rgb` |

`tAo`, `tBase` are sampled at the model's own UVs. `tPat`, `tWear`, `tGrg` are sampled at
their respective transformed UVs from §3.

```glsl
float flGrunge   = grg.r * grg.g * grg.b;
vec3  cGrunge    = mix(vec3(1.0), grg, pow(1.0 - cavity, 4.0) * 0.25 + 0.75 * wearAmt);

float patinaBlend = smoothstep(0.1, 0.2, paintWear * ao * cavity * cavity * wearAmt);

float oilRub = clamp(cavity * ao - wearAmt * 0.1, 0.0, 1.0) - flGrunge;
      oilRub = smoothstep(0.0, 0.15, oilRub + 0.08);

vec3 cPatina = mix(color1, color2, wearAmt);
vec3 cOilRub = mix(color1, color3, pow(wearAmt, 0.5));
     cPatina = mix(cOilRub, cPatina, oilRub) * pattern.rgb;   // ← the colour comes from here

float patLum     = dot(pattern.rgb, vec3(0.3, 0.59, 0.11));
vec3  cScratches = color0 * patLum;
      cPatina    = mix(cPatina, cScratches, patinaBlend);      // worn = grey scratches

vec3  cPaint = cPatina * cGrunge;
float modLum = dot(cPaint, vec3(0.3, 0.59, 0.11));
      modLum = (1.0 - smoothstep(0.08, 0.15, modLum)) * 0.005;
vec3  cMod   = vec3(ao_tex.b) * (cPaint + modLum) * 2.0;
      cPaint = clamp(cPaint + cMod, 0.0, 1.0) * ao;

vec3 albedo = mix(cPaint, baseDiffuse, 1.0 - masks.r);         // unpainted areas keep the base
```

**The important consequence:** Case Hardened's blue is *the pattern texture's own RGB*,
tinted by the kit colours — not a colour ramp keyed off the seed. The seed only decides
*which part of the pattern lands where*. Wear greys it out through the shared scratch mask,
which is why the float slider reproduces the FN→BS progression (grey, scratched steel at
high float) without any extra logic.

## 6. Lighting (not part of the finish maths)

The demo's lighting is deliberately crude and is **not** faithful: geometric normals from
screen-space derivatives (`normalize(cross(dFdx(vPos), dFdy(vPos)))`), a single light that
tracks the camera, Blinn-Phong at exponent 25, and an albedo-tinted specular standing in for
the real `$phongexponenttexture` + `phongalbedoboost 80` pipeline, then a `pow(col, 0.92)`
lift. Consequence: colours read flatter and darker than in-game screenshots, and the
blue-gem "glow" at grazing angles is only hinted at. Replacing this is orthogonal to the
pattern maths — nothing above changes.

## 7. Mesh and payload encoding

Mesh: 9,926 vertices / 13,069 triangles, stored as three `u16` arrays back to back
(positions ×3, UVs ×2, indices ×3), zlib-deflated, base64'd. `MESH_META` carries the
`pmin/pmax` and `umin/umax` ranges for dequantisation:
`value = min + (max − min) · q / 65535`. Inflated in-browser with `DecompressionStream('deflate')`.
u16 quantisation over the model's bounding box is well below a texel of error at any sane
render size.

Per item, the renderer needs only `(defindex, paintindex, paintseed, paintwear)`. As four
32-bit values that is **16 bytes**; packed as `u16 defindex + u16 paintindex + u16 seed +
f32 wear` it is **10**. Everything else is per-weapon+finish and cacheable indefinitely.

## 8. The in-page blue% measurement — and what it is not

`measureBlue()` renders the mesh twice (both sides) into a 512² offscreen framebuffer with
`uMode = 1`, an orthographic fit, writing `r = isBlue`, `g = isPainted` per pixel, then
`readPixels` and counts. "Blue" is a heuristic on the **raw pattern texel**:
`b > 0.35 && b > r*1.35 && b > g*1.15`.

This is a rough, unlit, texture-driven measurement, not csbluegem's protocol. The demo shows
both side by side with an explicit warning. **Compare ordering, not absolute values** — and
see the correlation problem in `skin-3d-findings.md`, which is currently unexplained.
