namespace FurRendering
{
    /// <summary>
    /// Which UV set the per-object fur maps are stored in.
    ///
    /// All four of them - wetness, length, thickness, colour - are pictures of the
    /// SURFACE, one texel per piece of it. That only works while each piece of surface
    /// owns a texel of its own, and this is the switch for meshes whose UV0 does not
    /// give it one.
    ///
    /// Why UV0 so often does not
    /// -------------------------
    /// Because UV0 is authored for TEXTURING, where sharing a texel is a feature rather
    /// than a fault. Mirroring a creature down its spine halves the texture budget at no
    /// visible cost. Packing two limbs onto one island costs nothing when they carry the
    /// same paint. Neither is a mistake, and both are ordinary practice.
    ///
    /// What they do to a per-object map is fatal, though, and it is not something the
    /// brush or the simulation can work around: two pieces of surface arriving at one
    /// texel of storage is one number where two are needed. Paint a marking on one flank
    /// and it appears on the other; soak a paw and the shoulder sharing its texels goes
    /// dark too. The stroke resolved the right piece of surface - there was simply
    /// nowhere separate to record the answer.
    ///
    /// The way out is a second unwrap, and Unity already builds exactly the right one:
    /// lightmapping fails in the same way for the same reason, so the lightmap set is
    /// guaranteed non-overlapping. It comes from 'Generate Lightmap UVs' on the model
    /// importer, or from Unwrapping.GenerateSecondaryUVSet for a mesh built in code.
    /// </summary>
    public enum FurDetailUvSet
    {
        /// <summary>
        /// mesh.uv - the texturing unwrap. The default, and right whenever it happens to
        /// be free of overlaps. Nothing else has to be generated or maintained, and the
        /// maps line up with any authored texture painted against the same layout.
        /// </summary>
        UV0 = 0,

        /// <summary>
        /// mesh.uv2 - the lightmap unwrap, guaranteed non-overlapping.
        ///
        /// The answer for a mirrored or overlapped UV0. Costs a second set of UVs on the
        /// mesh and an unwrap at import time; in exchange every piece of surface gets a
        /// texel it does not share with anywhere else.
        ///
        /// An authored source texture is still SAMPLED by UV0 when it is resampled into
        /// the map, so a length or colour map painted in an external package against the
        /// model's own unwrap keeps working - only the storage moves.
        /// </summary>
        UV1 = 1,
    }
}
