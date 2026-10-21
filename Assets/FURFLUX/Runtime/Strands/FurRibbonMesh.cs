using UnityEngine;
using UnityEngine.Rendering;

namespace FurRendering
{
    /// <summary>
    /// The batch mesh strand fur is drawn from.
    ///
    /// It contains no fur. It is a grid of blank ribbons - a fixed number of quad strips,
    /// one per strand - and every vertex of it carries nothing but its
    /// own address: which side of the ribbon it is on, how far up it is, and which root
    /// inside the batch it belongs to. Everything else, position included, is resolved in
    /// the vertex shader from the root buffer (see FurGeometry.hlsl).
    ///
    /// That addressing rides in POSITION rather than in extra vertex streams:
    ///   x = side of the ribbon, -1 or +1
    ///   y = height along it, 0 at the root and 1 at the tip
    ///   z = index of the root within this batch
    ///
    /// Packing it this way rather than into UV2/UV3 keeps the vertex layout identical to
    /// the one the shell path already declares, so all five passes read one Attributes
    /// struct and no pass needs a layout of its own.
    ///
    /// One mesh serves every furred object using the same segment count, so it is built
    /// once and shared - see the cache at the bottom.
    /// </summary>
    internal static class FurRibbonMesh
    {
        /// <summary>Most segments a ribbon may be split into along its length.</summary>
        public const int MaxSegments = 12;

        private static readonly System.Collections.Generic.Dictionary<int, Mesh> Cache =
            new System.Collections.Generic.Dictionary<int, Mesh>();

        /// <summary>
        /// A batch mesh for the given ribbon shape, built once and shared afterwards.
        /// </summary>
        public static Mesh Get(int segments)
        {
            segments = Mathf.Clamp(segments, 1, MaxSegments);

            int key = segments;
            if (Cache.TryGetValue(key, out Mesh cached) && cached != null)
                return cached;

            // The cache is static, so a domain reload empties it - but HideAndDontSave
            // objects SURVIVE that reload. Rebuilding blindly would therefore leak one
            // batch mesh per shape per recompile, and an editor session recompiles a lot.
            // Adopting the surviving one instead costs a single scan the first time each
            // shape is asked for.
            Mesh mesh = Adopt(NameFor(segments)) ?? Build(segments);
            Cache[key] = mesh;
            return mesh;
        }

        private static string NameFor(int segments)
        {
            // The version suffix is not decoration. Adopt() re-uses a batch mesh that
            // survived a domain reload by NAME, so without it a recompile that changes
            // this mesh's layout or winding silently keeps serving the old geometry -
            // and the change appears to have done nothing until the editor is restarted.
            // Bump it whenever Build changes what it produces.
            return $"FurRibbonBatch_v2_{segments}seg";
        }

        /// <summary>An identical batch mesh left behind by a previous domain reload, if any.</summary>
        private static Mesh Adopt(string name)
        {
            foreach (Mesh candidate in Resources.FindObjectsOfTypeAll<Mesh>())
            {
                if (candidate != null && candidate.name == name)
                    return candidate;
            }

            return null;
        }

        private static Mesh Build(int segments)
        {
            const int planes = 1;
            int roots = FurStrandDistribution.RootsPerBatch;
            int rings = segments + 1;

            int vertexCount = roots * planes * rings * 2;
            int indexCount = roots * planes * segments * 6;

            var positions = new Vector3[vertexCount];
            var normals = new Vector3[vertexCount];
            var tangents = new Vector4[vertexCount];
            var uv = new Vector2[vertexCount];
            var indices = new int[indexCount];

            int v = 0;
            int i = 0;

            for (int root = 0; root < roots; root++)
            {
                for (int plane = 0; plane < planes; plane++)
                {
                    int planeStart = v;

                    for (int ring = 0; ring < rings; ring++)
                    {
                        float t = (float)ring / segments;

                        for (int side = 0; side < 2; side++)
                        {
                            float x = side == 0 ? -1f : 1f;

                            positions[v] = new Vector3(x, t, root);

                            // A placeholder: the shading normal is built in the vertex
                            // shader from the resolved strand frame, which this mesh
                            // knows nothing about.
                            normals[v] = new Vector3(plane, 0f, 1f);
                            tangents[v] = new Vector4(1f, 0f, 0f, 1f);
                            uv[v] = new Vector2(side, t);
                            v++;
                        }
                    }

                    for (int segment = 0; segment < segments; segment++)
                    {
                        int a = planeStart + segment * 2;

                        // The ribbon's two edges out of `a` are `a + 2` (up the strand)
                        // and `a + 1` (across it), and the shader spans its plane as
                        // strandDir x sideDir.
                        //
                        // Which way that plane ends up facing is no longer a question this
                        // winding can answer. sideDir used to be cross(strandDir, viewDir),
                        // so the quad was built facing the viewer and one winding was
                        // simply correct; the ribbon now keeps the fixed, randomly twisted
                        // plane the strand jitter gave it (see FUR_STRAND_RIBBON_BILLBOARD),
                        // and roughly half the hairs in any coat therefore face away from
                        // any given camera.
                        //
                        // So the coat REQUIRES two-sided rendering. _Cull defaults to Off
                        // and the material inspector warns when it is not; with culling on,
                        // half the hairs vanish and the body reads as bald with a few furry
                        // patches - which is what this comment used to describe as the
                        // consequence of the wrong winding, and is now the consequence of
                        // the wrong cull mode.
                        indices[i++] = a;
                        indices[i++] = a + 1;
                        indices[i++] = a + 2;

                        indices[i++] = a + 1;
                        indices[i++] = a + 3;
                        indices[i++] = a + 2;
                    }
                }
            }

            var mesh = new Mesh
            {
                name = NameFor(segments),
                hideFlags = HideFlags.HideAndDontSave,

                // The root index reaches 255, so the batch mesh outgrows a 16-bit index
                // buffer as soon as the segment count rises. Asking for 32-bit outright
                // avoids a silently truncated mesh at the higher quality settings.
                indexFormat = IndexFormat.UInt32,
            };

            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetTangents(tangents);
            mesh.SetUVs(0, uv);
            mesh.SetTriangles(indices, 0, calculateBounds: false);

            // The vertex positions are addresses, not places, so their own extent means
            // nothing. Every draw supplies real world bounds through RenderParams; this
            // is only here so nothing downstream reads a zero-sized box.
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one);
            mesh.UploadMeshData(markNoLongerReadable: false);

            return mesh;
        }
    }
}
