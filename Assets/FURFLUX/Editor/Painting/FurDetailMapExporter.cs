using System.IO;
using UnityEditor;
using UnityEngine;

namespace FurRendering.EditorTools
{
    /// <summary>
    /// Writes a painted detail map out to a texture asset.
    ///
    /// Separate from <see cref="FurPaintWindow"/> because these are two different jobs:
    /// the window decides WHERE to save and the exporter decides WHAT the bytes mean.
    /// The second one is where the colour space lives, and it is the part that is worth
    /// being able to test without a file dialog in the way.
    /// </summary>
    public static class FurDetailMapExporter
    {
        /// <summary>
        /// Encodes a baked detail map as an 8-bit PNG at <paramref name="projectPath"/>,
        /// imports it with settings that round-trip, and returns the imported texture.
        /// </summary>
        /// <param name="isColor">
        /// Whether the map holds a COLOUR rather than a multiplier. This decides the
        /// gamma treatment, and getting it wrong is not subtle: a gamma curve applied to
        /// a length multiplier bends every value in it, so the fur comes back a
        /// different length from the one that was painted.
        /// </param>
        public static Texture2D WriteToAsset(RenderTexture source, string projectPath, bool isColor)
        {
            if (source == null || string.IsNullOrEmpty(projectPath))
                return null;

            byte[] png = Encode(source, isColor);
            if (png == null)
                return null;

            string directory = Path.GetDirectoryName(projectPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllBytes(projectPath, png);

            AssetDatabase.ImportAsset(projectPath, ImportAssetOptions.ForceUpdate);
            ConfigureImporter(projectPath, isColor);

            return AssetDatabase.LoadAssetAtPath<Texture2D>(projectPath);
        }

        /// <summary>
        /// Reads the map back and builds 8-bit RGBA bytes by hand.
        ///
        /// By hand rather than through EncodeToPNG on a float texture because the colour
        /// space has to be DECIDED here and then stated to the importer, and the two have
        /// to agree. A colour map is written as sRGB and imported as sRGB, so the round
        /// trip back through the bake lands on the values that were painted; the two
        /// scalar maps are multipliers rather than colours and go through raw.
        ///
        /// Eight bits is enough for what these maps are. They are multipliers a texture
        /// would normally have supplied anyway, and 256 levels of fur length is finer
        /// than the eye resolves on a coat.
        /// </summary>
        private static byte[] Encode(RenderTexture source, bool isColor)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = source;

            var readback = new Texture2D(source.width, source.height, TextureFormat.RGBAHalf, false, true);
            readback.ReadPixels(new Rect(0f, 0f, source.width, source.height), 0, 0);
            readback.Apply();

            RenderTexture.active = previous;

            Color[] linear = readback.GetPixels();
            Object.DestroyImmediate(readback);

            // In a gamma project nothing converts on sample either, so the values go
            // through unchanged and the round trip still closes.
            bool convert = isColor && PlayerSettings.colorSpace == ColorSpace.Linear;

            var pixels = new Color32[linear.Length];
            for (int i = 0; i < linear.Length; i++)
            {
                Color c = linear[i];

                if (convert)
                {
                    c.r = Mathf.LinearToGammaSpace(c.r);
                    c.g = Mathf.LinearToGammaSpace(c.g);
                    c.b = Mathf.LinearToGammaSpace(c.b);
                }

                pixels[i] = new Color32(ToByte(c.r), ToByte(c.g), ToByte(c.b), 255);
            }

            var output = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, true);
            output.SetPixels32(pixels);
            output.Apply();

            byte[] png = output.EncodeToPNG();
            Object.DestroyImmediate(output);

            return png;
        }

        private static byte ToByte(float value)
        {
            return (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(value) * 255f), 0, 255);
        }

        private static void ConfigureImporter(string projectPath, bool isColor)
        {
            var importer = AssetImporter.GetAtPath(projectPath) as TextureImporter;
            if (importer == null)
                return;

            importer.sRGBTexture = isColor;

            // Uncompressed and unmipped on purpose. Compression puts block artefacts in
            // exactly the gradients these maps are made of, and the bake reads the source
            // at LOD 0 from a flat unwrap, so mips would only be memory nothing samples.
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;

            importer.SaveAndReimport();
        }
    }
}
