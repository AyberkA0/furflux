namespace FurRendering
{
    /// <summary>
    /// Which of the three per-object detail maps something refers to.
    ///
    /// The three maps are one mechanism with three destinations - an authored or painted
    /// texture in the mesh's own UV0 layout, multiplied into a material value - so the
    /// code that allocates, clears and paints them takes the channel as an argument
    /// rather than existing three times over.
    /// </summary>
    public enum FurDetailChannel
    {
        /// <summary>Multiplies the material's Fur Length. 1 leaves it alone, 0 shaves the region bare.</summary>
        Length,

        /// <summary>Multiplies the material's strand thickness. 1 leaves it alone, 0 removes the strands.</summary>
        Thickness,

        /// <summary>Multiplies the albedo. White leaves the coat alone, so white is also the eraser.</summary>
        Color,

        /// <summary>
        /// Which way the hair LIES, as a direction in the surface's own tangent frame.
        ///
        /// The odd one out, and worth saying why. The three above are multipliers on a
        /// material value: one number, a neutral of "leave it alone", and a brush loaded
        /// from a slider. This one stores a 2D VECTOR - encoded in RG the way a normal map
        /// encodes its xy, with (0.5, 0.5) meaning uncombed - and its brush value does not
        /// come from a slider at all. It comes from the direction of the stroke, because
        /// that is what combing is: you drag across the coat and the hair lies down the way
        /// your hand went.
        ///
        /// Tangent space rather than world, and that is not a detail. A comb painted along
        /// an animal's back has to still run along its back when the animal turns around,
        /// and only a direction stored relative to the surface does that.
        /// </summary>
        Comb,
    }
}
