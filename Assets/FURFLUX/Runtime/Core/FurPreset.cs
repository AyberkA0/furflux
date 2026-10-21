using UnityEngine;

namespace FurRendering
{
    /// <summary>
    /// Which built-in preset a Fur Controller is pointed at.
    ///
    /// One per fur type, because a preset that spans both would be answering two
    /// different questions with one set of numbers: the same coat costs its budget in
    /// LAYERS on one type and in HAIRS on the other, and the values that make a shell
    /// coat read - a high shell count, a low density, a wide strand - are not values a
    /// ribbon coat has any use for.
    /// </summary>
    public enum FurPresetId
    {
        /// <summary>Nothing selected. Apply does nothing.</summary>
        None = 0,

        /// <summary>The shipped shell coat.</summary>
        ShellsMain = 1,

        /// <summary>The shipped strand coat.</summary>
        StrandsMain = 2,
    }

    /// <summary>
    /// A complete fur look, as the numbers rather than as an asset.
    ///
    /// WHAT A PRESET IS FOR
    /// --------------------
    /// A coat is authored across two objects - a Material for what the fur looks like
    /// and a Fur Controller for how this object spends its budget on it - and the two
    /// only make sense together. A shell material tuned for 64 layers looks wrong at 8,
    /// and a strand material tuned for 190k hairs looks bald at 20k. Handing somebody a
    /// material alone therefore hands them half a coat, and the half they cannot see is
    /// the half that decides whether the other half works.
    ///
    /// So a preset writes BOTH, in one action, and is the only thing in this package
    /// that does.
    ///
    /// WHAT IT DELIBERATELY DOES NOT WRITE
    /// -----------------------------------
    /// Everything that belongs to the OBJECT rather than to the look, because a preset
    /// cannot know any of it and overwriting it would destroy work:
    ///
    ///   - the Fur Material reference itself. A preset writes THROUGH the material the
    ///     controller already points at; it never swaps one in. Applying to a shared
    ///     material changes every object using it, which is the one genuinely surprising
    ///     thing here - the inspector says so before you press the button.
    ///   - the painted detail maps (length, thickness, colour, comb). These are strokes
    ///     somebody made on this specific mesh's UVs. Both shipped presets happen to
    ///     have none, so writing them would silently erase a painted coat.
    ///   - inertia bones, and the water and collision LayerMasks. Scene and project
    ///     specific: a layer index means a different layer in every project.
    ///   - the band gradient, and the two hidden shader references.
    ///
    /// The values here were captured from the two demo objects that ship with the
    /// package, which is what makes them worth having: they are a coat somebody looked
    /// at and kept, not a table of defaults.
    /// </summary>
    public sealed class FurPreset
    {
        // ------------------------------------------------------------------ //
        //  Material side
        // ------------------------------------------------------------------ //
        /// <summary>Name/value pairs written to the material, guarded by HasProperty.</summary>
        public (string Name, float Value)[] Floats = new (string, float)[0];
        public (string Name, Color Value)[] Colors = new (string, Color)[0];
        public (string Name, Vector4 Value)[] Vectors = new (string, Vector4)[0];

        /// <summary>True for the strand type. Drives _FURTYPE_STRANDS alongside _FurType.</summary>
        public bool StrandsKeyword;

        // ------------------------------------------------------------------ //
        //  Controller side
        // ------------------------------------------------------------------ //
        public FurController.FurShadowMode ShadowMode;
        public bool ReceiveShadows;

        public bool EnableLod;
        public int ShellCount;
        public Keyframe[] ShellCountByDistance;
        public float HardCullDistance;

        public int MaxStrandCount;
        public int RibbonSegments;
        public Keyframe[] RibbonDensityByDistance;
        public int RibbonSeed;

        public bool FollowBlendShapes;
        public bool MotionInertia;
        public float DragStrength;
        public float AccelerationResponse;
        public float InertiaDamping;
        public float InertiaDampingRatio;
        public FurController.FurInertiaMode InertiaMode;
        public float BoneInfluenceRadius;
        public float AngularInertiaScale;

        public float Wetness;
        public bool EnableWetnessMap;
        public int WetnessMapResolution;
        public bool EnableDrying;
        public float WettingRate;
        public float DryingRate;
        public float ContactFeather;

        public Vector4 ColorMapScaleOffset;
        public Color ColorMapFill;
        public FurDetailUvSet DetailUvSet;
        public int DetailMapResolution;
        public Color ColorMapTint;
        public float ColorMapStrength;
        public bool UseBandGradient;

        public bool EnableCollision;
        public float CollisionStrength;
        public float CollisionMargin;
        public float CollisionSpread;
        public int MaxSimultaneousColliders;

        /// <summary>Human-readable name, for the inspector and for undo entries.</summary>
        public string DisplayName;

        /// <summary>
        /// Writes the material half of this preset.
        ///
        /// Every write is guarded by HasProperty rather than assumed, so a preset stays
        /// applicable to a material whose shader has since dropped a property - which has
        /// already happened once to this package and cost a console full of warnings.
        /// </summary>
        public void ApplyTo(Material material)
        {
            if (material == null)
                return;

            foreach (var (name, value) in Floats)
                if (material.HasProperty(name))
                    material.SetFloat(name, value);

            foreach (var (name, value) in Colors)
                if (material.HasProperty(name))
                    material.SetColor(name, value);

            foreach (var (name, value) in Vectors)
                if (material.HasProperty(name))
                    material.SetVector(name, value);

            // The type is a float property AND a shader keyword, and they have to move
            // together: the float decides what the inspector greys out, the keyword
            // decides which variant is compiled. Out of step, a material claims to be one
            // type and renders as the other.
            if (StrandsKeyword)
                material.EnableKeyword(FurTypeKeyword);
            else
                material.DisableKeyword(FurTypeKeyword);
        }

        private const string FurTypeKeyword = "_FURTYPE_STRANDS";

        // ------------------------------------------------------------------ //
        //  The shipped presets
        // ------------------------------------------------------------------ //
        /// <summary>The preset for an id, or null for None / anything unrecognised.</summary>
        public static FurPreset Get(FurPresetId id)
        {
            switch (id)
            {
                case FurPresetId.ShellsMain:  return ShellsMain;
                case FurPresetId.StrandsMain: return StrandsMain;
                default:                      return null;
            }
        }

        // Both coats share their whole palette and most of their response - they are the
        // same animal rendered two ways, which is exactly what makes them useful as the
        // two mains. What differs is the handful of values the TYPE forces apart, and
        // those are called out where they sit.
        private static readonly Color CoatBase  = new Color(0.7264151f, 0.4341284f, 0.15419188f, 1f);
        private static readonly Color CoatRoot  = new Color(0.8207547f, 0.8207547f, 0.8207547f, 1f);
        private static readonly Color CoatTip   = Color.white;
        private static readonly Color CoatSheen = new Color(0.3490566f, 0.1029429f, 0.02140442f, 1f);
        private static readonly Color CoatRim   = new Color(0.20754719f, 0.0468253f, 0.01272696f, 1f);

        private static (string, float)[] SharedFloats(float furType, float furLength,
                                                      float alphaCutout, float stiffness)
        {
            return new (string, float)[]
            {
                ("_FurType",      furType),
                ("_Smoothness",   0f),
                ("_NormalMapToggle", 0f),
                ("_NormalScale",  0f),

                // --- Structure. Length, cutout and stiffness are the type's own; the
                //     rest describe the same coat on either. ---
                ("_FurLength",    furLength),
                ("_Density",      75f),
                ("_Thickness",    2.61f),
                ("_StrandJitter", 1f),
                ("_LengthRandom", 1f),
                // Straight. Both shipped coats were authored before curl existed, and a
                // preset that left it unwritten would inherit whatever the material had.
                ("_Curl",         0f),
                ("_AlphaCutout",  alphaCutout),

                // --- Fuzz, in screen-space mode. The stencil write mask is non-zero to
                //     match: it is what marks the coat's pixels for the full-screen pass,
                //     and the mode is inert without it. ---
                ("_FuzzAmount",           0.228f),
                ("_FuzzExtent",           0.09f),
                ("_FuzzSoftness",         12f),
                ("_FuzzMode",             1f),
                ("_FuzzStencilRef",       8f),
                ("_FuzzStencilWriteMask", 8f),

                // --- Motion ---
                ("_Stiffness",                stiffness),
                ("_Gravity",                  1.55f),
                ("_GravityMultiplierOnWet",   3f),
                ("_StiffnessMultiplierOnWet", 0.7f),
                ("_CombStrength",             1.5f),
                ("_WindStrength",             1f),
                ("_WindFrequency",            1.56f),
                ("_WindTurbulence",           2f),
                ("_WindGustiness",            0.6f),
                ("_WindShelter",              0.35f),

                // --- Shading ---
                ("_Occlusion",            0f),
                ("_OcclusionCurve",       4f),
                ("_StrandNormalStrength", 0.323f),
                ("_SheenIntensity",       2f),
                ("_SheenExponent",        55f),
                ("_RimIntensity",         0.41f),
                ("_RimPower",             5.22f),

                // --- Wet response ---
                ("_WetClumpSize",      3f),
                ("_WetClumpStrength",  0.8f),
                ("_WetThinning",       0.65f),
                ("_WetLengthScale",    0.838f),
                ("_WetDarkening",      1.7f),
                ("_WetSmoothness",     0.85f),
                ("_WetWindResponse",   1f),
                ("_WetRimScale",       0.25f),
                ("_WetSheenScale",     0.35f),
                ("_WetOcclusionScale", 1.35f),

                // --- Render state ---
                //
                // Cull OFF, and on both types, though the two reach it by different routes.
                //
                // A SHELL coat's layers extend past the body's silhouette, so the strands
                // that should be visible over the horizon of the shape are drawn from
                // back-facing triangles - culling removes exactly the ones that make the
                // outline read as fur.
                //
                // A STRAND coat needs it far more urgently. Its ribbons keep a fixed,
                // randomly twisted plane (see FUR_STRAND_RIBBON_BILLBOARD), so roughly
                // half the hairs face away from any given camera and back-face culling
                // deletes them outright - a bald body with a few furry patches on it. The
                // demo materials were both still on Cull Back when these were captured,
                // which is why this is stated rather than inherited.
                ("_Cull",     0f),
                ("_SrcBlend", 1f),
                ("_DstBlend", 0f),
                ("_ZWrite",   1f),
            };
        }

        private static (string, Color)[] SharedColors()
        {
            return new (string, Color)[]
            {
                ("_BaseColor",  CoatBase),
                ("_RootColor",  CoatRoot),
                ("_TipColor",   CoatTip),
                ("_SheenColor", CoatSheen),
                ("_RimColor",   CoatRim),
            };
        }

        private static (string, Vector4)[] SharedVectors()
        {
            return new (string, Vector4)[]
            {
                ("_WindDirection", new Vector4(0f, 1f, 0f, 0f)),
            };
        }

        /// <summary>
        /// Shell fur, main.
        ///
        /// LOD off and 64 fixed layers: a shell coat's cost is its layer count, so this
        /// is the setting that says "spend it, this is the hero object". Turn the LOD on
        /// for anything that will be seen at range and the curve below takes over.
        /// </summary>
        private static readonly FurPreset ShellsMain = new FurPreset
        {
            DisplayName = "Shell Fur - Main",

            Floats  = SharedFloats(furType: 0f, furLength: 0.557f, alphaCutout: 0.269f, stiffness: 0.76f),
            Colors  = SharedColors(),
            Vectors = SharedVectors(),
            StrandsKeyword = false,

            ShadowMode     = FurController.FurShadowMode.AllShells,
            ReceiveShadows = false,

            EnableLod        = false,
            ShellCount       = 64,
            HardCullDistance = 50f,
            ShellCountByDistance = new[]
            {
                new Keyframe(0.02295291f, 64.5511f),
                new Keyframe(5f, 24f),
                new Keyframe(23.33333f, 5.016666f),
                new Keyframe(50f, 1f),
            },

            // Carried even though a shell coat never reads them, so that switching a
            // material's type afterwards lands on a configured strand coat rather than on
            // whatever the component happened to be holding.
            MaxStrandCount  = 193928,
            RibbonSegments  = 4,
            RibbonSeed      = 12345,
            RibbonDensityByDistance = DefaultRibbonDensity(),

            FollowBlendShapes    = true,
            MotionInertia        = true,
            DragStrength         = 2f,
            AccelerationResponse = 0.05f,
            InertiaDamping       = 10f,
            InertiaDampingRatio  = 0.5f,
            InertiaMode          = FurController.FurInertiaMode.TransformAndBones,
            BoneInfluenceRadius  = 0.25f,
            AngularInertiaScale  = 1f,

            Wetness              = 0f,
            EnableWetnessMap     = true,
            WetnessMapResolution = 128,
            EnableDrying         = true,
            WettingRate          = 2f,
            DryingRate           = 0.1f,
            ContactFeather       = 0.04f,

            ColorMapScaleOffset = new Vector4(1f, 1f, 0f, 0f),
            ColorMapFill        = Color.white,
            DetailUvSet         = FurDetailUvSet.UV0,
            DetailMapResolution = 1024,
            ColorMapTint        = Color.white,
            ColorMapStrength    = 1f,
            UseBandGradient     = false,

            EnableCollision          = false,
            CollisionStrength        = 1f,
            CollisionMargin          = 0.05f,
            CollisionSpread          = 1f,
            MaxSimultaneousColliders = 4,
        };

        /// <summary>
        /// Strands, main.
        ///
        /// Shorter and with a higher cutout than the shell coat, and neither is a
        /// stylistic drift: a ribbon IS the hair, so its length reads at full strength
        /// where a shell coat's top layers fade out, and its edge is analytic rather than
        /// a threshold on a procedural pattern.
        ///
        /// LOD ON here, unlike the shell preset. A strand coat thins by drawing FEWER
        /// HAIRS, which is a smooth and cheap thing to do; a shell coat thins by dropping
        /// layers, which is visible. So the type that can afford the LOD gets it.
        /// </summary>
        private static readonly FurPreset StrandsMain = new FurPreset
        {
            DisplayName = "Strands - Main",

            Floats  = SharedFloats(furType: 1f, furLength: 0.311f, alphaCutout: 0.432f, stiffness: 0.96f),
            Colors  = SharedColors(),
            Vectors = SharedVectors(),
            StrandsKeyword = true,

            ShadowMode     = FurController.FurShadowMode.AllShells,
            ReceiveShadows = false,

            EnableLod        = true,
            ShellCount       = 32,
            HardCullDistance = 50f,
            ShellCountByDistance = new[]
            {
                new Keyframe(0.02295291f, 64.5511f),
                new Keyframe(5f, 24f),
                new Keyframe(23.33333f, 5.016666f),
                new Keyframe(50f, 1f),
            },

            MaxStrandCount  = 193928,
            RibbonSegments  = 3,
            RibbonSeed      = 12345,
            RibbonDensityByDistance = DefaultRibbonDensity(),

            FollowBlendShapes    = true,
            MotionInertia        = true,
            DragStrength         = 2f,
            AccelerationResponse = 0.05f,
            InertiaDamping       = 10f,
            InertiaDampingRatio  = 0.5f,
            InertiaMode          = FurController.FurInertiaMode.TransformAndBones,
            BoneInfluenceRadius  = 0.25f,
            AngularInertiaScale  = 1f,

            Wetness              = 0f,
            EnableWetnessMap     = true,
            WetnessMapResolution = 128,
            EnableDrying         = true,
            WettingRate          = 2f,
            DryingRate           = 0.1f,
            ContactFeather       = 0.04f,

            ColorMapScaleOffset = new Vector4(1f, 1f, 0f, 0f),
            ColorMapFill        = Color.white,
            DetailUvSet         = FurDetailUvSet.UV0,
            DetailMapResolution = 1024,
            ColorMapTint        = Color.white,
            ColorMapStrength    = 1f,
            UseBandGradient     = false,

            EnableCollision          = false,
            CollisionStrength        = 1f,
            CollisionMargin          = 0.05f,
            CollisionSpread          = 1f,
            MaxSimultaneousColliders = 4,
        };

        private static Keyframe[] DefaultRibbonDensity()
        {
            return new[]
            {
                new Keyframe(0f, 1f),
                new Keyframe(10f, 0.8f),
                new Keyframe(30f, 0.35f),
                new Keyframe(60f, 0.1f),
            };
        }
    }
}
