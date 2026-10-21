using UnityEngine;

namespace FurRendering.Demo
{
    /// <summary>
    /// Applies elegant, controlled procedural motion to showcase fur inertia.
    /// Combines gentle idle drift with smooth, alternating left/right sways
    /// that stay comfortably framed inside the camera viewport.
    /// </summary>
    [AddComponentMenu("FURFLUX/Demo/Preview Sway")]
    [RequireComponent(typeof(FurController))]
    public class FurPreviewSway : MonoBehaviour
    {
        [Header("Idle Drift (Perlin Noise)")]
        [Tooltip("Maximum displacement during idle breathing drift (meters).")]
        [SerializeField] private float idleDriftDistance = 0.05f;

        [Tooltip("Drift oscillation frequency (lower = slower sway).")]
        [SerializeField] private float idleDriftSpeed = 0.35f;

        [Tooltip("Maximum yaw rotation during idle drift (degrees).")]
        [SerializeField] private float idleYawDegrees = 3f;

        [Header("Dynamic Inertia Sway")]
        [Tooltip("Maximum sway distance (meters). Kept compact so it stays inside camera frame.")]
        [SerializeField] private float swayDistance = 0.35f;

        [Tooltip("Sway oscillation speed (radians/sec).")]
        [SerializeField] private float swaySpeed = 1.4f;

        [Tooltip("Maximum tilt/roll angle during sway (degrees).")]
        [SerializeField] private float swayTiltDegrees = 12f;

        [Tooltip("Maximum yaw twist angle during sway (degrees).")]
        [SerializeField] private float swayYawDegrees = 8f;

        private Vector3 _startPosition;
        private Quaternion _startRotation;
        private float _timer;
        private float _noiseOffsetX;
        private float _noiseOffsetY;

        private void OnEnable()
        {
            _startPosition = transform.localPosition;
            _startRotation = transform.localRotation;
            _timer = 0f;

            _noiseOffsetX = Mathf.PerlinNoise(transform.position.x * 0.1f, 0f) * 100f;
            _noiseOffsetY = Mathf.PerlinNoise(0f, transform.position.z * 0.1f) * 100f;
        }

        private void Update()
        {
            _timer += Time.deltaTime;

            // 1. Idle Perlin drift for organic breathing
            float driftX = (Mathf.PerlinNoise(_timer * idleDriftSpeed + _noiseOffsetX, 0f) - 0.5f) * 2f;
            float driftZ = (Mathf.PerlinNoise(0f, _timer * idleDriftSpeed + _noiseOffsetY) - 0.5f) * 2f;
            Vector3 driftOffset = new Vector3(driftX * idleDriftDistance, 0f, driftZ * idleDriftDistance);
            float driftYaw = driftX * idleYawDegrees;

            // 2. Smooth, alternating lateral & forward figure-8 sway that never blows up
            float swayPhase = _timer * swaySpeed;
            float swayX = Mathf.Sin(swayPhase) * swayDistance;
            float swayZ = Mathf.Sin(swayPhase * 2f) * (swayDistance * 0.35f);
            Vector3 dynamicOffset = new Vector3(swayX, 0f, swayZ);

            // Tilting into the motion (leans like a pendulum/creature)
            float roll = -Mathf.Cos(swayPhase) * swayTiltDegrees;
            float yaw = Mathf.Sin(swayPhase) * swayYawDegrees + driftYaw;
            float pitch = Mathf.Cos(swayPhase * 2f) * (swayTiltDegrees * 0.4f);

            Quaternion dynamicRotation = Quaternion.Euler(pitch, yaw, roll);

            // Apply combined transform
            transform.localPosition = _startPosition + driftOffset + dynamicOffset;
            transform.localRotation = _startRotation * dynamicRotation;
        }
    }
}
