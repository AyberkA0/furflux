using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace FurRendering
{
    /// <summary>
    /// A depth picture of the model taken from the brush's viewpoint, so a
    /// <see cref="FurPaintMode.Solid"/> stroke can tell a visible surface from one
    /// hidden behind the body.
    ///
    /// Why facing alone is not enough
    /// ------------------------------
    /// The obvious solid test - "does this texel face the brush?" - is correct on a
    /// convex shape and wrong on every animal. The far leg faces the brush just as
    /// squarely as the near one, and so does the belly behind a raised paw, so a wide
    /// stroke marked all of them. What separates them is not orientation but DEPTH:
    /// only one of those surfaces is the first thing the brush would reach.
    ///
    /// Captured once per stroke, not per stamp. The view cannot change while the cursor
    /// is held down, so re-rendering the model for each of the dozens of stamps in a
    /// stroke would be paying repeatedly for an answer that has not moved.
    ///
    /// Self-occlusion only: this renders the furred model and nothing else, so a wall
    /// standing between the camera and the creature does not block the brush. That is
    /// the right trade - the cases that actually bite are the model's own limbs and
    /// body, and including the rest of the scene would mean rendering it every stroke.
    /// </summary>
    internal sealed class FurBrushOcclusion : IDisposable
    {
        private const int PassOcclusion = 3;

        private static readonly int ViewProjId = Shader.PropertyToID("_FurBrushViewProj");

        private readonly MaterialPropertyBlock _props = new MaterialPropertyBlock();

        private CommandBuffer _commandBuffer;
        private Material _material;
        private RenderTexture _depth;
        private int _width;
        private int _height;

        /// <summary>The captured depth, or null before the first capture.</summary>
        public RenderTexture Depth => _depth;

        /// <summary>The matrix the capture was taken with. Strokes must project through the same one.</summary>
        public Matrix4x4 ViewProjection { get; private set; }

        /// <summary>True when a usable capture exists.</summary>
        public bool IsValid => _depth != null && _depth.IsCreated();

        /// <summary>
        /// Renders the model's depth as seen through <paramref name="viewProjection"/>.
        /// </summary>
        public bool Capture(Shader bakeShader, Mesh mesh,
            Matrix4x4 objectToWorld, Matrix4x4 viewProjection, int width, int height)
        {
            if (bakeShader == null || mesh == null)
                return false;

            if (_material == null || _material.shader != bakeShader)
            {
                if (_material != null)
                {
                    if (Application.isPlaying) UnityEngine.Object.Destroy(_material);
                    else UnityEngine.Object.DestroyImmediate(_material);
                }
                _material = new Material(bakeShader) { hideFlags = HideFlags.HideAndDontSave };
            }

            width = Mathf.Clamp(width, 64, 4096);
            height = Mathf.Clamp(height, 64, 4096);

            if (!EnsureTarget(width, height))
                return false;

            ViewProjection = viewProjection;

            _commandBuffer ??= new CommandBuffer { name = "Fur Brush Occlusion" };
            _commandBuffer.Clear();

            _props.Clear();
            _props.SetMatrix(ViewProjId, viewProjection);

            _commandBuffer.SetRenderTarget(_depth);

            // Colour cleared to the FAR value so anything the model does not cover reads
            // as "nothing in the way" rather than as a surface at the near plane, which
            // would reject every texel that projects outside the silhouette.
            float far = SystemInfo.usesReversedZBuffer ? 0f : 1f;
            _commandBuffer.ClearRenderTarget(true, true, new Color(far, 0f, 0f, 1f));

            int subMeshCount = mesh.subMeshCount;
            for (int subMesh = 0; subMesh < subMeshCount; subMesh++)
                _commandBuffer.DrawMesh(mesh, objectToWorld, _material, subMesh, PassOcclusion, _props);

            Graphics.ExecuteCommandBuffer(_commandBuffer);

            return true;
        }

        /// <summary>Drops the capture, so Solid strokes fall back to the facing test alone.</summary>
        public void Invalidate()
        {
            DestroyTarget();
        }

        private bool EnsureTarget(int width, int height)
        {
            if (_depth != null && _depth.IsCreated() && _width == width && _height == height)
                return true;

            DestroyTarget();

            // RFloat with a real depth buffer: the colour carries the depth value the
            // paint pass samples, while the attached depth buffer is what makes the
            // nearest fragment win in the first place.
            var descriptor = new RenderTextureDescriptor(width, height, RenderTextureFormat.RFloat, 24)
            {
                sRGB = false,
                useMipMap = false,
                autoGenerateMips = false,
                msaaSamples = 1,
            };

            _depth = new RenderTexture(descriptor)
            {
                name = "FurBrushOcclusion",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };

            if (!_depth.Create())
            {
                DestroyTarget();
                return false;
            }

            _width = width;
            _height = height;
            return true;
        }

        private void DestroyTarget()
        {
            if (_depth != null)
            {
                _depth.Release();

                if (Application.isPlaying) UnityEngine.Object.Destroy(_depth);
                else UnityEngine.Object.DestroyImmediate(_depth);

                _depth = null;
            }

            _width = 0;
            _height = 0;
        }

        public void Dispose()
        {
            _commandBuffer?.Release();
            _commandBuffer = null;

            if (_material != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(_material);
                else UnityEngine.Object.DestroyImmediate(_material);
                _material = null;
            }

            DestroyTarget();
        }
    }
}
