using UnityEngine;

namespace FurRendering.Demo
{
    /// <summary>
    /// Generic sine-wave oscillator used by the showcase scene to keep every station in
    /// constant, self-looping motion: bobbing an object through a water volume, swinging
    /// a collider arm through fur, or driving a "bone" transform the FurController reads
    /// for localised inertia.
    ///
    /// Drives BOTH local position and local rotation from the same clock, each with its
    /// own per-axis amplitude, so a single component can cover very different jobs just by
    /// zeroing out the axes a given station does not need.
    /// </summary>
    [AddComponentMenu("FURFLUX/Demo/Oscillator")]
    public sealed class DemoOscillator : MonoBehaviour
    {
        [Header("Position (local space, meters)")]
        [SerializeField] private Vector3 positionAmplitude = Vector3.zero;
        [SerializeField] private Vector3 positionSpeed = Vector3.one;
        [SerializeField] private Vector3 positionPhaseDeg = Vector3.zero;

        [Header("Rotation (local Euler, degrees)")]
        [SerializeField] private Vector3 rotationAmplitude = Vector3.zero;
        [SerializeField] private Vector3 rotationSpeed = Vector3.one;
        [SerializeField] private Vector3 rotationPhaseDeg = Vector3.zero;

        [Tooltip("0 = sine wave (smooth back and forth). 1 = clamped to the positive half " +
                 "of the wave (rises then holds at rest), useful for a one-way dip into water.")]
        [Range(0f, 1f)]
        [SerializeField] private float positiveHalfOnly = 0f;

        private Vector3 _basePosition;
        private Quaternion _baseRotation;
        private float _clock;

        private void OnEnable()
        {
            _basePosition = transform.localPosition;
            _baseRotation = transform.localRotation;
            _clock = 0f;
        }

        private void Update()
        {
            _clock += Time.deltaTime;

            Vector3 posOffset = new Vector3(
                Wave(positionSpeed.x, positionPhaseDeg.x) * positionAmplitude.x,
                Wave(positionSpeed.y, positionPhaseDeg.y) * positionAmplitude.y,
                Wave(positionSpeed.z, positionPhaseDeg.z) * positionAmplitude.z);

            Vector3 rotOffset = new Vector3(
                Wave(rotationSpeed.x, rotationPhaseDeg.x) * rotationAmplitude.x,
                Wave(rotationSpeed.y, rotationPhaseDeg.y) * rotationAmplitude.y,
                Wave(rotationSpeed.z, rotationPhaseDeg.z) * rotationAmplitude.z);

            transform.localPosition = _basePosition + posOffset;
            transform.localRotation = _baseRotation * Quaternion.Euler(rotOffset);
        }

        private float Wave(float speed, float phaseDeg)
        {
            float s = Mathf.Sin(_clock * speed + phaseDeg * Mathf.Deg2Rad);
            if (positiveHalfOnly <= 0f)
                return s;

            // Remaps [-1, 1] to [0, 1] then blends toward that remap, so 1 gives a
            // strictly one-way rise-and-settle motion instead of swinging below rest.
            float halfWave = (s + 1f) * 0.5f;
            return Mathf.Lerp(s, halfWave, positiveHalfOnly);
        }
    }
}
