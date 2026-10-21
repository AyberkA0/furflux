using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace FurRendering
{
    /// <summary>
    /// What a furred object asks the screen-space fuzz pass for, this frame, on this camera.
    ///
    /// The pass is full-screen and the coats are per-object, so something has to carry a
    /// number across that gap. A global would do it for one coat and silently let the last
    /// one written win for several; this at least merges them explicitly, and says how.
    ///
    /// The merge is a MAXIMUM, and that is the safe direction: the radius sets how far the
    /// haze reaches, so taking the largest means every coat in frame gets at least the
    /// halo it asked for. A denser coat sharing the screen with a shaggier one is haloed
    /// too generously rather than not at all, which is the failure you can see less.
    /// Exact whenever one coat is on screen, which is the case this is tuned for.
    /// </summary>
    internal static class FurScreenFuzzRequest
    {
        private static Camera _camera;
        private static int _frame = -1;
        private static float _radiusPixels;
        private static float _amount;

        /// <summary>Asks for the fuzz pass on this camera, merging with anything already asked.</summary>
        public static void Submit(Camera camera, float radiusPixels, float amount)
        {
            if (camera == null || amount <= 0f || radiusPixels <= 0f)
                return;

            // A new camera or a new frame starts the merge over. Without the stamp a coat
            // that stopped rendering - culled, disabled, switched back to geometric fuzz -
            // would leave its last request standing forever, and the haze would outlive
            // the coat that asked for it.
            if (_camera != camera || _frame != Time.frameCount)
            {
                _camera = camera;
                _frame = Time.frameCount;
                _radiusPixels = 0f;
                _amount = 0f;
            }

            _radiusPixels = Mathf.Max(_radiusPixels, radiusPixels);
            _amount = Mathf.Max(_amount, amount);
        }

        /// <summary>What this camera was asked for this frame, or false if nothing was.</summary>
        public static bool TryRead(Camera camera, out float radiusPixels, out float amount)
        {
            radiusPixels = _radiusPixels;
            amount = _amount;

            return camera != null && _camera == camera && _frame == Time.frameCount && amount > 0f;
        }
    }

    /// <summary>
    /// The screen-space fuzz layer: the coat's own drawn pixels, blurred, laid back down
    /// everywhere the coat itself is not.
    ///
    /// Why this exists next to the geometric fuzz layer rather than instead of it
    /// ------------------------------------------------------------------------
    /// The geometric layer draws the coat a second time - the same hairs, longer and much
    /// wider, blended instead of clipped. It is real geometry, so it is lit properly, it
    /// sorts properly against the rest of the frame, and it works with no renderer feature
    /// installed. It also costs a second coat, and what it softens is a set of substitute
    /// hairs rather than the ones you are looking at.
    ///
    /// This one blurs the 2D PROJECTION of the real hairs. The haze therefore carries
    /// their actual colours and their actual lighting - a coat lit warm on one side hazes
    /// warm on that side and cool on the other, which the geometric layer only approximates
    /// by lighting its substitutes the same way. It costs one small full-screen pass
    /// instead of a second coat, and it needs this feature on the renderer to exist at all.
    ///
    /// How the coat is told apart from everything else
    /// -----------------------------------------------
    /// A stencil bit, written by the coat's forward pass AFTER the strand clip. That
    /// ordering is the whole design:
    ///
    ///   * marked   = a hair. The composite skips these, so the sharp coat stays sharp.
    ///   * unmarked = background, other geometry, and - crucially - the SKIN showing
    ///     through the gaps between hairs, because the skin is drawn from a material copy
    ///     with the write mask cleared. So the haze fills the gaps, and the coat reads
    ///     denser without a hair being added.
    ///
    /// Where it runs
    /// -------------
    /// After the skybox and before transparents. Both halves of that matter: earlier and
    /// the halo would be painted over by the sky it is supposed to sit against; later and
    /// transparent objects drawn over the coat would be caught inside the mark and blurred
    /// as though they were fur.
    /// </summary>
    [DisallowMultipleRendererFeature("Fur Screen Fuzz")]
    public sealed class FurScreenFuzzFeature : ScriptableRendererFeature
    {
        [System.Serializable]
        public sealed class Settings
        {
            [Tooltip("The stencil bit the coat marks its pixels with. Must match the fur " +
                     "material's Fuzz Stencil Ref. Inside URP's user range (the low four " +
                     "bits) by default; move it only if the project already spends bit 3.")]
            public int stencilBit = 8;

            [Range(1, 4)]
            [Tooltip("How much the blur is downsampled. 2 is half resolution, which is " +
                     "invisible on a blur this wide and quarters its cost.")]
            public int downsample = 2;

            [Range(1f, 256f)]
            [Tooltip("Ceiling on the blur radius in screen pixels. A coat filling the " +
                     "screen can ask for a radius that costs more than it is worth; this " +
                     "is where that stops.")]
            public float maxRadiusPixels = 64f;
        }

        public Settings settings = new Settings();

        [SerializeField, HideInInspector]
        private Shader shader;

        private Material _material;
        private FuzzPass _pass;

        public override void Create()
        {
            if (shader == null)
                shader = Shader.Find("Hidden/FURFLUX/Screen Fuzz");

            _pass = new FuzzPass
            {
                // After the sky, before transparents - see the class note for why both
                // ends of that window are load-bearing.
                renderPassEvent = RenderPassEvent.AfterRenderingSkybox,
            };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (shader == null)
                return;

            if (_material == null)
            {
                _material = CoreUtils.CreateEngineMaterial(shader);
                if (_material == null)
                    return;
            }

            Camera camera = renderingData.cameraData.camera;

            // Nothing on this camera asked for the layer. Checked here rather than inside
            // the pass so a scene with no screen-space fuzz in it does not allocate the
            // render targets at all.
            if (!FurScreenFuzzRequest.TryRead(camera, out float radius, out float amount))
                return;

            if (camera.cameraType == CameraType.Preview || camera.cameraType == CameraType.Reflection)
                return;

            // MSAA would need the extract target to carry the same sample count as the
            // camera's depth attachment, and the blur that reads it afterwards cannot
            // sample a multisampled texture. Refused out loud rather than left to fail
            // inside render graph validation, where the message says nothing about fur.
            if (renderingData.cameraData.cameraTargetDescriptor.msaaSamples > 1)
            {
                WarnMsaaOnce();
                return;
            }

            _pass.Setup(_material, settings, radius, amount);
            renderer.EnqueuePass(_pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(_material);
            _material = null;
        }

        private static bool _msaaWarned;

        private static void WarnMsaaOnce()
        {
            if (_msaaWarned)
                return;

            _msaaWarned = true;
            Debug.LogWarning(
                "[FURFLUX] Screen-space fuzz is off because MSAA is enabled on the active " +
                "URP asset. The layer blurs the coat's own pixels, which it cannot read " +
                "back from a multisampled target. Either turn MSAA off and use " +
                "post-processing anti-aliasing, or set the fur material's Fuzz Mode back " +
                "to Geometric.");
        }

        // ----------------------------------------------------------------- //
        //  The pass
        // ----------------------------------------------------------------- //
        private sealed class FuzzPass : ScriptableRenderPass
        {
            private static readonly int FuzzParamsId    = Shader.PropertyToID("_FurFuzzParams");
            private static readonly int BlurStepId      = Shader.PropertyToID("_FurFuzzBlurStep");
            private static readonly int StencilRefId    = Shader.PropertyToID("_FuzzStencilRef");
            private static readonly int StencilMaskId   = Shader.PropertyToID("_FuzzStencilReadMask");

            private const int PassExtract   = 0;
            private const int PassBlur      = 1;
            private const int PassComposite = 2;

            // Taps per side in FurScreenFuzz.shader. The step between taps is the radius
            // divided by this, so the two have to agree or the kernel is the wrong width.
            private const float TapsPerSide = 7f;

            private Material _material;
            private Settings _settings;
            private float _radiusPixels;
            private float _amount;
            private Vector4 _fuzzParams;

            private class PassData
            {
                public TextureHandle source;
                public Material material;
                public int pass;
                public Vector2 blurStep;
                public Vector4 fuzzParams;
            }

            public void Setup(Material material, Settings settings, float radiusPixels, float amount)
            {
                _material = material;
                _settings = settings;
                _radiusPixels = radiusPixels;
                _amount = amount;

                requiresIntermediateTexture = true;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resourceData = frameData.Get<UniversalResourceData>();
                var cameraData = frameData.Get<UniversalCameraData>();

                if (resourceData.isActiveTargetBackBuffer)
                    return;

                RenderTextureDescriptor cameraDesc = cameraData.cameraTargetDescriptor;
                int width = Mathf.Max(1, cameraDesc.width);
                int height = Mathf.Max(1, cameraDesc.height);
                int scale = Mathf.Clamp(_settings.downsample, 1, 4);

                // A radius under a pixel or two is not a blur, it is a resample - and at
                // half resolution it aliases instead of softening. Floored rather than
                // skipped so a coat crossing the threshold does not pop.
                float radius = Mathf.Clamp(_radiusPixels, 2f, _settings.maxRadiusPixels);

                // Stencil ref and read mask are the exception that belongs on the
                // material: ShaderLab resolves render state from the material and a global
                // cannot reach it. Safe here only because both are the same for all four
                // blits - see the note in AddBlit for why anything that ISN'T has to go
                // through the command buffer instead.
                _material.SetFloat(StencilRefId, _settings.stencilBit);
                _material.SetFloat(StencilMaskId, _settings.stencilBit);

                // One step for both axes, and it is the same number for both passes: the
                // second reads a half-resolution source, so its radius in source pixels is
                // halved by exactly the factor its texel is widened by. They cancel.
                _fuzzParams = new Vector4(radius, _amount, 0f, 0f);

                float step = radius / TapsPerSide / width;
                float stepY = radius / TapsPerSide / height;

                // The camera's own format is NOT good enough, and inheriting it was a bug.
                //
                // This layer carries coverage in ALPHA - that is the whole mechanism: the
                // coat is extracted premultiplied, the blur spreads colour and coverage
                // together, and the composite blends by the coverage that comes back. A
                // camera target with no alpha channel cannot hold any of that, and it does
                // not fail loudly: sampling one returns alpha 1 everywhere, so every pixel
                // outside the coat reads as FULLY covered by a blur result that is black
                // there. The composite then paints the whole frame black, more of it the
                // higher Fuzz Amount goes, with only the hairs left standing.
                //
                // URP picks exactly such a format by default - B10G11R11_UFloatPack32,
                // which is what HDR at 32-bit precision resolves to, and it has no alpha.
                // Worse, whether it does depends on the camera and the pipeline settings,
                // so the bug hides in the scene view and appears in the game view.
                GraphicsFormat format = GraphicsFormatUtility.HasAlphaChannel(cameraDesc.graphicsFormat)
                    ? cameraDesc.graphicsFormat
                    : GraphicsFormat.R16G16B16A16_SFloat;

                int blurWidth = Mathf.Max(1, width / scale);
                int blurHeight = Mathf.Max(1, height / scale);

                TextureHandle extract = CreateTarget(renderGraph, width, height, format, "_FurFuzzExtract");
                TextureHandle blurA = CreateTarget(renderGraph, blurWidth, blurHeight, format, "_FurFuzzBlurA");
                TextureHandle blurB = CreateTarget(renderGraph, blurWidth, blurHeight, format, "_FurFuzzBlurB");

                // 1. Extract, stencil-limited to the coat. The camera's depth attachment
                //    comes along read-only because it is where the stencil lives.
                AddBlit(renderGraph, "Fur Fuzz Extract", resourceData.activeColorTexture, extract,
                        PassExtract, Vector2.zero, resourceData.activeDepthTexture);

                // 2. Blur, one axis each, downsampling on the way in.
                AddBlit(renderGraph, "Fur Fuzz Blur H", extract, blurA,
                        PassBlur, new Vector2(step, 0f), TextureHandle.nullHandle);
                AddBlit(renderGraph, "Fur Fuzz Blur V", blurA, blurB,
                        PassBlur, new Vector2(0f, stepY), TextureHandle.nullHandle);

                // 3. Composite, stencil-limited to everything the coat is NOT.
                AddBlit(renderGraph, "Fur Fuzz Composite", blurB, resourceData.activeColorTexture,
                        PassComposite, Vector2.zero, resourceData.activeDepthTexture);
            }

            /// <summary>
            /// A target for one stage of the layer, cleared to TRANSPARENT.
            ///
            /// Spelled out through TextureDesc because the convenience helper
            /// (UniversalRenderer.CreateRenderGraphTexture) takes a clear flag but no clear
            /// COLOUR, and the colour is load-bearing here. The extract pass is limited by
            /// the stencil to the coat's own pixels, so everywhere else keeps whatever the
            /// target was cleared to - and that value is what the halo fades out into. An
            /// opaque clear would make the whole frame read as covered.
            /// </summary>
            private static TextureHandle CreateTarget(RenderGraph renderGraph, int width, int height,
                                                      GraphicsFormat format, string name)
            {
                return renderGraph.CreateTexture(new TextureDesc(width, height)
                {
                    format = format,
                    clearBuffer = true,
                    clearColor = Color.clear,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    msaaSamples = MSAASamples.None,
                    useMipMap = false,
                    autoGenerateMips = false,
                    name = name,
                });
            }

            private void AddBlit(RenderGraph renderGraph, string name, TextureHandle source,
                                 TextureHandle target, int pass, Vector2 blurStep, TextureHandle depth)
            {
                using var builder = renderGraph.AddRasterRenderPass<PassData>(name, out PassData data);

                data.source = source;
                data.material = _material;
                data.pass = pass;
                data.blurStep = blurStep;
                data.fuzzParams = _fuzzParams;

                builder.UseTexture(source, AccessFlags.Read);
                builder.SetRenderAttachment(target, 0, AccessFlags.Write);

                // Read-only: the pass tests the stencil and must not disturb it. The coat's
                // marks have to survive the extract for the composite to still find them.
                if (depth.IsValid())
                    builder.SetRenderAttachmentDepth(depth, AccessFlags.Read);

                // Render graph forbids touching global shader state from a raster pass
                // unless the pass says it will, because a global set inside one pass
                // outlives it and the graph has to know that before it reorders anything.
                // This one does set globals, and has to - see the render function.
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc<PassData>(static (data, context) =>
                {
                    // Through the COMMAND BUFFER, not through the material, and the
                    // difference is not stylistic.
                    //
                    // All four blits share one material. A Material.SetVector lands on it
                    // the instant it is called, while the blits it was meant for are only
                    // recorded here and executed together afterwards - so every one of
                    // them would read whatever the LAST render function wrote. The two
                    // blur passes would run with the composite's step of zero, which is
                    // not a blur at all: the layer comes out as a pin-sharp copy of the
                    // coat, does not reach past the silhouette, and looks for all the
                    // world like the pass never ran.
                    //
                    // A SetGlobalVector is itself a command. It takes its place in the
                    // queue, so each blit sees the value recorded immediately before it.
                    context.cmd.SetGlobalVector(BlurStepId, data.blurStep);
                    context.cmd.SetGlobalVector(FuzzParamsId, data.fuzzParams);

                    Blitter.BlitTexture(context.cmd, data.source, new Vector4(1f, 1f, 0f, 0f),
                                        data.material, data.pass);
                });
            }
        }
    }
}
