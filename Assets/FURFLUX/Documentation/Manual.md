# FURFLUX Manual

Version 1.1.0 · Unity 6000.3+ · URP 17.x

1. [Getting started](#1-getting-started)
2. [How a coat is set up](#2-how-a-coat-is-set-up)
3. [Choosing a fur type](#3-choosing-a-fur-type)
4. [The fur material](#4-the-fur-material)
5. [The Fur Controller](#5-the-fur-controller)
6. [Detail maps and the Draw Fur brush](#6-detail-maps-and-the-draw-fur-brush)
7. [Wetness](#7-wetness)
8. [Collision](#8-collision)
9. [Fuzz layer](#9-fuzz-layer)
10. [Performance](#10-performance)
11. [Scripting](#11-scripting)
12. [Troubleshooting](#12-troubleshooting)
13. [Limitations](#13-limitations)

---

## 1. Getting started

**The fast way:** select a mesh, then **GameObject › FURFLUX › Add Fur (Shells)**. You can also
right-click the object in the Hierarchy. The object gets a Fur Controller plus its own copy of the
default material in `Assets/Fur Materials`.

**By hand:**

1. Add **Add Component › FURFLUX › Fur Controller** to an object with a `MeshRenderer` or
   `SkinnedMeshRenderer`.
2. Create a material with **Assets › Create › FURFLUX › Fur Material**, or use any material on the
   **FURFLUX/Fur Lit** shader.
3. Assign it to the controller's **Fur Material** slot.

GPU instancing is required, and the material inspector keeps it switched on for you.

The bundled scenes are under **Tools › FURFLUX**. *Demo* is a minimal scene. *Showcase* is a tour
of every feature, one pedestal each.

## 2. How a coat is set up

A coat is authored in two places, and the split is deliberate:

| The **material** says what the fur *is* | The **Fur Controller** says what this object *does* with it |
| --- | --- |
| Fur type, length, density, thickness, colours | Shell or strand budget, distance LOD, shadows |
| Stiffness, gravity, wind | Motion response (drag, throw, bones) |
| Lighting: root darkening, sheen, backlight | Wetness, water layers |
| How the coat reacts when wet | Detail maps, hair banding, collision |

Several objects can share one material. Everything per-object goes through the controller, so a
shared material never needs a runtime copy.

**Everything about a strand is relative.** Fur Length is a fraction of the object's size. Density
is strands *across* the object. Thickness is a fraction of the spacing between strands. The same
material therefore looks right on a mouse and on a bear, at any import scale. The controller's
**Source** section shows what the values come to on that particular object, in millimetres.

**Presets.** The controller's *Preset* section can seed a whole coat (material + controller) from
one of the two shipped looks. Choosing a preset writes nothing. **Apply Preset** writes to the
assigned material, and that material is a shared asset. The inspector warns you when other objects
use it too.

## 3. Choosing a fur type

Pick the type with the **Fur Type** dropdown at the top of the material.

| | **Shells** | **Strands** |
| --- | --- | --- |
| What it is | Stacked copies of the surface, hair drawn as a procedural pattern | One real ribbon per hair, skinned on the GPU |
| Cost scales with | Shell count × mesh triangles × screen coverage | Number of hairs × ribbon segments |
| Close-ups and silhouettes | Thins out; the hair is an illusion | Holds up |
| Mesh requirement | None beyond UVs and normals | **Read/Write Enabled** |
| Blend shapes | Followed | Followed (*Follow Blend Shapes* on the controller) |
| Best for | Many creatures, short dense coats | Hero characters, long hair |

Settings a type cannot use stay visible but greyed out, with a note saying which type uses them.

## 4. The fur material

The inspector is grouped into collapsible sections. Each one shows a summary while it is closed.

- **Surface**: the fur's own albedo, smoothness and optional normal map. This is the *hair's*
  colour, not the body's. The object's own renderer still draws its skin with its own material.
- **Fur**
  - **Fur Length**: fraction of the object's size.
  - **Strand Density**: strands across the object.
  - **Strand Thickness**: fraction of the strand spacing.
  - **Length Randomness**, **Curl**.
  - **Root / Tip Color**: a two-stop gradient along each hair. The alpha channel is how solid the
    hair is at that height. Fading the tip removes the hard cut-off edge.
  - **Fuzz layer**: see [section 9](#9-fuzz-layer).
- **Motion**: Stiffness (how much the root resists bending), Gravity, Wind Strength and Direction.
- **Lighting**: Root Darkening (depth), Sheen (Kajiya-Kay highlight along the strand), Backlight.
- **Wetness Response**: the *wet* end of each value: darkening, shorter fur, thinning, clumping,
  water-film smoothness, and how gravity and stiffness change.
- **Advanced**: alpha cutout, placement randomness, wind shape, lighting detail, render state.
  Keep **Cull Mode Off**: fur over the silhouette sits on back faces, and strand ribbons face both
  ways.

## 5. The Fur Controller

The **Live** section reports what is actually drawn: shells or strands, resolved colliders and
detail-map bakes.

### Quality

- **Shell Count** (Shells, LOD off): fixed number of layers, up to 256.
- **Max Strands** and **Ribbon Segments** (Strands): the hair budget for this object, and joints
  per hair. Density on the material asks for a count; whichever of the two is lower wins. The
  inspector says when the budget is the one holding the coat back.
- **Reduce With Distance**: drives the count from a curve.
  - The X axis is distance **in object lengths**, not metres. A key at 10 means ten body-lengths
    away at any scale.
  - The Y axis is the shell count (Shells) or the fraction of hairs kept (Strands).
  - The coat never drops below a small floor. Distant hairs are widened and averaged, so fur stays
    fur instead of turning into bare skin.
- **Hard Cull Distance** (Advanced): the one setting that removes the coat entirely. Also in
  object lengths; 0 disables it.
- **Shadows**
  - **None**: the fur casts nothing.
  - **Base Layer Only**: only the body's silhouette, cast by the object's own renderer. The fur adds
    nothing to the shadow map, so this is free.
  - **All Shells**: every layer or hair casts. This gives self-shadowing in the coat, at the cost of
    redrawing the fur into every shadow cascade.

### Motion

- **React To Movement** turns on motion inertia.
  - **Drag** follows speed: fur streams back while the object travels.
  - **Throw** follows acceleration: fur overshoots on starts and stops.
  - **Recovery Speed** sets how fast the spring settles.
- **Inertia Source › Transform And Bones** (Advanced) adds up to 8 **Motion Bones**. Their movement
  *relative to the body* drives the fur around them, so a tail flick moves the tail fur while the
  body stands still.

### Hair Banding

Replaces the material's two-stop Root/Tip colour with a **Gradient** of as many stops as you like.
Left is the root, right is the tip, and alpha is opacity. A dark root, a pale ring and a dark tip is
the classic agouti look. While banding is on, the material's Root and Tip Color do nothing.

### Advanced

Hard cull, ribbon seed, damping ratio, angular inertia, wetness resolution, detail-map resolution
and UV set, collision spread, the collider budget, and the hidden shader references that make sure
the shaders are included in builds.

## 6. Detail maps and the Draw Fur brush

The material states one value for the whole object. Detail maps vary four things **across the
surface**:

| Map | Effect | Neutral value |
| --- | --- | --- |
| **Length** (R) | Multiplies Fur Length, from 0 to 2× | mid-grey |
| **Thickness** (R) | Multiplies strand *width* (not count), from 0 to 2× | mid-grey |
| **Color** (RGB) | Multiplies the fur albedo. Hair only; the skin is untouched | white |
| **Comb** (RG) | The direction the hair *lies*, in the surface's own frame | flat grey (0.5, 0.5) |

For Length and Thickness, mid-grey is neutral so a map can make fur longer as well as shorter.

A map is live as soon as it holds something: a source texture or brush strokes. An empty slot
costs nothing. **Clear** frees a map's memory. Emptying the texture slot does not, because that is
the state you paint from.

- **Match Mesh Colours** points the Color map at the model's own albedo texture, including its
  tiling, so the coat takes the colours the mesh already has.
- **Baked Resolution** (Advanced) is shared by all maps. The brush window reports how many
  millimetres of surface one texel covers and suggests a resolution when that is too coarse for
  small features.

### UV set

Maps are stored per piece of surface, so the unwrap must not overlap. On a mirrored UV0, painting
one flank paints the other. Switch **Detail UV Set** to **UV1**: enable *Generate Lightmap UVs* on
the model importer first. The brush window measures the overlap and offers the switch for you.

### Draw Fur

Click **Draw Fur…** on the controller, tick the channels you want, then drag over the model in the
Scene view.

- One stroke writes **every ticked channel**, so length, thickness and colour share one edge.
- **Comb** takes its direction from the stroke itself. Drag across the coat and the hair lies down
  that way. Combing back the way you came un-combs.
- **Radius** is in metres, because the brush is a sphere in the world. **Strength** is flow, so
  strokes build up. **Hardness** shapes the falloff. **Spacing** keeps strokes even at any mouse
  speed.
- **Mode**
  - **Solid** paints only what you can see: back faces and surfaces behind the body are skipped.
  - **Wireframe** paints everything inside the sphere. Use it for collars, tail rings and thin
    ears.
- **Value** for Length and Thickness is in the material's own units. The material's value is
  neutral, and the window warns when the brush holds it.
- **Fill with the current value** floods the armed channels in one undoable step.

| Shortcut (Scene view) | |
| --- | --- |
| `[` / `]` | Smaller / larger brush |
| `I` | Pick the value under the cursor |
| `Ctrl+Z` / `Ctrl+Shift+Z` or `Ctrl+Y` | Undo / redo strokes |
| `Alt`, `Ctrl`, `Shift` held | Navigate the Scene view instead of painting |

**Save your work.** Strokes live in render textures and are lost on Play Mode, a script recompile
or disabling the object. **Save Painted Maps…** writes PNGs and assigns them as the sources. The
inspector and the window both warn while anything is unsaved.

## 7. Wetness

- **Global Wetness** is a floor for the whole coat, for rain or a scripted soak.
- **React To Water** makes only what touches water get wet, and keeps it wet after leaving.
  1. Put your water colliders (triggers are fine) on a layer.
  2. Select that layer in **Water Layers**.
  3. Optionally add **FURFLUX › Fur Water Volume** to a collider to override its strength or splash
     margin.
- **Soaks In / Drying Rate** control how fast fur gets wet and dries. **Contact Feather**
  (Advanced) is the splash margin around the waterline.

Wet fur darkens, shortens, clumps into tufts, sags, gets glossy and moves more sluggishly. The
amounts are on the material's *Wetness Response*. Call `ResetWetness()` after a teleport or
respawn.

## 8. Collision

Turn on **Enable Collision** and choose **Collision Layers**.

- Box, Sphere and Capsule colliders are exact. Mesh and Terrain colliders are approximated by
  their bounding box.
- Every hair tip is resolved against the nearest colliders, up to *Max Simultaneous Colliders* (at
  most 8).
- **Strength** sets how fully contact is resolved. **Margin** is how early fur starts leaning away.
  **Spread** (Advanced) controls how far fur combs aside instead of flattening.

This is the most expensive feature. Leave it off where nothing touches the coat.

## 9. Fuzz layer

A soft halo and extra fullness over the sharp coat, set with **Fuzz Amount** on the material. At 0
nothing extra is drawn.

- **Geometric**: a second, wider and translucent copy of the coat. Works everywhere and is lit
  like the coat.
- **Screen Space**: blurs the coat's own pixels in one full-screen pass. It needs the **Fur Screen
  Fuzz** renderer feature: the material inspector offers to add it, or use **Tools › FURFLUX ›
  Install Screen Fuzz Renderer Feature**. MSAA must be **off** on the URP asset; use FXAA, SMAA or
  TAA instead.

## 10. Performance

- **Shells:** cost is overdraw: shell count × screen area. Shorter fur, fewer shells and the
  distance LOD help most. Shells repeat the whole mesh, so a lower-poly mesh helps too.
- **Strands:** cost is hair count × segments. Use **Max Strands** as the budget dial.
- **Shadows:** *Base Layer Only* is free. *All Shells* redraws the coat into every cascade.
- **Collision** and **React To Water** run per vertex. Enable them only where needed.
- Off-screen objects skip all per-frame work. Detail maps re-bake only when something changes, and
  the *Detail Map Bakes* counter in **Live** should stop climbing.

## 11. Scripting

```csharp
using FurRendering;

var fur = GetComponent<FurController>();

// Budget and quality
fur.EnableLod   = true;
fur.ShellCount  = 24;                        // used while LOD is off
fur.ShadowMode  = FurController.FurShadowMode.BaseLayerOnly;

// Forces and state
fur.ExternalForce = explosionPush;           // persists until you set it back to zero
fur.Wetness       = 0.6f;                    // global wetness floor
fur.ResetWetness();                          // dry instantly (teleport, respawn)

// Detail maps
fur.LengthMap        = shavedCoat;           // re-bakes next frame; same value = no-op
fur.ColorMapStrength = 0.5f;                 // live, no re-bake
fur.RebakeDetailMaps();                      // back to the source textures

// Runtime painting: scorch marks, dye, a shaved patch
var brush = FurBrush.Default(radius: 0.15f);
brush.PositionWS = hit.point;
brush.DirectionWS = ray.direction;
fur.EnsureDetailMap(FurDetailChannel.Length);
fur.PaintLength(brush, 0f);                  // 0 = bare, 0.5 = material value, 1 = double
fur.PaintColor(brush, Color.grey);           // needs EnsureDetailMap(Color) first
fur.FinishPaintStroke();                     // once per stroke, not per stamp

// Diagnostics
int shells = fur.ActiveShellCount;
int hairs  = fur.ActiveRibbonCount;
```

Runtime paint is not saved anywhere. It lives as long as the component is enabled, which is usually
what transient damage should do.

## 12. Troubleshooting

| Symptom | Fix |
| --- | --- |
| No fur at all | Is a URP asset active? Is a **FURFLUX/Fur Lit** material assigned? Check the Console: the controller names the problem once. |
| Strands: bare object | Enable **Read/Write** on the mesh import settings. |
| Half the hairs missing | Set **Cull Mode** to Off (material › Advanced). |
| Visible bands or gaps between shells | Raise the shell count (or the near part of the LOD curve), increase Thickness, or shorten the fur. |
| Coat looks thin from above (Strands) | Raise Strand Density; a flat ribbon seen end-on covers little. |
| Painting does nothing | The brush holds the neutral value (see the window's warning), the channel is not ticked, or the mesh is not readable. |
| Paint appears on the other side of the model | The unwrap is mirrored. Switch **Detail UV Set** to UV1. |
| Strokes won't take in a tight crevice | Raise **Baked Resolution**: the window shows the texel size. |
| Painted strokes disappeared | They were never saved. Use **Save Painted Maps…**. |
| Screen Space fuzz does nothing | Install the renderer feature; turn MSAA off. |
| Detail Map Bakes keeps climbing | A script is assigning a different texture or resolution every frame. |
| Fur never gets wet | **Water Layers** is empty, or the water collider is not on that layer. |

## 13. Limitations

- Shell fur is an approximation and can show layering at extreme grazing angles.
- Strand ribbons are flat, and lose coverage when seen exactly end-on.
- Shell strand size follows the UV unwrap: tightly packed islands get finer strands.
- Motion vectors track the body's movement, not the sway of individual hairs.
- No lightmap variants: fur is meant for moving creatures and uses light probes.
- Desktop graphics APIs only. OpenGL ES and WebGL are excluded.
