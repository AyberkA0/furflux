using UnityEngine;

namespace FurRendering
{
    /// <summary>
    /// Converts a Unity <see cref="Collider"/> into the primitive form the fur shaders
    /// evaluate as a signed distance field.
    ///
    /// Two separate systems need exactly this: the wetness map (which volumes is this
    /// point inside?) and fur collision (how far is this strand tip from a surface?).
    /// The capsule axis handling and Unity's own collider scaling rules are fiddly
    /// enough that having them in one place is the difference between the two systems
    /// agreeing and them disagreeing in ways nobody notices until a capsule is rotated.
    ///
    /// The emitted transform is RIGID - rotation and translation only, with scale
    /// folded into the dimensions instead. Distances measured in that space are
    /// therefore real meters, which is what lets both the wetness feather and the
    /// collision margin be authored as plain metre values that do not silently change
    /// meaning when a volume is scaled.
    ///
    /// Type tags must match the shader side:
    ///   0 = box, 1 = sphere, 2 = capsule (segment along local Y)
    /// </summary>
    internal static class FurColliderPacker
    {
        public const float TypeBox = 0f;
        public const float TypeSphere = 1f;
        public const float TypeCapsule = 2f;

        /// <summary>
        /// Resolves <paramref name="collider"/> into a rigid world-to-local transform,
        /// its dimensions in meters, and a type tag.
        /// </summary>
        public static void Pack(Collider collider, out Matrix4x4 worldToLocal, out Vector4 shape, out float type)
        {
            Transform t = collider.transform;
            Vector3 lossy = t.lossyScale;
            var absScale = new Vector3(Mathf.Abs(lossy.x), Mathf.Abs(lossy.y), Mathf.Abs(lossy.z));

            Vector3 worldCenter;
            Quaternion worldRotation;

            switch (collider)
            {
                case BoxCollider box:
                {
                    worldCenter = t.TransformPoint(box.center);
                    worldRotation = t.rotation;
                    Vector3 half = Vector3.Scale(box.size * 0.5f, absScale);
                    shape = new Vector4(half.x, half.y, half.z, 0f);
                    type = TypeBox;
                    break;
                }

                case SphereCollider sphere:
                {
                    // Unity scales a sphere collider uniformly, by the largest axis.
                    float radius = sphere.radius * Mathf.Max(absScale.x, Mathf.Max(absScale.y, absScale.z));
                    worldCenter = t.TransformPoint(sphere.center);
                    worldRotation = t.rotation;
                    shape = new Vector4(radius, 0f, 0f, 0f);
                    type = TypeSphere;
                    break;
                }

                case CapsuleCollider capsule:
                {
                    // The shader's capsule always runs along local Y, so the collider's
                    // own axis choice is folded into the rotation rather than branched
                    // on per vertex.
                    Quaternion axisFix;
                    float radiusScale;
                    float heightScale;

                    switch (capsule.direction)
                    {
                        case 0:  // X
                            axisFix = Quaternion.Euler(0f, 0f, -90f);
                            radiusScale = Mathf.Max(absScale.y, absScale.z);
                            heightScale = absScale.x;
                            break;
                        case 2:  // Z
                            axisFix = Quaternion.Euler(90f, 0f, 0f);
                            radiusScale = Mathf.Max(absScale.x, absScale.y);
                            heightScale = absScale.z;
                            break;
                        default: // Y
                            axisFix = Quaternion.identity;
                            radiusScale = Mathf.Max(absScale.x, absScale.z);
                            heightScale = absScale.y;
                            break;
                    }

                    float radius = capsule.radius * radiusScale;
                    float height = Mathf.Max(capsule.height * heightScale, radius * 2f);

                    worldCenter = t.TransformPoint(capsule.center);
                    worldRotation = t.rotation * axisFix;
                    // y = half the length of the inner SEGMENT, not of the whole capsule.
                    shape = new Vector4(radius, height * 0.5f - radius, 0f, 0f);
                    type = TypeCapsule;
                    break;
                }

                default:
                {
                    // Mesh and terrain colliders have no closed-form SDF, so they come
                    // through as their world AABB. Fur will bend around that box rather
                    // than around the real surface - model water volumes and anything
                    // fur should hug with a box, sphere or capsule.
                    Bounds bounds = collider.bounds;
                    worldCenter = bounds.center;
                    worldRotation = Quaternion.identity;
                    shape = new Vector4(bounds.extents.x, bounds.extents.y, bounds.extents.z, 0f);
                    type = TypeBox;
                    break;
                }
            }

            worldToLocal = Matrix4x4.TRS(worldCenter, worldRotation, Vector3.one).inverse;
        }

        /// <summary>
        /// Smallest thickness of a packed primitive, in meters - how far a straight line
        /// can travel through it in its thinnest direction.
        ///
        /// Fur can only tunnel through a collider thinner than the fur is long, so this
        /// is what decides whether the shader needs to run its (much more expensive)
        /// strand-crossing march against a given collider at all.
        /// </summary>
        public static float MinimumThickness(Vector4 shape, float type)
        {
            // Sphere and capsule are both radius-based; a box is thinnest across its
            // smallest axis.
            if (type == TypeSphere || type == TypeCapsule)
                return shape.x * 2f;

            return Mathf.Min(shape.x, Mathf.Min(shape.y, shape.z)) * 2f;
        }

        /// <summary>
        /// Whether this collider should be ignored because it belongs to the furred
        /// object itself. Checking the whole transform hierarchy rather than just the
        /// one GameObject matters: a character's bone capsules and hitboxes live on
        /// children, and without this the creature collides with (or soaks in) its own body.
        /// </summary>
        public static bool IsSelf(Collider collider, GameObject owner)
        {
            return owner != null && collider.transform.IsChildOf(owner.transform);
        }
    }
}
