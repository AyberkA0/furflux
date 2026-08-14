using UnityEngine;

namespace FurRendering.Demo
{
    /// <summary>
    /// Applies procedural sway animation to a furred object: idle drift (Perlin noise)
    /// plus periodic deliberate motion (ease-out, hold, ease-back).
    /// </summary>
    [RequireComponent(typeof(FurController))]
    public class FurPreviewSway : MonoBehaviour
    {
        [Header("Idle Drift (Perlin Noise)")]
        [Tooltip("Maximum lateral displacement during idle drift (meters).")]
        [SerializeField] private float idleDriftDistance = 0.045f;

        [Tooltip("Drift oscillation frequency (lower = slower sway).")]
        [SerializeField] private float idleDriftSpeed = 0.22f;

        [Tooltip("Maximum yaw rotation during idle drift (degrees).")]
        [SerializeField] private float idleYawDegrees = 2f;

        [Header("Periodic Flick")]
        [Tooltip("Time between flick motions (seconds).")]
        [SerializeField] private float intervalSeconds = 6f;

        [Tooltip("Duration of the outward swipe (ease-out phase).")]
        [SerializeField] private float travelSeconds = 0.3f;

        [Tooltip("Duration to hold at flick peak before returning.")]
        [SerializeField] private float holdSeconds = 0.5f;

        [Tooltip("Maximum lateral distance during flick (meters).")]
        [SerializeField] private float horizontalDistance = 0.18f;

        [Tooltip("Maximum yaw rotation during flick (degrees).")]
        [SerializeField] private float flickYawDegrees = 9f;

        private FurController _furController;
        private Vector3 _startPosition;
        private Quaternion _startRotation;
        private float _timer;
        private float _flickTimer;
        private float _noiseOffsetX;
        private float _noiseOffsetY;

        private void OnEnable()
        {
            _furController = GetComponent<FurController>();
            _startPosition = transform.localPosition;
            _startRotation = transform.localRotation;
            _timer = 0f;
            _flickTimer = 0f;

            // Per-instance Perlin noise offset prevents lockstep sync across multiple objects
            _noiseOffsetX = Mathf.PerlinNoise(transform.position.x * 0.1f, 0f) * 100f;
            _noiseOffsetY = Mathf.PerlinNoise(0f, transform.position.z * 0.1f) * 100f;
        }

        private void Update()
        {
            _timer += Time.deltaTime;
            _flickTimer += Time.deltaTime;

            // Idle drift: Perlin noise at the base
            float driftX = Mathf.PerlinNoise(_timer * idleDriftSpeed + _noiseOffsetX, 0f) - 0.5f;
            float driftY = Mathf.PerlinNoise(0f, _timer * idleDriftSpeed + _noiseOffsetY) - 0.5f;
            float driftYaw = driftX * idleYawDegrees;

            Vector3 driftOffset = new Vector3(driftX * idleDriftDistance, 0f, driftY * idleDriftDistance);
            Quaternion driftRotation = Quaternion.Euler(0f, driftYaw, 0f);

            // Periodic flick: three phases
            float cycleDuration = intervalSeconds + travelSeconds + holdSeconds;
            float cycleT = _flickTimer % cycleDuration;

            Vector3 flickOffset = Vector3.zero;
            float flickYaw = 0f;

            if (cycleT < travelSeconds)
            {
                // Phase 1: ease-out (0 → 1)
                float t = cycleT / travelSeconds;
                float easeOut = 1f - Mathf.Pow(1f - t, 3f); // cubic ease-out
                flickOffset.z = horizontalDistance * easeOut;
                flickYaw = flickYawDegrees * easeOut;
            }
            else if (cycleT < travelSeconds + holdSeconds)
            {
                // Phase 2: hold at peak
                flickOffset.z = horizontalDistance;
                flickYaw = flickYawDegrees;
            }
            else if (cycleT < cycleDuration)
            {
                // Phase 3: ease-back (1 → 0)
                float t = (cycleT - travelSeconds - holdSeconds) / travelSeconds;
                float easeIn = Mathf.Pow(t, 3f); // cubic ease-in (return)
                flickOffset.z = horizontalDistance * (1f - easeIn);
                flickYaw = flickYawDegrees * (1f - easeIn);
            }

            Quaternion flickRotation = Quaternion.Euler(0f, flickYaw, 0f);

            // Combine: drift + flick
            Vector3 finalPosition = _startPosition + driftOffset + flickOffset;
            Quaternion finalRotation = _startRotation * driftRotation * flickRotation;

            transform.localPosition = finalPosition;
            transform.localRotation = finalRotation;

            // Push motion as external force to fur controller for inertia settling
            _furController.ExternalForce = (driftOffset + flickOffset) / Time.deltaTime;
        }

        // Runtime tuning properties
        public float IdleDriftDistance { get => idleDriftDistance; set => idleDriftDistance = value; }
        public float IdleDriftSpeed { get => idleDriftSpeed; set => idleDriftSpeed = value; }
        public float IdleYawDegrees { get => idleYawDegrees; set => idleYawDegrees = value; }
        public float IntervalSeconds { get => intervalSeconds; set => intervalSeconds = value; }
        public float TravelSeconds { get => travelSeconds; set => travelSeconds = value; }
        public float HoldSeconds { get => holdSeconds; set => holdSeconds = value; }
        public float HorizontalDistance { get => horizontalDistance; set => horizontalDistance = value; }
        public float FlickYawDegrees { get => flickYawDegrees; set => flickYawDegrees = value; }
    }
}
