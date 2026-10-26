# Recipe: Liquid Glass (Apple-style glass material)

Proven on After Effects 2023 with built-in effects only. Built live on 2026-10-26: `projects\Liquid Glass.aep`, render `renders\liquid-glass.mp4`.

## What sells it

- Refraction at the **edges** (content bends near the rim), slight **magnification** inside, a little frost and a lift in exposure and vibrance.
- Light: a crisp thin rim, a soft **inner glow** that fades from the edge inward, two **glints** at opposite angles, and a soft contact shadow **outside** the silhouette only.
- Something with detail behind it (large type, a faint grid), or the refraction is invisible.

## Rig A: metaball glass (split and merge)

Used in `projects\Liquid Glass.aep`.

1. **`LG Shapes`** precomp, comp-sized, white shapes on transparent:
   - Core rect. Its roundness has the expression `var s = content("Group 1").content("Rectangle Path 1").size; Math.min(s[0], s[1]) / 2;`. A literal roundness above half the size renders as a rounded *square* in AE 2023.
   - Orbs parented to Core. Parent them **before** animating Core's scale, because parenting to a zero-scale parent breaks.
   - On top, a "Gooey" adjustment layer:
     - Gaussian Blur 26 with **Repeat Edge Pixels off**. When it's on, the circle gets facets at its tangent points.
     - Levels (Individual Controls) with Alpha Input Black 0.47 and Alpha Input White 0.53. This threshold makes shapes merge like liquid.
2. **`LG Height`** precomp: `LG Shapes` plus Gaussian Blur 34 (repeat edges off). This is the dome-shaped height map.
3. **Main comp**, from top to bottom (AE 23+ track mattes can point anywhere):
   - **Glint A / Glint B**:
     - `LG Shapes` + Fill white + CC Light Sweep: Direction 72 / -108, Shape Smooth, Width 70, Sweep Intensity 0, Edge Intensity 100, Edge Thickness 6, Light Reception **Cutout**.
     - Center expression: `var p = comp("LG Shapes").layer("Core").toComp([0, 0]); [p[0], p[1]];`
     - Add blend, opacity 75 / 45.
   - **Inner Glow**: `LG Shapes` + Fill white, track matte = `Glass Height` **alpha inverted**, screen blend, opacity 35.
   - **Rim**: `LG Shapes` + Fill white + Linear Wipe for the falloff, track matte = `Rim Matte` (`LG Shapes` + Simple Choker 2.5, hidden) **alpha inverted**. Add blend, opacity 55.
   - **Glass FX** adjustment layer, track matte = `Glass Matte` (`LG Shapes`, hidden), with these effects in order:
     1. Transform. Anchor and Position use the Core expression above; Scale is keyed 104 → 107 for the capsule → 101 while the orbs are apart, otherwise the off-centre orbs ghost.
     2. CC Glass:
        - Bump Map: `Glass Height`, Property: Alpha.
        - Softness 14, Height 30, Displacement 55. Higher values double the letters in small orbs.
        - Light Height 35. Ambient **100**, Diffuse 10 (lower values make it a dark marble).
        - Specular 85, Roughness 0.04, **Metal 0** (white highlights).
     3. Gaussian Blur 3 (frost), then Exposure +0.3, then Vibrance 45.
   - **Glass Height**: hidden, used only as the bump map and matte.
   - **Glass Shadow**: `LG Shapes` + Fill black + blur 38, offset +30 px in y, opacity 42, track matte = `Glass Matte` **alpha inverted**. Without that matte, the glass shows its own shadow and turns muddy.
   - Content behind (title), wallpaper.
4. **Wallpaper**: large unblurred screen-blended ellipses with a slow `wiggle(0.12, 170)`, plus **one** comp-sized blur adjustment above them. A blur on each shape layer clips into rectangles.

## Rig B: shape-as-adjustment glass (bentomotion "Liquid Glass", AE 24.6)

This is simpler and faster, but has a single shape. It was read from the .aep with a RIFX parser because AE 23 cannot open 24.x files.

- **Box**: a rounded-rect shape layer with the **Adjustment switch on**, so its effects hit everything below inside the shape.
  - Size comes from two Slider Controls (X/Y). The sliders and position use an inertia bounce: `amp 0.02, freq 2, decay 6` with `velocityAtTime(key(n).time - frameDuration/10)`.
  - Effects in order: Fast Box Blur 16, Transform scale 108 (lens), Tint (amount keyed), Displacement Map (luminance, 220 px both axes).
- **Map**: the same rect as a **60 px stroke** with Fast Box Blur 20. It is the displacement source, so only the rim band refracts.
- **Lights**: four copies of the rect that follow Box by expression:
  - A: 19 px stroke, blur 4, Set Matte to Box, 20%.
  - B: 60 px stroke, blur 40, Set Matte, 40%.
  - C and D: 60 px stroke + CC Light Sweep, Cutout, directions 72° and -109°, about 41%.
- **Background**: a purple Fill plus two Grid effects (599 px / 8.6 px lines and 120 px / 1.3 px lines, light cyan). The grids make the bending obvious.

## Pitfalls found

- A blur on a shape layer clips at the layer bounds. Blur the composite with an adjustment layer or precomp instead.
- A white additive highlight over near-white type is invisible, because there is no headroom left. Use colour or darker content.
- CC Glass on an adjustment layer refracts whatever sits under it, including your own shadow. Matte the shadow to the outside of the glass.
