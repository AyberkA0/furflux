using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace FurRendering
{
    /// <summary>
    /// A per-object fur detail map: an authored texture resampled into the mesh's own
    /// UV0 layout, at a resolution this object chooses.
    ///
    /// Why this is not just "sample the authored texture in the fur shader"
    /// ---------------------------------------------------------------------
    /// Because the resolution has to belong to the OBJECT rather than to the asset.
    /// Ten squirrels sharing one authored length map should not be forced to share
    /// its memory footprint too - the hero one in front of the camera wants 512, the
    /// ones scattered across the far hillside want 64, and neither should require a
    /// second copy of the source texture on disk.
    ///
    /// Two further things fall out of owning a RenderTexture rather than reading the
    /// source directly:
    ///  * Gameplay can WRITE into it. Shave a patch, scorch it, dye it - the fur
    ///    picks the change up on the next frame and the source asset is untouched.
    ///  * UV seams are padded exactly once, at bake time, instead of every fur pass
    ///    having to cope with an unpadded texture.
    ///
    /// Deliberately NOT a per-frame system, and that is the whole difference from
    /// <see cref="FurWetnessMap"/>. Wetness is a simulation: it integrates contact
    /// over time, so it has to re-rasterise every frame. A detail map is authored
    /// data, so it re-rasterises only when something it actually depends on changes -
    /// the source texture, the resolution, the mesh, or an explicit
    /// <see cref="MarkDirty"/> from gameplay. A still object costs zero.
    ///
    /// Requirements on the mesh are the same as for the wetness map: UV0 must be a
    /// real unwrap. Overlapping or mirrored islands share texels, so one side of the
    /// model inherits the other's fur length - a property of the asset, not something
    /// this can work around.
    /// </summary>
    internal sealed class FurAttributeMap : IDisposable
    {
        private const int PassBake   = 0;
        private const int PassDilate = 1;
        private const int PassPaint  = 2;

        /// <summary>Bounds on the target size. Below 32 a map cannot describe a region;
        /// above 2048 it is larger than the authored source almost always is.</summary>
        public const int MinResolution = 32;
        public const int MaxResolution = 2048;

        private static readonly int SourceId     = Shader.PropertyToID("_FurAttributeSource");
        private static readonly int PrevId       = Shader.PropertyToID("_FurAttributePrev");
        private static readonly int TargetSizeId = Shader.PropertyToID("_FurAttributeTargetSize");
        private static readonly int NeutralId    = Shader.PropertyToID("_FurAttributeNeutral");
        private static readonly int HasSourceId  = Shader.PropertyToID("_FurAttributeHasSource");
        private static readonly int SourceSTId   = Shader.PropertyToID("_FurAttributeSourceST");
        private static readonly int BrushCenterId = Shader.PropertyToID("_FurBrushCenter");
        private static readonly int BrushDirId    = Shader.PropertyToID("_FurBrushDir");
        private static readonly int BrushValueId  = Shader.PropertyToID("_FurBrushValue");
        private static readonly int BrushParamsId = Shader.PropertyToID("_FurBrushParams");
        private static readonly int BrushDepthId  = Shader.PropertyToID("_FurBrushDepth");
        private static readonly int BrushViewProjId = Shader.PropertyToID("_FurBrushViewProj");
        private static readonly int BrushDepthSizeId = Shader.PropertyToID("_FurBrushDepthSize");
        private static readonly int DetailUvId       = Shader.PropertyToID("_FurDetailUV");
        private static readonly int BrushCombId      = Shader.PropertyToID("_FurBrushComb");

        private readonly MaterialPropertyBlock _bakeProps = new MaterialPropertyBlock();
        private readonly MaterialPropertyBlock _dilateProps = new MaterialPropertyBlock();
        private readonly MaterialPropertyBlock _paintProps = new MaterialPropertyBlock();
        private readonly string _name;

        private CommandBuffer _commandBuffer;
        private Material _material;
        private RenderTexture _target;

        // What the current contents of _target were produced from. Comparing against
        // these is what turns "bake every frame" into "bake when it matters".
        private int _resolution;
        private Texture _bakedSource;
        private Vector4 _bakedSourceST = new Vector4(1f, 1f, 0f, 0f);
        private Mesh _bakedMesh;
        private Color _bakedNeutral = Color.white;
        private int _uvChannel;
        private int _bakedUvChannel = -1;
        private bool _dirty = true;
        private bool _externallyEdited;

        public FurAttributeMap(string name)
        {
            _name = name;
        }

        /// <summary>
        /// Which UV set this map is stored in: 0 = UV0, 1 = UV1 (mesh.uv2).
        ///
        /// It decides the LAYOUT of the stored texels, so changing it invalidates the
        /// contents completely - including brush work, which normally survives everything
        /// short of an explicit <see cref="MarkDirty"/>. There is no way to keep it: the
        /// strokes were resolved against a different arrangement of the surface, and a
        /// texel that meant one place under UV0 means another under UV1.
        ///
        /// Set it before painting, not after.
        /// </summary>
        public int UvChannel
        {
            get => _uvChannel;
            set
            {
                int clamped = Mathf.Clamp(value, 0, 1);
                if (_uvChannel == clamped)
                    return;

                _uvChannel = clamped;
                MarkDirty();
            }
        }

        /// <summary>The baked map to bind to the fur material, or null before the first bake.</summary>
        public RenderTexture Texture => _target;

        /// <summary>True once a bake has produced usable contents.</summary>
        public bool IsBaked => _target != null && _target.IsCreated() && !_dirty;

        /// <summary>
        /// How many times this map has actually been rasterised - for debugging.
        ///
        /// The number that matters is that it STOPS CLIMBING. This map is meant to bake
        /// when something changes and never otherwise, so a count that rises with the
        /// frame counter means something upstream is handing back a different texture,
        /// mesh or resolution every frame and the whole point of the class has been lost.
        /// </summary>
        public int BakeCount { get; private set; }

        /// <summary>How many brush stamps have landed in this map - for debugging.</summary>
        public int PaintCount { get; private set; }

        /// <summary>
        /// Forces a re-bake on the next update. This is the hook gameplay uses: change
        /// the source texture, or write into <see cref="Texture"/> yourself and then
        /// leave it alone, or call this after editing whatever feeds the map.
        /// </summary>
        public void MarkDirty()
        {
            _dirty = true;

            // A rebake means "go back to what the source says", so it necessarily
            // discards brush work. Clearing the flag here rather than making callers
            // remember to is what keeps the two ideas from drifting apart: there is
            // exactly one way to throw paint away, and it is this.
            _externallyEdited = false;
        }

        /// <summary>
        /// Declares that something has drawn into <see cref="Texture"/> directly - a
        /// brush stroke, a scorch mark, a dye splash - so the contents no longer come
        /// from the source texture.
        ///
        /// This suspends the automatic rebake. Without it the very next frame would
        /// notice nothing had changed, decide the map was fine, and then the first
        /// unrelated change - a tiling tweak, an Inspector touch - would quietly wipe
        /// the work. Call <see cref="MarkDirty"/> to deliberately go back to the source.
        /// </summary>
        public void MarkExternallyEdited()
        {
            _externallyEdited = true;
            _dirty = false;
        }

        /// <summary>
        /// True when the map holds strokes that are not in its source texture, and that
        /// nothing but this RenderTexture is currently keeping. Editor tools use it to
        /// warn before the surface goes away.
        /// </summary>
        public bool HasUnsavedEdits => _externallyEdited;

        // ------------------------------------------------------------------ //
        //  Snapshots - what an undo step is made of
        // ------------------------------------------------------------------ //
        /// <summary>
        /// A copy of the map's current contents, on a surface the CALLER owns and must
        /// release.
        ///
        /// This is how painting gets an undo history. A stroke is hundreds of blend
        /// operations against a render texture and none of them is invertible, so the
        /// only way back to what was there before is to have kept it - and keeping it
        /// on the GPU, in the map's own format, means a step costs one copy rather than
        /// a readback.
        ///
        /// Returns null before the first bake, which is the honest answer: there is
        /// nothing yet for a stroke to have covered up.
        /// </summary>
        public RenderTexture CreateSnapshot()
        {
            if (_target == null || !_target.IsCreated())
                return null;

            var snapshot = new RenderTexture(Descriptor())
            {
                name = _name + "_Undo",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };

            if (!snapshot.Create())
            {
                DestroyRuntimeObject(ref snapshot);
                return null;
            }

            Graphics.CopyTexture(_target, snapshot);
            return snapshot;
        }

        /// <summary>
        /// Puts a snapshot back, and reports whether it fitted.
        ///
        /// Size and format have to match, and a mismatch is not an error worth throwing:
        /// the resolution or the UV channel can legitimately change while a history is
        /// held, and the honest response is that those steps no longer describe this map.
        /// The caller drops them.
        ///
        /// Marked as an external edit on the way back in, exactly as a stroke is. An
        /// undone map is still not what its source texture says, so it must go on being
        /// held against the automatic rebake - otherwise undoing to an empty map would
        /// let the next frame decide the map was untouched and bake over the redo.
        /// </summary>
        public bool RestoreSnapshot(RenderTexture snapshot)
        {
            if (snapshot == null || !snapshot.IsCreated()
                || _target == null || !_target.IsCreated())
                return false;

            if (snapshot.width != _target.width || snapshot.height != _target.height
                || snapshot.format != _target.format)
                return false;

            Graphics.CopyTexture(snapshot, _target);
            MarkExternallyEdited();
            return true;
        }

        /// <summary>How much memory one snapshot of this map costs, for a history budget.</summary>
        public long SnapshotByteSize
        {
            get
            {
                if (_target == null)
                    return 0;

                // ARGBHalf is four 16-bit channels; the 8-bit fallback is four bytes. See
                // Descriptor for why the format is what it is.
                long bytesPerTexel = _target.format == RenderTextureFormat.ARGBHalf ? 8 : 4;
                return (long)_target.width * _target.height * bytesPerTexel;
            }
        }

        // ------------------------------------------------------------------ //
        //  Frame update
        // ------------------------------------------------------------------ //
        /// <summary>
        /// Rebakes the map if anything it depends on has changed, and returns whether a
        /// usable texture now exists. Cheap to call every frame - the common path is a
        /// handful of reference comparisons and an early return.
        /// </summary>
        /// <param name="mesh">
        /// The mesh whose UV0 layout defines the map. For a skinned source this is the
        /// baked mesh, but only its UVs are read, and those do not deform - so skinning
        /// alone never triggers a rebake.
        /// </param>
        public bool Update(Shader bakeShader, int resolution, Mesh mesh, Texture source, Color neutral,
                           Vector4 sourceST)
        {
            if (bakeShader == null || mesh == null)
                return false;

            // No source is a legitimate state, not a missing one: it is where a map
            // that is going to be PAINTED starts. It bakes the channel's own neutral
            // value, which for the two scalar maps is MID-GREY rather than white - the
            // encoding has to leave room above neutral for a map that lengthens fur as
            // well as one that shortens it.
            resolution = Mathf.Clamp(Mathf.NextPowerOfTwo(resolution), MinResolution, MaxResolution);

            // A RenderTexture's contents do not survive a graphics device reset, and
            // IsCreated() is what reports that - so this check is not redundant with
            // the null test, it is the recovery path for a lost surface.
            //
            // These are separated from the source checks below because they are the
            // ones brush work cannot survive either way: the surface itself is gone or
            // is about to be reallocated at a different size.
            // The UV channel belongs in THIS group rather than with the source checks
            // below, and that placement is the whole of it: the checks below are skipped
            // for a painted map so a stroke survives them, while a channel change makes
            // the existing texels mean the wrong places. Salvaging paint across it is not
            // possible, so it has to rebake like a lost surface.
            bool surfaceLost = _target == null
                            || !_target.IsCreated()
                            || _resolution != resolution
                            || _bakedUvChannel != _uvChannel
                            || !ReferenceEquals(_bakedMesh, mesh);

            if (!surfaceLost)
            {
                // Painted maps hold their contents against every reason to rebake
                // except an explicit MarkDirty, which is what makes a brush stroke
                // survive the next Inspector touch.
                if (_externallyEdited)
                    return true;

                bool sourceChanged = _bakedSourceST != sourceST
                                  || !ReferenceEquals(_bakedSource, source)
                                  || _bakedNeutral != neutral;

                if (!_dirty && !sourceChanged)
                    return true;
            }

            if (!EnsureResources(bakeShader, resolution))
                return false;

            Bake(mesh, source, neutral, sourceST);

            _bakedSource = source;
            _bakedSourceST = sourceST;
            _bakedMesh = mesh;
            _bakedNeutral = neutral;
            _bakedUvChannel = _uvChannel;
            _dirty = false;
            _externallyEdited = false;

            return true;
        }

        // ------------------------------------------------------------------ //
        //  Painting
        // ------------------------------------------------------------------ //
        /// <summary>
        /// Stamps one brush mark into the map and returns whether it landed.
        ///
        /// The brush is a SPHERE IN WORLD SPACE. The pass rasterises the mesh into UV
        /// space and each texel tests its own world position against that sphere, so a
        /// stroke is the same physical size everywhere on the model and crosses UV
        /// seams without a join - neither of which a stamp of fixed UV radius can do,
        /// because UV density is never uniform and every island has an edge.
        ///
        /// Call <see cref="FinishStroke"/> when the stroke ends.
        /// </summary>
        /// <param name="objectToWorld">
        /// The matrix the mesh is actually drawn with. Unlike the bake, which throws
        /// the transform away, this pass needs real world positions to measure against
        /// the brush.
        /// </param>
        /// <param name="comb">
        /// True when <paramref name="value"/> is a world-space DIRECTION to lay the hair
        /// along rather than a value to write. Each texel then encodes it into its own
        /// tangent frame - see the Paint pass.
        /// </param>
        public bool Paint(Mesh mesh, Matrix4x4 objectToWorld, FurBrush brush, Color value,
            RenderTexture occlusionDepth, Matrix4x4 occlusionViewProjection, bool comb = false)
        {
            if (mesh == null || _material == null || _target == null || !_target.IsCreated())
                return false;

            if (brush.Radius <= 0f || brush.Strength <= 0f)
                return false;

            _commandBuffer.Clear();

            _paintProps.Clear();
            _paintProps.SetVector(BrushCenterId,
                new Vector4(brush.PositionWS.x, brush.PositionWS.y, brush.PositionWS.z, brush.Radius));
            bool solid = brush.Mode == FurPaintMode.Solid;
            _paintProps.SetVector(BrushDirId, new Vector4(
                brush.DirectionWS.x, brush.DirectionWS.y, brush.DirectionWS.z,
                solid ? 1f : 0f));
            _paintProps.SetVector(BrushValueId, value);

            // The occlusion test only runs when a capture actually exists. Solid mode
            // without one still rejects back faces, which is the useful half of it, and
            // is what a runtime caller that never captured depth will get.
            bool hasOcclusion = solid && occlusionDepth != null && occlusionDepth.IsCreated();
            _paintProps.SetVector(BrushParamsId, new Vector4(
                Mathf.Clamp01(brush.Strength), Mathf.Clamp01(brush.Hardness),
                hasOcclusion ? 1f : 0f, 0f));

            if (hasOcclusion)
            {
                _paintProps.SetTexture(BrushDepthId, occlusionDepth);
                _paintProps.SetMatrix(BrushViewProjId, occlusionViewProjection);
                _paintProps.SetVector(BrushDepthSizeId, new Vector4(
                    occlusionDepth.width, occlusionDepth.height,
                    1f / occlusionDepth.width, 1f / occlusionDepth.height));
            }

            // The same set the bake wrote into. A stroke that rasterised into a different
            // layout than the map is stored in would land in unrelated texels.
            _paintProps.SetFloat(DetailUvId, _uvChannel);
            _paintProps.SetFloat(BrushCombId, comb ? 1f : 0f);

            _commandBuffer.SetRenderTarget(_target);

            int subMeshCount = mesh.subMeshCount;
            for (int subMesh = 0; subMesh < subMeshCount; subMesh++)
            {
                _commandBuffer.DrawMesh(mesh, objectToWorld, _material, subMesh,
                    PassPaint, _paintProps);
            }

            Graphics.ExecuteCommandBuffer(_commandBuffer);

            MarkExternallyEdited();
            PaintCount++;

            return true;
        }

        /// <summary>
        /// Re-pads the UV islands after painting. Call once when a stroke ends.
        ///
        /// The paint pass rasterises the mesh, so it only reaches texels that are
        /// really on the surface - the padding ring outside each island keeps whatever
        /// the last bake put there. Left alone that shows up as a hairline of the old
        /// colour around every seam, which is precisely where a seam is most visible.
        ///
        /// Deferred to the end of the stroke rather than run per stamp because it is a
        /// full copy plus a full-screen pass, and a stroke is dozens of stamps a second.
        /// </summary>
        public void FinishStroke()
        {
            if (_material == null || _target == null || !_target.IsCreated())
                return;

            _commandBuffer.Clear();

            // Dilate cannot read and write the same surface, so the current map is
            // copied aside and the padding is rebuilt from the copy.
            RenderTexture scratch = RenderTexture.GetTemporary(Descriptor());
            scratch.filterMode = FilterMode.Point;

            _commandBuffer.CopyTexture(_target, scratch);

            _dilateProps.Clear();
            _dilateProps.SetTexture(PrevId, scratch);
            _dilateProps.SetVector(TargetSizeId, new Vector4(_resolution, _resolution,
                1f / _resolution, 1f / _resolution));

            _commandBuffer.SetRenderTarget(_target);
            _commandBuffer.DrawProcedural(Matrix4x4.identity, _material, PassDilate,
                MeshTopology.Triangles, 3, 1, _dilateProps);

            Graphics.ExecuteCommandBuffer(_commandBuffer);

            RenderTexture.ReleaseTemporary(scratch);
        }

        private void Bake(Mesh mesh, Texture source, Color neutral, Vector4 sourceST)
        {
            _commandBuffer.Clear();

            // The bake target is scratch: it exists only to carry the coverage mask
            // from pass 0 into pass 1. Keeping it around permanently would double this
            // map's memory for a surface that is read once and never again.
            RenderTexture scratch = RenderTexture.GetTemporary(Descriptor());
            scratch.filterMode = FilterMode.Point;

            // --- Pass 0: rasterise the mesh into UV space ---
            // Cleared first so texels outside the UV islands hold coverage 0 rather
            // than whatever the pooled surface last contained; the dilate below reads
            // that coverage to decide what is real.
            _bakeProps.Clear();
            // A texture is always bound even when there is no source: an unbound sampler
            // falls back to Unity's white, and the pass would then be relying on that
            // fallback rather than on the neutral it was told to use.
            _bakeProps.SetTexture(SourceId, source != null ? source : Texture2D.whiteTexture);
            _bakeProps.SetVector(NeutralId, neutral);
            _bakeProps.SetFloat(HasSourceId, source != null ? 1f : 0f);
            _bakeProps.SetVector(SourceSTId, sourceST);
            _bakeProps.SetFloat(DetailUvId, _uvChannel);

            _commandBuffer.SetRenderTarget(scratch);
            _commandBuffer.ClearRenderTarget(false, true, Color.clear);

            int subMeshCount = mesh.subMeshCount;
            for (int subMesh = 0; subMesh < subMeshCount; subMesh++)
            {
                // Identity matrix: the vertex stage throws the object-space position
                // away and uses UV0 as the clip position, so no transform is involved.
                _commandBuffer.DrawMesh(mesh, Matrix4x4.identity, _material, subMesh,
                    PassBake, _bakeProps);
            }

            // --- Pass 1: pad the UV islands by a texel ---
            _dilateProps.Clear();
            _dilateProps.SetTexture(PrevId, scratch);
            _dilateProps.SetVector(TargetSizeId, new Vector4(_resolution, _resolution,
                1f / _resolution, 1f / _resolution));

            _commandBuffer.SetRenderTarget(_target);
            _commandBuffer.DrawProcedural(Matrix4x4.identity, _material, PassDilate,
                MeshTopology.Triangles, 3, 1, _dilateProps);

            Graphics.ExecuteCommandBuffer(_commandBuffer);

            RenderTexture.ReleaseTemporary(scratch);

            BakeCount++;
        }

        // ------------------------------------------------------------------ //
        //  Resources
        // ------------------------------------------------------------------ //
        private bool EnsureResources(Shader bakeShader, int resolution)
        {
            if (_material == null || _material.shader != bakeShader)
            {
                DestroyRuntimeObject(ref _material);
                _material = new Material(bakeShader) { hideFlags = HideFlags.HideAndDontSave };
            }

            _commandBuffer ??= new CommandBuffer { name = "Fur Attribute Bake" };

            if (_resolution == resolution && _target != null && _target.IsCreated())
                return true;

            DestroyRuntimeObject(ref _target);
            _resolution = resolution;

            _target = new RenderTexture(Descriptor())
            {
                name = _name,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };

            if (!_target.Create())
            {
                DestroyRuntimeObject(ref _target);
                _resolution = 0;
                return false;
            }

            return true;
        }

        private RenderTextureDescriptor Descriptor()
        {
            // ARGBHalf rather than ARGB32, for the colour map's sake: an 8-bit target
            // is a LINEAR 8 bits here (sRGB is off, because the scalar maps store a
            // multiplier and not a colour), and linear 8-bit banding is plainly
            // visible in the darks. One format for all three maps keeps the bake and
            // dilate passes single-purpose - the alternative was a channel layout that
            // differed per map, which the dilate would then have to branch on.
            RenderTextureFormat format = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf)
                ? RenderTextureFormat.ARGBHalf
                : RenderTextureFormat.ARGB32;

            return new RenderTextureDescriptor(_resolution, _resolution, format, 0)
            {
                sRGB = false,
                useMipMap = false,
                autoGenerateMips = false,
                msaaSamples = 1,
            };
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
            DestroyRuntimeObject(ref _target);

            _resolution = 0;
            _bakedSource = null;
            _bakedSourceST = new Vector4(1f, 1f, 0f, 0f);
            _bakedMesh = null;
            _dirty = true;
            _externallyEdited = false;
        }
    }
}
