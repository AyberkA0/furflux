# Changelog

## 1.1.0 - 2026-10-21

### Added
- **Welcome window**, opened once after import or update, and again from **Tools › FURFLUX ›
  Welcome**. It has a quick start, a project check (URP, instancing) and links to the docs and
  demo scenes.
- **GameObject › FURFLUX › Add Fur (Shells / Strands)**, also on right-click in the Hierarchy. It
  adds a Fur Controller with its own material in `Assets/Fur Materials`, and warns about meshes
  that strand fur cannot read.
- **Assets › Create › FURFLUX › Fur Material (Shells / Strands)**.
- **Tools › FURFLUX** menu: Documentation, Open Demo / Showcase Scene, Install Screen Fuzz
  Renderer Feature, Clean Stale Material Properties.
- Component icons for Fur Controller and Fur Water Volume.
- An identity banner (version, Manual, Welcome) on the Fur Controller and fur material inspectors.
- `FurController.RebakeCombMap()`.

### Fixed
- **Shell fur vanished a few metres from the camera.** Distant shells now switch from the
  fixed alpha cutout to a dithered one, as they did before the 1.0 clean-up. A pixel keeps the
  fraction of fur it should, at any distance, so the coat no longer drops out in one step.
  There is no new setting, and close-up rendering is unchanged.
  - Only the coverage the fixed cutout would have discarded is dithered. Fragments that
    already pass are always drawn.
  - The dither is attached to each strand, not to the screen, so it does not crawl over
    moving fur. With TAA it averages out to a smooth coat; TAA is recommended for fur, and
    both demo cameras now use it.
  - When to aggregate is measured on the pixel's area, so silhouettes no longer switch to
    the distant mode while the coat is still close.
- **Close-ups turned shell fur into dithered blocks.** The LOD treated layers drifting
  apart on screen, which happens as the camera comes close, as a reason to switch the coat
  to its distant, averaged form. Only the height the LOD removes drives that switch now.
- **Objects that carry the fur material on their own renderer went see-through at random.**
  Unity GPU-instances renderers that share a material, and the shader read each object's
  slot in that batch as a fur layer, so whichever body landed in slot 3 was clipped like the
  third layer of a coat. Which one depended on draw order, so it came and went with the
  camera. The instance index is now only a layer index in the Fur Controller's own draws.
- **Distance LOD thinned coats into floating specks.** The distance curve could leave a few
  layers several pixels apart on screen. It now keeps enough layers to hold the gap under
  about 1.5 pixels, up to the count the curve gives at distance zero.
- **Showcase, Distance station:** the five spheres had hand-set layer counts rather than
  LOD. They now share one curve, so the LOD picks the count from the distance.
- **Strand Thickness** now reaches 20 (was 5). Low-density, stylised coats need wider strands
  than the old range allowed, and the Showcase's stylised coat now has thick, cartoon-like
  tufts.
- **Materials that stored an Alpha Cutout of 1.0** (the slider used to reach 1) rendered almost
  bare. The shader now holds the cutout to 0.9, and the Showcase materials use sensible values
  again.
- **Showcase:** the caption was rewritten as a small, plain label in the corner, and the unused
  3D text labels were removed. Zoom now runs from about 1 m to 35 m, in steps proportional
  to the distance, toward whatever is under the cursor; W/S and +/- zoom as well.
- **Comb could not be painted on its own.** With only Comb ticked, the brush never took the mouse
  in the Scene view.
- **Revert to Source Textures** and `RebakeDetailMaps()` left comb strokes in place. The comb map
  now reverts with the others and is counted in *Detail Map Bakes*.
- The brush cursor label and colour now show when Comb is armed. *Fill* is disabled when Comb is
  the only armed channel, because a fill has no direction.
- **Base Layer Only** shadows no longer submit a shadow draw that the shader then discards
  completely. The body's own renderer casts that shadow.
- The material inspector no longer claims Strands fur ignores blend shapes.
- The density note no longer claims strand size is corrected per fragment.
- Inspector notes about the detail UV set, and API docs that mentioned the removed Length and
  Thickness ranges, are corrected.

### Changed
- Shader names: **FURFLUX/Fur Lit**, plus `Hidden/FURFLUX/*` for the internal ones. Existing
  materials keep working, because they reference the shader by GUID.
- Component menu: **Add Component › FURFLUX** (was *Rendering › Fur*). Demo helpers are under
  **FURFLUX › Demo**.
- Folder layout reorganised. GUIDs are unchanged, so scenes and prefabs keep their references.
  - `Runtime/` and `Editor/` split into subfolders by feature.
  - Shader includes moved to `Shaders/Includes`, internal shaders to `Shaders/Hidden`.
  - Default materials renamed to `FUR_Shells_Default` and `FUR_Strands_Default`.
  - Preview prefabs renamed to `FurPreview_Shells` and `FurPreview_Strands`.
  - Demo content gathered under `Demo/` (Scenes, Scripts, Materials, Textures).
- `FreeCamera` (demo) moved into the `FurRendering.Demo` namespace, so it cannot collide with a
  project's own class of the same name.
- README and Manual rewritten to match the current feature set: two fur types, four detail maps,
  hair banding, presets, the fuzz layer and LOD in object lengths.

## 1.0.0 - 2026-08-15
- First release.
