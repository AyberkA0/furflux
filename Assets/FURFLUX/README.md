# FURFLUX

**Shell and strand fur for the Universal Render Pipeline.**
Version 1.1.0 · Unity 6000.3+ · URP 17.x

FURFLUX grows fur on any `MeshRenderer` or `SkinnedMeshRenderer`. It renders without
geometry shaders: every layer or batch of hairs is a GPU instance. The coat moves with the
object, reacts to wind, gets wet in water, bends around colliders, and can be painted right in
the Scene view.

---

## Quick start (one minute)

1. Select a mesh in your scene.
2. **GameObject › FURFLUX › Add Fur (Shells)**, or right-click it in the Hierarchy and pick
   the same entry.
3. Press Play, or just look at the Scene view. The fur also renders in Edit Mode.

That's it. The object gets a **Fur Controller** and its own material in `Assets/Fur Materials`,
so tuning it never changes another object.

To try both fur types side by side, open **Tools › FURFLUX › Open Showcase Scene**.

---

## Where things are

| Menu | What it does |
| --- | --- |
| **Tools › FURFLUX › Welcome** | Quick start, project check and links |
| **Tools › FURFLUX › Documentation** | Opens the full manual |
| **Tools › FURFLUX › Open Demo / Showcase Scene** | The bundled scenes |
| **Tools › FURFLUX › Install Screen Fuzz Renderer Feature** | Needed only for *Screen Space* fuzz |
| **GameObject › FURFLUX › Add Fur (Shells / Strands)** | Furs the selected objects |
| **Assets › Create › FURFLUX › Fur Material** | A new fur material of either type |
| **Add Component › FURFLUX** | Fur Controller, Fur Water Volume, demo helpers |

## Folder layout

```
FURFLUX/
├─ README.md            ← you are here
├─ CHANGELOG.md
├─ LICENSE.txt
├─ Documentation/       Manual.md: the full guide
├─ Materials/           FUR_Shells_Default, FUR_Strands_Default
├─ Prefabs/             FurPreview_Shells, FurPreview_Strands
├─ Runtime/             Core · Strands · DetailMaps · Wetness · Collision · RendererFeatures
├─ Editor/              Inspectors · Painting · Tools · Icons
├─ Shaders/             FurLit.shader · Includes/ · Hidden/
└─ Demo/                Scenes · Scripts · Materials · Textures  (safe to delete)
```

The `Demo` folder can be deleted in a shipping project. Nothing outside it depends on it.

## Requirements

- Unity **6000.3.21f1** or newer, Universal Render Pipeline **17.x**
- A desktop graphics API: DirectX 11/12, Vulkan or Metal (Shader Model 4.5, GPU instancing)
- A mesh with normals and UVs. MikkTSpace tangents are recommended.
- **Strands** fur additionally needs **Read/Write Enabled** on the mesh import settings.

OpenGL ES and WebGL are not supported.

## License

See `LICENSE.txt`. Commercial use requires attribution, for example
*"Fur rendering powered by FURFLUX"*.
