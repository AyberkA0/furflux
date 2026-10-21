using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace FurRendering
{
    /// <summary>
    /// Drives one furred object: what its coat costs, how it moves, and what it reacts to.
    ///
    /// The coat's LOOK is on the material - which of the two fur types it uses, and
    /// every property of the strand. This component owns the per-object half: the budget,
    /// the level of detail, the motion, the wetness, the detail maps and the collision.
    ///
    /// Architecture:
    ///  - No geometry shader. Every fur layer (shell) is a GPU instance, submitted
    ///    in a single draw call through <see cref="Graphics.RenderMeshInstanced"/>.
    ///  - The shell index is read on the shader side from unity_InstanceID.
    ///  - Drawing happens per camera via <see cref="RenderPipelineManager.beginCameraRendering"/>,
    ///    so it renders correctly in both the Scene View and the Game View without Play Mode.
    ///
    /// Shell count is driven by a distance curve (see <see cref="ResolveShellCount"/>).
    /// This gives each asset explicit artistic control over quality falloff.
    ///
    /// Per-object dynamic values (_ShellCount, _ExternalForce) are declared
    /// OUTSIDE the UnityPerMaterial constant buffer in the shader, so they can be
    /// supplied per draw with a MaterialPropertyBlock and never require a runtime
    /// material copy. As a result, every furred object can share a single material and
    /// the LOD decision is computed correctly per camera.
    ///
    /// Usage: add to a GameObject with a MeshRenderer or SkinnedMeshRenderer, then
    /// assign a material using the "FURFLUX/Fur Lit" shader.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("FURFLUX/Fur Controller")]
    public sealed class FurController : MonoBehaviour
    {
        /// <summary>
        /// Which of the two fur types the material is using.
        ///
        /// The value LIVES ON THE MATERIAL, not here - it decides which of that
        /// material's sixty-odd properties mean anything, so it belongs beside them and
        /// is drawn at the top of the material inspector. This component only reads it,
        /// because the type also decides how the object is SUBMITTED, and that is this
        /// component's job.
        /// </summary>
        public enum FurType
        {
            /// <summary>
            /// Concentric copies of the surface, one per layer, with the individual hair
            /// resolved as a procedural pattern in the fragment stage. Cheap per hair and
            /// expensive per layer, needs no readable mesh, and handles blend shapes -
            /// but the hair is an illusion, so it thins out under close inspection and at
            /// a silhouette.
            /// </summary>
            Shells = 0,

            /// <summary>
            /// One ribbon per hair, planted on the surface and skinned on the GPU. Real
            /// geometry: it holds up in a close-up and along a silhouette, and its cost
            /// scales with hair count rather than with layer count. The type to reach for
            /// on a hero character.
            /// </summary>
            Strands = 1,
        }

        /// <summary>How the fur writes into the shadow map.</summary>
        public enum FurShadowMode
        {
            /// <summary>Fur casts no shadow (fastest).</summary>
            None = 0,
            /// <summary>
            /// The fur adds nothing to the shadow map; the body's own renderer casts the
            /// silhouette. Visually near-identical to AllShells for short fur, and free.
            /// </summary>
            BaseLayerOnly = 1,
            /// <summary>Every shell casts a shadow. The most realistic self-shadowing, the most expensive.</summary>
            AllShells = 2,
        }

        /// <summary>Where the fur's inertia comes from.</summary>
        public enum FurInertiaMode
        {
            /// <summary>
            /// The GameObject's own transform only. Correct for anything rigid, and free -
            /// but a skinned creature standing still while its tail whips produces no
            /// transform motion at all, so that fur never reacts.
            /// </summary>
            TransformOnly = 0,

            /// <summary>
            /// Transform motion plus a set of bones that report their own movement
            /// RELATIVE to the body. A flicking tail, a shaking head or a turning ear
            /// then drives the fur around it while the rest of the coat stays put.
            /// </summary>
            TransformAndBones = 1,
        }

        public const int MaxShellCount = 256;

        /// <summary>Must match FUR_MAX_MOTION_BONES in FurShellInput.hlsl.</summary>
        public const int MaxMotionBones = 8;

        /// <summary>Must match FUR_MAX_COLLIDERS in FurShellCollision.hlsl.</summary>
        public const int MaxFurColliders = 8;

        /// <summary>
        /// Ceiling on the acceleration fed to the inertia spring, in m/s squared. Roughly
        /// fifteen g - past this the value is almost certainly a frame hitch rather than
        /// real motion, and letting it through launches the fur.
        /// </summary>
        private const float MaxTrackedAcceleration = 150f;

        /// <summary>
        /// Natural-frequency scale for the inertia spring at full wetness. Below 1 because
        /// a waterlogged coat is heavier, so it swings and settles more slowly.
        /// </summary>
        private const float WetInertiaScale = 0.6f;

        /// <summary>
        /// Longest frame the motion integrators will accept, in seconds. A hitch longer
        /// than this is replayed as one step of this length rather than as the real
        /// elapsed time, which keeps a stall from launching the fur across the screen.
        /// </summary>
        private const float MaxTimeStep = 0.05f;

        /// <summary>
        /// Largest ω·h the inertia spring is integrated at. Well inside the stability
        /// limit of semi-implicit Euler, so the response comes from the tuning rather
        /// than from the frame rate.
        /// </summary>
        private const float MaxSpringStepAngle = 0.25f;

        /// <summary>Ceiling on spring sub-steps per frame, so a long hitch cannot spiral.</summary>
        private const int MaxSpringSubsteps = 8;

        /// <summary>
        /// Where the LOD length fade finishes, as a multiple of the skin-only threshold.
        /// Deliberately small: the fade only covers fur that is already a pixel or two
        /// tall, because anywhere above that the lost height is plainly visible.
        /// </summary>
        private const float LodFadeSpan = 3f;

        // The floors "Keep Fur Visible At Distance" holds the LOD to. Constants, not
        // sliders: they are not a look, they are the least fur that still reads as fur,
        // and every value below them is the bug the checkbox is named after.
        private const float LodMinFade = 0.35f;

        // Two LOD numbers that used to be inspector sliders on this component.
        //
        // Both describe the coat in PIXELS, which is the one measure that decides whether
        // thinning it is visible - and that is exactly why neither worked as a control.
        // A pixel budget is not an artistic choice about a creature; it is a statement
        // about the display, it is the same statement for every coat, and nobody tuning a
        // squirrel has any way to look at the result and tell whether 1.4 was better than
        // 1.0. The curve above is where a coat's distance behaviour is actually authored,
        // and it is per-object, visible, and drawn as a curve.
        //
        // Shells per pixel of fur HEIGHT: the cap that stops a coat from stacking layers
        // finer than the screen can resolve. Two is one layer per half pixel.
        private const float LodShellsPerPixel = 2f;

        // Where the height fade starts, in pixels of fur. Below this the coat is
        // shortening toward LodMinFade rather than holding full height; it never reaches
        // zero, so this sets where the ramp begins and not where the fur ends.
        private const float LodFadePixels = 1f;
        private const int   LodMinShells = 4;
        private const float LodMinRibbonFraction = 0.06f;

        // How much of the coat the fuzz layer is drawn from.
        //
        // Less than all of it, because a blur has no fine detail to lose - but not as little
        // as it first looked. The layer's smoothness comes from its instances OVERLAPPING;
        // too few and each widened hair is individually visible, which reads as spikes past
        // the silhouette rather than as haze. A quarter was too few and looked exactly like
        // that. Half overlaps enough to merge.
        //
        // Shells can afford to stay at a quarter: its instances are whole concentric
        // surfaces, which overlap completely by construction, so thinning them costs
        // smoothness in HEIGHT only - where the profile's own tip fade is already doing the
        // work.
        // Shells thin their fuzz layer; ribbons do not.
        //
        // A shell instance is a whole concentric surface, so they overlap completely by
        // construction and dropping three quarters of them costs smoothness in HEIGHT only -
        // where the profile's own tip fade is already doing the work.
        //
        // A ribbon coat has no such guarantee: its fuzz is smooth because its hairs overlap,
        // and thinning them is what makes the layer come apart into separate soft spikes
        // instead of haze. How sparse that is depends on the coat, so there is no fraction
        // that is right for every one of them - which is why this is 1 and not a setting.
        // The layer's cost is controlled where its cost belongs, on Max Strands.
        private const float FuzzShellFraction  = 0.25f;
        private const float FuzzRibbonFraction = 1f;

        // ------------------------------------------------------------------ //
        //  Source
        // ------------------------------------------------------------------ //
        // Always resolved from the same GameObject; deliberately not exposed in the Inspector.
        private Renderer sourceRenderer;

        [Tooltip("A material using the \"FURFLUX/Fur Lit\" shader. Several furred objects can share one.")]
        [SerializeField] private Material furMaterial;

        // ------------------------------------------------------------------ //
        //  Preset
        // ------------------------------------------------------------------ //
        //  Which built-in look this component is POINTED AT, which is not the same as
        //  which one it is showing. Selecting one changes nothing; the Apply button in
        //  the inspector is what writes.
        //
        //  Split that way on purpose. A preset write covers this component AND the
        //  material it points at, and a material is a shared asset - so an apply can
        //  reach objects that are not the one being looked at. An action that can do
        //  that has to be an action, not a side effect of moving a dropdown while
        //  browsing what is available.
        //
        //  The field also keeps its value after applying, and stays put when the
        //  settings are then edited by hand. It records where a coat STARTED, which is
        //  the only honest thing a stored id can claim - there is no comparison against
        //  the preset's values, and one would be noise: a coat is meant to be tuned
        //  after it is seeded.
        [Tooltip("A starting point for this coat. Selecting one does nothing; Apply Preset writes it, " +
                 "covering this component and the material together. Leaves the material reference, " +
                 "painted maps, inertia bones and layer masks alone.")]
        [SerializeField] private FurPresetId preset = FurPresetId.None;

        [Tooltip("A low-poly mesh to draw the shells from. Shells are drawn once per layer, so " +
                 "base-mesh detail finer than the fur costs shell count and buys nothing. Uses the " +
                 "source mesh when empty.")]
        [HideInInspector]
        [SerializeField] private Mesh furProxyMesh;

        // ------------------------------------------------------------------ //
        //  Quality
        // ------------------------------------------------------------------ //
        [Tooltip("How fur writes shadows. BaseLayerOnly is a large saving whenever shell count is " +
                 "high.")]
        [SerializeField] private FurShadowMode shadowMode = FurShadowMode.BaseLayerOnly;

        [SerializeField] private bool receiveShadows = true;

        // ------------------------------------------------------------------ //
        //  LOD
        // ------------------------------------------------------------------ //
        [Tooltip("Reduces shell count as the camera moves away.")]
        [SerializeField] private bool enableLod = true;

        [Tooltip("Shell count when LOD is off. With LOD on the curve below decides instead.")]
        // 0 means "not set yet". Assets authored before this field existed took their
        // fixed count from the curve's value at zero distance, so that is what they
        // migrate to rather than silently dropping to some new default.
        [Min(0)]
        [SerializeField] private int shellCount;

        [Tooltip("X = camera distance in OBJECT LENGTHS, so the curve means the same thing at any " +
                 "import scale. Y = shell count. Never taken below a few shells - Hard Cull Distance " +
                 "is what removes a coat entirely.")]
        [SerializeField] private AnimationCurve shellCountByDistance = new AnimationCurve(
            new Keyframe(0f, 32f), new Keyframe(10f, 20f), new Keyframe(30f, 4f), new Keyframe(50f, 1f));

        [Tooltip("Beyond this the fur is not drawn at all. In OBJECT LENGTHS, like the curves. 0 " +
                 "disables it. The only thing that takes a coat to bare skin.")]
        [Min(0f)]
        [SerializeField] private float hardCullDistance = 0f;

        // ------------------------------------------------------------------ //
        //  Strand fur
        // ------------------------------------------------------------------ //
        //
        //  Ignored entirely while the material is set to Shells - a shell coat has no
        //  hair count, because it has no individual hairs.
        //
        //  How DENSE the coat is lives on the MATERIAL, per square meter of surface AS
        //  DRAWN - it describes what the coat is made of, in the same world units the fur
        //  length and strand width already use, so one material clothes a mouse and a bear
        //  at the same coarseness however either of them was scaled or imported. What
        //  lives here is the CEILING on what this particular object may spend on that:
        //  whichever of the two asks for fewer wins, so a density tuned on a squirrel
        //  cannot quietly cost a million hairs when somebody puts the same material on a
        //  bear.
        //
        [Tooltip("Hair ceiling for this object, whatever the material's density asks for. Every hair " +
                 "is real geometry, so this is the budget dial.")]
        [Range(256, 400000)]
        [SerializeField] private int maxStrandCount = 150000;


        [Tooltip("Segments along each ribbon. At 1 a hair is a straight quad and nothing curves it. " +
                 "Paid per hair.")]
        [Range(1, FurRibbonMesh.MaxSegments)]
        [SerializeField] private int ribbonSegments = 4;


        [Tooltip("X = camera distance in OBJECT LENGTHS, Y = the fraction of the full hair count still " +
                 "drawn. Separate from the shell curve because it means a different thing: fewer " +
                 "shells thins a coat evenly, fewer hairs makes it sparser.")]
        [SerializeField] private AnimationCurve ribbonDensityByDistance = new AnimationCurve(
            new Keyframe(0f, 1f), new Keyframe(10f, 0.8f), new Keyframe(30f, 0.35f), new Keyframe(60f, 0.1f));

        [Tooltip("Seed for hair placement. Change it to reshuffle a coat that landed badly.")]
        [SerializeField] private int ribbonSeed = 12345;

        [Tooltip("Strand fur only. Unity deforms in three steps - bind pose, blend shapes, skinning - " +
                 "and the GPU root path does the third, so without this a face rig moves the skin and " +
                 "leaves the fur behind. Only the roots a shape actually moves cost anything.")]
        [SerializeField] private bool followBlendShapes = true;

        // ------------------------------------------------------------------ //
        //  Motion / inertia
        // ------------------------------------------------------------------ //
        [Tooltip("Strands trail backward as the object moves.")]
        [SerializeField] private bool motionInertia = true;

        [Tooltip("How strongly SUSTAINED motion drags fur backward. Proportional to speed, so it holds " +
                 "fur swept back while the object moves and does nothing once it stops.")]
        [Range(0f, 10f)]
        [FormerlySerializedAs("inertiaStrength")]
        [SerializeField] private float dragStrength = 0.5f;

        [Tooltip("How strongly CHANGES in motion throw the fur. Proportional to acceleration, so it is " +
                 "zero at constant velocity however fast and peaks on a hard start or stop. " +
                 "Accelerations are numerically much larger than velocities, so useful values are far " +
                 "below Drag Strength.")]
        [Range(0f, 0.5f)]
        [SerializeField] private float accelerationResponse = 0.05f;

        [Tooltip("How fast the trailing force responds. Higher = stiffer, faster recovery.")]
        [Range(0.5f, 30f)]
        [SerializeField] private float inertiaDamping = 8f;

        [Tooltip("Spring damping. Below 1 oscillates after a stop, 1 settles without overshoot, above " +
                 "1 is mushy. 0.4-0.7 reads as fur.")]
        [Range(0.1f, 2f)]
        [SerializeField] private float inertiaDampingRatio = 0.55f;

        [Tooltip("Transform Only reads the GameObject's own movement - free, but a skinned creature " +
                 "standing still while its tail whips moves no transform at all. Transform And Bones " +
                 "adds bones that report their motion relative to the body.")]
        [SerializeField] private FurInertiaMode inertiaMode = FurInertiaMode.TransformOnly;

        [Tooltip("Bones whose own movement drives the fur near them - tail joints, head, ears. Only " +
                 "motion RELATIVE to the body counts. Each one costs a distance test per vertex per " +
                 "shell.")]
        [SerializeField] private Transform[] inertiaBones = new Transform[0];

        [Tooltip("How far a bone's motion reaches, in meters. Roughly the radius of the body part it " +
                 "drives.")]
        [Range(0.01f, 3f)]
        [SerializeField] private float boneInfluenceRadius = 0.25f;

        [Tooltip("How much turning adds to the trailing force, on top of straight-line motion. 0 " +
                 "disables it.")]
        [Range(0f, 3f)]
        [SerializeField] private float angularInertiaScale = 1f;

        // ------------------------------------------------------------------ //
        //  Appearance
        // ------------------------------------------------------------------ //
        [Tooltip("Wetness FLOOR for the whole coat - a rain soak, a scripted fade. The map below adds " +
                 "regional wetness on top, so leave this at 0 when only what touched water should be " +
                 "wet.")]
        [Range(0f, 1f)]
        [SerializeField] private float wetness = 0f;

        // The wet gravity / stiffness response used to live here as HIDDEN per-object
        // multipliers. It is still a multiplier - that is what keeps it tracking a
        // Gravity value driven at runtime - but it now lives on the MATERIAL, next to
        // the values it scales and visible in the Inspector, and the shader clamps the
        // result to those sliders' own ranges.

        // ------------------------------------------------------------------ //
        //  Regional wetness
        // ------------------------------------------------------------------ //
        [Tooltip("Only the surface that actually touches water gets wet, and it stays wet after " +
                 "leaving. Needs a non-overlapping UV0: mirrored islands share texels, so wetting one " +
                 "side wets the other.")]
        [SerializeField] private bool enableWetnessMap = false;

        [Tooltip("Physics layers holding the water volumes. A trigger works and is usually what you " +
                 "want, so the character does not physically collide with it.")]
        [SerializeField] private LayerMask waterLayers = 0;

        [Tooltip("Wetness is a low-frequency signal, so 128 is plenty for a character. Costs res^2 * 4 " +
                 "bytes, three times over.")]
        [SerializeField] private int wetnessMapResolution = 128;

        [Tooltip("Off, wetness is permanent until something wets it further - for a sequence that has " +
                 "to stay soaked, or when you drive drying yourself.")]
        [SerializeField] private bool enableDrying = true;

        [Tooltip("Wetness units per second at full contact. 2 = soaked after half a second in water.")]
        [Range(0.05f, 10f)]
        [SerializeField] private float wettingRate = 2f;

        [Tooltip("Wetness units per second while clear of water. 0.1 = about ten seconds from soaked " +
                 "to dry.")]
        [Range(0.001f, 2f)]
        [SerializeField] private float dryingRate = 0.1f;

        [Tooltip("How far wetting reaches outside a water collider, in meters - the splash margin. 0 " +
                 "gives a razor-sharp waterline. A FurWaterVolume can override it per volume.")]
        [Range(0f, 1f)]
        [SerializeField] private float contactFeather = 0.04f;

        [Tooltip("Auto-resolved in the Editor; the serialized reference is what keeps it in a build.")]
        [SerializeField] private Shader wetnessSplatShader;

        // ------------------------------------------------------------------ //
        //  Detail maps (length / thickness / colour)
        // ------------------------------------------------------------------ //
        //
        //  Three maps that vary the coat ACROSS THE SURFACE, where the material's
        //  sliders can only state one value for the whole object: long on the tail and
        //  short on the muzzle, fine on the belly and coarse on the back, a white bib
        //  on a grey body.
        //
        //  Each is an authored - or painted - texture resampled into this object's own
        //  UV0 layout, at a resolution chosen here rather than by the asset (see
        //  FurAttributeMap for why that indirection earns its keep). The bake runs when
        //  something it depends on changes, not every frame, so a still object pays
        //  nothing.
        //
        //  There is no enable toggle. A map is live when it has something IN it: a
        //  source texture, or strokes from the Draw Fur brush. A toggle beside a texture
        //  slot only ever says the same thing twice, and gives the two ways to say it a
        //  chance to disagree.
        //
        //  Every map is a MULTIPLIER of the material value it modulates. Leave one empty
        //  and the fur is exactly what the material says, at no cost: no RenderTexture is
        //  allocated and the shader skips the fetch on a uniform branch.
        //
        [Tooltip("Red channel, multiplying the material's Fur Length: 1 leaves a region alone, 0 " +
                 "shaves it bare. Leave empty and paint one with Draw Fur. Needs a non-overlapping " +
                 "UV0.")]
        [SerializeField] private Texture lengthMap = null;

        [Tooltip("Red channel, multiplying strand WIDTH - not strand count. A thinned region reads " +
                 "finer and wispier at full height, which is guard hair against undercoat.")]
        [SerializeField] private Texture thicknessMap = null;

        [Tooltip("RGB, multiplying the albedo; white leaves the coat alone. Applied alongside the " +
                 "Root/Tip gradient rather than replacing it - that describes the strand, this " +
                 "describes the region.")]
        [SerializeField] private Texture colorMap = null;

        [Tooltip("Which way the hair LIES, painted with the Comb brush. Stored like a normal map's xy, " +
                 "so flat grey means uncombed. This is a resting pose, not a force the coat springs " +
                 "back from.")]
        [SerializeField] private Texture combMap = null;

        [Tooltip("Tiling and offset for the colour map. A texture tiled 3x3 on the mesh has to be " +
                 "resampled 3x3 here or the coat drifts out of phase with the surface it matches. " +
                 "Match Mesh Colours fills this in.")]
        [SerializeField] private Vector4 colorMapScaleOffset = new Vector4(1f, 1f, 0f, 0f);

        [Tooltip("What the colour map holds where its texture does not reach - everywhere, with no " +
                 "texture assigned. White leaves the coat alone.")]
        [SerializeField] private Color colorMapFill = Color.white;

        [Tooltip("Which UV set the per-object maps live in. They hold one value per piece of SURFACE, " +
                 "so two places sharing a texel means painting one paints the other and a wet paw " +
                 "soaks a dry shoulder. UV1 is the lightmap set, which Unity guarantees not to overlap " +
                 "- enable 'Generate Lightmap UVs' on the importer first. Changing this rebuilds every " +
                 "map, painted strokes included.")]
        [SerializeField] private FurDetailUvSet detailUvSet = FurDetailUvSet.UV0;

        [Tooltip("Shared by all three maps, which describe the same surface at the same scale. Costs " +
                 "res^2 * 8 bytes per map in use.")]
        [SerializeField] private int detailMapResolution = 256;

        [Tooltip("Tint multiplied into the colour map. Needs no rebake, so it can be animated.")]
        [SerializeField] private Color colorMapTint = Color.white;

        [Tooltip("How far the colour map is applied. Needs no rebake, so it can be faded at runtime.")]
        [Range(0f, 1f)]
        [SerializeField] private float colorMapStrength = 1f;

        // ------------------------------------------------------------------ //
        //  Hair banding
        // ------------------------------------------------------------------ //
        //  The material's Root Color and Tip Color are two stops on a gradient, and two
        //  stops can only describe a hair that shades from one thing to another. This
        //  replaces them with as many stops as the coat needs.
        //
        //  It lives on the component rather than on the material, which is against this
        //  package's usual split - the look belongs to the material - and the reason is
        //  simply that a material cannot hold one. Unity serializes Gradients on objects,
        //  not in shader property blocks, and a gradient is the right control here by a
        //  wide margin: it is already the editor everyone knows for "a list of stops you
        //  can add to", and it carries alpha per stop for free.
        // ------------------------------------------------------------------ //
        [Tooltip("Replaces the material's two-stop Root/Tip colour with as many stops as you like. Two " +
                 "colours cannot describe a banded hair - dark root, pale ring, dark again toward the " +
                 "tip - because there is no middle to tune.")]
        [SerializeField] private bool useBandGradient;

        [Tooltip("Left is the root, right is the tip. RGB multiplies the albedo, alpha is how solid " +
                 "the hair is at that height. HDR, because a real tip is a thin bright fringe.")]
        [GradientUsage(true, ColorSpace.Linear)]
        [SerializeField] private Gradient bandGradient = DefaultBandGradient();

        [Tooltip("Auto-resolved in the Editor; the serialized reference is what keeps it in a build.")]
        [SerializeField] private Shader attributeBakeShader;

        // ------------------------------------------------------------------ //
        //  Collision
        // ------------------------------------------------------------------ //
        [Tooltip("Off, no physics query runs and the shader's contact solve is skipped entirely. " +
                 "Contact is resolved per strand tip per shell, so this is the most expensive feature " +
                 "here.")]
        [SerializeField] private bool enableCollision = false;

        [Tooltip("Colliders on these layers go to the GPU as primitives and every strand tip is " +
                 "resolved against them individually. Box, Sphere and Capsule are exact; Mesh and " +
                 "Terrain are approximated by their bounding box.")]
        [SerializeField] private LayerMask collisionLayers = 0;

        // NOTE: this used to be a 0..5 force multiplier and is now a 0..1 resolve
        // fraction. Assets saved before the change carry values like 1.5 or 5, which
        // would over-push by that factor, so every read of it is clamped rather than
        [Tooltip("How fully a contact is resolved. 1 moves strands clear of the collider; lower lets " +
                 "them sink in, higher deflects them harder.")]
        [Range(0f, 5f)]
        [SerializeField] private float collisionStrength = 1f;

        [Tooltip("How far outside a collider fur starts leaning away, in meters. This is what makes " +
                 "contact soft instead of a snap the instant surfaces touch.")]
        [Range(0f, 2f)]
        [SerializeField] private float collisionMargin = 0.05f;

        [Tooltip("How much a contact COMBS the fur aside instead of squashing it flat. Real fur barely " +
                 "compresses: at 1 strands keep their length and lie over, at 0 the contact patch " +
                 "looks shaved.")]
        [Range(0f, 1f)]
        [SerializeField] private float collisionSpread = 1f;

        [Tooltip("When more are in range the nearest ones win. Each costs a distance evaluation per " +
                 "vertex per shell.")]
        [Range(1, MaxFurColliders)]
        [SerializeField] private int maxSimultaneousColliders = 4;

        // ------------------------------------------------------------------ //
        //  Shader property IDs
        // ------------------------------------------------------------------ //
        private static readonly int ShellCountId              = Shader.PropertyToID("_ShellCount");
        private static readonly int LodFadeId                 = Shader.PropertyToID("_LodFade");
        private static readonly int PrevObjectToWorldId       = Shader.PropertyToID("_FurPrevObjectToWorld");
        private static readonly int ExternalForceId         = Shader.PropertyToID("_ExternalForce");
        private static readonly int DragForceId             = Shader.PropertyToID("_DragForce");
        private static readonly int AngularForceId          = Shader.PropertyToID("_AngularForce");
        private static readonly int RotationCenterId        = Shader.PropertyToID("_RotationCenterWS");
        private static readonly int BoneMotionCountId       = Shader.PropertyToID("_BoneMotionCount");
        private static readonly int BoneMotionOriginId      = Shader.PropertyToID("_BoneMotionOrigin");
        private static readonly int BoneMotionForceId       = Shader.PropertyToID("_BoneMotionForce");
        private static readonly int FurLengthId             = Shader.PropertyToID("_FurLength");
        private static readonly int WetnessId               = Shader.PropertyToID("_Wetness");
        private static readonly int WetnessMapId            = Shader.PropertyToID("_WetnessMap");
        private static readonly int WetnessEnabledId        = Shader.PropertyToID("_WetnessEnabled");
        private static readonly int LengthMapId             = Shader.PropertyToID("_FurLengthMap");
        private static readonly int LengthMapEnabledId      = Shader.PropertyToID("_FurLengthMapEnabled");
        private static readonly int ThicknessMapId          = Shader.PropertyToID("_FurThicknessMap");
        private static readonly int ThicknessMapEnabledId   = Shader.PropertyToID("_FurThicknessMapEnabled");
        private static readonly int ColorMapId              = Shader.PropertyToID("_FurColorMap");
        private static readonly int ColorMapEnabledId       = Shader.PropertyToID("_FurColorMapEnabled");
        private static readonly int CombMapId               = Shader.PropertyToID("_FurCombMap");
        private static readonly int CombMapEnabledId        = Shader.PropertyToID("_FurCombMapEnabled");
        private static readonly int ColorMapTintId          = Shader.PropertyToID("_FurColorMapTint");
        private static readonly int ColliderToLocalId       = Shader.PropertyToID("_FurColliderToLocal");
        private static readonly int ColliderShapeId         = Shader.PropertyToID("_FurColliderShape");
        private static readonly int ColliderParamsId        = Shader.PropertyToID("_FurColliderParams");
        private static readonly int ColliderCountId         = Shader.PropertyToID("_FurColliderCount");
        private static readonly int CollisionStrengthId     = Shader.PropertyToID("_CollisionStrength");
        private static readonly int CollisionSpreadId       = Shader.PropertyToID("_CollisionSpread");

        // --- Strand fur ---
        private static readonly int FurTypeId               = Shader.PropertyToID("_FurType");
        private static readonly int GeometryModeId          = Shader.PropertyToID("_FurGeometryMode");
        private static readonly int FuzzModeId              = Shader.PropertyToID("_FurFuzzMode");
        private static readonly int FuzzAmountId            = Shader.PropertyToID("_FuzzAmount");
        private static readonly int FuzzExtentId            = Shader.PropertyToID("_FuzzExtent");
        private static readonly int FuzzMaterialModeId      = Shader.PropertyToID("_FuzzMode");
        private static readonly int FuzzStencilWriteMaskId  = Shader.PropertyToID("_FuzzStencilWriteMask");
        private static readonly int SrcBlendId              = Shader.PropertyToID("_SrcBlend");
        private static readonly int DstBlendId              = Shader.PropertyToID("_DstBlend");
        private static readonly int ZWriteId                = Shader.PropertyToID("_ZWrite");
        private static readonly int InstanceCountId         = Shader.PropertyToID("_FurInstanceCount");
        private static readonly int RootsPerBatchId         = Shader.PropertyToID("_FurRootsPerBatch");
        private static readonly int SkinnedId               = Shader.PropertyToID("_FurSkinned");
        private static readonly int DrawSkinId              = Shader.PropertyToID("_FurDrawSkin");
        private static readonly int ObjectToWorldId         = Shader.PropertyToID("_FurObjectToWorld");
        private static readonly int WorldToObjectId         = Shader.PropertyToID("_FurWorldToObject");
        private static readonly int RootsBufferId           = Shader.PropertyToID("_FurRoots");
        private static readonly int SkinMatricesId          = Shader.PropertyToID("_FurSkinMatrices");
        private static readonly int DensityId               = Shader.PropertyToID("_Density");
        private static readonly int AggregationId           = Shader.PropertyToID("_FurAggregation");
        private static readonly int CountFractionId         = Shader.PropertyToID("_FurCountFraction");
        private static readonly int MetersPerUvId           = Shader.PropertyToID("_FurMetersPerUV");
        private static readonly int DetailUvId              = Shader.PropertyToID("_FurDetailUV");
        private static readonly int BandMapId               = Shader.PropertyToID("_FurBandMap");
        private static readonly int BandEnabledId           = Shader.PropertyToID("_FurBandEnabled");
        private static readonly int BandMeanId              = Shader.PropertyToID("_FurBandMean");
        private static readonly int MetersPerUvMaxId        = Shader.PropertyToID("_FurMetersPerUVMax");
        private static readonly int ObjectSizeId            = Shader.PropertyToID("_FurObjectSize");

        /// <summary>Shader keywords the fur type selects between. Off = Shells.</summary>
        private const string StrandsKeyword = "_FURTYPE_STRANDS";

        private const string WetnessSplatShaderName = "Hidden/FURFLUX/Wetness Splat";
        private const string AttributeBakeShaderName = "Hidden/FURFLUX/Attribute Bake";

        /// <summary>
        /// How far above the material's own value a detail map can push length and
        /// thickness. MUST match FUR_DETAIL_SCALE_MAX in FurShellInput.hlsl.
        ///
        /// The map stores 0..1 and is multiplied by this, so the value meaning "exactly
        /// what the material says" is MID-GREY, not white. That is the only encoding in
        /// which a map can both shorten and lengthen: with white as neutral there is
        /// nothing above it to grow into.
        /// </summary>
        public const float DetailScaleMax = 2f;

        /// <summary>Largest detail-map resolution, mirroring FurAttributeMap's own bound.</summary>
        public const int MaxDetailMapResolution = FurAttributeMap.MaxResolution;

        // What each channel bakes to where nothing has painted or authored it - the value
        // that leaves the fur exactly as the material describes it. Mid-grey for the two
        // scalar maps because of the encoding above; white for colour, which is a plain
        // multiplier with no headroom to reserve.
        private static readonly Color NeutralScalar = new Color(1f / DetailScaleMax, 1f / DetailScaleMax, 1f / DetailScaleMax, 1f);

        // An uncombed surface. RG carry a direction encoded the way a normal map encodes
        // its xy, so the zero vector sits at half - not at black, which would decode to a
        // hard comb pointing diagonally backwards across every texel nobody has touched.
        private static readonly Color NeutralComb = new Color(0.5f, 0.5f, 0f, 1f);

        // ------------------------------------------------------------------ //
        //  Runtime state
        // ------------------------------------------------------------------ //
        private readonly Matrix4x4[] _instanceMatrices = new Matrix4x4[MaxShellCount];
        private readonly Plane[] _frustumPlanes = new Plane[6];

        // Reused every frame - Physics.OverlapBox (the allocating overload) would hand
        // back a fresh array per object per frame.
        private readonly Collider[] _waterColliderBuffer = new Collider[FurWetnessMap.MaxSources * 2];
        private FurWetnessMap _wetnessMap;

        // Allocated lazily, and only for the maps that are actually switched on: an
        // unused detail map should cost neither a RenderTexture nor a bake.
        private FurAttributeMap _lengthAttributeMap;
        private FurAttributeMap _thicknessAttributeMap;
        private FurAttributeMap _colorAttributeMap;
        private FurAttributeMap _combAttributeMap;

        // Depth of the model from the brush's viewpoint, so a Solid stroke can tell a
        // visible surface from one hidden behind the body. Allocated only once something
        // asks for it - painting is an editor-time activity for most projects.
        private FurBrushOcclusion _brushOcclusion;

        // --- Fur collision, resolved per vertex on the GPU ---
        private readonly Collider[] _collisionColliderBuffer = new Collider[32];
        private readonly Matrix4x4[] _colliderToLocal = new Matrix4x4[MaxFurColliders];
        private readonly Vector4[] _colliderShape = new Vector4[MaxFurColliders];
        private readonly Vector4[] _colliderParams = new Vector4[MaxFurColliders];
        private readonly float[] _colliderSortKeys = new float[MaxFurColliders];
        private int _activeColliderCount;

        private MaterialPropertyBlock _propertyBlock;

        // --- Strand fur ---
        //
        // A SECOND property block, not a second set of values in the first one. Both
        // ribbon types issue two draws per object - the skin, then the fur - that differ
        // only in _FurGeometryMode and the mesh, and reusing one block would mean mutating
        // it between two submissions in the same frame. Two blocks make the two draws
        // independent of each other and of submission order.
        private MaterialPropertyBlock _skinPropertyBlock;
        // Its own block for the same reason the skin has one: the fuzz layer differs
        // from the coat in _FurFuzzMode, and reusing one block would mean mutating it
        // between two draws that have already been recorded.
        private MaterialPropertyBlock _fuzzPropertyBlock;
        private FurStrandDistribution _distribution;
        private Matrix4x4[] _ribbonMatrices = new Matrix4x4[1];
        private GraphicsBuffer _skinMatrixBuffer;
        private FurSkinMatrix[] _skinMatrices;
        private Transform[] _skinBones;
        private Matrix4x4[] _bindposes;
        private int _activeRibbonCount;
        private int _requestedRibbonCount;
        private bool _ribbonWarningShown;
        private bool _materialWarningShown;

        // The skeleton is the same for every camera in a frame, so uploading it once per
        // camera would be the same data sent two or three times over in any scene with a
        // reflection probe or a second view.
        private int _skinMatrixFrame = -1;

        private SkinnedMeshRenderer _skinnedRenderer;
        private MeshFilter _meshFilter;
        private Mesh _bakedMesh;                // Mesh baked from the SkinnedMeshRenderer each frame
        private Mesh _activeMesh;
        private Matrix4x4 _objectToWorld = Matrix4x4.identity;

        // Last frame's transform, handed to the motion vector pass. Tracked here rather
        // than relying on UNITY_PREV_MATRIX_M, which the pipeline fills in for
        // renderer-driven draws and not for Graphics.RenderMeshInstanced.
        private Matrix4x4 _previousObjectToWorld = Matrix4x4.identity;
        private bool _previousObjectToWorldValid;
        private Bounds _worldBounds;
        private float _furLength = 0.05f;
        private float _lodFade = 1f;   // 1 = full length; falls to 0 just before the fur is dropped

        // Screen scale for the camera being drawn, from the same measurement the LOD makes.
        // Kept so the ribbon types can work out how many hairs fall inside a pixel, which
        // is what decides whether the coat still has strands worth resolving.
        private float _pixelsPerMeter = 1f;

        // Height of the fur on screen, in pixels, for the camera being drawn. The LOD uses
        // it to decide when the coat is too small to bother with; the aggregation uses it
        // to decide when the gaps BETWEEN its shells have grown wider than a pixel.
        private float _furPixels = 1f;

        // World meters per UV unit for this object's mesh - the reference the shader
        // clamps its own per-fragment measurement against. Resolved when the mesh changes,
        // not per frame: it is a property of the unwrap and of the object's scale.
        private float _metersPerUV = 1f;
        private float _metersPerUVMax = 1f;

        // How big this object is, in meters. Everything the material says about a strand
        // is a fraction of it - see the note above FurObjectSize in FurShellInput.hlsl.
        private float _objectSize = 1f;
        private Material _fuzzMaterial;
        private Material _fuzzMaterialSource;
        private int _fuzzMaterialVersion = -1;

        // The skin's own copy of the fur material, and it exists for exactly one property.
        //
        // In screen-space fuzz mode the coat marks its pixels with a stencil bit so the
        // full-screen pass can tell hair from everything else - and the SKIN must not
        // carry that mark, or the gaps between hairs would be masked out of the composite
        // and the coat would get a silhouette halo with no fullness behind it.
        //
        // Stencil state is resolved from the MATERIAL, not from a property block, so the
        // two draws cannot differ in it while they share one. Hence a copy, and only
        // while screen-space fuzz is actually on.
        private Material _skinMaterial;
        private Material _skinMaterialSource;
        private int _skinMaterialVersion = -1;
        private Vector3 _lodCenter;    // Pose-independent centre the LOD measures from
        private Vector2 _meshMetersPerUv;
        private Mesh _metersPerUvMesh;
        private bool _detailUvWarningShown;

        // The baked band gradient and what it averages to. Not serialized: it is derived
        // from the gradient above and cheap to rebuild, so carrying a stale copy through a
        // domain reload would only be a way to get it wrong.
        private Texture2D _bandTexture;
        private Vector4 _bandMean = Vector4.one;
        private bool _bandDirty = true;

        // 0 = the whole coat is being drawn, 1 = none of it is. Handed to the shader, which
        // uses it to move the coat's appearance down onto its base layer as the fur goes -
        // see the distance aggregation block in FurShellCommon.hlsl.
        private float _aggregation;

        // Ribbon types: how much of the planted hair the LOD is still drawing. 1 while the
        // whole coat is in use.
        private float _countFraction = 1f;
        private Material _furLengthPropertyChecked;   // material the HasProperty result below belongs to
        private bool _hasFurLengthProperty;

        private Vector3 _previousPosition;
        private Quaternion _previousRotation;
        private Vector3 _smoothedForce;   // spring STATE - what the shader receives
        private Vector3 _forceVelocity;   // rate of change of _smoothedForce - the spring's own momentum
        private Vector3 _externalForce;   // caller-supplied spring INPUT, kept separate so the integrator cannot eat it
        private Vector3 _previousVelocity; // last frame's linear velocity - differentiated for the acceleration term

        // Airflow from travel, kept apart from the body forces above so the shader can
        // shelter it on the object's lee side.
        private Vector3 _smoothedDragForce;
        private Vector3 _dragForceVelocity;

        // --- Per-bone motion, all tracked in the BODY's local space ---
        // Local space is what makes these compose with the transform terms instead of
        // double-counting them: a bone carried along by a running creature does not move
        // relative to the body, so it contributes nothing and the root force covers that
        // fur. Only motion the skeleton adds on top shows up here.
        private readonly Vector3[] _bonePreviousLocalPosition = new Vector3[MaxMotionBones];
        private readonly Vector3[] _bonePreviousLocalVelocity = new Vector3[MaxMotionBones];
        private readonly Vector3[] _boneSmoothedForce = new Vector3[MaxMotionBones];
        private readonly Vector3[] _boneForceVelocity = new Vector3[MaxMotionBones];
        private readonly Vector4[] _boneOrigin = new Vector4[MaxMotionBones];   // xyz world position, w reach
        private readonly Vector4[] _boneForce = new Vector4[MaxMotionBones];    // xyz world force
        private int _boneStateCount = -1;   // -1 = state not seeded yet
        private int _activeBoneCount;

        // Rotational counterpart. Held as a force PER METRE from the pivot; the shader
        // crosses it with each vertex's offset from the pivot to get the actual force.
        private Vector3 _smoothedAngularForce;
        private Vector3 _angularForceVelocity;
        private Vector3 _previousAngularVelocity;
        private float _lastRealtime;
        private int _lastUpdatedFrame = -1;
        private int _lastMotionVectorFrame = -1;
        private bool _instancingWarningShown;

        // Value from the most recent draw, for debugging / profiling.
        private int _activeShellCount;

        /// <summary>Whether LOD is active - can be changed at runtime.</summary>
        public bool EnableLod
        {
            get => enableLod;
            set => enableLod = value;
        }

        /// <summary>Shadow mode - can be changed at runtime.</summary>
        public FurShadowMode ShadowMode
        {
            get => shadowMode;
            set => shadowMode = value;
        }

        public int ActiveShellCount => _activeShellCount;

        /// <summary>
        /// Strands drawn last frame. Zero while the material is set to Shells,
        /// which has no such thing.
        /// </summary>
        public int ActiveRibbonCount => _activeRibbonCount;

        /// <summary>
        /// What the material's density asked for over this object's surface, before the
        /// ceiling was applied. Equal to the baked count unless the ceiling is binding.
        ///
        /// Worth reporting separately: a coat clamped to its budget is not visibly
        /// different from a coat whose density is simply too low, and the two want
        /// opposite fixes.
        /// </summary>
        public int RequestedRibbonCount => _requestedRibbonCount;

        public int ShellCount
        {
            get => shellCount;
            set => shellCount = Mathf.Clamp(value, 1, MaxShellCount);
        }

        public Material FurMaterial
        {
            get => furMaterial;
            set => furMaterial = value;
        }

        /// <summary>
        /// Extra force pushed into the fur on top of whatever the object's own movement
        /// produces - wind gusts, an explosion, a scripted effect.
        ///
        /// It PERSISTS until you change it, so set it every frame for a continuous
        /// effect and back to zero to end one. It feeds the same damped spring as the
        /// motion inertia, so it lags and settles the same way rather than snapping.
        ///
        /// Do NOT use this to report the object's own motion: <see cref="ResolvedForce"/>
        /// already derives that from the transform. Adding it here as well double-counts it.
        /// </summary>
        public Vector3 ExternalForce
        {
            get => _externalForce;
            set => _externalForce = value;
        }

        /// <summary>
        /// The force the shader is actually using this frame - the spring's output, which
        /// combines <see cref="ExternalForce"/> with the inertia from the object's motion.
        /// Read-only; for debugging and for driving effects off the fur's response.
        /// </summary>
        public Vector3 ResolvedForce => _smoothedForce + _smoothedDragForce;

        /// <summary>
        /// Global wetness FLOOR, 0..1. The regional wetness map adds on top of this, so
        /// leave it at 0 when only water contact should wet the fur.
        /// </summary>
        public float Wetness
        {
            get => wetness;
            set => wetness = Mathf.Clamp01(value);
        }

        /// <summary>
        /// How fully a fur/collider contact is resolved, 0..1. Clamped on read so assets
        /// serialised against the old 0..5 force multiplier cannot over-push.
        /// </summary>
        public float CollisionStrength
        {
            get => Mathf.Clamp(collisionStrength, 0f, 5f);
            set => collisionStrength = Mathf.Clamp(value, 0f, 5f);
        }

        /// <summary>
        /// How much a contact combs fur aside (1) rather than squashing it in place (0).
        /// </summary>
        public float CollisionSpread
        {
            get => Mathf.Clamp01(collisionSpread);
            set => collisionSpread = Mathf.Clamp01(value);
        }

        /// <summary>How far outside a collider fur begins to lean away, in meters.</summary>
        public float CollisionMargin
        {
            get => Mathf.Clamp(collisionMargin, 0f, 2f);
            set => collisionMargin = Mathf.Clamp(value, 0f, 2f);
        }

        /// <summary>Colliders resolved against in the most recent frame (debugging / profiling).</summary>
        public int ActiveColliderCount => _activeColliderCount;

        /// <summary>Whether fur dries out over time, or holds its wetness until something wets it further.</summary>
        public bool EnableDrying
        {
            get => enableDrying;
            set => enableDrying = value;
        }

        /// <summary>The live regional wetness texture, or null when the map is disabled. For debugging.</summary>
        public Texture RegionalWetnessTexture => _wetnessMap?.Texture;

        /// <summary>
        /// Wipes accumulated regional wetness back to fully dry. Call after a teleport,
        /// a respawn or a cutscene cut, where a gradual dry-off would read as a mistake.
        /// </summary>
        public void ResetWetness()
        {
            _wetnessMap?.Reset();
        }

        // ------------------------------------------------------------------ //
        //  Presets
        // ------------------------------------------------------------------ //
        /// <summary>
        /// Which built-in preset this component is pointed at. Setting it applies
        /// nothing - see <see cref="ApplyPreset"/>.
        /// </summary>
        public FurPresetId Preset
        {
            get => preset;
            set => preset = value;
        }

        /// <summary>
        /// Writes the selected preset over this component and over its fur material.
        /// Returns false when nothing was selected, or when there is no material to
        /// write through.
        ///
        /// THIS WRITES A SHARED ASSET. The material half lands on the material this
        /// controller points at, which any number of other objects may also be using.
        /// That is the intended behaviour - a coat is the pair, and writing only half of
        /// a preset would produce a look neither half was tuned for - but it is the part
        /// worth knowing before calling this. The Editor path wraps both writes in a
        /// single Undo group; a runtime caller gets no such safety and should be
        /// applying to a material instance it owns.
        ///
        /// What it leaves alone is listed on FurPreset, and the short version is:
        /// anything that belongs to this OBJECT rather than to the look - the material
        /// reference, painted detail maps, inertia bones, layer masks.
        /// </summary>
        public bool ApplyPreset()
        {
            FurPreset p = FurPreset.Get(preset);
            if (p == null || furMaterial == null)
                return false;

            p.ApplyTo(furMaterial);
            ApplyPresetToComponent(p);
            return true;
        }

        /// <summary>
        /// The component half on its own, without the material.
        ///
        /// Public because the Editor lives in its own assembly and needs it: an Undo has
        /// to be recorded against this object and against the material BEFORE either is
        /// written, which is not something ApplyPreset can do from in here. Applying only
        /// this half leaves the component describing a coat the material is not drawing,
        /// so prefer ApplyPreset unless you are handling the material yourself.
        /// </summary>
        public void ApplyPresetToComponent(FurPreset p)
        {
            if (p == null)
                return;

            shadowMode     = p.ShadowMode;
            receiveShadows = p.ReceiveShadows;

            enableLod            = p.EnableLod;
            shellCount           = p.ShellCount;
            hardCullDistance     = p.HardCullDistance;
            shellCountByDistance = new AnimationCurve(p.ShellCountByDistance);

            maxStrandCount          = p.MaxStrandCount;
            ribbonSegments          = p.RibbonSegments;
            ribbonSeed              = p.RibbonSeed;
            ribbonDensityByDistance = new AnimationCurve(p.RibbonDensityByDistance);

            followBlendShapes    = p.FollowBlendShapes;
            motionInertia        = p.MotionInertia;
            dragStrength         = p.DragStrength;
            accelerationResponse = p.AccelerationResponse;
            inertiaDamping       = p.InertiaDamping;
            inertiaDampingRatio  = p.InertiaDampingRatio;
            inertiaMode          = p.InertiaMode;
            boneInfluenceRadius  = p.BoneInfluenceRadius;
            angularInertiaScale  = p.AngularInertiaScale;

            wetness              = p.Wetness;
            enableWetnessMap     = p.EnableWetnessMap;
            wetnessMapResolution = p.WetnessMapResolution;
            enableDrying         = p.EnableDrying;
            wettingRate          = p.WettingRate;
            dryingRate           = p.DryingRate;
            contactFeather       = p.ContactFeather;

            colorMapScaleOffset = p.ColorMapScaleOffset;
            colorMapFill        = p.ColorMapFill;
            detailUvSet         = p.DetailUvSet;
            detailMapResolution = p.DetailMapResolution;
            colorMapTint        = p.ColorMapTint;
            colorMapStrength    = p.ColorMapStrength;
            useBandGradient     = p.UseBandGradient;

            enableCollision          = p.EnableCollision;
            collisionStrength        = p.CollisionStrength;
            collisionMargin          = p.CollisionMargin;
            collisionSpread          = p.CollisionSpread;
            maxSimultaneousColliders = p.MaxSimultaneousColliders;

            // The detail maps are keyed on their resolution and UV set, and both may have
            // just moved. Nothing else needs poking: FurAttributeMap.Update compares what
            // it holds against what it was produced from, so a preset that did not change
            // either of them costs one comparison and no rebake.
            MarkAttributeMapsDirty();

            // The band texture is rebuilt from the gradient on demand, and the gradient
            // is deliberately not part of a preset - but the ENABLE flag is, so a coat
            // switching to banding off has to stop sampling a texture it no longer wants.
            DestroyRuntimeObject(ref _bandTexture);
        }

        // ------------------------------------------------------------------ //
        //  Detail maps - runtime API
        // ------------------------------------------------------------------ //
        //
        //  These maps are baked on demand, not per frame, so gameplay drives them by
        //  saying WHAT CHANGED rather than by pushing a value every tick. Assigning
        //  through any setter here marks the map dirty and the next frame rebakes it;
        //  nothing rebakes while nobody is asking.
        //
        //  Setters compare before assigning. That is not a micro-optimisation - it is
        //  what makes the obvious idiom safe. Code that reassigns the same texture
        //  every frame (a state machine writing its current coat, say) would otherwise
        //  never actually settle, and would rebake three maps every frame forever.
        //

        /// <summary>Source texture for the fur LENGTH map. Setting it rebakes on the next frame.</summary>
        public Texture LengthMap
        {
            get => lengthMap;
            set
            {
                if (lengthMap == value)
                    return;

                lengthMap = value;
                _lengthAttributeMap?.MarkDirty();
            }
        }

        /// <summary>Source texture for the strand THICKNESS map. Setting it rebakes on the next frame.</summary>
        public Texture ThicknessMap
        {
            get => thicknessMap;
            set
            {
                if (thicknessMap == value)
                    return;

                thicknessMap = value;
                _thicknessAttributeMap?.MarkDirty();
            }
        }

        /// <summary>Source texture for the fur COLOR map. Setting it rebakes on the next frame.</summary>
        public Texture ColorMap
        {
            get => colorMap;
            set
            {
                if (colorMap == value)
                    return;

                colorMap = value;
                _colorAttributeMap?.MarkDirty();
            }
        }

        /// <summary>
        /// Source texture for the COMB map - which way the hair lies, as a tangent-space
        /// direction encoded in RG. Setting it rebakes on the next frame.
        /// </summary>
        public Texture CombMap
        {
            get => combMap;
            set
            {
                if (combMap == value)
                    return;

                combMap = value;
                _combAttributeMap?.MarkDirty();
            }
        }

        /// <summary>
        /// Which UV set every per-object map is stored in - wetness and all three detail
        /// channels. Setting it rebuilds them from their sources on the next frame,
        /// painted strokes included: a texel means a different piece of surface in each
        /// set, so there is nothing to carry across. See <see cref="FurDetailUvSet"/>.
        /// </summary>
        public FurDetailUvSet DetailUvSet
        {
            get => detailUvSet;
            set
            {
                if (detailUvSet == value)
                    return;

                detailUvSet = value;

                // The warning is about one mesh and one setting, so a change of either
                // deserves a fresh look rather than being silenced by an earlier one.
                _detailUvWarningShown = false;
            }
        }

        /// <summary>
        /// Baked resolution shared by all three maps. Clamped to a power of two in
        /// [32, 2048]; setting it reallocates and rebakes on the next frame.
        ///
        /// One value rather than three because the three maps describe the same surface
        /// at the same scale. A coat whose length map needs a crisp border needs it in
        /// every channel, and none has ever wanted a quarter of the others' resolution.
        /// </summary>
        public int DetailMapResolution
        {
            get => detailMapResolution;
            set => detailMapResolution = ClampMapResolution(value);
        }

        /// <summary>The colour the coat's colour map holds where no texture reaches.</summary>
        public Color ColorMapFill
        {
            get => colorMapFill;
            set
            {
                if (colorMapFill == value)
                    return;

                colorMapFill = value;
                RebakeColorMap();
            }
        }

        /// <summary>Tiling and offset the colour map's source texture is sampled with.</summary>
        public Vector4 ColorMapScaleOffset
        {
            get => colorMapScaleOffset;
            set
            {
                if (colorMapScaleOffset == value)
                    return;

                colorMapScaleOffset = value;
                RebakeColorMap();
            }
        }

        /// <summary>Tint multiplied into the color map. Read live by the shader - no rebake.</summary>
        public Color ColorMapTint
        {
            get => colorMapTint;
            set
            {
                if (colorMapTint == value)
                    return;

                bool wasFlat = ColorMapHasFlatValue;
                colorMapTint = value;

                // Only when it crosses the line between "nothing to say" and "something":
                // the tint itself is live and needs no bake, but whether the map EXISTS
                // depends on it.
                if (wasFlat != ColorMapHasFlatValue)
                    RebakeColorMap();
            }
        }

        /// <summary>
        /// How far the color map is applied, 0..1. Read live by the shader, so this is
        /// the handle to fade a marking in or out; 0 contributes exactly nothing.
        /// </summary>
        public float ColorMapStrength
        {
            get => colorMapStrength;
            set => colorMapStrength = Mathf.Clamp01(value);
        }

        /// <summary>
        /// Allocates a channel's map if it does not exist yet, so something can paint
        /// into it, and reports whether one is now available.
        ///
        /// Needed because there is no enable toggle any more: a map normally comes into
        /// being by having a source texture assigned, and painting is the other way -
        /// you ask for an empty one and put the first stroke in it yourself.
        /// </summary>
        public bool EnsureDetailMap(FurDetailChannel channel)
        {
            if (_activeMesh == null || attributeBakeShader == null)
                return false;

            int uvChannel = ResolveDetailUvChannel();

            switch (channel)
            {
                case FurDetailChannel.Length:
                    _lengthAttributeMap ??= new FurAttributeMap("FurLengthMap");
                    _lengthAttributeMap.UvChannel = uvChannel;
                    return _lengthAttributeMap.Update(attributeBakeShader, detailMapResolution,
                        _activeMesh, lengthMap, NeutralScalar, new Vector4(1f, 1f, 0f, 0f));

                case FurDetailChannel.Thickness:
                    _thicknessAttributeMap ??= new FurAttributeMap("FurThicknessMap");
                    _thicknessAttributeMap.UvChannel = uvChannel;
                    return _thicknessAttributeMap.Update(attributeBakeShader, detailMapResolution,
                        _activeMesh, thicknessMap, NeutralScalar, new Vector4(1f, 1f, 0f, 0f));

                case FurDetailChannel.Color:
                    _colorAttributeMap ??= new FurAttributeMap("FurColorMap");
                    _colorAttributeMap.UvChannel = uvChannel;
                    return _colorAttributeMap.Update(attributeBakeShader, detailMapResolution,
                        _activeMesh, colorMap, colorMapFill, colorMapScaleOffset);

                case FurDetailChannel.Comb:
                    _combAttributeMap ??= new FurAttributeMap("FurCombMap");
                    _combAttributeMap.UvChannel = uvChannel;

                    // No tiling for the comb, ever. The other maps let a source texture be
                    // repeated over the model; a comb repeated across a seam would send the
                    // hair in a different direction on each tile of it.
                    return _combAttributeMap.Update(attributeBakeShader, detailMapResolution,
                        _activeMesh, combMap, NeutralComb, new Vector4(1f, 1f, 0f, 0f));

                default:
                    return false;
            }
        }

        /// <summary>Whether a channel currently has a map - a source texture, strokes, or both.</summary>
        public bool HasDetailMap(FurDetailChannel channel)
        {
            switch (channel)
            {
                case FurDetailChannel.Length:    return _lengthAttributeMap != null;
                case FurDetailChannel.Thickness: return _thicknessAttributeMap != null;
                case FurDetailChannel.Color:     return _colorAttributeMap != null;
                case FurDetailChannel.Comb:      return _combAttributeMap != null;
                default:                         return false;
            }
        }

        /// <summary>
        /// Releases a channel's map and clears its source texture - the way to give the
        /// memory back and return the fur to what the material alone says.
        ///
        /// Deliberately the ONLY thing that frees a map. Clearing a texture slot leaves
        /// the map allocated and neutral, because that is the state you paint from, and
        /// a slot being emptied must never silently discard someone's strokes.
        /// </summary>
        public void ClearDetailMap(FurDetailChannel channel)
        {
            switch (channel)
            {
                case FurDetailChannel.Length:
                    lengthMap = null;
                    _lengthAttributeMap?.Dispose();
                    _lengthAttributeMap = null;
                    break;

                case FurDetailChannel.Thickness:
                    thicknessMap = null;
                    _thicknessAttributeMap?.Dispose();
                    _thicknessAttributeMap = null;
                    break;

                case FurDetailChannel.Color:
                    colorMap = null;
                    _colorAttributeMap?.Dispose();
                    _colorAttributeMap = null;
                    break;

                case FurDetailChannel.Comb:
                    combMap = null;
                    _combAttributeMap?.Dispose();
                    _combAttributeMap = null;
                    break;
            }
        }

        /// <summary>The live baked length map, or null while the map is off. For debugging.</summary>
        public Texture BakedLengthMap => _lengthAttributeMap?.Texture;

        /// <summary>The live baked thickness map, or null while the map is off. For debugging.</summary>
        public Texture BakedThicknessMap => _thicknessAttributeMap?.Texture;

        /// <summary>The live baked color map, or null while the map is off. For debugging.</summary>
        public Texture BakedColorMap => _colorAttributeMap?.Texture;

        /// <summary>The live baked comb map, or null when the coat has never been combed.</summary>
        public Texture BakedCombMap => _combAttributeMap?.Texture;

        /// <summary>
        /// Total number of detail-map bakes this object has performed - for debugging.
        ///
        /// The useful reading is that it holds STILL. These maps are meant to bake when
        /// something changes and never otherwise; a count that climbs frame after frame
        /// means a source texture, mesh or resolution is being handed back different
        /// every frame, and the object is paying a full rasterisation for it.
        /// </summary>
        public int DetailMapBakeCount =>
            (_lengthAttributeMap != null ? _lengthAttributeMap.BakeCount : 0) +
            (_thicknessAttributeMap != null ? _thicknessAttributeMap.BakeCount : 0) +
            (_colorAttributeMap != null ? _colorAttributeMap.BakeCount : 0) +
            (_combAttributeMap != null ? _combAttributeMap.BakeCount : 0);

        /// <summary>
        /// Forces every detail map to rebake on the next frame.
        ///
        /// Needed when something the maps depend on changed WITHOUT going through the
        /// properties above - most often the contents of a source texture that was
        /// written to in place, which the controller has no way to notice on its own.
        /// Also the way to reclaim a map after gameplay has drawn into
        /// <see cref="BakedLengthMap"/> and friends directly.
        /// </summary>
        public void RebakeDetailMaps()
        {
            MarkAttributeMapsDirty();
        }

        /// <summary>Forces just the length map to rebake. See <see cref="RebakeDetailMaps"/>.</summary>
        public void RebakeLengthMap() => _lengthAttributeMap?.MarkDirty();

        /// <summary>Forces just the thickness map to rebake. See <see cref="RebakeDetailMaps"/>.</summary>
        public void RebakeThicknessMap() => _thicknessAttributeMap?.MarkDirty();

        /// <summary>Forces just the color map to rebake. See <see cref="RebakeDetailMaps"/>.</summary>
        public void RebakeColorMap() => _colorAttributeMap?.MarkDirty();

        /// <summary>Forces just the comb map to rebake. See <see cref="RebakeDetailMaps"/>.</summary>
        public void RebakeCombMap() => _combAttributeMap?.MarkDirty();

        // ------------------------------------------------------------------ //
        //  Detail maps - painting
        // ------------------------------------------------------------------ //
        //
        //  Strokes go straight into the baked RenderTexture. That is what makes them
        //  cheap enough to run under a moving cursor, and it is also what makes them
        //  VOLATILE: the texture is per-object and lives only as long as the component
        //  is enabled, so paint that matters has to be written back to a source
        //  texture. The Draw Fur tool does exactly that; gameplay damage usually
        //  should not bother, because it is meant to be transient.
        //
        //  Painting suspends the automatic rebake for the map it touches, so a stroke
        //  survives the next Inspector edit. RebakeDetailMaps() is the way back to the
        //  authored source.
        //

        /// <summary>
        /// The mesh the fur is currently drawn from - the baked one for a skinned
        /// source. This is the surface brush strokes land on, so tools raycast against
        /// exactly this rather than against the renderer's shared mesh.
        /// </summary>
        public Mesh ActiveMesh => _activeMesh;

        /// <summary>The matrix <see cref="ActiveMesh"/> is drawn with.</summary>
        public Matrix4x4 ActiveObjectToWorld => _objectToWorld;

        /// <summary>
        /// Paints fur LENGTH. <paramref name="value"/> is the raw map value, 0..1, which is
        /// multiplied by <see cref="DetailScaleMax"/>: 0.5 leaves the material's Fur Length
        /// alone, 0 shaves the region bare and 1 doubles it.
        /// </summary>
        public bool PaintLength(FurBrush brush, float value)
        {
            return PaintMap(_lengthAttributeMap, brush, ScalarColor(value));
        }

        /// <summary>
        /// Paints strand THICKNESS. <paramref name="value"/> is the raw map value, 0..1, on
        /// the same scale as <see cref="PaintLength"/>: 0.5 is the material's own value.
        /// </summary>
        public bool PaintThickness(FurBrush brush, float value)
        {
            return PaintMap(_thicknessAttributeMap, brush, ScalarColor(value));
        }

        /// <summary>Paints fur COLOR. The value is the tint multiplied into the albedo.</summary>
        public bool PaintColor(FurBrush brush, Color value)
        {
            return PaintMap(_colorAttributeMap, brush, value);
        }

        /// <summary>
        /// Combs the fur: lays it down along <paramref name="directionWS"/>.
        ///
        /// The direction is given in WORLD space - the way the artist's hand moved across
        /// the model - and each texel turns it into its own tangent frame as the stroke
        /// lands. That conversion cannot happen out here, because there is no single frame
        /// to convert into: every texel has a different one, and the whole value of a comb
        /// is that it is stored relative to the surface.
        ///
        /// Length is the strength, so a direction shorter than one combs proportionally
        /// less hard and a zero vector combs the region back out flat.
        /// </summary>
        public bool PaintComb(FurBrush brush, Vector3 directionWS)
        {
            // Handed through the colour channels because that is what the paint pass
            // carries; the pass reads it as a world direction rather than as a tint. See
            // the Paint pass in FurAttributeBake.shader.
            var value = new Color(directionWS.x, directionWS.y, directionWS.z, 1f);
            return PaintMap(_combAttributeMap, brush, value, comb: true);
        }

        /// <summary>
        /// Ends a stroke, re-padding the UV islands of every painted map.
        ///
        /// Call it on mouse-up rather than after each stamp: it is a full copy plus a
        /// full-screen pass per map, and a stroke is dozens of stamps a second. Skipping
        /// it leaves a hairline of the pre-stroke colour around every seam.
        /// </summary>
        public void FinishPaintStroke()
        {
            if (_lengthAttributeMap != null && _lengthAttributeMap.HasUnsavedEdits)
                _lengthAttributeMap.FinishStroke();

            if (_thicknessAttributeMap != null && _thicknessAttributeMap.HasUnsavedEdits)
                _thicknessAttributeMap.FinishStroke();

            if (_colorAttributeMap != null && _colorAttributeMap.HasUnsavedEdits)
                _colorAttributeMap.FinishStroke();

            if (_combAttributeMap != null && _combAttributeMap.HasUnsavedEdits)
                _combAttributeMap.FinishStroke();
        }

        /// <summary>
        /// Captures the model's depth from a camera, which is what lets
        /// <see cref="FurPaintMode.Solid"/> reject surfaces hidden behind the body
        /// rather than only ones facing away.
        ///
        /// Call it when a stroke STARTS. The view cannot change while the cursor is
        /// held, so capturing per stamp would re-render the model dozens of times a
        /// second for an answer that has not moved.
        ///
        /// Optional: Solid strokes work without it, they simply cannot see occlusion.
        /// Wireframe ignores it entirely.
        /// </summary>
        public bool CaptureBrushOcclusion(Camera camera)
        {
            if (camera == null || _activeMesh == null || attributeBakeShader == null)
                return false;

            _brushOcclusion ??= new FurBrushOcclusion();

            // GL.GetGPUProjectionMatrix is what makes this correct on every graphics API:
            // it applies the platform's own clip-space conventions, including reversed Z,
            // which the depth comparison in the paint pass then relies on.
            Matrix4x4 projection = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
            Matrix4x4 viewProjection = projection * camera.worldToCameraMatrix;

            return _brushOcclusion.Capture(attributeBakeShader, _activeMesh, _objectToWorld,
                viewProjection, camera.pixelWidth, camera.pixelHeight);
        }

        /// <summary>
        /// Drops the occlusion capture. Solid strokes fall back to the facing test until
        /// something captures again - call this when the view has moved.
        /// </summary>
        public void InvalidateBrushOcclusion()
        {
            _brushOcclusion?.Invalidate();
        }

        /// <summary>Whether one channel holds strokes that are not in its source texture.</summary>
        public bool HasUnsavedPaintIn(FurDetailChannel channel)
        {
            switch (channel)
            {
                case FurDetailChannel.Length:
                    return _lengthAttributeMap != null && _lengthAttributeMap.HasUnsavedEdits;
                case FurDetailChannel.Thickness:
                    return _thicknessAttributeMap != null && _thicknessAttributeMap.HasUnsavedEdits;
                case FurDetailChannel.Color:
                    return _colorAttributeMap != null && _colorAttributeMap.HasUnsavedEdits;
                case FurDetailChannel.Comb:
                    return _combAttributeMap != null && _combAttributeMap.HasUnsavedEdits;
                default:
                    return false;
            }
        }

        /// <summary>The live baked map for one channel, or null when there is none.</summary>
        public Texture BakedDetailMap(FurDetailChannel channel)
        {
            switch (channel)
            {
                case FurDetailChannel.Length:    return BakedLengthMap;
                case FurDetailChannel.Thickness: return BakedThicknessMap;
                case FurDetailChannel.Color:     return BakedColorMap;
                case FurDetailChannel.Comb:      return BakedCombMap;
                default:                         return null;
            }
        }

        /// <summary>
        /// A copy of one channel's current contents, for an undo history. The caller owns
        /// the surface and must release it with <see cref="ReleaseDetailSnapshot"/>.
        ///
        /// Exposed here rather than on the map itself because the map type is internal:
        /// this is the seam the paint tool reaches through, and keeping it narrow means
        /// the history cannot get at anything else.
        ///
        /// Null when the channel has no map yet - there is nothing a stroke could have
        /// covered up, so there is nothing to remember.
        /// </summary>
        public RenderTexture CaptureDetailSnapshot(FurDetailChannel channel)
        {
            FurAttributeMap map = AttributeMapFor(channel);
            return map != null ? map.CreateSnapshot() : null;
        }

        /// <summary>
        /// Puts a snapshot back into its channel, and reports whether it still fitted.
        ///
        /// False means the map has been reallocated since - a resolution change, a switch
        /// of UV set - and that step no longer describes it. The caller drops the history
        /// rather than forcing it: a snapshot of a different layout restored into this one
        /// would be worse than having no undo at all.
        /// </summary>
        public bool RestoreDetailSnapshot(FurDetailChannel channel, RenderTexture snapshot)
        {
            FurAttributeMap map = AttributeMapFor(channel);
            return map != null && map.RestoreSnapshot(snapshot);
        }

        /// <summary>What one snapshot of a channel costs, so a history can budget itself.</summary>
        public long DetailSnapshotByteSize(FurDetailChannel channel)
        {
            FurAttributeMap map = AttributeMapFor(channel);
            return map != null ? map.SnapshotByteSize : 0;
        }

        /// <summary>Releases a surface handed out by <see cref="CaptureDetailSnapshot"/>.</summary>
        public static void ReleaseDetailSnapshot(RenderTexture snapshot)
        {
            if (snapshot == null)
                return;

            if (snapshot.IsCreated())
                snapshot.Release();

            if (Application.isPlaying)
                Destroy(snapshot);
            else
                DestroyImmediate(snapshot);
        }

        // ------------------------------------------------------------------ //
        //  Hair banding
        // ------------------------------------------------------------------ //
        /// <summary>
        /// Texels the band gradient is baked into. MUST match FUR_BAND_RESOLUTION in
        /// FurShellInput.hlsl - the shader maps hair height onto texel CENTRES, and half a
        /// texel of disagreement moves every stop.
        /// </summary>
        private const int BandResolution = 128;

        /// <summary>
        /// The band gradient, as a list of stops from root to tip. Setting it rebuilds the
        /// strip the shader reads on the next frame. See <see cref="UseBandGradient"/>.
        /// </summary>
        public Gradient BandGradient
        {
            get => bandGradient;
            set
            {
                bandGradient = value ?? DefaultBandGradient();
                _bandDirty = true;
            }
        }

        /// <summary>
        /// Whether the band gradient replaces the material's Root and Tip colours. Off is
        /// the two-stop behaviour, unchanged.
        /// </summary>
        public bool UseBandGradient
        {
            get => useBandGradient;
            set
            {
                if (useBandGradient == value)
                    return;

                useBandGradient = value;
                _bandDirty = true;
            }
        }

        /// <summary>
        /// A ramp matching the shader's own Root and Tip defaults, so switching the
        /// gradient on does not jump the coat's colour before a single stop has been
        /// moved. Somewhere to start from, not a look.
        /// </summary>
        private static Gradient DefaultBandGradient()
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(0.55f, 0.55f, 0.55f), 0f),
                    new GradientColorKey(new Color(1.15f, 1.10f, 1.00f), 1f),
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(1f, 1f),
                });
            return gradient;
        }

        /// <summary>
        /// Bakes the gradient into the strip the shader samples, and resolves what the
        /// whole hair averages to.
        ///
        /// The mean is integrated rather than sampled, and that is the part worth the
        /// arithmetic. A hair too small to resolve is replaced by its average; for a
        /// straight ramp any sensible midpoint gives that, which is why the two-stop path
        /// gets away with one sample at FUR_STRAND_MEAN_HEIGHT. A banded hair has no such
        /// midpoint - land on a pale ring and the distant coat washes out, land on a dark
        /// band and it goes muddy, and which one happens is an accident of where the
        /// stops were dragged.
        ///
        /// Weighted by the strand's own width profile, 1 - t^1.35, because that is how
        /// much hair there actually is at each height: a tapered strand carries most of
        /// its area down near the root and almost none at the tip. An unweighted average
        /// would read the tip colour far too strongly, which is the same mistake
        /// FUR_STRAND_MEAN_HEIGHT exists to avoid.
        /// </summary>
        private void RebuildBandTexture()
        {
            _bandDirty = false;

            if (_bandTexture == null)
            {
                // Linear and HDR: the gradient is authored in linear (see GradientUsage on
                // the field) and its stops routinely exceed one, so an 8-bit sRGB strip
                // would clip the tips and mangle the rest.
                _bandTexture = new Texture2D(BandResolution, 1, TextureFormat.RGBAHalf, false, true)
                {
                    name = "FurBandGradient",
                    hideFlags = HideFlags.HideAndDontSave,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
            }

            Gradient gradient = bandGradient ?? DefaultBandGradient();

            var texels = new Color[BandResolution];
            Vector4 weighted = Vector4.zero;
            float weightTotal = 0f;

            for (int i = 0; i < BandResolution; i++)
            {
                // i / (n-1), so the first and last texel sit exactly ON the root and the
                // tip. The shader's sampling maps height onto these centres.
                float t = BandResolution > 1 ? i / (float)(BandResolution - 1) : 0f;

                Color value = gradient.Evaluate(t);
                texels[i] = value;

                float width = 1f - Mathf.Pow(t, 1.35f);
                weighted += (Vector4)value * width;
                weightTotal += width;
            }

            _bandTexture.SetPixels(texels);
            _bandTexture.Apply(false, false);

            _bandMean = weightTotal > 1e-6f ? weighted / weightTotal : Vector4.one;
        }

        /// <summary>The strip to bind, rebuilt if anything asked for one.</summary>
        private Texture BandTexture()
        {
            if (_bandDirty || _bandTexture == null)
                RebuildBandTexture();

            return _bandTexture;
        }

        private FurAttributeMap AttributeMapFor(FurDetailChannel channel)
        {
            switch (channel)
            {
                case FurDetailChannel.Length:    return _lengthAttributeMap;
                case FurDetailChannel.Thickness: return _thicknessAttributeMap;
                case FurDetailChannel.Color:     return _colorAttributeMap;
                case FurDetailChannel.Comb:      return _combAttributeMap;
                default:                         return null;
            }
        }

        /// <summary>Whether any detail map holds strokes that are not in its source texture.</summary>
        public bool HasUnsavedPaint =>
            (_lengthAttributeMap != null && _lengthAttributeMap.HasUnsavedEdits) ||
            (_thicknessAttributeMap != null && _thicknessAttributeMap.HasUnsavedEdits) ||
            (_colorAttributeMap != null && _colorAttributeMap.HasUnsavedEdits) ||
            (_combAttributeMap != null && _combAttributeMap.HasUnsavedEdits);

        // The two scalar maps store their value in R, but the paint pass writes RGB in
        // one go - so the value is broadcast rather than masked. Cheaper than a channel
        // mask in the shader, and it keeps a scalar map readable as a greyscale image
        // when it is written back out to a texture.
        private static Color ScalarColor(float value)
        {
            float v = Mathf.Clamp01(value);
            return new Color(v, v, v, 1f);
        }

        private bool PaintMap(FurAttributeMap map, FurBrush brush, Color value, bool comb = false)
        {
            if (map == null || _activeMesh == null)
                return false;

            RenderTexture depth = _brushOcclusion != null && _brushOcclusion.IsValid
                ? _brushOcclusion.Depth
                : null;
            Matrix4x4 viewProjection = _brushOcclusion != null
                ? _brushOcclusion.ViewProjection
                : Matrix4x4.identity;

            return map.Paint(_activeMesh, _objectToWorld, brush, value, depth, viewProjection, comb);
        }

        /// <summary>Low-poly proxy mesh the shells are drawn from. Uses the source mesh when null.</summary>
        public Mesh FurProxyMesh
        {
            get => furProxyMesh;
            set => furProxyMesh = value;
        }

        // ------------------------------------------------------------------ //
        //  Unity messages
        // ------------------------------------------------------------------ //
        private void Reset()
        {
            sourceRenderer = GetComponent<Renderer>();
        }

        private void OnEnable()
        {
            CacheSource();

            // Belt and braces alongside the load-time hook: a shader variant with an
            // unbound structured buffer does not render at all, and this costs nothing
            // once the defaults are already bound.
            FurShaderDefaults.Ensure();

            _propertyBlock ??= new MaterialPropertyBlock();
            _previousPosition = transform.position;
            _previousRotation = transform.rotation;
            _lastRealtime = Time.realtimeSinceStartup;
            _smoothedForce = Vector3.zero;
            _forceVelocity = Vector3.zero;
            _externalForce = Vector3.zero;
            _previousVelocity = Vector3.zero;
            _smoothedDragForce = Vector3.zero;
            _dragForceVelocity = Vector3.zero;
            _smoothedAngularForce = Vector3.zero;
            _angularForceVelocity = Vector3.zero;
            _previousAngularVelocity = Vector3.zero;
            _boneStateCount = -1;
            _previousObjectToWorldValid = false;
            _lastMotionVectorFrame = -1;
            ClearUnusedBoneSlots(0);
            ClearUnusedColliderSlots(0);

            ShowSourceRenderer();

            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;

            ShowSourceRenderer();

            DestroyFuzzMaterial();
            DestroySkinMaterial();
            DestroyRuntimeObject(ref _bakedMesh);

            // Rebuilt on demand from the gradient, so throwing it away costs one bake and
            // keeping it would leak a texture per furred object across every enable cycle.
            DestroyRuntimeObject(ref _bandTexture);
            _bandDirty = true;

            _wetnessMap?.Dispose();
            _wetnessMap = null;

            DisposeAttributeMaps();

            _brushOcclusion?.Dispose();
            _brushOcclusion = null;

            _distribution?.Dispose();
            _distribution = null;

            _skinMatrixBuffer?.Release();
            _skinMatrixBuffer = null;

            // Re-resolved on the next enable rather than kept: the component may come back
            // on a different rig, and a stale bone array skins the fur to a skeleton that
            // is no longer there.
            _skinBones = null;
            _bindposes = null;
            _skinMatrixFrame = -1;
            _ribbonWarningShown = false;
            _materialWarningShown = false;
            _instancingWarningShown = false;
        }

        private void OnValidate()
        {
            if (shellCountByDistance == null || shellCountByDistance.length == 0)
                shellCountByDistance = new AnimationCurve(new Keyframe(0f, 32f), new Keyframe(50f, 1f));

            // Fills the field in on first sight so the Inspector shows the number actually
            // in use, rather than a zero that quietly means "ask the curve".
            shellCount = Mathf.Clamp(ResolveFixedShellCount(), 1, MaxShellCount);

            wetnessMapResolution = Mathf.Clamp(Mathf.NextPowerOfTwo(wetnessMapResolution), 32, 1024);

            // A Gradient has no cheap way to say whether it changed, and this fires on any
            // Inspector touch - so the strip is simply rebuilt. It is 128 texels.
            _bandDirty = true;

            ribbonSegments = Mathf.Clamp(ribbonSegments, 1, FurRibbonMesh.MaxSegments);

            if (ribbonDensityByDistance == null || ribbonDensityByDistance.length == 0)
                ribbonDensityByDistance = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(60f, 0.1f));

            // A density or budget change has to reach the baked roots, and the bake keys
            // itself on the count it was built with - so simply letting the next frame ask
            // for the new count is enough to rebuild it. Clearing the warning latch is not:
            // that one is about a mesh that could not be sampled at all, and the reason it
            // could not may well have just been fixed.
            _ribbonWarningShown = false;
            _materialWarningShown = false;
            _instancingWarningShown = false;

            detailMapResolution = ClampMapResolution(detailMapResolution);

            // Deliberately does NOT mark the detail maps dirty.
            //
            // It used to, on the reasoning that OnValidate cannot tell which field was
            // touched so rebaking all three was the honest answer. That was written
            // before the maps could be PAINTED, and it became actively destructive the
            // moment they could: a rebake discards brush work, so nudging the shell
            // count - or any other unrelated field on this component - silently wiped
            // every stroke on the object.
            //
            // Nothing is lost by removing it. FurAttributeMap.Update already compares
            // the source texture, resolution and mesh against what its contents were
            // produced from, so every change an Inspector edit can make is detected on
            // its own. What OnValidate added was catching a source texture edited IN
            // PLACE, and RebakeDetailMaps() is the explicit way to say that.

            collisionStrength = Mathf.Clamp(collisionStrength, 0f, 5f);
            collisionMargin = Mathf.Clamp(collisionMargin, 0f, 2f);
            maxSimultaneousColliders = Mathf.Clamp(maxSimultaneousColliders, 1, MaxFurColliders);

            if (isActiveAndEnabled)
            {
                CacheSource();
                ShowSourceRenderer();
            }
        }

        private static void DestroyRuntimeObject<T>(ref T obj) where T : Object
        {
            if (obj == null)
                return;

            if (Application.isPlaying) Destroy(obj);
            else DestroyImmediate(obj);
            obj = null;
        }

        // ------------------------------------------------------------------ //
        //  Source resolution
        // ------------------------------------------------------------------ //
        [ContextMenu("Auto-Find Source Renderer")]
        private void CacheSource()
        {
            if (sourceRenderer == null)
                sourceRenderer = GetComponent<Renderer>();

            _skinnedRenderer = sourceRenderer as SkinnedMeshRenderer;
            _meshFilter = sourceRenderer != null ? sourceRenderer.GetComponent<MeshFilter>() : null;

            // Resolved once here rather than per frame. Serializing the reference is
            // what pulls a Hidden/ shader into a player build - Shader.Find alone
            // would return null there.
            if (wetnessSplatShader == null)
                wetnessSplatShader = Shader.Find(WetnessSplatShaderName);

            if (attributeBakeShader == null)
                attributeBakeShader = Shader.Find(AttributeBakeShaderName);
        }

        /// <summary>
        /// Clears any rendering suppression this component may have left on the source.
        ///
        /// There used to be a "Hide Source Renderer" toggle here, and with it on the coat
        /// drew the skin itself. That is one surface being drawn by two different
        /// materials depending on a checkbox, and it fails in a way nobody attributes to
        /// the checkbox: a model whose own material is skin-coloured, with a fur material
        /// left at a white Base Colour, shows WHITE through the coat instead of its own
        /// texture. Both surfaces are being drawn exactly as asked, and no amount of
        /// tinting work in the shader fixes it.
        ///
        /// The object's own renderer draws its own surface now, always. This call only
        /// undoes a forceRenderingOff left behind by an older version of this component,
        /// which is why it is unconditional rather than guarded by a flag.
        /// </summary>
        private void ShowSourceRenderer()
        {
            if (sourceRenderer != null)
                sourceRenderer.forceRenderingOff = false;
        }

        // ------------------------------------------------------------------ //
        //  Drawing
        // ------------------------------------------------------------------ //
        private void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (camera == null || sourceRenderer == null || furMaterial == null)
                return;

            // Skip preview and reflection cameras - unnecessary cost.
            if (camera.cameraType == CameraType.Preview || camera.cameraType == CameraType.Reflection)
                return;

            // Culling mask check
            if ((camera.cullingMask & (1 << gameObject.layer)) == 0)
                return;

            // --- CPU-side culling, BEFORE any of the per-frame work ---
            // This used to sit after UpdateFrameState, which meant an off-screen creature
            // still paid for its skinned mesh bake, its wetness map render and two physics
            // queries every frame purely to be discarded a few lines later. The renderer's
            // own bounds plus the fur length is a conservative stand-in for the real fur
            // bounds - no shader term can displace a vertex further than one fur length
            // (they all only rotate the shell direction) - so culling on it is exact,
            // not an approximation.
            // _FurLength lives inside UnityPerMaterial, so it is deliberately a material-
            // wide value - a MaterialPropertyBlock cannot override it per object, and the
            // old comment claiming this "ignores the MPB" described a capability the
            // buffer layout never offered. What is worth avoiding is the HasProperty call:
            // that one does a name lookup, and it was running per object per camera per
            // frame to answer a question that only changes when the material does.
            if (_furLengthPropertyChecked != furMaterial)
            {
                _furLengthPropertyChecked = furMaterial;
                _hasFurLengthProperty = furMaterial.HasProperty(FurLengthId);
            }

            // Resolved into meters here, because everything downstream of it - the cull
            // bounds, the pixel height the LOD works from - is a real distance. The
            // material states a FRACTION of the object; the object is the only thing that
            // knows what that comes to, so its size is measured first.
            ResolveObjectSize();
            _furLength = (_hasFurLengthProperty ? furMaterial.GetFloat(FurLengthId) : 0.05f) * _objectSize;

            Bounds cullBounds = sourceRenderer.bounds;
            cullBounds.Expand(ResolvedMaxFurLength() * 2f);

            // In object-lengths, not meters. An absolute cap here is the same trap the LOD
            // curves fell into - on a model imported at 150x scale it fires while the
            // creature still fills the screen - and this one does not thin the coat, it
            // deletes it.
            if (hardCullDistance > 0f &&
                Vector3.Distance(camera.transform.position, cullBounds.center) >
                    hardCullDistance * Mathf.Max(_objectSize, 1e-4f))
                return;

            GeometryUtility.CalculateFrustumPlanes(camera, _frustumPlanes);
            if (!GeometryUtility.TestPlanesAABB(_frustumPlanes, cullBounds))
                return;

            UpdateFrameState();

            if (_activeMesh == null)
                return;

            if (!CanSubmitDraws())
                return;

            FurType furType = ResolveFurType();

            if (furType == FurType.Shells)
            {
                int shells = ResolveShellCount(camera);
                if (shells <= 0)
                    return;

                _activeShellCount = shells;
                _activeRibbonCount = 0;
                _requestedRibbonCount = 0;

                // Two things the fragment stage cannot see, and the LOD decided both:
                // how much HEIGHT has been taken off, and how many LAYERS have been
                // removed. The second one matters more than it looks - the distance curve
                // takes a coat from thirty-two shells to four long before the fur is short
                // enough for the height fade to start, and each shell removed is coverage
                // the coat no longer has.
                _aggregation = Mathf.Clamp01(Mathf.Max(1f - _lodFade, ResolveLayerGap(shells)));
                _countFraction = 1f;

                RenderShells(camera, shells);
            }
            else
            {
                // ResolveShellCount is still what runs here, and not only for its return
                // value: it is where _lodFade is decided, and the ribbon types shorten
                // their hair over the last stretch exactly as the shells do. The count it
                // returns is discarded - a ribbon coat has no layers.
                ResolveShellCount(camera);
                _activeShellCount = 1;

                if (!RenderRibbons(camera, furType))
                    return;
            }

            // After the draws, and only if they happened: the screen-space layer is a haze
            // over pixels this coat actually put on screen, so a frame where it was culled,
            // shortened to nothing or refused a rebuild must not leave a halo behind.
            SubmitScreenFuzz(camera);

            // Advanced after the draw, and only once per frame, so every camera in the
            // frame reprojects against the same previous transform.
            if (_lastMotionVectorFrame != Time.frameCount)
            {
                _lastMotionVectorFrame = Time.frameCount;
                _previousObjectToWorld = _objectToWorld;
            }
        }

        /// <summary>
        /// How big this object is, in meters.
        ///
        /// From the mesh's BIND POSE rather than the renderer's live bounds. A skinned
        /// creature's bounds change as it moves, and fur whose length breathed with a walk
        /// cycle would be a worse bug than fur of the wrong length. The renderer is the
        /// fallback only when there is no mesh to ask.
        ///
        /// This is what every strand setting on the material is a fraction of, so it is
        /// resolved before anything reads one - see FurObjectSize in FurShellInput.hlsl.
        /// </summary>
        private void ResolveObjectSize()
        {
            Mesh mesh = ResolveRootMesh();

            if (mesh != null)
            {
                float linearScale = Mathf.Sqrt(FurStrandDistribution.AreaScale(transform.lossyScale));
                _objectSize = Mathf.Max(mesh.bounds.extents.magnitude * linearScale, 1e-3f);

                // The BIND pose's centre, carried into world space by the transform.
                //
                // Deliberately not the renderer's live bounds - see LodDistanceMeters. The
                // bind pose is the one description of this object that does not change while
                // it animates, which is the entire property the LOD needs from it.
                _lodCenter = transform.TransformPoint(mesh.bounds.center);
                return;
            }

            _objectSize = sourceRenderer != null
                ? Mathf.Max(sourceRenderer.bounds.extents.magnitude, 1e-3f)
                : 1f;

            _lodCenter = sourceRenderer != null ? sourceRenderer.bounds.center : transform.position;
        }

        /// <summary>
        /// Re-measures the mesh's UV scale when the mesh or the object's scale changes.
        ///
        /// Measured on the SHARED mesh rather than the baked one: the unwrap is the same
        /// whatever pose the skeleton is in, and the shared mesh is what the per-mesh
        /// cache is keyed on, so a herd of the same creature measures it once between them.
        /// </summary>
        private void UpdateUvScale()
        {
            Mesh mesh = ResolveRootMesh();
            float linearScale = Mathf.Sqrt(FurStrandDistribution.AreaScale(transform.lossyScale));

            if (mesh == null)
            {
                _metersPerUV = _objectSize;
                _metersPerUVMax = _objectSize * 64f;
                return;
            }

            if (mesh != _metersPerUvMesh)
            {
                _metersPerUvMesh = mesh;
                _meshMetersPerUv = FurStrandDistribution.MetersPerUvOf(mesh);
            }

            if (_meshMetersPerUv.x > 0f)
            {
                // The scale term is re-applied every frame because a transform can be
                // animated while the mesh behind it never changes. It is a multiply, not a
                // re-measure - the measurement itself is cached per mesh.
                _metersPerUV = Mathf.Max(_meshMetersPerUv.x * linearScale, 1e-4f);
                _metersPerUVMax = Mathf.Max(_meshMetersPerUv.y * linearScale, _metersPerUV);
                return;
            }

            // The mesh could not be read, which shell fur is entitled to. The shader only
            // wants this as a BAND to clamp its own measurement into - a factor of four
            // either way - so an estimate of the right order does the job, and the object's
            // own size is one: a model is usually unwrapped into roughly the unit square,
            // so its extent across is roughly how many meters one UV unit covers.
            _metersPerUV = _objectSize;

            // Nothing was measured, so nothing is known about how uneven this unwrap is.
            // A generous ceiling is the safe side of that: it lets the per-fragment
            // measurement correct a badly stretched island, and the only thing it gives up
            // is catching a derivative spike, which the minimum pixel width already covers.
            _metersPerUVMax = _metersPerUV * 64f;
        }

        /// <summary>
        /// Warns once if GPU instancing is disabled on the material.
        /// (The material asset is never modified at runtime - that would be a
        /// permanent asset change and would show up as noise in version control.)
        /// </summary>
        /// <summary>
        /// Whether this object is in a state where submitting a draw is safe.
        ///
        /// Both checks used to be warnings that the code then walked straight past.
        /// Graphics.RenderMeshInstanced does not tolerate a material without instancing -
        /// it THROWS - so what the caller actually got was one warning followed by an
        /// InvalidOperationException per submesh per camera per frame, buried under which
        /// was the one line that said what to do about it. A misconfigured object should
        /// cost one warning and then draw nothing, quietly, until it is fixed.
        /// </summary>
        private bool CanSubmitDraws()
        {
            if (furMaterial == null)
                return false;

            // A material that is not this shader at all. Common enough to be worth naming:
            // dragging the object's own body material into the Fur Material slot is the
            // first thing somebody tries.
            if (!furMaterial.HasProperty(FurLengthId))
            {
                if (!_materialWarningShown)
                {
                    _materialWarningShown = true;
                    Debug.LogWarning(
                        $"[FurController] '{furMaterial.name}' is not a fur material - it has no " +
                        "Fur Length property, so it was probably not made from the " +
                        "FURFLUX/Fur Lit shader. Nothing is drawn until one is assigned.", this);
                }

                return false;
            }

            if (furMaterial.enableInstancing)
                return true;

            VerifyInstancingEnabled();
            return false;
        }

        private void VerifyInstancingEnabled()
        {
            if (_instancingWarningShown || furMaterial == null || furMaterial.enableInstancing)
                return;

            _instancingWarningShown = true;
            Debug.LogWarning(
                $"[FurController] GPU Instancing is disabled on '{furMaterial.name}', and " +
                "Graphics.RenderMeshInstanced draws nothing without it. Select the material " +
                "once - its inspector turns this back on and keeps it on.", this);
        }

        /// <summary>Runs once per frame: mesh resolution, inertia, bounds.</summary>
        private void UpdateFrameState()
        {
            if (_lastUpdatedFrame == Time.frameCount)
                return;
            _lastUpdatedFrame = Time.frameCount;

            // --- Mesh resolution ---
            if (_skinnedRenderer != null)
            {
                if (_bakedMesh == null)
                {
                    _bakedMesh = new Mesh { name = "FurBakedMesh", hideFlags = HideFlags.HideAndDontSave };
                    _bakedMesh.MarkDynamic();
                }

                // Bake the animated mesh every frame so fur follows the skeleton.
                // (A proxy mesh cannot be used with a skinned source - the baked mesh IS the
                // skeleton's output.)
                _skinnedRenderer.BakeMesh(_bakedMesh, useScale: false);
                _activeMesh = _bakedMesh;
                _objectToWorld = _skinnedRenderer.transform.localToWorldMatrix;
            }
            else
            {
                _activeMesh = furProxyMesh != null
                    ? furProxyMesh
                    : (_meshFilter != null ? _meshFilter.sharedMesh : null);
                _objectToWorld = sourceRenderer.transform.localToWorldMatrix;
            }

            // --- Time step ---
            //
            // Play Mode reads Time.deltaTime so the fur obeys timeScale: it stops dead
            // when the game is paused and slows with everything else in slow motion.
            // Wall-clock time does neither - paused fur went on drifting, and in slow
            // motion it kept whipping about at full speed while the character crawled.
            //
            // Edit Mode has no meaningful Time.deltaTime, so it still measures the clock.
            float now = Time.realtimeSinceStartup;
            float dt = Application.isPlaying ? Time.deltaTime : now - _lastRealtime;
            _lastRealtime = now;

            // Frozen: leave every integrator exactly where it stands. The old code
            // clamped dt UP to a millisecond floor, which is precisely what kept the fur
            // creeping through a pause - there is no such thing as a smallest possible
            // step when no time has passed at all.
            bool timeIsRunning = dt > 0f;
            dt = Mathf.Min(dt, MaxTimeStep);

            // --- Motion inertia: strands trail opposite the object's motion ---
            //
            // The spring's target is the sum of what the caller asked for and what the
            // object's own motion implies. Keeping the caller's contribution as a
            // separate field is the whole fix: it used to be written straight into
            // _smoothedForce, which is the spring's STATE, so the integrator below
            // overwrote it a few lines later and the public setter did nothing at all
            // whenever motion inertia was on.
            //
            // Feeding it through the same spring rather than adding it to the result
            // also means an externally driven force lags, overshoots and settles like
            // real motion instead of snapping the fur to a new pose.
            Vector3 position = transform.position;
            Quaternion rotation = transform.rotation;

            Vector3 target = _externalForce;

            Vector3 dragTarget = Vector3.zero;
            Vector3 angularTarget = Vector3.zero;

            if (motionInertia && timeIsRunning)
            {
                // --- Two physically distinct forces, not one ---
                //
                // DRAG is proportional to VELOCITY. It is air resistance, and it is what
                // makes fur stream backward while the object is actually travelling.
                //
                // INERTIA is proportional to ACCELERATION. In the object's own
                // (accelerating) frame a strand feels a pseudo-force of -m*a. That force
                // is ZERO at constant velocity no matter how fast the object is going,
                // and it is largest exactly when the motion CHANGES. It is the entire
                // reason a hard stop throws fur forward past its rest pose while a gentle
                // stop does not.
                Vector3 linearVelocity = (position - _previousPosition) / dt;

                // Acceleration is a second difference of position, so it is noisy, and a
                // short frame divides by a small dt twice over. Bound it before it reaches
                // the spring - one hitched frame should not launch the fur.
                Vector3 linearAcceleration = Vector3.ClampMagnitude(
                    (linearVelocity - _previousVelocity) / dt, MaxTrackedAcceleration);
                _previousVelocity = linearVelocity;

                // Kept as two channels rather than summed, because the shader treats them
                // differently: drag is airflow and gets sheltered on the lee side, while
                // the inertial pseudo-force is a body force that reaches every strand
                // whichever way it faces. Summing them here would make that distinction
                // impossible to recover.
                dragTarget += Vector3.ClampMagnitude(-linearVelocity * dragStrength, 4f);
                target += Vector3.ClampMagnitude(-linearAcceleration * accelerationResponse, 4f);

                // --- Rotation ---
                //
                // A rotation cannot be expressed as one world-space force, for the same
                // reason a pair of colliders cannot be summed into one: on opposite sides
                // of the object the surface travels in opposite directions. The previous
                // version picked a single point - the object's forward axis at its bounds
                // radius - and applied that one point's velocity everywhere. Rolling about
                // the forward axis therefore produced cross(omega, forward) = 0 and the
                // fur did not react at all.
                //
                // The per-point force is -(k_drag * (omega x r) + k_accel * (alpha x r)),
                // which factors into cross(-(k_drag*omega + k_accel*alpha), r). So one
                // world vector still carries it - the cross product with each vertex's own
                // r just has to happen per vertex, in the shader.
                if (angularInertiaScale > 0f)
                {
                    // ToAngleAxis already returns 0..180 paired with the axis that makes it
                    // so; the old "if (angleDeg > 180) angleDeg -= 360" could never fire.
                    (rotation * Quaternion.Inverse(_previousRotation))
                        .ToAngleAxis(out float angleDeg, out Vector3 axis);
                    Vector3 angularVelocity = axis * (angleDeg * Mathf.Deg2Rad / dt);

                    Vector3 angularAcceleration = Vector3.ClampMagnitude(
                        (angularVelocity - _previousAngularVelocity) / dt, MaxTrackedAcceleration);
                    _previousAngularVelocity = angularVelocity;

                    angularTarget = -(angularVelocity * dragStrength
                                    + angularAcceleration * accelerationResponse) * angularInertiaScale;

                    // The shader multiplies this by each vertex's distance from the pivot,
                    // so the bound has to account for the object's reach - otherwise a big
                    // creature's outermost fur gets a force its size alone made huge. At
                    // the furthest vertex this lands on the same ceiling as the linear term.
                    float reach = Mathf.Max(sourceRenderer.bounds.extents.magnitude, 1e-3f);
                    angularTarget = Vector3.ClampMagnitude(angularTarget, 4f / reach);
                }
                else
                {
                    _previousAngularVelocity = Vector3.zero;
                }
            }
            else
            {
                // Otherwise re-enabling inertia differentiates against a velocity from
                // however long ago it was switched off.
                _previousVelocity = Vector3.zero;
                _previousAngularVelocity = Vector3.zero;
            }

            // Both springs run unconditionally. With motion inertia off the target is just
            // the caller's force - or zero, which the spring settles to on its own rather
            // than being snapped there. Hard-zeroing it here used to discard the spring's
            // momentum mid-swing, so turning inertia off made fur jump.
            //
            // The rotational force gets its own spring rather than being folded into the
            // linear one, because they are different quantities: one is a force, the other
            // is a force PER METRE of distance from the pivot. Running the same integrator
            // on both keeps their lag and settle identical, so a creature that turns while
            // running still reads as one piece of fur.
            if (timeIsRunning)
            {
                IntegrateForceSpring(ref _smoothedForce, ref _forceVelocity, target, dt);
                IntegrateForceSpring(ref _smoothedDragForce, ref _dragForceVelocity, dragTarget, dt);
                IntegrateForceSpring(ref _smoothedAngularForce, ref _angularForceVelocity, angularTarget, dt);
            }

            UpdateBoneMotion(dt, timeIsRunning);

            // Updated even while inertia is off, so re-enabling it does not read a
            // velocity accumulated over however long it was disabled.
            _previousPosition = position;
            _previousRotation = rotation;

            // --- World bounds ---
            // _furLength was already resolved by the culling step that gated this call.
            // Fur length alone is the exact bound. Every force term in the shader only
            // rotates the shell direction (it is normalised before use) and collision
            // re-seats the tip at its own strand length, so no term can ever displace a
            // vertex further than _FurLength from the surface. The old expansion by the
            // inertia and collision magnitudes was padding for a displacement that
            // cannot happen, which inflated the bounds - and therefore the culling and
            // the LOD distance - whenever the object moved.
            _worldBounds = sourceRenderer.bounds;
            _worldBounds.Expand(ResolvedMaxFurLength() * 2f);

            // Snapshot the transform for the next frame's motion vectors. Seeded to the
            // current one the first time through, so a freshly enabled object reports zero
            // motion instead of a jump from wherever the matrix happened to be.
            if (!_previousObjectToWorldValid)
            {
                _previousObjectToWorld = _objectToWorld;
                _previousObjectToWorldValid = true;
            }

            UpdateUvScale();
            GatherFurColliders();

            // Detail maps are NOT gated on the clock the way wetness is below. They
            // integrate nothing, so a frozen frame is not a reason to skip them - and
            // skipping them would leave a map that was just switched on unbaked for as
            // long as the game stayed paused, which is exactly when someone is looking
            // at it. The call returns immediately unless something is actually dirty.
            UpdateAttributeMaps();

            // Wetness accumulates and dries over time, so a frozen clock means it has
            // nothing to do - and re-rendering the map to integrate a zero step is pure
            // cost. Collider gathering above is not time-based and still runs, so fur
            // keeps reacting to anything dragged around it while the game is paused.
            if (timeIsRunning)
                UpdateWetnessMap(dt);
        }

        // ------------------------------------------------------------------ //
        //  Per-bone motion
        // ------------------------------------------------------------------ //
        /// <summary>
        /// Measures each assigned bone's movement relative to the body and turns it into a
        /// localised force the shader applies to the fur around that bone.
        ///
        /// This is what the transform-only model structurally cannot see. A squirrel
        /// standing still and flicking its tail moves no transform, so root velocity and
        /// root angular velocity are both zero and every strand on the tail is told
        /// nothing is happening - while the tail itself sweeps through the air.
        ///
        /// Deliberately bone-driven rather than per-vertex. The exact answer is to
        /// difference the skinned vertex positions against last frame's, which means
        /// keeping the skinned vertex buffer on the GPU and reading it back in the vertex
        /// stage. That is a real feature, not a tweak, and it lands on the hottest path
        /// the fur has. A handful of bones with a falloff captures what is actually
        /// visible - the parts that swing independently - for a distance test per vertex.
        /// </summary>
        private void UpdateBoneMotion(float dt, bool timeIsRunning)
        {
            _activeBoneCount = 0;

            if (inertiaMode != FurInertiaMode.TransformAndBones || inertiaBones == null)
            {
                ClearUnusedBoneSlots(0);
                _boneStateCount = -1;
                return;
            }

            // Reseed rather than differentiate against another bone's history when the
            // list is edited, which would otherwise fire a spike out of nowhere.
            int usable = Mathf.Min(inertiaBones.Length, MaxMotionBones);
            bool seed = _boneStateCount != usable;

            for (int i = 0; i < usable; i++)
            {
                Transform bone = inertiaBones[i];
                if (bone == null)
                    continue;

                int slot = _activeBoneCount++;

                // Relative to the body, so travelling and turning - both already covered
                // by the transform terms - contribute nothing here.
                Vector3 localPosition = transform.InverseTransformPoint(bone.position);

                if (seed || !timeIsRunning)
                {
                    _bonePreviousLocalPosition[slot] = localPosition;
                    _bonePreviousLocalVelocity[slot] = Vector3.zero;
                }
                else
                {
                    Vector3 localVelocity = (localPosition - _bonePreviousLocalPosition[slot]) / dt;
                    Vector3 localAcceleration = Vector3.ClampMagnitude(
                        (localVelocity - _bonePreviousLocalVelocity[slot]) / dt, MaxTrackedAcceleration);

                    _bonePreviousLocalPosition[slot] = localPosition;
                    _bonePreviousLocalVelocity[slot] = localVelocity;

                    Vector3 target = Vector3.ClampMagnitude(
                        -(localVelocity * dragStrength + localAcceleration * accelerationResponse), 4f);

                    Vector3 value = _boneSmoothedForce[slot];
                    Vector3 velocity = _boneForceVelocity[slot];
                    IntegrateForceSpring(ref value, ref velocity, target, dt);
                    _boneSmoothedForce[slot] = value;
                    _boneForceVelocity[slot] = velocity;
                }

                _boneOrigin[slot] = new Vector4(bone.position.x, bone.position.y, bone.position.z,
                    Mathf.Max(boneInfluenceRadius, 1e-3f));

                // Back to world space for the shader; the force was integrated in body space.
                Vector3 worldForce = transform.TransformDirection(_boneSmoothedForce[slot]);
                _boneForce[slot] = new Vector4(worldForce.x, worldForce.y, worldForce.z, 0f);
            }

            _boneStateCount = usable;
            ClearUnusedBoneSlots(_activeBoneCount);
        }

        /// <summary>
        /// Blanks the slots past <paramref name="used"/>. The shader reads only up to the
        /// active count, but a stale force left behind becomes a phantom the moment the
        /// count rises again.
        /// </summary>
        private void ClearUnusedBoneSlots(int used)
        {
            for (int i = used; i < MaxMotionBones; i++)
            {
                _boneOrigin[i] = Vector4.zero;
                _boneForce[i] = Vector4.zero;
                _boneSmoothedForce[i] = Vector3.zero;
                _boneForceVelocity[i] = Vector3.zero;
                _bonePreviousLocalPosition[i] = Vector3.zero;
                _bonePreviousLocalVelocity[i] = Vector3.zero;
            }
        }

        // ------------------------------------------------------------------ //
        //  Fur collision
        // ------------------------------------------------------------------ //
        /// <summary>
        /// Packs the nearest colliders into the arrays the shader resolves each strand
        /// tip against.
        ///
        /// Nothing here decides how hard the fur is pushed - contact is worked out per
        /// vertex on the GPU, against the real collider surfaces. That is what the CPU
        /// side used to get wrong: it collapsed every collider into one force vector, so
        /// two surfaces on opposite sides cancelled out, and it measured distance from
        /// the bounds centre against a radius taken from the AABB, which is wrong
        /// everywhere at once on anything that is not a ball.
        /// </summary>
        private void GatherFurColliders()
        {
            _activeColliderCount = 0;

            int budget = Mathf.Clamp(maxSimultaneousColliders, 1, MaxFurColliders);
            if (!enableCollision || collisionLayers == 0 || CollisionStrength <= 0f)
            {
                ClearUnusedColliderSlots(0);
                return;
            }

            // Query the fur's own bounds widened by the margin, so a surface is picked up
            // exactly when the strands start to lean away from it and not before.
            Vector3 halfExtents = _worldBounds.extents + Vector3.one * CollisionMargin;
            int found = Physics.OverlapBoxNonAlloc(_worldBounds.center, halfExtents,
                _collisionColliderBuffer, Quaternion.identity, collisionLayers,
                QueryTriggerInteraction.Ignore);

            Vector3 center = _worldBounds.center;

            for (int i = 0; i < found; i++)
            {
                Collider col = _collisionColliderBuffer[i];
                if (col == null || !col.enabled || FurColliderPacker.IsSelf(col, gameObject))
                    continue;

                // Distance to the collider's surface, not to its origin: a long wall
                // whose pivot sits far away is still in contact right here.
                float distance = (col.ClosestPoint(center) - center).sqrMagnitude;

                int slot;
                if (_activeColliderCount < budget)
                {
                    slot = _activeColliderCount++;
                }
                else
                {
                    // Over budget: replace the farthest entry, but only if this one is
                    // actually nearer. Keeping the NEAREST colliders is what makes the
                    // limit safe to lower - the ones dropped are the ones whose margin
                    // the fur was never going to reach anyway, so their contribution was
                    // already zero and nothing pops when they fall out.
                    slot = FarthestColliderSlot(budget);
                    if (distance >= _colliderSortKeys[slot])
                        continue;
                }

                FurColliderPacker.Pack(col, out Matrix4x4 worldToLocal, out Vector4 shape, out float type);

                // Fur can only tunnel through a collider thinner than it is long. Telling
                // the shader which colliders those are lets it skip the strand-crossing
                // march for every solid wall and floor, which is nearly all of them.
                float thickness = FurColliderPacker.MinimumThickness(shape, type);
                float needsCrossingTest = thickness < _furLength + CollisionMargin ? 1f : 0f;

                _colliderToLocal[slot] = worldToLocal;
                _colliderShape[slot] = shape;
                _colliderParams[slot] = new Vector4(type, CollisionMargin, needsCrossingTest, 0f);
                _colliderSortKeys[slot] = distance;
            }

            ClearUnusedColliderSlots(_activeColliderCount);
        }

        private int FarthestColliderSlot(int count)
        {
            int worst = 0;
            for (int i = 1; i < count; i++)
            {
                if (_colliderSortKeys[i] > _colliderSortKeys[worst])
                    worst = i;
            }
            return worst;
        }

        /// <summary>
        /// Blanks the slots past <paramref name="used"/>. The shader only reads up to the
        /// active count, but a stale matrix left in an unused slot becomes a phantom
        /// collider the moment the count rises again.
        /// </summary>
        private void ClearUnusedColliderSlots(int used)
        {
            for (int i = used; i < MaxFurColliders; i++)
            {
                _colliderToLocal[i] = Matrix4x4.identity;
                _colliderShape[i] = Vector4.zero;
                _colliderParams[i] = Vector4.zero;
                _colliderSortKeys[i] = float.MaxValue;
            }
        }

        // ------------------------------------------------------------------ //
        //  Detail maps
        // ------------------------------------------------------------------ //
        /// <summary>
        /// The longest a strand on this object can be, in meters.
        ///
        /// Bounds have to be built from this rather than from the material value alone,
        /// because a length map reaches up to <see cref="DetailScaleMax"/> times it - and
        /// bounds sized for the material value would cull the object while its longest
        /// painted strands were still on screen.
        ///
        /// Conservative on purpose: it assumes the map's maximum wherever one exists,
        /// without reading the texture back to find out what was actually painted. The
        /// cost of being wrong the other way is a visible pop at the screen edge.
        /// </summary>
        private float ResolvedMaxFurLength()
        {
            return _lengthAttributeMap != null ? _furLength * DetailScaleMax : _furLength;
        }

        private static int ClampMapResolution(int resolution)
        {
            return Mathf.Clamp(Mathf.NextPowerOfTwo(resolution),
                FurAttributeMap.MinResolution, FurAttributeMap.MaxResolution);
        }

        /// <summary>
        /// Rebakes whichever detail maps are switched on and out of date, and releases
        /// the ones that are switched off.
        ///
        /// Cheap enough to call every frame: each map compares a handful of references
        /// against what its current contents were produced from and returns immediately
        /// when nothing has moved. The bake itself is one mesh rasterisation plus one
        /// full-screen pass, and only when something asked for it.
        /// </summary>
        private void UpdateAttributeMaps()
        {
            var identity = new Vector4(1f, 1f, 0f, 0f);
            UpdateAttributeMap(ref _lengthAttributeMap, "FurLengthMap", lengthMap, NeutralScalar, identity);
            UpdateAttributeMap(ref _combAttributeMap, "FurCombMap", combMap, NeutralComb, identity);
            UpdateAttributeMap(ref _thicknessAttributeMap, "FurThicknessMap", thicknessMap, NeutralScalar, identity);

            // A fill that is not white is a reason for the colour map to EXIST on its own -
            // there is no texture to make it live, and the whole point is a coat carrying one
            // flat colour the model already has.
            UpdateAttributeMap(ref _colorAttributeMap, "FurColorMap", colorMap, colorMapFill,
                               colorMapScaleOffset, ColorMapHasFlatValue);
        }

        /// <summary>
        /// Whether the colour channel has been given something to say, with no texture in the
        /// slot: a fill, a tint, or both.
        ///
        /// The TINT counts, and that is not just for the button's benefit. The tint multiplies
        /// the map, so with no map there is nothing to multiply and setting it did exactly
        /// nothing - silently, which is the worst way for a control to behave. Letting it bring
        /// the map into existence means a flat coat colour costs one live value and no rebake,
        /// which is also the cheapest way to hold one.
        /// </summary>
        private bool ColorMapHasFlatValue =>
            colorMapFill.r < 0.999f || colorMapFill.g < 0.999f || colorMapFill.b < 0.999f ||
            colorMapTint.r < 0.999f || colorMapTint.g < 0.999f || colorMapTint.b < 0.999f;

        /// <summary>
        /// The UV set the maps are ACTUALLY stored in, which is not always the one asked
        /// for.
        ///
        /// Selecting UV1 on a mesh that carries no second set would unwrap every triangle
        /// to (0,0): one texel holding the whole body, every map a flat colour and every
        /// brush stroke landing on all of it at once. Falling back to UV0 leaves the coat
        /// exactly as it was and keeps the failure to a warning, which is the difference
        /// between a setting that needs a checkbox ticked and a coat that looks broken.
        ///
        /// Checked through HasVertexAttribute rather than by reading mesh.uv2, which
        /// allocates an array the size of the mesh every time it is touched.
        /// </summary>
        private int ResolveDetailUvChannel()
        {
            if (detailUvSet != FurDetailUvSet.UV1)
                return 0;

            // No mesh resolved yet is not the same as a mesh without the set: this runs
            // before the first frame state update as well, and warning there would fire
            // on every furred object at load whatever its unwrap actually holds.
            if (_activeMesh == null)
                return 0;

            if (_activeMesh.HasVertexAttribute(VertexAttribute.TexCoord1))
                return 1;

            WarnDetailUvMissing();
            return 0;
        }

        private void WarnDetailUvMissing()
        {
            if (_detailUvWarningShown)
                return;

            _detailUvWarningShown = true;
            Debug.LogWarning(
                $"[FurController] '{name}' asks for the UV1 detail set, but its mesh has no " +
                "second UV set - the fur maps are falling back to UV0. Enable 'Generate " +
                "Lightmap UVs' on the model importer to build one.", this);
        }

        private void UpdateAttributeMap(ref FurAttributeMap map, string name, Texture source, Color neutral,
                                        Vector4 sourceST, bool forceLive = false)
        {
            // A map is live when it holds something: a source texture, or a surface some
            // brush has already been given. Note that an allocated map is NOT released
            // when its source is cleared - it rebakes neutral and stays, because that is
            // exactly the state you paint from. ClearDetailMap is the way to give the
            // memory back, and being explicit about it means a stroke can never be
            // thrown away by a texture slot being emptied.
            bool live = source != null || map != null || forceLive;

            if (!live || _activeMesh == null || attributeBakeShader == null)
                return;

            map ??= new FurAttributeMap(name);

            // Set BEFORE Update: the channel decides the layout, so Update has to see the
            // change in the same call that would otherwise reuse the old contents.
            map.UvChannel = ResolveDetailUvChannel();
            map.Update(attributeBakeShader, detailMapResolution, _activeMesh, source, neutral, sourceST);
        }

        /// <summary>
        /// Binds one detail map plus its skip flag.
        ///
        /// The flag is only raised once the bake has actually produced contents, so the
        /// shader never reads a map that exists but has not been filled in yet. And the
        /// fallback texture is WHITE, never black: these are multipliers, so white is the
        /// value that means "leave the material alone" - black would mean fur of zero
        /// length, zero thickness and no colour for the one frame in between.
        /// </summary>
        private void BindAttributeMap(MaterialPropertyBlock block, FurAttributeMap map,
                                      int textureId, int enabledId)
        {
            bool usable = map != null && map.IsBaked;

            block.SetTexture(textureId,
                usable ? (Texture)map.Texture : Texture2D.whiteTexture);
            block.SetFloat(enabledId, usable ? 1f : 0f);
        }

        private void MarkAttributeMapsDirty()
        {
            _lengthAttributeMap?.MarkDirty();
            _thicknessAttributeMap?.MarkDirty();
            _colorAttributeMap?.MarkDirty();

            // The comb is a detail map like the others. Leaving it out meant "Revert to
            // Source Textures" and RebakeDetailMaps() kept every comb stroke.
            _combAttributeMap?.MarkDirty();
        }

        private void DisposeAttributeMaps()
        {
            _lengthAttributeMap?.Dispose();
            _lengthAttributeMap = null;

            _thicknessAttributeMap?.Dispose();
            _thicknessAttributeMap = null;

            _colorAttributeMap?.Dispose();
            _colorAttributeMap = null;

            _combAttributeMap?.Dispose();
            _combAttributeMap = null;
        }

        // ------------------------------------------------------------------ //
        //  Regional wetness
        // ------------------------------------------------------------------ //
        /// <summary>
        /// Advances the per-object wetness map by one frame.
        ///
        /// The heavy lifting happens on the GPU in texture space (see
        /// <see cref="FurWetnessMap"/>); this only gathers the nearby water volumes
        /// and hands over the mesh actually being drawn. For a skinned source that
        /// is the BAKED mesh, so the world positions tested against the water follow
        /// the skeleton rather than the bind pose.
        /// </summary>
        private void UpdateWetnessMap(float deltaTime)
        {
            if (!enableWetnessMap || _activeMesh == null || wetnessSplatShader == null)
            {
                // Release the targets rather than leaving three RenderTextures alive on
                // every furred object whose wetness was switched off.
                if (_wetnessMap != null)
                {
                    _wetnessMap.Dispose();
                    _wetnessMap = null;
                }
                return;
            }

            _wetnessMap ??= new FurWetnessMap();
            _wetnessMap.UvChannel = ResolveDetailUvChannel();

            // Query around the fur's own bounds, widened by the spray margin so a
            // volume is picked up just before it reaches the strands.
            int found = 0;
            if (waterLayers != 0)
            {
                Vector3 halfExtents = _worldBounds.extents + Vector3.one * contactFeather;
                found = Physics.OverlapBoxNonAlloc(_worldBounds.center, halfExtents,
                    _waterColliderBuffer, Quaternion.identity, waterLayers,
                    QueryTriggerInteraction.Collide);
            }

            _wetnessMap.Update(
                wetnessSplatShader,
                wetnessMapResolution,
                _activeMesh,
                _objectToWorld,
                _waterColliderBuffer,
                found,
                gameObject,
                contactFeather,
                wettingRate,
                dryingRate,
                enableDrying,
                deltaTime);
        }

        /// <summary>
        /// Advances one damped harmonic oscillator: ẍ + 2ζω_n·ẋ + ω_n²·x = 0, with
        /// ω_n = inertiaDamping and ζ = inertiaDampingRatio.
        ///
        /// The value carries its own momentum in <paramref name="velocity"/>, so it keeps
        /// moving after the target stops changing - that overshoot and settle IS the
        /// visible fur inertia. Semi-implicit Euler: velocity is updated first and the
        /// new velocity moves the value, which is stable at far larger steps than the
        /// explicit form.
        ///
        /// Sub-stepped so that ω·h stays small. At the slider's maximum damping of 30 and
        /// a 0.05 s frame, ω·dt reaches 1.5 - close enough to the method's stability limit
        /// that the spring rang numerically rather than physically. Worse, the integration
        /// error scales with the step, so the same settings visibly behaved differently at
        /// 30 fps and at 144 fps. Splitting the frame fixes both: the response now depends
        /// on the tuning rather than on the frame rate.
        /// </summary>
        private void IntegrateForceSpring(ref Vector3 value, ref Vector3 velocity, Vector3 target, float dt)
        {
            // Water is most of a soaked coat's mass, and a heavier mass on the same root
            // stiffness oscillates more slowly - omega_n goes as 1/sqrt(m). So wet fur
            // lags further behind the body and takes longer to settle, which is a large
            // part of why a wet animal moves differently from a dry one.
            //
            // Driven by the GLOBAL wetness only, not the map: a spring is one state for
            // the whole object, so there is nothing sensible for a per-texel value to do
            // here. A creature soaked in patches gets the dry timing.
            float omega = inertiaDamping * Mathf.Lerp(1f, WetInertiaScale, Mathf.Clamp01(wetness));
            float zeta = inertiaDampingRatio;

            int steps = Mathf.Clamp(
                Mathf.CeilToInt(omega * dt / MaxSpringStepAngle), 1, MaxSpringSubsteps);
            float h = dt / steps;

            float stiffness = omega * omega;
            float damping = 2f * zeta * omega;

            for (int i = 0; i < steps; i++)
            {
                Vector3 acceleration = (target - value) * stiffness - velocity * damping;
                velocity += acceleration * h;
                value += velocity * h;
            }
        }

        /// <summary>
        /// Resolves shell count from the artist-authored distance curve.
        /// </summary>
        /// <summary>
        /// The shell count to draw when LOD is off, migrating assets that predate the
        /// explicit field. Also the fallback in a player build, where OnValidate never
        /// runs to fill the field in.
        /// </summary>
        private int ResolveFixedShellCount()
        {
            if (shellCount > 0)
                return shellCount;

            return shellCountByDistance != null && shellCountByDistance.length > 0
                ? Mathf.RoundToInt(shellCountByDistance.Evaluate(0f))
                : 32;
        }

        private int ResolveShellCount(Camera camera)
        {
            // Measured whether or not distance LOD is on. It is the LOD's input, but it is
            // also what the distance aggregation reads to decide when a coat has stopped
            // having strands a pixel can resolve - and that is worth doing for a coat whose
            // shell count is pinned.
            // Both are wanted here, and they are not interchangeable. The perspective
            // divide needs real meters; the LOD curves need object-lengths.
            float meters   = LodDistanceMeters(camera);
            float distance = meters / Mathf.Max(_objectSize, 1e-4f);

            _pixelsPerMeter = camera.orthographic
                ? camera.pixelHeight / (2f * Mathf.Max(camera.orthographicSize, 1e-4f))
                : camera.pixelHeight / (2f * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * meters);

            _furPixels = _furLength * _pixelsPerMeter;

            if (!enableLod)
            {
                _lodFade = 1f;
                return Mathf.Clamp(ResolveFixedShellCount(), 1, MaxShellCount);
            }

            float pixelsPerMeter = _pixelsPerMeter;
            float furPixels = _furPixels;

            // Shrink the fur over the last stretch before it is dropped entirely, so the
            // step down to bare skin is a fade instead of a pop. Measured in PIXELS of fur
            // height, which is the only thing that decides whether losing that height is
            // visible - the shell count says how cheap the fur is, not how small.
            float fadeEnd = LodFadePixels * LodFadeSpan;
            _lodFade = Mathf.Clamp01((furPixels - LodFadePixels) / Mathf.Max(fadeEnd - LodFadePixels, 1e-4f));

            // Never all the way to nothing, and never down to the one shell that IS bare
            // skin. Those are the two ways this component deletes a coat outright, and the
            // cost argument for either does not survive contact with the case: fur that
            // small is a handful of pixels, and those pixels are the whole difference
            // between a furry animal in the distance and a plastic one. The coat stays at
            // a floor thin enough to be nearly free and thick enough to still read, and
            // the shader's aggregation carries it from there.
            _lodFade = Mathf.Max(_lodFade, LodMinFade);

            int distanceShells = Mathf.RoundToInt(shellCountByDistance.Evaluate(distance));
            int screenSpaceShells = Mathf.CeilToInt(furPixels * LodShellsPerPixel);

            return Mathf.Clamp(Mathf.Min(distanceShells, screenSpaceShells), LodMinShells, MaxShellCount);
        }

        /// <summary>
        /// How far this camera is from the object, measured in the object's OWN lengths.
        ///
        /// Not in meters, and this is the one thing about the LOD worth understanding.
        /// Every other number in this package is relative - length is a fraction of the
        /// object, density is strands across it, thickness is a fraction of the spacing -
        /// precisely so that one material clothes a mouse and a bear. The LOD curves were
        /// the last absolute distances left in it, and they quietly undid all of that: a
        /// model imported at 150x scale reaches "50 meters" while it still fills a third
        /// of the screen, so the curve returned its final key, the coat dropped to one
        /// shell, and the creature went bare in full view of the camera.
        ///
        /// Dividing by the object's size makes the curves say what they were always read
        /// as saying - "this far away, relative to how big the thing is" - and a key at 50
        /// means fifty body-lengths at every import scale. For an object about a meter
        /// across the numbers barely move, so existing curves keep their tuning.
        /// </summary>
        private float LodDistance(Camera camera)
        {
            return LodDistanceMeters(camera) / Mathf.Max(_objectSize, 1e-4f);
        }

        /// <summary>
        /// Camera to the near face of the object, in meters, never below 1cm.
        ///
        /// Measured against the BIND pose - a stable centre and a stable radius - and not
        /// against the renderer's live bounds, which is what this used to do and which was
        /// a flicker bug.
        ///
        /// A SkinnedMeshRenderer recomputes its bounds from the current pose every frame,
        /// so on anything animated both the centre and the extents breathe continuously.
        /// That noise lands on a distance that is about to be quantised - RoundToInt for
        /// the shell count, CeilToInt for the screen-space cap, RoundToInt for the ribbon
        /// count - so the counts oscillate between adjacent values every frame. The coat
        /// then pulses: shells restack, and because hair width is compensated by
        /// rsqrt(_FurCountFraction), a one-hair change in the count changes the width of
        /// EVERY hair. A walking character shimmered with the camera standing still.
        ///
        /// The live bounds are still what culling uses, where being pose-accurate is the
        /// whole point. The LOD wants the opposite property, so it gets its own measure.
        /// </summary>
        private float LodDistanceMeters(Camera camera)
        {
            return Mathf.Max(
                Vector3.Distance(camera.transform.position, _lodCenter) - _objectSize,
                0.01f);
        }

        private void RenderShells(Camera camera, int shells)
        {
            // All instances share the same transform; the per-layer offset happens in the
            // shader via unity_InstanceID, so a single draw call is enough.
            for (int i = 0; i < shells; i++)
                _instanceMatrices[i] = _objectToWorld;

            WriteSharedProperties(_propertyBlock, shells);

            // Shell fur is one draw, so it is also the one geometry mode. Written anyway
            // rather than left to the material default: a material switched from a ribbon
            // type back to Shells would otherwise keep the last value written to it and
            // the coat would vanish.
            _propertyBlock.SetFloat(GeometryModeId, 0f);
            _propertyBlock.SetFloat(DrawSkinId, DrawsOwnSkin ? 1f : 0f);

            bool allShellsCastShadows = shadowMode == FurShadowMode.AllShells;
            var renderParams = BuildRenderParams(camera, _propertyBlock, allShellsCastShadows);

            int subMeshCount = _activeMesh.subMeshCount;
            for (int subMesh = 0; subMesh < subMeshCount; subMesh++)
                Graphics.RenderMeshInstanced(renderParams, _activeMesh, subMesh, _instanceMatrices, shells);

            RenderShellFuzz(camera, shells, subMeshCount);

            // "Base layer only" shadow mode: the body's silhouette is the shadow. Instance 0
            // is the skin shell (layerT = 0), and while the object's own renderer draws the
            // skin that shell is discarded in every pass - so a draw here would submit work
            // that writes nothing. The renderer already casts that exact shadow itself.
            if (shadowMode == FurShadowMode.BaseLayerOnly && DrawsOwnSkin)
            {
                renderParams.shadowCastingMode = ShadowCastingMode.ShadowsOnly;

                for (int subMesh = 0; subMesh < subMeshCount; subMesh++)
                    Graphics.RenderMeshInstanced(renderParams, _activeMesh, subMesh, _instanceMatrices, 1);
            }
        }

        /// <summary>
        /// The per-object values every fur type and every one of its draws needs.
        ///
        /// All of these are declared OUTSIDE the UnityPerMaterial buffer in the shader, so
        /// a property block override actually takes effect - no runtime material copy is
        /// needed, one material serves any number of objects, and the LOD decision stays
        /// correct per camera.
        /// </summary>
        private void WriteSharedProperties(MaterialPropertyBlock block, int shells)
        {
            block.SetFloat(ShellCountId, shells);
            block.SetFloat(LodFadeId, _lodFade);
            block.SetFloat(AggregationId, _aggregation);
            block.SetFloat(CountFractionId, _countFraction);
            block.SetFloat(DetailUvId, ResolveDetailUvChannel());

            // Always bound, never conditionally: an unbound sampler falls back to Unity's
            // white, and a coat that read its whole hair gradient as white would be a
            // baffling way to find out the texture had not been assigned.
            block.SetFloat(BandEnabledId, useBandGradient ? 1f : 0f);
            block.SetTexture(BandMapId, BandTexture());
            block.SetVector(BandMeanId, _bandMean);
            block.SetFloat(MetersPerUvId, _metersPerUV);
            block.SetFloat(MetersPerUvMaxId, _metersPerUVMax);
            block.SetFloat(ObjectSizeId, _objectSize);
            block.SetMatrix(PrevObjectToWorldId, _previousObjectToWorld);
            block.SetVector(ExternalForceId, _smoothedForce);
            block.SetVector(DragForceId, _smoothedDragForce);
            // The pivot the object rotates about, so the shader can build each vertex's
            // lever arm. transform.position rather than the bounds centre: rotation happens
            // about the transform, and an off-centre mesh would otherwise swing the wrong way.
            block.SetVector(AngularForceId, _smoothedAngularForce);
            block.SetVector(RotationCenterId, transform.position);

            block.SetFloat(BoneMotionCountId, _activeBoneCount);
            if (_activeBoneCount > 0)
            {
                block.SetVectorArray(BoneMotionOriginId, _boneOrigin);
                block.SetVectorArray(BoneMotionForceId, _boneForce);
            }
            block.SetFloat(WetnessId, wetness);
            // Lets the shader skip the wetness texture fetch outright - it runs per vertex
            // per shell as well as per fragment, so it is not free even when the map is black.
            block.SetFloat(WetnessEnabledId, enableWetnessMap ? 1f : 0f);
            // Collider primitives for the per-vertex contact solve.
            block.SetFloat(ColliderCountId, _activeColliderCount);
            block.SetFloat(CollisionStrengthId, CollisionStrength);
            block.SetFloat(CollisionSpreadId, Mathf.Clamp01(collisionSpread));
            if (_activeColliderCount > 0)
            {
                block.SetMatrixArray(ColliderToLocalId, _colliderToLocal);
                block.SetVectorArray(ColliderShapeId, _colliderShape);
                block.SetVectorArray(ColliderParamsId, _colliderParams);
            }

            // Always bound, never left to the material default: an unset texture
            // property falls back to Unity's WHITE texture, which the shader would
            // read as "soaked" over the entire object.
            Texture wetnessTexture = _wetnessMap != null && _wetnessMap.Texture != null
                ? _wetnessMap.Texture
                : (Texture)Texture2D.blackTexture;
            block.SetTexture(WetnessMapId, wetnessTexture);

            // Detail maps. White, never black, when one is absent: these are
            // MULTIPLIERS, so the neutral binding is 1 - black would mean fur of zero
            // length and zero thickness, painted black, the moment a bake had not yet
            // landed. The companion flag still lets the shader skip the fetch, but the
            // binding has to be safe on its own for the frame in between.
            BindAttributeMap(block, _lengthAttributeMap, LengthMapId, LengthMapEnabledId);
            BindAttributeMap(block, _thicknessAttributeMap, ThicknessMapId, ThicknessMapEnabledId);
            BindAttributeMap(block, _colorAttributeMap, ColorMapId, ColorMapEnabledId);
            BindAttributeMap(block, _combAttributeMap, CombMapId, CombMapEnabledId);
            block.SetVector(ColorMapTintId, new Vector4(
                colorMapTint.r, colorMapTint.g, colorMapTint.b, Mathf.Clamp01(colorMapStrength)));
        }

        // ------------------------------------------------------------------ //
        //  The fuzz layer
        // ------------------------------------------------------------------ //

        /// <summary>
        /// The material the soft layer is drawn with: this object's fur material, copied,
        /// with its render state switched to alpha blending.
        ///
        /// A copy rather than another property block, because blending is MATERIAL state -
        /// Blend, ZWrite and the render queue are compiled into the pass, and a property
        /// block cannot reach them. Copying also means the fuzz layer inherits every
        /// setting the artist made without a second set of controls to keep in step.
        ///
        /// Built on demand and only when the amount is above zero, so a coat that does not
        /// use the effect never allocates it.
        /// </summary>
        private Material ResolveFuzzMaterial()
        {
            if (furMaterial == null)
                return null;

            if (_fuzzMaterial == null || _fuzzMaterialSource != furMaterial)
            {
                DestroyFuzzMaterial();
                _fuzzMaterial = new Material(furMaterial)
                {
                    name = furMaterial.name + " (Fuzz)",
                    hideFlags = HideFlags.HideAndDontSave,
                };
                _fuzzMaterialSource = furMaterial;
                _fuzzMaterialVersion = -1;
            }

            // The copy is only re-synced when the source actually changes, because
            // CopyPropertiesFromMaterial is not cheap and this runs per camera per frame.
            int version = furMaterial.ComputeCRC();
            if (_fuzzMaterialVersion != version)
            {
                _fuzzMaterial.CopyPropertiesFromMaterial(furMaterial);
                _fuzzMaterialVersion = version;

                // Straight alpha over the coat, no depth writes: the fuzz layer is a
                // translucent thing standing in front of an opaque one. It still depth
                // TESTS, which is what keeps the half of it that is inside the body hidden.
                _fuzzMaterial.SetFloat(SrcBlendId, (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _fuzzMaterial.SetFloat(DstBlendId, (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _fuzzMaterial.SetFloat(ZWriteId, 0f);
                _fuzzMaterial.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }

            return _fuzzMaterial;
        }

        private void DestroyFuzzMaterial()
        {
            if (_fuzzMaterial == null)
                return;

            if (Application.isPlaying)
                Destroy(_fuzzMaterial);
            else
                DestroyImmediate(_fuzzMaterial);

            _fuzzMaterial = null;
            _fuzzMaterialSource = null;
        }

        /// <summary>How strong the fuzz layer is, or zero if the material does not want one.</summary>
        private float ResolveFuzzAmount()
        {
            if (furMaterial == null || !furMaterial.HasProperty(FuzzAmountId))
                return 0f;

            return Mathf.Clamp01(furMaterial.GetFloat(FuzzAmountId));
        }

        /// <summary>
        /// True when this material asks for the screen-space fuzz layer rather than the
        /// geometric one. Absent property means the geometric layer, which is what every
        /// material predating the choice has and what it should keep.
        /// </summary>
        private bool UsesScreenFuzz =>
            furMaterial != null &&
            furMaterial.HasProperty(FuzzMaterialModeId) &&
            furMaterial.GetFloat(FuzzMaterialModeId) > 0.5f;

        /// <summary>
        /// The material the SKIN is drawn with - the fur material itself, unless the
        /// screen-space fuzz layer needs it not to write the coat's stencil mark.
        ///
        /// Returning the shared material in the common case is the point: no copy is
        /// allocated, no CRC is computed, and a coat using the geometric layer behaves
        /// exactly as it did before this existed.
        /// </summary>
        private Material ResolveSkinMaterial()
        {
            if (furMaterial == null || !UsesScreenFuzz)
                return furMaterial;

            if (_skinMaterial == null || _skinMaterialSource != furMaterial)
            {
                DestroySkinMaterial();
                _skinMaterial = new Material(furMaterial)
                {
                    name = furMaterial.name + " (Skin)",
                    hideFlags = HideFlags.HideAndDontSave,
                };
                _skinMaterialSource = furMaterial;
                _skinMaterialVersion = -1;
            }

            // Re-synced only when the source actually changed - this runs per camera per
            // frame and CopyPropertiesFromMaterial is not cheap.
            int version = furMaterial.ComputeCRC();
            if (_skinMaterialVersion != version)
            {
                _skinMaterial.CopyPropertiesFromMaterial(furMaterial);
                _skinMaterialVersion = version;

                // The one difference, and the whole reason the copy exists: the skin does
                // not mark itself as coat. What shows between the hairs is therefore
                // unmarked, and the composite lays the haze into it.
                _skinMaterial.SetFloat(FuzzStencilWriteMaskId, 0f);
            }

            return _skinMaterial;
        }

        private void DestroySkinMaterial()
        {
            if (_skinMaterial == null)
                return;

            if (Application.isPlaying)
                Destroy(_skinMaterial);
            else
                DestroyImmediate(_skinMaterial);

            _skinMaterial = null;
            _skinMaterialSource = null;
        }

        /// <summary>
        /// Tells the screen-space fuzz pass how wide to blur this coat, in screen pixels.
        ///
        /// The radius is the fur's own LENGTH on screen times _FuzzExtent - the same
        /// number, with the same meaning, that sets how far the geometric layer reaches
        /// past the coat. So the two layers are authored by one slider and produce halos
        /// of the same size, and switching between them compares like with like.
        ///
        /// Length rather than strand SPACING, which is the obvious other candidate and is
        /// wrong by orders of magnitude: spacing is what separates neighbouring hairs, and
        /// on any coat dense enough to look like one that is a fraction of a pixel. This
        /// coat's hairs sit 0.7 mm apart and its fur is 39 mm long - a spacing-derived
        /// radius came out at one pixel, which is a resample, not a halo.
        ///
        /// Relative on both counts, which is what makes it hold: fur length is authored as
        /// a fraction of the object, and pixels per meter is measured at this camera. Move
        /// the camera, scale the model by 150, or change the density, and the haze keeps
        /// the same proportion to the coat.
        /// </summary>
        private void SubmitScreenFuzz(Camera camera)
        {
            if (!UsesScreenFuzz)
                return;

            float amount = ResolveFuzzAmount();
            if (amount <= 0f)
                return;

            float extent = furMaterial.HasProperty(FuzzExtentId)
                ? Mathf.Max(furMaterial.GetFloat(FuzzExtentId), 0f)
                : 0.35f;

            // _furPixels is the fur's length in screen pixels, resolved for this camera a
            // few lines upstream by ResolveShellCount - which both fur types run.
            FurScreenFuzzRequest.Submit(camera, _furPixels * extent * _lodFade, amount);
        }

        /// <summary>
        /// The soft layer over a shell coat: the same stack, fewer layers, drawn blended.
        ///
        /// FEWER layers on purpose. The fuzz layer is a blur, and a blur has no fine detail
        /// to lose - so resolving it at the coat's own layer count would pay full price for
        /// something nobody can see the difference in. A quarter of them, floored at two, is
        /// what the effect costs.
        /// </summary>
        private void RenderShellFuzz(Camera camera, int shells, int subMeshCount)
        {
            // The two layers are alternatives, not a stack: in screen-space mode the
            // haze comes from a full-screen pass over the pixels this coat has already
            // drawn, so submitting a second geometric coat here would be paying for both
            // and blurring the wrong one.
            if (UsesScreenFuzz)
                return;

            float amount = ResolveFuzzAmount();
            if (amount <= 0f)
                return;

            Material fuzz = ResolveFuzzMaterial();
            if (fuzz == null)
                return;

            int fuzzShells = Mathf.Clamp(Mathf.CeilToInt(shells * FuzzShellFraction), 2, shells);
            for (int i = 0; i < fuzzShells; i++)
                _instanceMatrices[i] = _objectToWorld;

            _fuzzPropertyBlock ??= new MaterialPropertyBlock();
            WriteSharedProperties(_fuzzPropertyBlock, fuzzShells);
            _fuzzPropertyBlock.SetFloat(GeometryModeId, 0f);
            _fuzzPropertyBlock.SetFloat(DrawSkinId, DrawsOwnSkin ? 1f : 0f);
            _fuzzPropertyBlock.SetFloat(FuzzModeId, 1f);

            // Never a shadow caster. A translucent halo has no business darkening the ground,
            // and the coat it sits on has already cast the shape's shadow.
            var fuzzParams = BuildRenderParams(camera, _fuzzPropertyBlock, false, fuzz);

            for (int subMesh = 0; subMesh < subMeshCount; subMesh++)
                Graphics.RenderMeshInstanced(fuzzParams, _activeMesh, subMesh, _instanceMatrices, fuzzShells);
        }

        /// <summary>
        /// Whether this component draws the surface the fur grows out of, or leaves it to the
        /// object's own renderer. Always the renderer now - kept as a named switch because
        /// the shell base layer, the strand skin draw and the base-layer shadow all branch
        /// on it.
        ///
        /// The object already draws itself with its OWN material, and drawing a second copy
        /// on top is not a fallback: it is two coincident surfaces fighting over the same
        /// depth, with the fur material's Base Colour winning wherever it happens to.
        ///
        /// That fight is what "the mesh is painted the fur's colour" turns out to be. A model
        /// whose own material is skin-coloured, with a fur material left at a white Base
        /// Colour, shows WHITE through the coat instead of its own texture - and no amount of
        /// tinting work in the shader fixes it, because both surfaces are being drawn exactly
        /// as asked.
        /// </summary>
        private bool DrawsOwnSkin => false;

        private RenderParams BuildRenderParams(Camera camera, MaterialPropertyBlock block, bool castShadows)
        {
            return BuildRenderParams(camera, block, castShadows, furMaterial);
        }

        private RenderParams BuildRenderParams(Camera camera, MaterialPropertyBlock block, bool castShadows,
                                               Material material)
        {
            return new RenderParams(material)
            {
                worldBounds = _worldBounds,
                camera = camera,
                layer = gameObject.layer,
                renderingLayerMask = sourceRenderer.renderingLayerMask,
                receiveShadows = receiveShadows,
                shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off,
                reflectionProbeUsage = ReflectionProbeUsage.BlendProbes,
                // Object, not Camera. Camera mode reports only where the CAMERA moved, so a
                // creature crossing the screen left its history behind and TAA blended the
                // two into a smear trailing it. The shader's MotionVectors pass supplies the
                // real per-fragment answer.
                motionVectorMode = MotionVectorGenerationMode.Object,
                matProps = block,
            };
        }

        // ------------------------------------------------------------------ //
        //  Strand fur
        // ------------------------------------------------------------------ //

        /// <summary>
        /// The fur type the assigned material is set to, with the shader keywords brought
        /// into line with it.
        ///
        /// The float is the material's stored value and the keyword is what the shader
        /// branches on, so the two can drift apart - a material built by script, or one
        /// whose property was set through Material.SetFloat, carries the value without the
        /// keyword. Reconciling them here means the type is decided in exactly one place
        /// however the material was authored.
        /// </summary>
        private FurType ResolveFurType()
        {
            if (furMaterial == null || !furMaterial.HasProperty(FurTypeId))
                return FurType.Shells;

            var type = (FurType)Mathf.Clamp(Mathf.RoundToInt(furMaterial.GetFloat(FurTypeId)), 0, 1);

            bool wantStrands = type == FurType.Strands;

            if (furMaterial.IsKeywordEnabled(StrandsKeyword) != wantStrands)
                SetKeyword(furMaterial, StrandsKeyword, wantStrands);

            return type;
        }

        private static void SetKeyword(Material material, string keyword, bool enabled)
        {
            if (enabled)
                material.EnableKeyword(keyword);
            else
                material.DisableKeyword(keyword);
        }

        /// <summary>
        /// Draws a strand coat, and reports whether anything was submitted.
        ///
        /// The skin under the hair is the object's own renderer, drawn with its own
        /// material (see <see cref="DrawsOwnSkin"/>), so this submits the FUR only: the
        /// ribbon batch mesh, instanced once per batch of roots, plus the fuzz layer when
        /// the material asks for the geometric one.
        /// </summary>
        private bool RenderRibbons(Camera camera, FurType furType)
        {
            Mesh rootMesh = ResolveRootMesh();

            string problem = FurStrandDistribution.DescribeUnusable(rootMesh);
            if (problem != null)
            {
                WarnRibbonsUnavailable(problem);
                return false;
            }

            // WORLD area, not the mesh's own. Fur length and strand width are both in
            // world meters, so a density that was not would be the one measurement in the
            // system that changed meaning when somebody scaled the object - and it would
            // change by the SQUARE of that scale. See FurStrandDistribution.AreaScale.
            float area = FurStrandDistribution.SurfaceAreaOf(rootMesh)
                       * FurStrandDistribution.AreaScale(transform.lossyScale);

            int ceiling = maxStrandCount;
            float density = ResolveRibbonDensity();

            _requestedRibbonCount = FurStrandDistribution.ResolveRootCount(area, density, int.MaxValue);

            int bakedCount = FurStrandDistribution.ResolveRootCount(area, density, ceiling);

            // Density zero, or a surface with no area. The SKIN is still drawn - the coat
            // is switched off, not the object - so this falls through to the draw below
            // rather than returning.
            if (bakedCount <= 0)
            {
                _activeRibbonCount = 0;
                _requestedRibbonCount = 0;

                // No fur to aggregate INTO the skin - the coat was switched off rather than
                // lost to distance, so the skin stays the skin.
                _aggregation = 0f;
                _countFraction = 1f;

                if (DrawsOwnSkin)
                    DrawRibbonSkin(camera);
                return true;
            }

            _distribution ??= new FurStrandDistribution();
            if (!_distribution.EnsureBuilt(rootMesh, bakedCount, ribbonSeed))
                return false;

            // The morph step, between the bind pose and the GPU skinning that follows.
            //
            // Here rather than in the frame update because it belongs to the ribbon draw
            // and to nothing else: a shell coat is drawn from the baked mesh and has
            // followed blend shapes all along. It returns early on its own when no weight
            // has moved, so a character standing still costs one comparison per shape.
            if (followBlendShapes && _skinnedRenderer != null)
                _distribution.ApplyBlendShapes(_skinnedRenderer);

            // May legitimately be zero - at the far end of the density curve, or once the
            // LOD has shrunk the fur to nothing. The SKIN still has to be drawn in that
            // case: the object is a long way off, not invisible, and returning early here
            // is what used to make a distant creature disappear altogether.
            int drawCount = ResolveRibbonCount(camera, bakedCount);
            if (_lodFade <= 0f)
                drawCount = 0;

            _activeRibbonCount = drawCount;

            // How much of the coat is left.
            //
            // The HEIGHT fade only, deliberately - not how many hairs fall inside a pixel.
            //
            // A ribbon coat has its own answer to being under-resolved and it works one
            // stage earlier: hairs are held to a minimum pixel width, the ones the LOD
            // removes are compensated for by widening the rest, and the dither that keeps
            // their coverage honest relaxes as the coat aggregates. By the time a pixel
            // holds several hairs, all three of those are already keeping the coat solid.
            // Handing the base layer the coat's colour on top of that double-counts it:
            // it was measured as a jump in brightness the moment the hairs went sub-pixel,
            // which is a pop at close range in exchange for a problem that had already
            // been solved. So the base only takes over at the far end, where the fur is
            // being shortened out of existence and there is genuinely nothing else left.
            _countFraction = Mathf.Clamp01(drawCount / (float)Mathf.Max(bakedCount, 1));
            _aggregation = Mathf.Clamp01(1f - _lodFade);

            // Roots are stored in the source mesh's BIND pose, so a skinned coat is
            // resolved through the skeleton on the GPU rather than through this matrix.
            // It is still supplied, because it is what an unskinned coat is placed with
            // and what the motion vector pass reprojects through in both cases.
            bool skinned = _skinnedRenderer != null && _distribution.IsSkinned && UpdateSkinMatrices();
            Matrix4x4 objectToWorld = _objectToWorld;

            // --- 1. The skin ---
            if (DrawsOwnSkin)
                DrawRibbonSkin(camera);

            // --- 2. The fur ---
            if (drawCount <= 0)
                return true;

            WriteSharedProperties(_propertyBlock, 1);
            _propertyBlock.SetFloat(GeometryModeId, 1f);
            _propertyBlock.SetFloat(InstanceCountId, drawCount);
            _propertyBlock.SetFloat(RootsPerBatchId, FurStrandDistribution.RootsPerBatch);
            _propertyBlock.SetFloat(SkinnedId, skinned ? 1f : 0f);
            _propertyBlock.SetMatrix(ObjectToWorldId, objectToWorld);
            _propertyBlock.SetMatrix(WorldToObjectId, objectToWorld.inverse);
            BindRibbonBuffers(_propertyBlock);

            int batches = Mathf.CeilToInt(drawCount / (float)FurStrandDistribution.RootsPerBatch);
            if (_ribbonMatrices.Length < batches)
                _ribbonMatrices = new Matrix4x4[Mathf.NextPowerOfTwo(batches)];

            for (int i = 0; i < batches; i++)
                _ribbonMatrices[i] = objectToWorld;

            Mesh ribbonMesh = FurRibbonMesh.Get(ribbonSegments);

            var furParams = BuildRenderParams(camera, _propertyBlock,
                shadowMode == FurShadowMode.AllShells);

            Graphics.RenderMeshInstanced(furParams, ribbonMesh, 0, _ribbonMatrices, batches);

            RenderRibbonFuzz(camera, ribbonMesh, drawCount, batches, skinned, objectToWorld);
            return true;
        }

        /// <summary>
        /// The soft layer over a strand coat: the same ribbons, fewer of them, drawn wider
        /// and blended.
        ///
        /// Every hair the coat has - see FuzzRibbonFraction for why this is not thinned.
        /// </summary>
        private void RenderRibbonFuzz(Camera camera, Mesh ribbonMesh, int drawCount, int batches,
                                      bool skinned, Matrix4x4 objectToWorld)
        {
            // The two layers are alternatives, not a stack: in screen-space mode the
            // haze comes from a full-screen pass over the pixels this coat has already
            // drawn, so submitting a second geometric coat here would be paying for both
            // and blurring the wrong one.
            if (UsesScreenFuzz)
                return;

            float amount = ResolveFuzzAmount();
            if (amount <= 0f)
                return;

            Material fuzz = ResolveFuzzMaterial();
            if (fuzz == null)
                return;

            int fuzzBatches = Mathf.Clamp(Mathf.CeilToInt(batches * FuzzRibbonFraction), 1, batches);
            int fuzzCount = Mathf.Min(drawCount, fuzzBatches * FurStrandDistribution.RootsPerBatch);

            _fuzzPropertyBlock ??= new MaterialPropertyBlock();
            WriteSharedProperties(_fuzzPropertyBlock, 1);
            _fuzzPropertyBlock.SetFloat(GeometryModeId, 1f);
            _fuzzPropertyBlock.SetFloat(FuzzModeId, 1f);
            _fuzzPropertyBlock.SetFloat(InstanceCountId, fuzzCount);
            _fuzzPropertyBlock.SetFloat(RootsPerBatchId, FurStrandDistribution.RootsPerBatch);
            _fuzzPropertyBlock.SetFloat(SkinnedId, skinned ? 1f : 0f);
            _fuzzPropertyBlock.SetMatrix(ObjectToWorldId, objectToWorld);
            _fuzzPropertyBlock.SetMatrix(WorldToObjectId, objectToWorld.inverse);
            BindRibbonBuffers(_fuzzPropertyBlock);

            var fuzzParams = BuildRenderParams(camera, _fuzzPropertyBlock, false, fuzz);
            Graphics.RenderMeshInstanced(fuzzParams, ribbonMesh, 0, _ribbonMatrices, fuzzBatches);
        }

        /// <summary>
        /// The surface the fur grows out of, drawn once from the fur material.
        ///
        /// It has to come from the fur material rather than from the object's own
        /// renderer, or the Base Surface settings would describe shell fur and nothing
        /// else. Split out because it is also the whole of what a ribbon coat draws when
        /// its density, its LOD or its budget have taken the hair count to zero - the
        /// object is bare then, not absent.
        /// </summary>
        private void DrawRibbonSkin(Camera camera)
        {
            // One instance, one matrix. The shell path fills this array per layer; here
            // there is only ever the one entry, and it still has to be written - the array
            // is shared, and a coat switched over from Shells would otherwise inherit
            // whatever the last shell draw left in slot zero.
            _instanceMatrices[0] = _objectToWorld;

            _skinPropertyBlock ??= new MaterialPropertyBlock();
            WriteSharedProperties(_skinPropertyBlock, 1);
            _skinPropertyBlock.SetFloat(GeometryModeId, 0f);

            // The skin draw uses the SAME shader variant as the fur draw, and a variant
            // compiled with a ribbon keyword declares the root and skin-matrix buffers
            // whether or not this particular draw reads from them. D3D12 rejects the draw
            // outright when a declared SRV is unbound - it does not care that the branch
            // guarding the read is never taken - so both draws bind both buffers.
            BindRibbonBuffers(_skinPropertyBlock);

            var skinParams = BuildRenderParams(camera, _skinPropertyBlock,
                shadowMode != FurShadowMode.None, ResolveSkinMaterial());

            int subMeshCount = _activeMesh.subMeshCount;
            for (int subMesh = 0; subMesh < subMeshCount; subMesh++)
                Graphics.RenderMeshInstanced(skinParams, _activeMesh, subMesh, _instanceMatrices, 1);
        }

        /// <summary>
        /// Binds the two structured buffers the ribbon variants declare.
        ///
        /// Both, always, on every draw that uses a ribbon variant - including the skin
        /// draw, which reads neither of them. The buffers are part of the compiled
        /// shader signature, and D3D12 skips a draw with an unbound SRV rather than
        /// tolerating one it can prove is never read.
        /// </summary>
        private void BindRibbonBuffers(MaterialPropertyBlock block)
        {
            if (_distribution?.RootBuffer != null)
                block.SetBuffer(RootsBufferId, _distribution.RootBuffer);

            block.SetBuffer(SkinMatricesId, EnsureSkinMatrixBuffer());
        }

        /// <summary>
        /// Ribbons per square meter, from the material's single _Density setting.
        ///
        /// SQUARED, because _Density is strands per UNIT and this wants strands per unit
        /// AREA - the same relationship shell fur uses, where _Density cells across a UV
        /// unit means _Density squared cells inside it. That is what lets one number
        /// serve all three types: it always means how finely the coat is divided, and
        /// each type reads it in the space it works in.
        ///
        /// Falls back to the shader's own default rather than to zero when the property
        /// is absent, so a material built from a different shader still grows a coat
        /// rather than leaving a bald object with no explanation.
        /// </summary>
        private float ResolveRibbonDensity()
        {
            const float Fallback = 250f;

            // The same floor the shader clamps to - see FUR_MIN_DENSITY. Zero used to mean a
            // coat with no hairs in it, which is indistinguishable from a broken setup; the
            // way to have no fur is to have no Fur Controller.
            const float MinDensity = 1f;

            float across = furMaterial != null && furMaterial.HasProperty(DensityId)
                ? Mathf.Max(furMaterial.GetFloat(DensityId), MinDensity)
                : Fallback;

            // Across the object, into per meter, into per square meter. Squaring is what
            // turns a linear count into an area count, and it is the same step every fur
            // type makes - a grid of N by N cells across a UV unit holds N squared of them.
            float perMeter = across / Mathf.Max(_objectSize, 1e-4f);
            return perMeter * perMeter;
        }

        /// <summary>
        /// The mesh hair roots are planted on.
        ///
        /// The SHARED mesh in every case, never the baked one: roots are stored in the
        /// bind pose and animated on the GPU, so planting them on a mesh that has already
        /// been posed would apply the skeleton twice.
        /// </summary>
        private Mesh ResolveRootMesh()
        {
            if (_skinnedRenderer != null)
                return _skinnedRenderer.sharedMesh;

            return furProxyMesh != null ? furProxyMesh : (_meshFilter != null ? _meshFilter.sharedMesh : null);
        }

        /// <summary>
        /// How far apart the remaining shells have drifted ON SCREEN, as a fade amount.
        ///
        /// The same question the fragment stage asks sideways, asked upward: a shell stack
        /// reads as a continuous coat exactly while its layers land less than a pixel
        /// apart, and it starts showing the gaps between them as soon as they do not. So
        /// the measure is the fur's height in pixels divided by the number of layers
        /// spanning it, and anything past one pixel is a gap the aggregation should close.
        ///
        /// This is deliberately NOT a ratio of shell counts. Whether eight shells is
        /// plenty or nowhere near depends entirely on how tall the fur is on screen -
        /// eight is luxurious on a distant creature and threadbare on one filling the
        /// frame - and a ratio cannot tell those apart, which is how a coat ends up
        /// aggregating at a distance where it still looks perfect, or not aggregating at
        /// one where it has visibly come apart.
        /// </summary>
        private float ResolveLayerGap(int shells)
        {
            float gapPixels = _furPixels / Mathf.Max(shells, 1);
            return Mathf.Clamp01(gapPixels - 1f);
        }

        /// <summary>How many of the baked roots this camera actually draws.</summary>
        private int ResolveRibbonCount(Camera camera, int bakedCount)
        {
            if (!enableLod || ribbonDensityByDistance == null || ribbonDensityByDistance.length == 0)
                return bakedCount;

            float distance = LodDistance(camera);

            // Taking a PREFIX of the root buffer rather than every n-th root. The roots
            // were placed by independent random sampling, so any prefix of them is itself
            // a uniform sample of the surface - which is what makes thinning the coat look
            // like thinning rather than like a pattern appearing in it.
            float fraction = Mathf.Clamp01(ribbonDensityByDistance.Evaluate(distance));

            // A floor rather than zero, for the same reason the shell path keeps a shell:
            // the hairs the LOD removes are paid for by widening the ones it keeps, and
            // that trade only works while there are some left to widen. At zero there is
            // nothing to aggregate and the coat is simply gone.
            fraction = Mathf.Max(fraction, LodMinRibbonFraction);

            return Mathf.Clamp(Mathf.RoundToInt(bakedCount * fraction), 0, bakedCount);
        }

        /// <summary>
        /// Rebuilds the skeleton-to-world matrices the GPU skins roots with, and reports
        /// whether they are usable.
        ///
        /// One matrix per BONE - not per vertex and not per hair. That is the whole
        /// economy of skinning the roots on the GPU: the CPU cost of a hundred thousand
        /// hairs following a skeleton is the cost of the fifty or so bones it has.
        /// </summary>
        private bool UpdateSkinMatrices()
        {
            Mesh mesh = _skinnedRenderer.sharedMesh;
            if (mesh == null)
                return false;

            // Already sent this frame by another camera. The buffer is still bound and
            // still correct, so the answer is yes - just not the upload again.
            if (_skinMatrixFrame == Time.frameCount && _skinMatrices != null)
                return true;

            // Read ONCE and cached. SkinnedMeshRenderer.bones allocates a fresh
            // Transform[] on every access, so touching it per frame - even only to compare
            // its length - hands the collector one array per furred object per frame. The
            // rig is re-read when the component is re-enabled, which is when a rig swap
            // reaches everything else too.
            _skinBones ??= _skinnedRenderer.bones;
            if (_skinBones == null || _skinBones.Length == 0)
                return false;

            _bindposes ??= mesh.bindposes;
            if (_bindposes == null || _bindposes.Length == 0)
                return false;

            int count = Mathf.Min(_skinBones.Length, _bindposes.Length);
            if (_skinMatrices == null || _skinMatrices.Length != count)
                _skinMatrices = new FurSkinMatrix[count];

            for (int i = 0; i < count; i++)
            {
                Transform bone = _skinBones[i];

                // bone-to-world composed with mesh-to-bone: mesh object space straight to
                // world, which is exactly what the vertex shader wants and saves it a
                // second transform per root.
                Matrix4x4 skin = bone != null
                    ? bone.localToWorldMatrix * _bindposes[i]
                    : _objectToWorld;

                // Sent as three explicit ROWS. See FurSkinMatrix for why a Matrix4x4
                // cannot be handed to a structured buffer and trusted.
                _skinMatrices[i] = new FurSkinMatrix
                {
                    Row0 = skin.GetRow(0),
                    Row1 = skin.GetRow(1),
                    Row2 = skin.GetRow(2),
                };
            }

            GraphicsBuffer buffer = EnsureSkinMatrixBuffer(count);
            buffer.SetData(_skinMatrices);
            _skinMatrixFrame = Time.frameCount;
            return true;
        }

        private GraphicsBuffer EnsureSkinMatrixBuffer(int count = 1)
        {
            count = Mathf.Max(count, 1);

            if (_skinMatrixBuffer == null || !_skinMatrixBuffer.IsValid() || _skinMatrixBuffer.count < count)
            {
                _skinMatrixBuffer?.Release();
                _skinMatrixBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    count, FurSkinMatrix.Stride);

                // Seeded with identity so the very first frame - and every unskinned coat,
                // which never writes to it - reads a matrix that at least does nothing,
                // rather than whatever the allocation happened to contain.
                var identity = new FurSkinMatrix[count];
                for (int i = 0; i < count; i++)
                {
                    identity[i] = new FurSkinMatrix
                    {
                        Row0 = new Vector4(1f, 0f, 0f, 0f),
                        Row1 = new Vector4(0f, 1f, 0f, 0f),
                        Row2 = new Vector4(0f, 0f, 1f, 0f),
                    };
                }
                _skinMatrixBuffer.SetData(identity);
            }

            return _skinMatrixBuffer;
        }

        private void WarnRibbonsUnavailable(string problem)
        {
            if (_ribbonWarningShown)
                return;

            _ribbonWarningShown = true;
            Debug.LogWarning($"[FurController] Strand fur cannot be drawn on " +
                             $"'{name}': {problem}", this);
        }

        // ------------------------------------------------------------------ //
        //  Scene view visualization
        // ------------------------------------------------------------------ //
        private void OnDrawGizmosSelected()
        {
            if (sourceRenderer == null)
                return;

            Gizmos.color = new Color(0.9f, 0.7f, 0.3f, 0.35f);
            Gizmos.DrawWireCube(_worldBounds.center, _worldBounds.size);
        }
    }
}
