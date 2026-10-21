using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace FurRendering
{
    /// <summary>
    /// One strand root planted on the source mesh.
    ///
    /// Laid out as six 16-byte members rather than as the natural
    /// float3/float3/float4/float2 sequence. Structured buffers are tightly packed on
    /// both sides, so the natural layout WOULD line up - but it lines up by arithmetic
    /// somebody has to redo in their head every time either side is touched, and a
    /// mismatch surfaces as fur planted in a scatter of wrong places rather than as an
    /// error. Every member starting on a 16-byte boundary removes the question.
    ///
    /// MUST match struct FurRootData in FurShellInput.hlsl.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FurRootData
    {
        public Vector4 PositionAndSeedX;    // xyz: root position in mesh object space, w: random 0..1
        public Vector4 NormalAndSeedY;      // xyz: surface normal, w: random 0..1
        public Vector4 TangentOS;           // xyz: surface tangent, w: handedness sign
        // xy: UV0 at the root - the strand grid and _BaseMap live here.
        // zw: the DETAIL UV at the root (see _FurDetailUV). Falls back to UV0 on a mesh
        //     with no second set, so the shader never has to ask whether one was planted.
        public Vector4 UvAndDetailUV;
        public uint BoneIndex0, BoneIndex1, BoneIndex2, BoneIndex3;
        public Vector4 BoneWeight;

        public const int Stride = 96;
    }

    /// <summary>
    /// One bone's skinning transform on its way to the GPU, as three explicit rows.
    ///
    /// Rows rather than a Matrix4x4 because a matrix in a structured buffer is read back
    /// under whichever packing the shader compiler defaults to, while Matrix4x4 always
    /// stores column-major - so the two agree or disagree on a convention neither side
    /// states. When they disagree the transform arrives transposed, which does not error:
    /// it scatters every hair to a plausible-looking wrong place.
    ///
    /// The fourth row of an affine transform is always (0,0,0,1) and is not sent.
    ///
    /// MUST match struct FurSkinMatrix in FurShellInput.hlsl.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FurSkinMatrix
    {
        public Vector4 Row0;
        public Vector4 Row1;
        public Vector4 Row2;

        public const int Stride = 48;
    }

    /// <summary>
    /// Plants strand roots over a mesh and keeps them on the GPU.
    ///
    /// Why roots rather than a per-frame scatter
    /// -----------------------------------------
    /// The two ribbon fur types need a fixed, repeatable set of hair positions: fur that
    /// re-randomises itself every frame boils. So placement happens ONCE, on the CPU,
    /// against the source mesh in its bind pose, and the result lives in a structured
    /// buffer for as long as the mesh and the density hold still.
    ///
    /// Animation is then applied to the roots on the GPU, from their own skin weights -
    /// see FurGeometry.hlsl. That is the whole reason this class stores bone influences:
    /// the CPU cost of a hundred thousand hairs following a skeleton becomes the cost of
    /// the fifty or so bones it has, where re-sampling the surface every frame would
    /// dominate the frame outright.
    ///
    /// Placement is AREA-WEIGHTED, not per-vertex and not per-triangle. Meshes are not
    /// uniformly tessellated - a face carries ten times the triangle density of a flank -
    /// and either of the cheaper distributions would put ten times the hair there, which
    /// reads as a bald body with a furry head. Sampling proportional to triangle area
    /// gives a coat of even density whatever the topology underneath is doing.
    ///
    /// Blend shapes are handled separately, and cheaply - see ApplyBlendShapes. Unity
    /// deforms a character as bind pose, then morph, then skinning; the GPU path above is
    /// the third step, so the roots used to miss the second one entirely and a face rig
    /// moved the skin without moving the fur on it. The missing step is now done on the
    /// CPU, at the roots rather than at the vertices, which is a few thousand additions
    /// rather than a hundred thousand.
    /// </summary>
    internal sealed class FurStrandDistribution : IDisposable
    {
        /// <summary>
        /// Roots carried by one instance of the ribbon batch mesh.
        ///
        /// The alternative - one instance per hair - would need a per-instance transform
        /// matrix per hair, and Graphics.RenderMeshInstanced takes those as a managed
        /// array: at a hundred thousand hairs that is a 6 MB array rebuilt every frame to
        /// hold a hundred thousand copies of the same matrix. Batching turns that into a
        /// few hundred entries and costs only the partly-used final batch, whose spare
        /// slots collapse to a point in the vertex shader.
        /// </summary>
        public const int RootsPerBatch = 256;

        // Surface area is needed BEFORE a build, to work out how many roots the density
        // asks for - and it is the same answer for every object sharing a mesh. Cached
        // here rather than recomputed per object, because summing a few hundred thousand
        // triangle areas is not something to do once per squirrel.
        // Keyed by instance ID rather than by the Mesh itself: a Mesh key would be a
        // strong reference, so this cache would keep every mesh it has ever seen alive
        // for the lifetime of the domain.
        private static readonly System.Collections.Generic.Dictionary<int, float> AreaCache =
            new System.Collections.Generic.Dictionary<int, float>();

        private static readonly System.Collections.Generic.Dictionary<int, Vector2> UvScaleCache =
            new System.Collections.Generic.Dictionary<int, Vector2>();

        private GraphicsBuffer _rootBuffer;

        // --- Blend shape support ---
        //
        // Only allocated when the mesh actually has shapes, so a coat on a mesh with none
        // pays nothing at all - not a branch, not an array.
        //
        // _bindRoots is the buffer's contents in the BIND POSE, kept because a morph is an
        // offset from it: rebuilding from the deformed state each frame would let rounding
        // accumulate, and a face would drift away from its own skull over a long take.
        private FurRootData[] _bindRoots;
        private FurRootData[] _workRoots;

        // Which triangle each root sits on and where in it. The morph delta is interpolated
        // across the same three corners the root's position was, so the hair moves with the
        // exact piece of surface it grows from.
        private int[] _rootCorners;      // three vertex indices per root
        private Vector3[] _rootBary;     // the barycentric weights that go with them

        private MorphEntry[][] _morphByShape;
        private int[] _morphedRoots;
        private float[] _shapeWeights;
        private float[] _shapeFrameWeights;
        private bool _morphApplied;

        /// <summary>One root a single blend shape actually moves, and by how much at full weight.</summary>
        private struct MorphEntry
        {
            public int Root;
            public Vector3 Position;
            public Vector3 Normal;
        }

        /// <summary>True when this mesh has blend shapes and the roots are bound to them.</summary>
        public bool HasBlendShapes => _morphByShape != null;

        /// <summary>How many roots any blend shape moves - the cost of a morph update.</summary>
        public int MorphedRootCount => _morphedRoots != null ? _morphedRoots.Length : 0;

        // What the buffer currently holds, so a frame that changes nothing rebuilds nothing.
        private Mesh _sourceMesh;
        private int _bakedCount;
        private int _bakedSeed;

        public GraphicsBuffer RootBuffer => _rootBuffer;

        /// <summary>Roots actually in the buffer. The draw may use fewer - that is the LOD.</summary>
        public int RootCount => _bakedCount;

        /// <summary>True when the roots carry usable bone influences.</summary>
        public bool IsSkinned { get; private set; }

        /// <summary>Surface area of the mesh it was built from, in object-space square meters.</summary>
        public float SurfaceArea { get; private set; }

        /// <summary>
        /// Why a rebuild is impossible, or null when it is not. Reported rather than
        /// silently falling back: a mesh with Read/Write off produces no fur at all, and
        /// there is nothing in the scene view to explain that on its own.
        /// </summary>
        public static string DescribeUnusable(Mesh mesh)
        {
            if (mesh == null)
                return "no mesh to plant fur on.";

            if (!mesh.isReadable)
                return $"'{mesh.name}' has Read/Write disabled. Strand fur samples the " +
                       "mesh on the CPU to place hair roots, so the mesh data has to be readable - " +
                       "turn on Read/Write in its import settings, or use Shells fur, which does not " +
                       "need it.";

            // Counted through GetIndexCount rather than mesh.triangles: this runs per
            // object per camera per frame, and mesh.triangles allocates a copy of the
            // whole index buffer every time it is read.
            uint indices = 0;
            for (int i = 0; i < mesh.subMeshCount; i++)
                indices += mesh.GetIndexCount(i);

            if (mesh.vertexCount < 3 || indices < 3)
                return $"'{mesh.name}' has no triangles to plant fur on.";

            return null;
        }

        /// <summary>
        /// Rebuilds the root set if anything it depends on has changed, and reports
        /// whether a usable buffer is now in place.
        /// </summary>
        public bool EnsureBuilt(Mesh mesh, int count, int seed)
        {
            count = Mathf.Max(count, 1);

            if (_rootBuffer != null && _rootBuffer.IsValid() &&
                _sourceMesh == mesh && _bakedCount == count && _bakedSeed == seed)
                return true;

            if (DescribeUnusable(mesh) != null)
                return false;

            Build(mesh, count, seed);
            return _rootBuffer != null;
        }

        private void Build(Mesh mesh, int count, int seed)
        {
            Vector3[] vertices = mesh.vertices;
            Vector3[] normals = mesh.normals;
            Vector4[] tangents = mesh.tangents;
            Vector2[] uv = mesh.uv;
            // The lightmap set. Planted alongside UV0 rather than instead of it: the strand
            // grid stays in UV0 (overlapping islands there merely repeat a noise pattern,
            // which nobody can see) while the per-object MAPS need an unwrap that gives every
            // piece of surface a texel of its own. See _FurDetailUV.
            Vector2[] uv2 = mesh.uv2;
            BoneWeight[] boneWeights = mesh.boneWeights;
            int[] triangles = mesh.triangles;

            bool hasNormals = normals != null && normals.Length == vertices.Length;
            bool hasTangents = tangents != null && tangents.Length == vertices.Length;
            bool hasUv = uv != null && uv.Length == vertices.Length;
            bool hasUv2 = uv2 != null && uv2.Length == vertices.Length;
            Matrix4x4[] bindposes = mesh.bindposes;
            bool hasSkin = boneWeights != null && boneWeights.Length == vertices.Length
                           && bindposes != null && bindposes.Length > 0;

            // The controller uploads min(bones, bindposes) matrices, so an index past that
            // reads off the end of the structured buffer. D3D returns zero there, which is
            // a zero MATRIX - the hair collapses to the world origin and streaks across the
            // screen. Clamping here costs nothing and cannot be got wrong per frame.
            int highestBone = hasSkin ? bindposes.Length - 1 : 0;

            IsSkinned = hasSkin;

            int triangleCount = triangles.Length / 3;

            // Cumulative triangle area, so a root can be placed by one binary search
            // against a single uniform random number. Rebuilding this table is the
            // expensive half of the bake, which is exactly why the bake is cached.
            var cumulativeArea = new float[triangleCount];
            float total = 0f;

            for (int t = 0; t < triangleCount; t++)
            {
                Vector3 a = vertices[triangles[t * 3 + 0]];
                Vector3 b = vertices[triangles[t * 3 + 1]];
                Vector3 c = vertices[triangles[t * 3 + 2]];

                total += Vector3.Cross(b - a, c - a).magnitude * 0.5f;
                cumulativeArea[t] = total;
            }

            SurfaceArea = total;

            var roots = new FurRootData[count];

            // The surface binding, kept only when there is something to morph. A root's
            // triangle and barycentric weights are known here and nowhere else afterwards,
            // so they are recorded on the way past rather than re-derived later.
            bool wantMorph = mesh.blendShapeCount > 0;
            int[] corners = wantMorph ? new int[count * 3] : null;
            Vector3[] bary = wantMorph ? new Vector3[count] : null;

            // Seeded explicitly and restored afterwards: fur placement has to be identical
            // every time the same coat is built, and it must not consume draws from
            // whatever else in the project is using the global random stream.
            UnityEngine.Random.State previousState = UnityEngine.Random.state;
            UnityEngine.Random.InitState(seed);

            for (int i = 0; i < count; i++)
            {
                int t = total > 0f
                    ? FindTriangle(cumulativeArea, UnityEngine.Random.value * total)
                    : UnityEngine.Random.Range(0, triangleCount);

                int i0 = triangles[t * 3 + 0];
                int i1 = triangles[t * 3 + 1];
                int i2 = triangles[t * 3 + 2];

                // Uniform barycentric sampling. The fold is what makes it uniform: two
                // independent uniforms land in a square, and reflecting the half that
                // falls outside the triangle back into it keeps the density even. Without
                // it roots crowd one corner of every triangle.
                float u = UnityEngine.Random.value;
                float v = UnityEngine.Random.value;
                if (u + v > 1f)
                {
                    u = 1f - u;
                    v = 1f - v;
                }
                float w = 1f - u - v;

                if (wantMorph)
                {
                    corners[i * 3 + 0] = i0;
                    corners[i * 3 + 1] = i1;
                    corners[i * 3 + 2] = i2;
                    bary[i] = new Vector3(w, u, v);
                }

                Vector3 position = vertices[i0] * w + vertices[i1] * u + vertices[i2] * v;

                Vector3 normal = hasNormals
                    ? (normals[i0] * w + normals[i1] * u + normals[i2] * v).normalized
                    : Vector3.Cross(vertices[i1] - vertices[i0], vertices[i2] - vertices[i0]).normalized;

                Vector4 tangent = hasTangents
                    ? tangents[i0] * w + tangents[i1] * u + tangents[i2] * v
                    : new Vector4(1f, 0f, 0f, 1f);

                // Re-orthogonalised against the interpolated normal. An interpolated
                // tangent is no longer perpendicular to it, and the ribbon plane is built
                // from their cross product - a tangent leaning into the normal makes that
                // plane collapse.
                var tangentDir = new Vector3(tangent.x, tangent.y, tangent.z);
                tangentDir -= normal * Vector3.Dot(normal, tangentDir);
                tangentDir = tangentDir.sqrMagnitude > 1e-10f
                    ? tangentDir.normalized
                    : Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.9f ? Vector3.up : Vector3.right).normalized;

                Vector2 texcoord = hasUv
                    ? uv[i0] * w + uv[i1] * u + uv[i2] * v
                    : Vector2.zero;

                // Falling back to UV0 rather than to zero: a coat told to use the detail set
                // on a mesh that has none should render as it always did, not collapse every
                // hair onto one texel.
                Vector2 detailUV = hasUv2
                    ? uv2[i0] * w + uv2[i1] * u + uv2[i2] * v
                    : texcoord;

                roots[i].PositionAndSeedX = new Vector4(position.x, position.y, position.z, UnityEngine.Random.value);
                roots[i].NormalAndSeedY = new Vector4(normal.x, normal.y, normal.z, UnityEngine.Random.value);
                roots[i].TangentOS = new Vector4(tangentDir.x, tangentDir.y, tangentDir.z,
                    tangent.w < 0f ? -1f : 1f);
                roots[i].UvAndDetailUV = new Vector4(texcoord.x, texcoord.y, detailUV.x, detailUV.y);

                if (hasSkin)
                {
                    // The influences of the NEAREST vertex, not a blend of the three.
                    // Bone indices are labels, not quantities - averaging index 4 and
                    // index 9 does not give a bone halfway between them - so the only
                    // meaningful interpolation is to pick one vertex and take its set
                    // whole. The corner with the largest barycentric weight is the
                    // nearest one, which keeps the choice continuous across the triangle.
                    int nearest = w >= u ? (w >= v ? i0 : i2) : (u >= v ? i1 : i2);
                    BoneWeight bw = boneWeights[nearest];

                    roots[i].BoneIndex0 = (uint)Mathf.Clamp(bw.boneIndex0, 0, highestBone);
                    roots[i].BoneIndex1 = (uint)Mathf.Clamp(bw.boneIndex1, 0, highestBone);
                    roots[i].BoneIndex2 = (uint)Mathf.Clamp(bw.boneIndex2, 0, highestBone);
                    roots[i].BoneIndex3 = (uint)Mathf.Clamp(bw.boneIndex3, 0, highestBone);
                    roots[i].BoneWeight = new Vector4(bw.weight0, bw.weight1, bw.weight2, bw.weight3);
                }
                else
                {
                    roots[i].BoneWeight = Vector4.zero;
                }
            }

            UnityEngine.Random.state = previousState;

            ReleaseBuffer();
            _rootBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, FurRootData.Stride);
            _rootBuffer.SetData(roots);

            _rootCorners = corners;
            _rootBary = bary;
            BuildMorphBindings(mesh, roots, count);

            _sourceMesh = mesh;
            _bakedCount = count;
            _bakedSeed = seed;
        }

        // ------------------------------------------------------------------ //
        //  Blend shapes
        // ------------------------------------------------------------------ //
        /// <summary>
        /// Works out, once, which roots each blend shape moves and by how much.
        ///
        /// Sparse on purpose, and that sparsity is the whole reason this is cheap enough
        /// to leave switched on. A blend shape is a facial expression: it moves a few
        /// hundred vertices out of a hundred thousand, so the roots it touches are a small
        /// fraction of the coat. A delta per root per shape would be dense and enormous -
        /// sixty thousand roots against fifty shapes is three million entries for a face
        /// that moves a twentieth of them. Keeping only the pairs that are actually
        /// non-zero costs what the rig actually does.
        ///
        /// The delta is read at the shape's LAST frame and divided by that frame's weight,
        /// so a weight of 100 reproduces it exactly. A rig with in-between frames is
        /// therefore approximated by a straight line through the final one - exact for the
        /// single-frame shapes almost every rig uses, and a smooth approximation rather
        /// than a wrong answer for the rest.
        /// </summary>
        private void BuildMorphBindings(Mesh mesh, FurRootData[] roots, int count)
        {
            _morphByShape = null;
            _morphedRoots = null;
            _bindRoots = null;
            _workRoots = null;
            _shapeWeights = null;
            _shapeFrameWeights = null;
            _morphApplied = false;

            int shapes = mesh.blendShapeCount;
            if (shapes == 0 || _rootCorners == null)
                return;

            var deltaPositions = new Vector3[mesh.vertexCount];
            var deltaNormals = new Vector3[mesh.vertexCount];
            var deltaTangents = new Vector3[mesh.vertexCount];

            _morphByShape = new MorphEntry[shapes][];
            _shapeFrameWeights = new float[shapes];
            _shapeWeights = new float[shapes];

            var touched = new System.Collections.Generic.HashSet<int>();
            var entries = new System.Collections.Generic.List<MorphEntry>();

            // Below this a delta is not a movement, it is the exporter's floating point
            // noise - and keeping it would make a shape that moves nothing still cost a
            // pass over its roots every frame it is held.
            const float MinDelta = 1e-6f;

            for (int shape = 0; shape < shapes; shape++)
            {
                int frames = mesh.GetBlendShapeFrameCount(shape);
                if (frames <= 0)
                {
                    _morphByShape[shape] = new MorphEntry[0];
                    continue;
                }

                int lastFrame = frames - 1;
                _shapeFrameWeights[shape] = Mathf.Max(
                    mesh.GetBlendShapeFrameWeight(shape, lastFrame), 1e-4f);

                mesh.GetBlendShapeFrameVertices(shape, lastFrame,
                    deltaPositions, deltaNormals, deltaTangents);

                entries.Clear();

                for (int i = 0; i < count; i++)
                {
                    int i0 = _rootCorners[i * 3 + 0];
                    int i1 = _rootCorners[i * 3 + 1];
                    int i2 = _rootCorners[i * 3 + 2];
                    Vector3 b = _rootBary[i];

                    Vector3 dp = deltaPositions[i0] * b.x + deltaPositions[i1] * b.y + deltaPositions[i2] * b.z;
                    Vector3 dn = deltaNormals[i0] * b.x + deltaNormals[i1] * b.y + deltaNormals[i2] * b.z;

                    if (dp.sqrMagnitude < MinDelta && dn.sqrMagnitude < MinDelta)
                        continue;

                    entries.Add(new MorphEntry { Root = i, Position = dp, Normal = dn });
                    touched.Add(i);
                }

                _morphByShape[shape] = entries.ToArray();
            }

            if (touched.Count == 0)
            {
                // Shapes exist but not one of them reaches a root - a rig whose morphs are
                // all on unfurred geometry. Nothing to do, ever, so the whole apparatus is
                // dropped rather than carried.
                _morphByShape = null;
                _shapeFrameWeights = null;
                _shapeWeights = null;
                return;
            }

            _morphedRoots = new int[touched.Count];
            touched.CopyTo(_morphedRoots);
            System.Array.Sort(_morphedRoots);

            _bindRoots = roots;
            _workRoots = (FurRootData[])roots.Clone();
        }

        /// <summary>
        /// Moves the roots the live blend shape weights say should move, and reports
        /// whether the buffer was rewritten.
        ///
        /// This is the step Unity takes between the bind pose and the skinning, done here
        /// at the roots instead of at the vertices. The GPU still applies the skeleton
        /// afterwards, exactly as before, so the two halves compose the way they do for the
        /// mesh itself.
        ///
        /// Skipped outright when no weight has moved since the last call, which is most
        /// frames on most characters.
        /// </summary>
        public bool ApplyBlendShapes(SkinnedMeshRenderer renderer)
        {
            if (_morphByShape == null || _workRoots == null || renderer == null
                || _rootBuffer == null || !_rootBuffer.IsValid())
                return false;

            Mesh shared = renderer.sharedMesh;
            int shapes = shared == null
                ? 0
                : Mathf.Min(_morphByShape.Length, shared.blendShapeCount);

            bool changed = !_morphApplied;
            for (int shape = 0; shape < shapes; shape++)
            {
                float weight = renderer.GetBlendShapeWeight(shape);
                if (!Mathf.Approximately(weight, _shapeWeights[shape]))
                {
                    _shapeWeights[shape] = weight;
                    changed = true;
                }
            }

            if (!changed)
                return false;

            // Back to the bind pose first, and only for the roots a shape can reach. A
            // morph is an absolute pose rather than an increment, so accumulating onto
            // last frame's result would compound it every frame it was held.
            for (int r = 0; r < _morphedRoots.Length; r++)
                _workRoots[_morphedRoots[r]] = _bindRoots[_morphedRoots[r]];

            for (int shape = 0; shape < shapes; shape++)
            {
                float weight = _shapeWeights[shape] / _shapeFrameWeights[shape];
                if (Mathf.Abs(weight) < 1e-5f)
                    continue;

                MorphEntry[] shapeEntries = _morphByShape[shape];
                for (int e = 0; e < shapeEntries.Length; e++)
                {
                    MorphEntry entry = shapeEntries[e];

                    // The w channels carry this root's random seeds, not geometry, so the
                    // delta goes in component-wise rather than as a Vector4.
                    Vector4 position = _workRoots[entry.Root].PositionAndSeedX;
                    position.x += entry.Position.x * weight;
                    position.y += entry.Position.y * weight;
                    position.z += entry.Position.z * weight;
                    _workRoots[entry.Root].PositionAndSeedX = position;

                    Vector4 normal = _workRoots[entry.Root].NormalAndSeedY;
                    normal.x += entry.Normal.x * weight;
                    normal.y += entry.Normal.y * weight;
                    normal.z += entry.Normal.z * weight;
                    _workRoots[entry.Root].NormalAndSeedY = normal;
                }
            }

            _rootBuffer.SetData(_workRoots);
            _morphApplied = true;
            return true;
        }

        /// <summary>First triangle whose cumulative area reaches <paramref name="target"/>.</summary>
        private static int FindTriangle(float[] cumulativeArea, float target)
        {
            int low = 0;
            int high = cumulativeArea.Length - 1;

            while (low < high)
            {
                int mid = (low + high) >> 1;
                if (cumulativeArea[mid] < target)
                    low = mid + 1;
                else
                    high = mid;
            }

            return low;
        }

        /// <summary>
        /// Meters of surface per UV unit, averaged over the mesh and weighted by area.
        ///
        /// The shader measures this per fragment, exactly and locally, because that is
        /// what makes a strand the same size in a stretched UV island as in a packed one.
        /// What it needs from here is only a REFERENCE to clamp that measurement against:
        /// screen-space derivatives break down at UV seams and at silhouettes, and an
        /// unbounded spike there would ring the seam with wrongly sized strands.
        ///
        /// Object space, like SurfaceAreaOf - the caller scales it into world units.
        /// Cached per mesh by instance id, so a mesh shared by fifty objects is measured
        /// once and dropping a mesh does not keep it alive.
        /// </summary>
        public static Vector2 MetersPerUvOf(Mesh mesh)
        {
            // ZERO means "cannot be measured", not "one meter". Shell fur is allowed to run
            // on a mesh with Read/Write off - not needing it is one of the reasons to pick
            // that type - so the caller has to be able to tell an unmeasurable mesh from a
            // mesh that genuinely maps one UV unit to one meter, and fall back to something
            // of the right order rather than to a number that happens to be plausible.
            if (mesh == null || !mesh.isReadable)
                return Vector2.zero;

            int key = mesh.GetInstanceID();
            if (UvScaleCache.TryGetValue(key, out Vector2 cached))
                return cached;

            Vector3[] vertices = mesh.vertices;
            Vector2[] uv = mesh.uv;
            int[] triangles = mesh.triangles;

            var result = new Vector2(1f, 1f);

            if (uv != null && uv.Length == vertices.Length)
            {
                double worldArea = 0d;
                double uvArea = 0d;
                var perTriangle = new System.Collections.Generic.List<float>(triangles.Length / 3);

                for (int t = 0; t + 2 < triangles.Length; t += 3)
                {
                    int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];

                    float w = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).magnitude;
                    Vector2 e1 = uv[b] - uv[a];
                    Vector2 e2 = uv[c] - uv[a];
                    float u = Mathf.Abs(e1.x * e2.y - e1.y * e2.x);

                    // Degenerate triangles carry no information about the mapping and would
                    // drag the average toward whichever of the two areas collapsed first.
                    if (w < 1e-12f || u < 1e-12f)
                        continue;

                    worldArea += w;
                    uvArea += u;
                    perTriangle.Add(Mathf.Sqrt(w / u));
                }

                if (uvArea > 1e-12 && perTriangle.Count > 0)
                {
                    perTriangle.Sort();

                    // The CEILING is a high percentile of what the mesh actually contains,
                    // not a multiple of its average.
                    //
                    // Real unwraps are not uniform, and the places they are worst are the
                    // places a character has the most geometry: fingers, eyelids, the corners
                    // of a mouth. On a production model those triangles run to a hundred
                    // times the average, and the whole point of the clamp is to tell that
                    // apart from a screen-space derivative spiking at a seam - so the bound
                    // has to be the mesh's own range. A fixed multiple of the average puts
                    // it far below what the mesh legitimately holds, and the correction then
                    // stops short exactly where it was needed most.
                    int index = Mathf.Clamp(Mathf.RoundToInt(0.999f * (perTriangle.Count - 1)),
                                            0, perTriangle.Count - 1);

                    result = new Vector2(Mathf.Sqrt((float)(worldArea / uvArea)),
                                         Mathf.Max(perTriangle[index], 1e-4f));
                }
            }

            UvScaleCache[key] = result;
            return result;
        }

        /// <summary>
        /// How much a transform multiplies SURFACE AREA.
        ///
        /// Not the scale, and not its cube: area is two-dimensional, so under a uniform
        /// scale s it grows by s squared. The expression below is that for a uniform
        /// scale and exact for an axis-aligned box under a non-uniform one, which is as
        /// close as a single number gets - the true factor for a non-uniform scale
        /// depends on which way each individual triangle faces.
        ///
        /// This matters more than it sounds. Model scales of 100 or more are ordinary
        /// (an asset authored in centimeters, an FBX imported at the wrong unit), and at
        /// a scale of 150 the area is out by a factor of twenty-two thousand. A coat
        /// asking for forty thousand hairs per square meter then gets FIVE, and the fur
        /// simply does not appear.
        /// </summary>
        public static float AreaScale(Vector3 lossyScale)
        {
            Vector3 s = new Vector3(Mathf.Abs(lossyScale.x), Mathf.Abs(lossyScale.y), Mathf.Abs(lossyScale.z));
            return Mathf.Max((s.x * s.y + s.y * s.z + s.z * s.x) / 3f, 1e-8f);
        }

        /// <summary>
        /// Number of roots a given density asks for over this surface, bounded by what
        /// the caller is willing to draw.
        ///
        /// <paramref name="area"/> is in WORLD square meters, because that is the space
        /// the coat is seen in - and the space _FurLength and _StrandWidth are already
        /// measured in. A density in object-space units would mean something different
        /// for every model depending on what scale it happened to be imported at.
        /// </summary>
        public static int ResolveRootCount(float area, float perSquareMeter, int maximum)
        {
            // A density of zero means NO FUR, and it has to, because that is the only
            // thing it can mean to somebody who typed it. This used to return the ceiling
            // instead - on the reasoning that an unset density should fall back to the
            // budget - which meant the one value an artist reaches for to turn the coat
            // off gave them the densest coat the object could afford.
            if (area <= 0f || perSquareMeter <= 0f)
                return 0;

            maximum = Mathf.Max(maximum, 1);

            // Rounded up to a whole batch. The count feeds the bake, and the bake rebuilds
            // whenever the count changes - so without this an object whose scale is
            // animated, even slightly, would re-plant its entire coat every frame. A batch
            // is the granularity the draw works in anyway, so nothing is lost by snapping
            // to it.
            int wanted = Mathf.CeilToInt(area * perSquareMeter / RootsPerBatch) * RootsPerBatch;
            return Mathf.Clamp(wanted, 1, maximum);
        }

        private void ReleaseBuffer()
        {
            _rootBuffer?.Release();
            _rootBuffer = null;
        }

        public void Dispose()
        {
            ReleaseBuffer();
            _sourceMesh = null;
            _bakedCount = 0;
        }

        /// <summary>
        /// Object-space surface area of a mesh, in square meters. Cached per mesh.
        ///
        /// Object space because that is what a mesh HAS - it knows nothing about the
        /// transforms it is drawn under, and one mesh is usually drawn under several.
        /// Callers scale it into world units with <see cref="AreaScale"/>, which is where
        /// the density is actually applied.
        /// </summary>
        public static float SurfaceAreaOf(Mesh mesh)
        {
            if (mesh == null || !mesh.isReadable)
                return 0f;

            int key = mesh.GetInstanceID();
            if (AreaCache.TryGetValue(key, out float cached))
                return cached;

            Vector3[] vertices = mesh.vertices;
            int[] triangles = mesh.triangles;

            float total = 0f;
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                Vector3 a = vertices[triangles[t + 0]];
                Vector3 b = vertices[triangles[t + 1]];
                Vector3 c = vertices[triangles[t + 2]];
                total += Vector3.Cross(b - a, c - a).magnitude * 0.5f;
            }

            AreaCache[key] = total;
            return total;
        }
    }
}
