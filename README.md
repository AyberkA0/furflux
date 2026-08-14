# FURFLUX - Shell Fur Shader for URP - Manual

![Screenshot4](https://raw.githubusercontent.com/AyberkA0/furflux/main/Screenshots/4.png)

## Compatibility

PC Shell Fur targets Unity 6.3.21f1+ with URP 17.x. It is designed for desktop rendering APIs (DirectX 11/12, Vulkan, and Metal) and requires Shader Model 4.5-class GPU instancing. Mobile and WebGL are not supported.

## Setup

1. Select a GameObject containing a `MeshRenderer` or `SkinnedMeshRenderer`.
2. Add **Rendering > Fur > Fur Controller**.
3. Create a material with the **Fur/Shell Lit (PC)** shader, then assign it to **Fur Material**.
4. Leave **Source Renderer** empty to use the renderer on the same GameObject, or assign a different renderer explicitly.
5. Enable GPU instancing on the material. The controller also enables it automatically when it first renders.

`Prefabs/FurPreviewSphere.prefab` is a minimal, self-contained reference. `Demo/FurDemo.unity` demonstrates the same setup in a clean URP scene.

## Source mesh requirements

The source mesh needs UVs and normals. Tangents are strongly recommended for correct normal mapping and anisotropic highlights. In the model import settings, use **Normals: Import** or **Calculate** and **Tangents: Calculate MikkTSpace**.

Use a low-poly proxy mesh when practical. Each shell repeats the source mesh, so base mesh triangles are multiplied by the active shell count.

## Quality and LOD

- **Distance LOD Curve** maps camera distance in meters (X) to shell count (Y). Its first key controls close-range quality; shape tangent handles into a parabola or smooth falloff for the desired profile. Shell values are capped at 64.
- **Shells Per Pixel** caps fur density by on-screen thickness, avoiding imperceptible overdraw at distance.
- **Skin Only Pixel Threshold** falls back to the base shell when fur is too thin to resolve.
- **Hard Cull Distance** is optional; use it only when the object can be bare at distance.
- **Shadow Mode**: `BaseLayerOnly` is recommended. `AllShells` is expensive; `None` is fastest.

Fur cost is dominated by overdraw. Shorter fur and fewer shells generally perform better than very long, dense fur.

## Material tuning

### Fur structure

- **Fur Length** controls shell displacement in meters.
- **Strand Density**, **Thickness**, **Placement Randomness**, and **Length Randomness** control procedural strand distribution.
- Enable **Use Fur Mask Texture** to use the mask texture's red channel for localized density and length.
- **Disc Billboard** helps reduce shell banding at glancing angles. Use the limit control to prevent excessive stretching on the silhouette.

If strands appear separated, first raise shell count; then increase thickness or reduce fur length. The custom inspector displays a continuity diagnostic for procedural materials.

### Motion and lighting

- **Stiffness**, **Gravity**, wind controls, and **Motion Inertia** shape strand motion.
- **Fur Base Darkening** and its curve add volume near the root.
- **Anisotropic Sheen** and **Backlight Scatter** add fur-like highlights.
- If shadow acne appears, increase **Extra Shadow Bias** slightly. Reduce it if shadows detach visibly.

## Limitations

- Shell fur is an approximation and can exhibit silhouette artifacts at extreme grazing angles.
- It does not provide per-object baked light probe data for instanced shells; ambient/probe lighting is used.
- Rendering Debugger material views are intentionally not supported to keep shader variant count down.
- The shader is intended for moving creatures and does not include lightmap variants.

## Troubleshooting

**Material needs to enable instancing:** enable GPU Instancing on the fur material.

**Flat or incorrect highlights:** ensure the mesh has correct tangents.

**Visible bands/gaps:** raise the near portion of the Distance LOD Curve, increase Thickness, or reduce Fur Length.

**Low frame rate:** reduce Fur Length, use a proxy mesh, use `BaseLayerOnly` shadows, and lower the Distance LOD Curve at medium and far ranges.

![Screenshot1](https://raw.githubusercontent.com/AyberkA0/furflux/main/Screenshots/1.png)

![Screenshot5](https://raw.githubusercontent.com/AyberkA0/furflux/main/Screenshots/5.png)
