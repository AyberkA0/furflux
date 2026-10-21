using System.Collections.Generic;
using UnityEngine;

namespace FurRendering.EditorTools
{
    /// <summary>
    /// Measures whether a mesh's UV0 can hold a per-object map at all.
    ///
    /// Every fur map - wetness, length, thickness, colour - stores one value per piece of
    /// SURFACE. That works only while each piece of surface owns a texel of its own, and a
    /// great many perfectly good unwraps do not give it one: mirroring a creature down its
    /// spine halves the texture budget for free, and packing two limbs onto one island
    /// costs nothing when they carry the same paint. Both are ordinary texturing practice
    /// and both are fatal here.
    ///
    /// The symptom is unmistakable once named and baffling until then - paint a marking on
    /// one flank, watch it appear on the other - so this exists to name it BEFORE the first
    /// stroke rather than leaving it to be discovered.
    ///
    /// Measured the way the bake measures it: rasterise UV0 into the map's own grid and
    /// count the texels that more than one triangle claims. Nothing here approximates the
    /// question - this is the artefact itself, counted.
    /// </summary>
    internal static class FurUnwrapAnalysis
    {
        /// <summary>
        /// The grid this is measured on, whatever the map's real resolution.
        ///
        /// The fraction being measured is a property of the LAYOUT, not of how finely it is
        /// sampled, so it settles quickly as the grid gets denser - a mirrored model reads
        /// about the same at 256 as at 2048. Measuring at the map's own resolution would
        /// make a 2048 map sixty-four times the work for a number that moves in its first
        /// decimal, and this runs from OnGUI.
        /// </summary>
        private const int MeasureResolution = 256;

        private readonly struct Key : System.IEquatable<Key>
        {
            public readonly int MeshId;
            public readonly int Resolution;

            public Key(int meshId, int resolution)
            {
                MeshId = meshId;
                Resolution = resolution;
            }

            public bool Equals(Key other) => MeshId == other.MeshId && Resolution == other.Resolution;
            public override bool Equals(object obj) => obj is Key k && Equals(k);
            public override int GetHashCode() => (MeshId * 397) ^ Resolution;
        }

        private static readonly Dictionary<Key, float> Cache = new Dictionary<Key, float>();

        /// <summary>
        /// What fraction of the covered map is claimed by more than one triangle, 0..1.
        ///
        /// Measured against the COVERED texels rather than against the whole square,
        /// because that is the question being asked: not how much of the texture is in
        /// use, but how much of the surface has nowhere of its own to record an answer.
        ///
        /// Returns 0 for anything it cannot measure - an unreadable mesh, a missing
        /// unwrap. Silence is the right answer there: the tool has nothing to report, and
        /// a warning it cannot substantiate would train people to ignore this one.
        /// </summary>
        public static float SharedTexelFraction(Mesh mesh, int mapResolution)
        {
            if (mesh == null || !mesh.isReadable)
                return 0f;

            // The resolution is part of the key even though the measurement is taken at a
            // fixed grid: a caller that changes it expects a fresh answer, and the cost of
            // honouring that is one extra entry.
            var key = new Key(mesh.GetInstanceID(), mapResolution);
            if (Cache.TryGetValue(key, out float cached))
                return cached;

            float result = Measure(mesh);
            Cache[key] = result;
            return result;
        }

        /// <summary>Forgets what was measured, for when a mesh is reimported under it.</summary>
        public static void Invalidate() => Cache.Clear();

        private static float Measure(Mesh mesh)
        {
            Vector2[] uv = mesh.uv;
            int[] triangles = mesh.triangles;

            if (uv == null || uv.Length != mesh.vertexCount || triangles == null || triangles.Length < 3)
                return 0f;

            const int res = MeasureResolution;

            // Which triangle claimed each texel: 0 for none, id+1 for one, -1 once a second
            // one has arrived. Storing the claimant rather than a count is what keeps a
            // triangle from reporting itself as an overlap when it covers a texel twice
            // through the two halves of a quad.
            var owner = new int[res * res];
            int covered = 0;

            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                Vector2 a = uv[triangles[t]] * res;
                Vector2 b = uv[triangles[t + 1]] * res;
                Vector2 c = uv[triangles[t + 2]] * res;

                int minX = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))));
                int maxX = Mathf.Min(res - 1, Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))));
                int minY = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y))));
                int maxY = Mathf.Min(res - 1, Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))));

                float d = (b.y - c.y) * (a.x - c.x) + (c.x - b.x) * (a.y - c.y);
                if (Mathf.Abs(d) < 1e-9f)
                    continue;   // degenerate in UV - carries no claim on any texel

                int id = t / 3 + 1;

                for (int y = minY; y <= maxY; y++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        // Texel CENTRES, which is the rule the rasteriser fills by. Testing
                        // the corners instead would report an overlap for every pair of
                        // islands that merely touch.
                        float px = x + 0.5f, py = y + 0.5f;
                        float w0 = ((b.y - c.y) * (px - c.x) + (c.x - b.x) * (py - c.y)) / d;
                        float w1 = ((c.y - a.y) * (px - c.x) + (a.x - c.x) * (py - c.y)) / d;
                        if (w0 < 0f || w1 < 0f || 1f - w0 - w1 < 0f)
                            continue;

                        int index = y * res + x;
                        if (owner[index] == 0)
                        {
                            owner[index] = id;
                            covered++;
                        }
                        else if (owner[index] != id)
                        {
                            owner[index] = -1;
                        }
                    }
                }
            }

            if (covered == 0)
                return 0f;

            int shared = 0;
            for (int i = 0; i < owner.Length; i++)
                if (owner[i] == -1)
                    shared++;

            return (float)shared / covered;
        }
    }
}
