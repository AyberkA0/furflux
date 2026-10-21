using UnityEngine;
using UnityEngine.Rendering;

namespace FurRendering
{
    /// <summary>
    /// Global defaults for everything the fur shader expects a Fur Controller to supply.
    ///
    /// Why this exists
    /// ---------------
    /// The shader's per-object values deliberately sit OUTSIDE the UnityPerMaterial
    /// constant buffer, so a property block can override them per draw and a single
    /// material can serve any number of objects (see FurShellInput.hlsl). The cost of
    /// that is what an unset one reads as: zero.
    ///
    /// Zero is not a neutral value for most of them. _LodFade at zero means fur of no
    /// length - so a material with no controller behind it renders bare skin, which is
    /// what a material preview, an asset thumbnail, a shader warm-up and a MeshRenderer
    /// somebody pointed at the fur material by hand all are. _FurCountFraction at zero
    /// feeds a reciprocal square root and comes back as a strand thirty times too wide.
    /// The two structured buffers are worse still: D3D12 skips a draw outright when a
    /// declared SRV is unbound, so those draws rendered nothing at all and logged an
    /// error per frame.
    ///
    /// A GLOBAL binding is exactly the right shape for that. It is the lowest-priority
    /// source Unity consults, so it never competes with the per-draw values a controller
    /// supplies - it only fills the gap where nothing else did.
    /// </summary>
    internal static class FurShaderDefaults
    {
        private static GraphicsBuffer _roots;
        private static GraphicsBuffer _skinMatrices;

        private static readonly int RootsId = Shader.PropertyToID("_FurRoots");
        private static readonly int SkinMatricesId = Shader.PropertyToID("_FurSkinMatrices");

        /// <summary>
        /// Binds the defaults if they are not already bound. Cheap enough to call from
        /// anywhere; the work happens once per domain.
        /// </summary>
        public static void Ensure()
        {
            if (_roots != null && _roots.IsValid() && _skinMatrices != null && _skinMatrices.IsValid())
                return;

            Release();
            BindBuffers();
            BindValues();
        }

        /// <summary>
        /// One element each, and their contents do not matter. Any draw that reaches them
        /// is a draw taking the SKIN path (_FurGeometryMode defaults to 0), which reads
        /// neither - they exist to satisfy the shader signature, not to be read.
        /// </summary>
        private static void BindBuffers()
        {
            _roots = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, FurRootData.Stride);
            _roots.SetData(new FurRootData[1]);

            _skinMatrices = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, FurSkinMatrix.Stride);
            _skinMatrices.SetData(new[]
            {
                new FurSkinMatrix
                {
                    Row0 = new Vector4(1f, 0f, 0f, 0f),
                    Row1 = new Vector4(0f, 1f, 0f, 0f),
                    Row2 = new Vector4(0f, 0f, 1f, 0f),
                },
            });

            Shader.SetGlobalBuffer(RootsId, _roots);
            Shader.SetGlobalBuffer(SkinMatricesId, _skinMatrices);
        }

        /// <summary>
        /// The value each per-object constant means when nobody is driving it: a single
        /// shell of full-length, dry, uncollided, unlit-by-any-bone fur on an object with
        /// no LOD applied. That is what a material preview should show.
        /// </summary>
        private static void BindValues()
        {
            Shader.SetGlobalFloat("_ShellCount", 1f);
            Shader.SetGlobalFloat("_LodFade", 1f);
            Shader.SetGlobalFloat("_FurAggregation", 0f);
            Shader.SetGlobalFloat("_FurCountFraction", 1f);
            Shader.SetGlobalFloat("_FurMetersPerUV", 1f);
            Shader.SetGlobalFloat("_FurMetersPerUVMax", 64f);

            Shader.SetGlobalFloat("_FurGeometryMode", 0f);
            Shader.SetGlobalFloat("_FurDrawSkin", 1f);
            Shader.SetGlobalFloat("_FurFuzzMode", 0f);
            Shader.SetGlobalFloat("_FurInstanceCount", 1f);
            Shader.SetGlobalFloat("_FurRootsPerBatch", FurStrandDistribution.RootsPerBatch);
            Shader.SetGlobalFloat("_FurSkinned", 0f);

            Shader.SetGlobalVector("_ExternalForce", Vector4.zero);
            Shader.SetGlobalVector("_DragForce", Vector4.zero);
            Shader.SetGlobalVector("_AngularForce", Vector4.zero);
            Shader.SetGlobalVector("_RotationCenterWS", Vector4.zero);
            Shader.SetGlobalFloat("_BoneMotionCount", 0f);

            Shader.SetGlobalFloat("_Wetness", 0f);
            Shader.SetGlobalFloat("_WetnessEnabled", 0f);

            Shader.SetGlobalFloat("_FurLengthMapEnabled", 0f);
            Shader.SetGlobalFloat("_FurThicknessMapEnabled", 0f);
            Shader.SetGlobalFloat("_FurColorMapEnabled", 0f);
            Shader.SetGlobalVector("_FurColorMapTint", new Vector4(1f, 1f, 1f, 0f));

            Shader.SetGlobalFloat("_FurColliderCount", 0f);
            Shader.SetGlobalFloat("_CollisionStrength", 0f);
            Shader.SetGlobalFloat("_CollisionSpread", 0f);

            Shader.SetGlobalMatrix("_FurObjectToWorld", Matrix4x4.identity);
            Shader.SetGlobalMatrix("_FurWorldToObject", Matrix4x4.identity);
            Shader.SetGlobalMatrix("_FurPrevObjectToWorld", Matrix4x4.identity);
        }

        public static void Release()
        {
            _roots?.Release();
            _roots = null;

            _skinMatrices?.Release();
            _skinMatrices = null;
        }

        // A player binds them before the first scene loads, because the first frame can
        // already contain a furred object.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void BindOnLoad() => Ensure();

#if UNITY_EDITOR
        // The editor binds them on every domain reload, and lets go of the native memory
        // before the next one - a GraphicsBuffer is not a UnityEngine.Object, so nothing
        // else would.
        [UnityEditor.InitializeOnLoadMethod]
        private static void BindInEditor()
        {
            Ensure();
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= Release;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Release;
        }
#endif
    }
}
