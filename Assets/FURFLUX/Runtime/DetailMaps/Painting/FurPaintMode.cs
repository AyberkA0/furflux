namespace FurRendering
{
    /// <summary>
    /// How far through the model a brush stroke reaches.
    ///
    /// The two names come from the equivalent modes in DCC tools, and mean the same
    /// things here: <see cref="Solid"/> paints what you can see, <see cref="Wireframe"/>
    /// paints everything the brush sphere physically contains.
    /// </summary>
    public enum FurPaintMode
    {
        /// <summary>
        /// Only the surface actually visible from the brush direction.
        ///
        /// Two separate things have to be true for a texel to qualify, and leaving out
        /// either one produces a mode that looks right until it does not:
        ///  * it faces the brush, so the back of the model is excluded; and
        ///  * nothing else on the model is in front of it, so painting the near leg does
        ///    not also mark the far one.
        ///
        /// The second test needs a depth capture of the model from the brush's viewpoint
        /// - see <see cref="FurController.CaptureBrushOcclusion"/>. Without one this mode
        /// still excludes back faces, but cannot tell an occluded surface from a visible
        /// one, so a wide brush reaches through the body.
        /// </summary>
        Solid,

        /// <summary>
        /// Everything inside the brush sphere: front faces, back faces, and surfaces
        /// hidden behind other geometry alike.
        ///
        /// This is the mode for anything the coat does all the way through - a band
        /// around a tail, a collar, the whole of a thin ear - where having to rotate the
        /// model and paint the far side to match is exactly the work that produces two
        /// edges that do not line up.
        /// </summary>
        Wireframe,
    }
}
