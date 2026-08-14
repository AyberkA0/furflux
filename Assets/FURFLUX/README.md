# PC Shell Fur for URP

High-quality, shell-based fur rendering for Unity 6 and the Universal Render Pipeline (URP). Fur layers are rendered as GPU instances in a single submission per camera, without geometry shaders.

## Quick start

1. Import the `Fur` folder into a Unity 6 URP project.
2. Add **Rendering > Fur > Fur Controller** to an object with a `MeshRenderer` or `SkinnedMeshRenderer`.
3. Assign a material using **Fur/Shell Lit (PC)** to the controller's **Fur Material** field.
4. Use `Prefabs/FurPreviewSphere.prefab` as a self-contained reference setup, or open `Demo/FurDemo.unity`.

## Requirements

- Unity 6.3.21f1 or newer
- Universal Render Pipeline 17.x
- Windows, Linux, or macOS desktop target with DirectX 11/12, Vulkan, or Metal support
- A source mesh with valid normals, UVs, and tangents (MikkTSpace tangents are recommended)

This is a desktop/PC shader. It excludes GLES and is not intended for mobile or WebGL.

## Package contents

- `Runtime/` - runtime controller and assembly definition
- `Editor/` - custom inspector and density/continuity diagnostics
- `Shaders/` - URP shell fur shader and includes
- `Materials/` - ready-to-use demonstration material
- `Prefabs/` - self-contained preview prefab using Unity's built-in sphere mesh
- `Demo/` - a minimal runnable demonstration scene
- `Documentation/Manual.md` - setup, tuning, performance, and limitations

## Support

Read the local manual before reporting an issue. When contacting the publisher, include your Unity version, URP version, target platform, a screenshot, and the Console output.
