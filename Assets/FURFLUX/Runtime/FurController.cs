using UnityEngine;
using UnityEngine.Rendering;

namespace FurRendering
{
    /// <summary>
    /// Shell-based fur controller (targets PC / desktop).
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
    /// assign a material using the "Fur/Shell Lit (PC)" shader.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Rendering/Fur/Fur Controller")]
    public sealed class FurController : MonoBehaviour
    {
        /// <summary>How the fur writes into the shadow map.</summary>
        public enum FurShadowMode
        {
            /// <summary>Fur casts no shadow (fastest).</summary>
            None = 0,
            /// <summary>Only the base shell casts a shadow. Visually near-identical, much cheaper.</summary>
            BaseLayerOnly = 1,
            /// <summary>Every shell casts a shadow. The most realistic self-shadowing, the most expensive.</summary>
            AllShells = 2,
        }

        public const int MaxShellCount = 256;

        // ------------------------------------------------------------------ //
        //  Source
        // ------------------------------------------------------------------ //
        // Always resolved from the same GameObject; deliberately not exposed in the Inspector.
        private Renderer sourceRenderer;

        [Tooltip("A material using the \"Fur/Shell Lit (PC)\" shader. Multiple furred objects " +
                 "can share the same material - per-object values are supplied through a " +
                 "MaterialPropertyBlock.")]
        [SerializeField] private Material furMaterial;

        [Tooltip("A LOW-POLY proxy mesh the shells are drawn from. Uses the source mesh when empty.\n\n" +
                 "In shell fur, base-mesh detail finer than the fur length buys nothing - it is " +
                 "hidden under the fur. Since shells are drawn once per layer, base mesh density " +
                 "is directly multiplied by the shell count.")]
        [SerializeField] private Mesh furProxyMesh;

        [Tooltip("When enabled, the original renderer's own draw is turned off; the base shell " +
                 "of the fur shader takes its place.")]
        [SerializeField] private bool hideSourceRenderer = true;

        // ------------------------------------------------------------------ //
        //  Quality
        // ------------------------------------------------------------------ //
        [Tooltip("How fur writes shadows. 'BaseLayerOnly' is a large saving whenever shell count is high.")]
        [SerializeField] private FurShadowMode shadowMode = FurShadowMode.BaseLayerOnly;

        [SerializeField] private bool receiveShadows = true;

        // ------------------------------------------------------------------ //
        //  LOD
        // ------------------------------------------------------------------ //
        [Header("LOD (Distance)")]
        [Tooltip("Reduces shell count as the camera moves away.")]
        [SerializeField] private bool enableLod = true;

        [Tooltip("X = camera distance in meters. Y = shell count. Use a smooth or parabolic curve " +
                 "to control the quality falloff. A value of 1 keeps only the base shell.")]
        [SerializeField] private AnimationCurve shellCountByDistance = new AnimationCurve(
            new Keyframe(0f, 32f), new Keyframe(10f, 20f), new Keyframe(30f, 4f), new Keyframe(50f, 1f));

        [Tooltip("Maximum shell density allowed by the fur's thickness on screen. " +
                 "Higher values improve close-up continuity at the cost of overdraw.")]
        [Range(0.5f, 6f)]
        [SerializeField] private float shellsPerPixel = 2f;

        [Tooltip("When fur is thinner than this many pixels on screen, only the base shell is drawn.")]
        [Range(0.1f, 8f)]
        [SerializeField] private float skinOnlyPixelThreshold = 1f;

        [Tooltip("Beyond this distance fur is not drawn at all (the object becomes fully bare). " +
                 "0 = disabled.")]
        [Min(0f)]
        [SerializeField] private float hardCullDistance = 0f;

        // ------------------------------------------------------------------ //
        //  Motion / inertia
        // ------------------------------------------------------------------ //
        [Header("Motion Inertia")]
        [Tooltip("Strands trail backward as the object moves.")]
        [SerializeField] private bool motionInertia = true;

        [Tooltip("How strongly fur trails behind object motion. Increase for a more visible sway response.")]
        [Range(0f, 10f)]
        [SerializeField] private float inertiaStrength = 0.5f;

        [Tooltip("How fast the trailing force responds. Higher = stiffer, faster recovery.")]
        [Range(0.5f, 30f)]
        [SerializeField] private float inertiaDamping = 8f;

        [Tooltip("Damping ratio (zeta) for the trailing force spring. Below 1 = underdamped (oscillates " +
                 "after stop, most visible inertia effect). 1 = critically damped (no overshoot, smooth settle). " +
                 "Above 1 = overdamped (mushy, slow). Typical 0.4-0.7 for fur feel.")]
        [Range(0.1f, 2f)]
        [SerializeField] private float inertiaDampingRatio = 0.55f;

        [Tooltip("How much turning (yaw/pitch/roll) adds to the trailing force, on top of straight-line " +
                 "motion. Lets fur react to a character spinning or turning in place, not just moving. " +
                 "0 disables the rotational contribution.")]
        [Range(0f, 3f)]
        [SerializeField] private float angularInertiaScale = 1f;

        // ------------------------------------------------------------------ //
        //  Appearance
        // ------------------------------------------------------------------ //
        [Header("Appearance")]
        [Tooltip("Wetness level. 0 = dry, 1 = fully wet. Wet fur appears darker, flatter, and more reflective.")]
        [Range(0f, 1f)]
        [SerializeField] private float wetness = 0f;

        [HideInInspector]
        [Tooltip("How much gravity increases when fully wet (multiplier applied to _Gravity).")]
        [SerializeField] private float gravityMultiplierOnWet = 2.8f;

        [HideInInspector]
        [Tooltip("How much stiffness increases when fully wet (multiplier applied to _Stiffness).")]
        [SerializeField] private float stiffnessMultiplierOnWet = 2.5f;

        [Tooltip("Strand mask texture. Red channel: 0 = strands removed, 1 = strands visible. Allows per-region strand density control.")]
        [SerializeField] private Texture2D strandMask = null;

        // ------------------------------------------------------------------ //
        //  Collision
        // ------------------------------------------------------------------ //
        [Header("Collision")]
        [Tooltip("Physics layers that can collide with and deflect the fur.")]
        [SerializeField] private LayerMask collisionLayers = 0;

        [Tooltip("How strongly collisions push the fur away.")]
        [Range(0f, 5f)]
        [SerializeField] private float collisionStrength = 1.5f;

        [Tooltip("How far beyond the fur's own surface the push-back starts and ramps to full " +
                 "strength (in meters). Measured from the fur surface, not the object center, so " +
                 "it scales correctly with the object's size.")]
        [Range(0.01f, 2f)]
        [SerializeField] private float collisionRadius = 0.4f;

        [Tooltip("How fast the collision push-back eases in and out. Higher = snappier response, " +
                 "lower = softer/smoother. This is what stops contact from popping the fur instantly.")]
        [Range(0.5f, 30f)]
        [SerializeField] private float collisionResponseSpeed = 10f;

        // ------------------------------------------------------------------ //
        //  Shader property IDs
        // ------------------------------------------------------------------ //
        private static readonly int ShellCountId              = Shader.PropertyToID("_ShellCount");
        private static readonly int ExternalForceId         = Shader.PropertyToID("_ExternalForce");
        private static readonly int FurLengthId             = Shader.PropertyToID("_FurLength");
        private static readonly int WetnessId               = Shader.PropertyToID("_Wetness");
        private static readonly int GravityMultiplierOnWetId = Shader.PropertyToID("_GravityMultiplierOnWet");
        private static readonly int StiffnessMultiplierOnWetId = Shader.PropertyToID("_StiffnessMultiplierOnWet");
        private static readonly int StrandMaskId            = Shader.PropertyToID("_StrandMask");
        private static readonly int CollisionForceId        = Shader.PropertyToID("_CollisionForce");

        // ------------------------------------------------------------------ //
        //  Runtime state
        // ------------------------------------------------------------------ //
        private readonly Matrix4x4[] _instanceMatrices = new Matrix4x4[MaxShellCount];
        private readonly Plane[] _frustumPlanes = new Plane[6];

        private MaterialPropertyBlock _propertyBlock;
        private SkinnedMeshRenderer _skinnedRenderer;
        private MeshFilter _meshFilter;
        private Mesh _bakedMesh;                // Mesh baked from the SkinnedMeshRenderer each frame
        private Mesh _activeMesh;
        private Matrix4x4 _objectToWorld = Matrix4x4.identity;
        private Bounds _worldBounds;
        private float _furLength = 0.05f;

        private Vector3 _previousPosition;
        private Quaternion _previousRotation;
        private Vector3 _smoothedForce;
        private Vector3 _forceVelocity;   // rate of change of _smoothedForce - the spring's own momentum
        private Vector3 _collisionForce;  // force from nearby colliders
        private float _lastRealtime;
        private int _lastUpdatedFrame = -1;
        private bool _sourceHiddenByUs;
        private bool _instancingWarningShown;

        // Value from the most recent draw, for debugging / profiling.
        private int _activeShellCount;

        /// <summary>Whether LOD is active - can be changed at runtime.</summary>
        public bool EnableLod
        {
            get => enableLod;
            set => enableLod = value;
        }

        /// <summary>Maximum number of fur shells permitted per screen pixel.</summary>
        public float ShellsPerPixel
        {
            get => shellsPerPixel;
            set => shellsPerPixel = Mathf.Clamp(value, 0.5f, 6f);
        }

        /// <summary>Shadow mode - can be changed at runtime.</summary>
        public FurShadowMode ShadowMode
        {
            get => shadowMode;
            set => shadowMode = value;
        }

        /// <summary>The shell count actually drawn after LOD, from the most recent draw (debugging / profiling).</summary>
        public int ActiveShellCount => _activeShellCount;

        public Material FurMaterial
        {
            get => furMaterial;
            set => furMaterial = value;
        }

        /// <summary>External force applied to the fur (velocity-based). Used for sway animation and motion inertia.</summary>
        public Vector3 ExternalForce
        {
            get => _smoothedForce;
            set => _smoothedForce = value;
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

            _propertyBlock ??= new MaterialPropertyBlock();
            _previousPosition = transform.position;
            _previousRotation = transform.rotation;
            _lastRealtime = Time.realtimeSinceStartup;
            _smoothedForce = Vector3.zero;
            _forceVelocity = Vector3.zero;
            _collisionForce = Vector3.zero;

            ApplySourceVisibility(hideSourceRenderer);

            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;

            ApplySourceVisibility(false);

            DestroyRuntimeObject(ref _bakedMesh);
        }

        private void OnValidate()
        {
            if (shellCountByDistance == null || shellCountByDistance.length == 0)
                shellCountByDistance = new AnimationCurve(new Keyframe(0f, 32f), new Keyframe(50f, 1f));

            if (isActiveAndEnabled)
            {
                CacheSource();
                ApplySourceVisibility(hideSourceRenderer);
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
        }

        private void ApplySourceVisibility(bool hide)
        {
            if (sourceRenderer == null)
                return;

            // forceRenderingOff only turns off drawing without disabling the component;
            // a SkinnedMeshRenderer's bone updates and BakeMesh keep working.
            if (hide)
            {
                sourceRenderer.forceRenderingOff = true;
                _sourceHiddenByUs = true;
            }
            else if (_sourceHiddenByUs)
            {
                sourceRenderer.forceRenderingOff = false;
                _sourceHiddenByUs = false;
            }
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

            UpdateFrameState();

            if (_activeMesh == null)
                return;

            // --- CPU-side culling ---
            // Unity already culls the GPU draw via worldBounds, but in an open world,
            // issuing a RenderMeshInstanced call for every furred object in the scene adds
            // up on the CPU. Cull what is off-screen before it is ever submitted.
            if (hardCullDistance > 0f &&
                Vector3.Distance(camera.transform.position, _worldBounds.center) > hardCullDistance)
                return;

            GeometryUtility.CalculateFrustumPlanes(camera, _frustumPlanes);
            if (!GeometryUtility.TestPlanesAABB(_frustumPlanes, _worldBounds))
                return;

            VerifyInstancingEnabled();

            int shells = ResolveShellCount(camera);
            if (shells <= 0)
                return;

            _activeShellCount = shells;
            RenderShells(camera, shells);
        }

        /// <summary>
        /// Warns once if GPU instancing is disabled on the material.
        /// (The material asset is never modified at runtime - that would be a
        /// permanent asset change and would show up as noise in version control.)
        /// </summary>
        private void VerifyInstancingEnabled()
        {
            if (_instancingWarningShown || furMaterial == null || furMaterial.enableInstancing)
                return;

            _instancingWarningShown = true;
            Debug.LogWarning(
                $"[FurController] GPU Instancing is disabled on '{furMaterial.name}'. " +
                "Graphics.RenderMeshInstanced requires it - enable 'Enable GPU Instancing' " +
                "on the material in the Inspector.", this);
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

            // --- Time step (also correct in Edit Mode) ---
            // Capped at 0.05s (not just for smoothness): the inertia spring below integrates with
            // explicit Euler, which can start oscillating out of control if dt * inertiaDamping grows
            // too large - this keeps that product in a safe range even at the slider's max damping.
            float now = Time.realtimeSinceStartup;
            float dt = Mathf.Clamp(now - _lastRealtime, 1f / 1000f, 0.05f);
            _lastRealtime = now;

            // --- Motion inertia: strands trail opposite the object's motion ---
            Vector3 position = transform.position;
            Quaternion rotation = transform.rotation;
            if (motionInertia)
            {
                Vector3 linearVelocity = (position - _previousPosition) / dt;

                // Turning drags fur sideways too. Tangential velocity from rotation is v = omega x r.
                // Fur is applied at the surface, so use bounds extents as the radius from center.
                Vector3 angularVelocity = Vector3.zero;
                if (angularInertiaScale > 0f)
                {
                    (rotation * Quaternion.Inverse(_previousRotation)).ToAngleAxis(out float angleDeg, out Vector3 axis);
                    if (angleDeg > 180f) angleDeg -= 360f;
                    angularVelocity = axis * (angleDeg * Mathf.Deg2Rad / dt);
                }

                float radiusFromCenter = sourceRenderer.bounds.extents.magnitude;
                // v = omega x r: tangential velocity at fur surface from rotation
                Vector3 surfaceRotationVelocity = Vector3.Cross(angularVelocity,
                    transform.TransformDirection(Vector3.forward * radiusFromCenter));
                Vector3 totalVelocity = linearVelocity + surfaceRotationVelocity * angularInertiaScale;

                // The force is used directly by the shader to bend shell tips. Do not damp it
                // with an arbitrary scalar here: short fur otherwise receives an imperceptible
                // displacement even when the object moves quickly.
                Vector3 target = Vector3.ClampMagnitude(-totalVelocity * inertiaStrength, 4f);

                // Damped harmonic oscillator: ẍ + 2ζω_n·ẋ + ω_n²·x = 0
                // Natural frequency: ω_n = inertiaDamping, Damping ratio: ζ = inertiaDampingRatio
                // This gives _smoothedForce its own momentum (_forceVelocity), so it continues moving
                // after the object stops - the overshoot and settle IS the visible fur inertia.
                float omega_n = inertiaDamping;
                float zeta = inertiaDampingRatio;
                Vector3 error = target - _smoothedForce;
                Vector3 accel = error * (omega_n * omega_n) - _forceVelocity * (2f * zeta * omega_n);
                _forceVelocity += accel * dt;
                _smoothedForce += _forceVelocity * dt;
            }
            else
            {
                _smoothedForce = Vector3.zero;
                _forceVelocity = Vector3.zero;
            }
            _previousPosition = position;
            _previousRotation = rotation;

            // --- Collision detection ---
            Vector3 targetCollisionForce = Vector3.zero;
            if (collisionLayers != 0 && collisionRadius > 0f)
            {
                Vector3 center = sourceRenderer.bounds.center;
                // The fur itself already occupies bodyRadius meters around the center - the
                // detection sphere must reach past that to catch a wall before it reaches the
                // fur surface, not just when it gets within collisionRadius of the center.
                // extents.magnitude is the AABB's half-diagonal, not the mesh's actual radius -
                // for a sphere it overshoots by sqrt(3) (~73%), which was firing the collision
                // response while the collider was still visibly clear of the fur. The largest
                // single axis is a much closer match for round/compact meshes (exact for a
                // sphere) and never picks up the diagonal's extra reach.
                Vector3 extents = sourceRenderer.bounds.extents;
                float bodyRadius = Mathf.Max(extents.x, Mathf.Max(extents.y, extents.z));
                float detectionRadius = bodyRadius + collisionRadius;

                Collider[] nearbyColliders = Physics.OverlapSphere(center, detectionRadius, collisionLayers);

                foreach (Collider col in nearbyColliders)
                {
                    if (col.gameObject == gameObject) continue;

                    Vector3 closestPoint = col.ClosestPoint(center);
                    Vector3 toCenter = center - closestPoint;
                    float distanceFromCenter = toCenter.magnitude;

                    Vector3 pushDir;
                    if (distanceFromCenter > 1e-4f)
                    {
                        pushDir = toCenter / distanceFromCenter;
                    }
                    else
                    {
                        // ClosestPoint degenerates to the query point when center is already
                        // inside the collider (deep penetration) - fall back to pushing away
                        // from the collider's own bounds center instead of a zero vector.
                        Vector3 fallback = center - col.bounds.center;
                        pushDir = fallback.sqrMagnitude > 1e-6f ? fallback.normalized : Vector3.up;
                    }

                    // Ramp the force in as the wall crosses the fur's own outer radius, so it
                    // starts pushing back before the wall reaches the strands, not after.
                    float surfaceGap = distanceFromCenter - bodyRadius; // negative = wall inside the fur volume
                    float falloff = Mathf.Clamp01(1f - surfaceGap / collisionRadius);

                    targetCollisionForce += pushDir * falloff * collisionStrength;
                }
                targetCollisionForce = Vector3.ClampMagnitude(targetCollisionForce, 2f);
            }

            // Ease toward the target instead of snapping straight to it - this is what turns an
            // instant contact into a smooth push, and also smooths the release when the collider
            // leaves (target drops back to zero but _collisionForce eases down instead of vanishing).
            _collisionForce = Vector3.Lerp(_collisionForce, targetCollisionForce,
                1f - Mathf.Exp(-collisionResponseSpeed * dt));

            // --- Fur length: used by both the bounds and LOD below ---
            _furLength = furMaterial.HasProperty(FurLengthId) ? furMaterial.GetFloat(FurLengthId) : 0.05f;

            // --- World bounds: expanded by fur length plus trailing displacement and collision ---
            _worldBounds = sourceRenderer.bounds;
            _worldBounds.Expand((_furLength + (_smoothedForce.magnitude + _collisionForce.magnitude) * _furLength) * 2f);
        }

        /// <summary>
        /// Resolves shell count from the artist-authored distance curve.
        /// </summary>
        private int ResolveShellCount(Camera camera)
        {
            if (!enableLod)
                return Mathf.Clamp(Mathf.RoundToInt(shellCountByDistance.Evaluate(0f)), 1, MaxShellCount);

            float distance = Mathf.Max(
                Vector3.Distance(camera.transform.position, _worldBounds.center) - _worldBounds.extents.magnitude,
                0.01f);

            float pixelsPerMeter = camera.orthographic
                ? camera.pixelHeight / (2f * Mathf.Max(camera.orthographicSize, 1e-4f))
                : camera.pixelHeight / (2f * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * distance);

            float furPixels = _furLength * pixelsPerMeter;
            if (furPixels < skinOnlyPixelThreshold)
                return 1;

            int distanceShells = Mathf.RoundToInt(shellCountByDistance.Evaluate(distance));
            int screenSpaceShells = Mathf.CeilToInt(furPixels * shellsPerPixel);
            return Mathf.Clamp(Mathf.Min(distanceShells, screenSpaceShells), 1, MaxShellCount);
        }

        private void RenderShells(Camera camera, int shells)
        {
            // All instances share the same transform; the per-layer offset happens in the
            // shader via unity_InstanceID, so a single draw call is enough.
            for (int i = 0; i < shells; i++)
                _instanceMatrices[i] = _objectToWorld;

            // Per-object values go through the property block. These constants are declared
            // outside the UnityPerMaterial buffer in the shader, so the override actually
            // takes effect - no material copy needed, and LOD is correct per camera.
            _propertyBlock.SetFloat(ShellCountId, shells);
            _propertyBlock.SetVector(ExternalForceId, _smoothedForce);
            _propertyBlock.SetFloat(WetnessId, wetness);
            _propertyBlock.SetFloat(GravityMultiplierOnWetId, gravityMultiplierOnWet);
            _propertyBlock.SetFloat(StiffnessMultiplierOnWetId, stiffnessMultiplierOnWet);
            // Set strand mask (white texture if none assigned, so all strands remain visible).
            _propertyBlock.SetTexture(StrandMaskId, strandMask != null ? strandMask : Texture2D.whiteTexture);
            _propertyBlock.SetVector(CollisionForceId, _collisionForce);
            bool allShellsCastShadows = shadowMode == FurShadowMode.AllShells;

            var renderParams = new RenderParams(furMaterial)
            {
                worldBounds = _worldBounds,
                camera = camera,
                layer = gameObject.layer,
                renderingLayerMask = sourceRenderer.renderingLayerMask,
                receiveShadows = receiveShadows,
                shadowCastingMode = allShellsCastShadows ? ShadowCastingMode.On : ShadowCastingMode.Off,
                reflectionProbeUsage = ReflectionProbeUsage.BlendProbes,
                motionVectorMode = MotionVectorGenerationMode.Camera,
                matProps = _propertyBlock,
            };

            int subMeshCount = _activeMesh.subMeshCount;
            for (int subMesh = 0; subMesh < subMeshCount; subMesh++)
                Graphics.RenderMeshInstanced(renderParams, _activeMesh, subMesh, _instanceMatrices, shells);

            // "Base layer only" shadow mode: a single instance writes into the shadow map.
            // Instance 0 is always the skin shell (layerT = 0), so no further adjustment
            // to shell count is needed.
            if (shadowMode == FurShadowMode.BaseLayerOnly)
            {
                renderParams.shadowCastingMode = ShadowCastingMode.ShadowsOnly;

                for (int subMesh = 0; subMesh < subMeshCount; subMesh++)
                    Graphics.RenderMeshInstanced(renderParams, _activeMesh, subMesh, _instanceMatrices, 1);
            }
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
