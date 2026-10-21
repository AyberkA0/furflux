using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace FurRendering
{
    /// <summary>
    /// Per-object wetness stored in texture space.
    ///
    /// Why texture space: a wetness value has to live SOMEWHERE between frames for
    /// a wet patch to persist after the object leaves the water, and it has to be
    /// addressable per point on the surface. A world-space formula (spheres, a
    /// water plane) cannot do either - it is stateless and it bakes assumptions
    /// about the object's shape into the shader. A map indexed by UV0 has neither
    /// problem, which is also why this works on any mesh rather than only on
    /// roughly spherical ones.
    ///
    /// Each frame the object's mesh is rasterised into a small RGHalf target using
    /// its UV0 layout as the clip-space position (see FurWetnessSplat.shader), so
    /// one fragment lands on each texel of the surface. That fragment knows its
    /// real world position and tests itself against the water volumes.
    ///
    /// Two accumulation targets are ping-ponged because the pass reads the
    /// previous frame's value while writing the new one. A third target holds the
    /// dilated (UV-island-padded) result and is the one the fur shader samples.
    ///
    /// Requirements on the mesh:
    ///  * UV0 must be a real unwrap. Overlapping / mirrored islands share texels,
    ///    so wetting one side wets the other - that is a property of the asset,
    ///    not something this system can work around.
    ///  * Skinned meshes must be fed the BAKED mesh so world positions follow the
    ///    skeleton. FurController already does this.
    /// </summary>
    internal sealed class FurWetnessMap : IDisposable
    {
        /// <summary>Must match FUR_MAX_WET_SOURCES in FurWetnessSplat.shader.</summary>
        public const int MaxSources = 8;

        private const int PassAccumulate = 0;
        private const int PassDilate = 1;

        private static readonly int PrevWetnessId    = Shader.PropertyToID("_PrevWetness");
        private static readonly int SourceToLocalId  = Shader.PropertyToID("_WetSourceToLocal");
        private static readonly int SourceShapeId    = Shader.PropertyToID("_WetSourceShape");
        private static readonly int SourceParamsId   = Shader.PropertyToID("_WetSourceParams");
        private static readonly int SourceCountId    = Shader.PropertyToID("_WetSourceCount");
        private static readonly int WettingRateId    = Shader.PropertyToID("_WettingRate");
        private static readonly int DryingRateId     = Shader.PropertyToID("_DryingRate");
        private static readonly int DryingEnabledId  = Shader.PropertyToID("_DryingEnabled");
        private static readonly int WetDeltaTimeId    = Shader.PropertyToID("_WetDeltaTime");
        private static readonly int WetTargetSizeId   = Shader.PropertyToID("_WetTargetSize");
        private static readonly int DetailUvId        = Shader.PropertyToID("_FurDetailUV");

        private readonly Matrix4x4[] _sourceToLocal = new Matrix4x4[MaxSources];
        private readonly Vector4[] _sourceShape = new Vector4[MaxSources];
        private readonly Vector4[] _sourceParams = new Vector4[MaxSources];

        /// <summary>
        /// Which UV set this map is stored in: 0 = UV0, 1 = UV1 (mesh.uv2). Must match the
        /// fur material's _FurDetailUV - the simulation writes here and the coat reads
        /// there, so a disagreement soaks the wrong parts of the body.
        ///
        /// Unlike the painted maps this one carries no authored work, so a change costs
        /// nothing but the wetness accumulated so far, which the next few frames rebuild.
        /// </summary>
        public int UvChannel { get; set; }

        private readonly MaterialPropertyBlock _accumulateProps = new MaterialPropertyBlock();
        private readonly MaterialPropertyBlock _dilateProps = new MaterialPropertyBlock();

        private CommandBuffer _commandBuffer;
        private Material _material;
        private RenderTexture _accumFront;   // holds the current accumulated state
        private RenderTexture _accumBack;    // written this frame, then swapped
        private RenderTexture _dilated;      // what the fur shader samples
        private int _resolution;
        private bool _needsClear = true;

        /// <summary>The wetness texture to bind to the fur material, or null before the first update.</summary>
        public RenderTexture Texture => _dilated;

        /// <summary>Number of water volumes packed into the last update - for debugging.</summary>
        public int ActiveSourceCount { get; private set; }

        // ------------------------------------------------------------------ //
        //  Resources
        // ------------------------------------------------------------------ //
        private bool EnsureResources(Shader splatShader, int resolution)
        {
            if (splatShader == null)
                return false;

            if (_material == null || _material.shader != splatShader)
            {
                DestroyRuntimeObject(ref _material);
                _material = new Material(splatShader) { hideFlags = HideFlags.HideAndDontSave };
            }

            _commandBuffer ??= new CommandBuffer { name = "Fur Wetness" };

            resolution = Mathf.Clamp(Mathf.NextPowerOfTwo(resolution), 32, 1024);
            if (_resolution == resolution && _accumFront != null && _accumFront.IsCreated())
                return true;

            ReleaseTargets();
            _resolution = resolution;

            _accumFront = CreateTarget("FurWetnessAccumA");
            _accumBack  = CreateTarget("FurWetnessAccumB");
            _dilated    = CreateTarget("FurWetnessDilated");
            _needsClear = true;

            return _accumFront != null;
        }

        private RenderTexture CreateTarget(string name)
        {
            // RGHalf, not R8: drying integrates deltas around 0.002 per frame, which
            // an 8-bit target quantises straight back to zero - fur would never dry.
            // R = wetness, G = coverage (whether the texel belongs to the mesh).
            RenderTextureFormat format = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGHalf)
                ? RenderTextureFormat.RGHalf
                : RenderTextureFormat.ARGBHalf;

            var descriptor = new RenderTextureDescriptor(_resolution, _resolution, format, 0)
            {
                sRGB = false,
                useMipMap = false,
                autoGenerateMips = false,
                msaaSamples = 1,
            };

            var rt = new RenderTexture(descriptor)
            {
                name = name,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };

            return rt.Create() ? rt : null;
        }

        private void ReleaseTargets()
        {
            DestroyRuntimeObject(ref _accumFront);
            DestroyRuntimeObject(ref _accumBack);
            DestroyRuntimeObject(ref _dilated);
            _resolution = 0;
        }

        private static void DestroyRuntimeObject<T>(ref T obj) where T : UnityEngine.Object
        {
            if (obj == null)
                return;

            if (obj is RenderTexture rt)
                rt.Release();

            if (Application.isPlaying) UnityEngine.Object.Destroy(obj);
            else UnityEngine.Object.DestroyImmediate(obj);

            obj = null;
        }

        public void Dispose()
        {
            _commandBuffer?.Release();
            _commandBuffer = null;

            DestroyRuntimeObject(ref _material);
            ReleaseTargets();
        }

        /// <summary>Wipes accumulated wetness back to fully dry (teleports, respawns, cutscene cuts).</summary>
        public void Reset()
        {
            _needsClear = true;
        }

        // ------------------------------------------------------------------ //
        //  Frame update
        // ------------------------------------------------------------------ //
        /// <summary>
        /// Rasterises one frame of wetness accumulation. <paramref name="mesh"/> must be the
        /// mesh actually being rendered (the baked one for skinned sources) and
        /// <paramref name="objectToWorld"/> its matrix, so the world positions tested against
        /// the water volumes match the visible surface.
        /// </summary>
        public bool Update(
            Shader splatShader,
            int resolution,
            Mesh mesh,
            Matrix4x4 objectToWorld,
            Collider[] waterColliders,
            int waterColliderCount,
            GameObject owner,
            float defaultFeather,
            float wettingRate,
            float dryingRate,
            bool dryingEnabled,
            float deltaTime)
        {
            if (mesh == null || !EnsureResources(splatShader, resolution))
                return false;

            ActiveSourceCount = PackSources(waterColliders, waterColliderCount, owner, defaultFeather);

            _commandBuffer.Clear();

            // A freshly allocated target contains garbage; both accumulation buffers
            // have to start fully dry or the first frames splash random wetness.
            if (_needsClear)
            {
                _commandBuffer.SetRenderTarget(_accumFront);
                _commandBuffer.ClearRenderTarget(false, true, Color.clear);
                _needsClear = false;
            }

            // --- Pass 0: accumulate into the back buffer ---
            // Cleared every frame so texels outside the UV islands stay at coverage 0.
            // Without that, ping-ponging would leave them holding values from two
            // frames ago and the dilate pass would pull that stale data across seams.
            _accumulateProps.Clear();
            _accumulateProps.SetTexture(PrevWetnessId, _accumFront);
            _accumulateProps.SetMatrixArray(SourceToLocalId, _sourceToLocal);
            _accumulateProps.SetVectorArray(SourceShapeId, _sourceShape);
            _accumulateProps.SetVectorArray(SourceParamsId, _sourceParams);
            _accumulateProps.SetFloat(SourceCountId, ActiveSourceCount);
            _accumulateProps.SetFloat(WettingRateId, wettingRate);
            _accumulateProps.SetFloat(DryingRateId, dryingRate);
            _accumulateProps.SetFloat(DryingEnabledId, dryingEnabled ? 1f : 0f);
            _accumulateProps.SetFloat(WetDeltaTimeId, deltaTime);
            _accumulateProps.SetFloat(DetailUvId, UvChannel);

            _commandBuffer.SetRenderTarget(_accumBack);
            _commandBuffer.ClearRenderTarget(false, true, Color.clear);

            int subMeshCount = mesh.subMeshCount;
            for (int subMesh = 0; subMesh < subMeshCount; subMesh++)
            {
                _commandBuffer.DrawMesh(mesh, objectToWorld, _material, subMesh,
                    PassAccumulate, _accumulateProps);
            }

            // --- Pass 1: pad the UV islands by a texel ---
            _dilateProps.Clear();
            _dilateProps.SetTexture(PrevWetnessId, _accumBack);
            _dilateProps.SetVector(WetTargetSizeId, new Vector4(_resolution, _resolution,
                1f / _resolution, 1f / _resolution));

            _commandBuffer.SetRenderTarget(_dilated);
            _commandBuffer.DrawProcedural(Matrix4x4.identity, _material, PassDilate,
                MeshTopology.Triangles, 3, 1, _dilateProps);

            Graphics.ExecuteCommandBuffer(_commandBuffer);

            (_accumFront, _accumBack) = (_accumBack, _accumFront);

            return true;
        }

        // ------------------------------------------------------------------ //
        //  Collider -> shader primitive
        // ------------------------------------------------------------------ //
        private int PackSources(Collider[] colliders, int count, GameObject owner, float defaultFeather)
        {
            int packed = 0;

            for (int i = 0; i < count && packed < MaxSources; i++)
            {
                Collider col = colliders[i];
                if (col == null || !col.enabled)
                    continue;

                if (FurColliderPacker.IsSelf(col, owner))
                    continue;

                var volume = col.GetComponent<FurWaterVolume>();
                float strength = volume != null ? volume.Strength : 1f;
                float feather = volume != null ? volume.ResolveFeather(defaultFeather) : defaultFeather;

                if (strength <= 0f)
                    continue;

                Pack(col, packed, strength, Mathf.Max(feather, 1e-4f));
                packed++;
            }

            // Zero out the unused slots so a stale volume from a previous frame cannot
            // keep wetting the object after it is gone.
            for (int i = packed; i < MaxSources; i++)
            {
                _sourceToLocal[i] = Matrix4x4.identity;
                _sourceShape[i] = Vector4.zero;
                _sourceParams[i] = Vector4.zero;
            }

            return packed;
        }

        private void Pack(Collider col, int slot, float strength, float feather)
        {
            FurColliderPacker.Pack(col, out Matrix4x4 worldToLocal, out Vector4 shape, out float type);

            _sourceToLocal[slot] = worldToLocal;
            _sourceShape[slot] = shape;
            _sourceParams[slot] = new Vector4(type, strength, feather, 0f);
        }
    }
}
