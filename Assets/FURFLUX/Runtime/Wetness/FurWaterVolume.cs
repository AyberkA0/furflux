using UnityEngine;

namespace FurRendering
{
    /// <summary>
    /// Optional marker for a water volume that wets fur.
    ///
    /// The wetness system finds volumes purely by physics layer, so this component
    /// is not required - a collider on the configured layer is enough. Add it only
    /// when a specific volume needs to behave differently from the defaults on
    /// <see cref="FurController"/>: a waterfall that soaks instantly, a damp patch
    /// of grass that never gets fur past half wet, or a wider spray margin around
    /// a churning surface.
    ///
    /// Put it on the same GameObject as the collider.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("FURFLUX/Fur Water Volume")]
    public sealed class FurWaterVolume : MonoBehaviour
    {
        [Tooltip("Maximum wetness this volume can drive fur to. 1 = soaked, 0.4 = damp at most.")]
        [Range(0f, 1f)]
        [SerializeField] private float strength = 1f;

        [Tooltip("How far the wetting reaches OUTSIDE the collider, in meters - the spray / " +
                 "splash margin around the waterline. 0 gives a hard edge exactly at the collider " +
                 "surface, which reads as a razor line across the fur. Negative values are not " +
                 "allowed; use Strength to weaken a volume instead.\n\n" +
                 "-1 = use the Contact Feather value on the FurController.")]
        [SerializeField] private float featherOverride = -1f;

        /// <summary>Maximum wetness this volume drives fur to, 0..1.</summary>
        public float Strength
        {
            get => strength;
            set => strength = Mathf.Clamp01(value);
        }

        /// <summary>Wetting reach outside the collider in meters, or a negative value to inherit the controller's.</summary>
        public float FeatherOverride
        {
            get => featherOverride;
            set => featherOverride = value;
        }

        /// <summary>Resolves the feather to use, falling back to the controller's value.</summary>
        public float ResolveFeather(float controllerFeather)
        {
            return featherOverride >= 0f ? featherOverride : controllerFeather;
        }
    }
}
