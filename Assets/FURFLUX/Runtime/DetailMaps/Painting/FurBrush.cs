using UnityEngine;

namespace FurRendering
{
    /// <summary>
    /// One brush stamp, described in WORLD SPACE.
    ///
    /// World space rather than UV space because that is the only frame in which the
    /// brush behaves the way an artist expects. UV density is never uniform across a
    /// model: a stamp of fixed UV radius is a fat blob where the unwrap is tight and a
    /// pinprick where it is loose, and it stops dead at every island edge. A sphere in
    /// world space is the same physical size wherever it lands and crosses seams
    /// without a join - see <see cref="FurAttributeMap.Paint"/> for how that is
    /// resolved on the GPU.
    ///
    /// This is a plain value type on purpose: a stroke is dozens of stamps a second,
    /// and each one should cost nothing to describe.
    /// </summary>
    public struct FurBrush
    {
        /// <summary>Centre of the brush sphere, in world space - normally the point under the cursor.</summary>
        public Vector3 PositionWS;

        /// <summary>
        /// Direction the brush is looking along - normally the camera ray. Only used
        /// when <see cref="FrontFacingOnly"/> is set, to tell the surface the artist is
        /// looking at from the one behind it.
        /// </summary>
        public Vector3 DirectionWS;

        /// <summary>Brush radius in meters.</summary>
        public float Radius;

        /// <summary>
        /// How far one stamp moves a texel toward the target value, 0..1. This is flow
        /// rather than opacity: strokes accumulate, so a low value builds up gradually
        /// as the cursor lingers.
        /// </summary>
        public float Strength;

        /// <summary>
        /// Edge falloff, 0..1. At 0 the weight ramps across the whole radius; at 1 it is
        /// flat to the very edge. Both keep the same footprint, so the on-screen circle
        /// stays honest either way.
        /// </summary>
        public float Hardness;

        /// <summary>
        /// How far through the model the stroke reaches: <see cref="FurPaintMode.Solid"/>
        /// paints only what is visible from <see cref="DirectionWS"/>, while
        /// <see cref="FurPaintMode.Wireframe"/> paints everything the sphere contains.
        ///
        /// A sphere has no idea which side of the model the artist was looking at, so
        /// without a mode every stroke would be a wireframe one.
        /// </summary>
        public FurPaintMode Mode;

        /// <summary>A brush with sane defaults, sized in meters.</summary>
        public static FurBrush Default(float radius = 0.1f)
        {
            return new FurBrush
            {
                PositionWS = Vector3.zero,
                DirectionWS = Vector3.forward,
                Radius = radius,
                Strength = 0.5f,
                Hardness = 0.5f,
                Mode = FurPaintMode.Solid,
            };
        }
    }
}
