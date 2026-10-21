using UnityEngine;

namespace FurRendering.Demo
{
    /// <summary>
    /// Continuous rotation for the lighting showcase pedestal, so the anisotropic sheen
    /// and backlight rim scatter sweep across every angle without user input.
    /// </summary>
    [AddComponentMenu("FURFLUX/Demo/Turntable")]
    public sealed class DemoTurntable : MonoBehaviour
    {
        [SerializeField] private Vector3 axis = Vector3.up;
        [SerializeField] private float degreesPerSecond = 25f;

        private void Update()
        {
            transform.Rotate(axis, degreesPerSecond * Time.deltaTime, Space.Self);
        }
    }
}
