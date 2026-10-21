using System.IO;
using UnityEditor;
using UnityEngine;

namespace FurRendering.EditorTools
{
    /// <summary>
    /// Scene-view brush for the four fur detail maps (length, thickness, colour, comb).
    ///
    /// Opened from the Draw Fur button on <see cref="FurController"/>'s inspector. While
    /// it is open it owns the scene view's left mouse button, so it is a WINDOW rather
    /// than an inspector section: closing it is how you stop painting, and that has to
    /// be one obvious action rather than a toggle buried in a component.
    ///
    /// ONE BRUSH, THREE CHANNELS
    /// -------------------------
    /// A stroke writes to every channel that is ticked, in one pass of the cursor. The
    /// alternative - a channel selector, one stroke per map - turned the common case
    /// ("this patch is short, fine and grey") into three separate passes that had to
    /// line up by hand, and they never quite did: three strokes over the same region
    /// leave three slightly different edges.
    ///
    /// How a stroke reaches a map
    /// --------------------------
    ///  1. The cursor ray is raycast against the fur's ACTIVE mesh - the baked one for a
    ///     skinned character - to find the point being pointed at.
    ///  2. That world point becomes the centre of a brush sphere.
    ///  3. FurAttributeMap rasterises the mesh into UV space and every texel tests its
    ///     own world position against the sphere.
    ///
    /// Step 3 is what makes the brush behave: it is round in the WORLD, not in the UV
    /// atlas, so it does not swell where the unwrap is tight or stop at island edges.
    ///
    /// Strokes live in render textures that belong to the component, so they do not
    /// survive play mode, a domain reload or disabling the object. Save writes them out
    /// as texture assets and assigns them as the maps' sources, which is the only thing
    /// that makes them permanent. The window says so, loudly, while anything is unsaved.
    /// </summary>
    public sealed class FurPaintWindow : EditorWindow
    {
        private const float MinRadius = 0.001f;

        // The closest two stamps in a stroke may sit, as a fraction of the brush DIAMETER.
        // Not zero: a spacing of nothing is an infinite number of stamps over any distance.
        private const float MinSpacing = 0.01f;

        // How many stamps one mouse event may ask for. See StampAlongTo - past this the
        // cursor jumped rather than travelled, and joining the two points would draw
        // through whatever lies between them.
        private const int MaxStampsPerEvent = 256;

        // The bracket keys multiply rather than add. A brush is judged by ratio, so a
        // fixed increment is a leap at 5mm and imperceptible at half a metre; a fifth
        // either way takes about a dozen presses to cross the whole range.
        private const float RadiusStep = 1.2f;
        private const float MaxRadius = 2f;

        private static readonly int FurLengthId = Shader.PropertyToID("_FurLength");
        private static readonly int ThicknessId = Shader.PropertyToID("_Thickness");

        private static readonly FurDetailChannel[] AllChannels =
        {
            FurDetailChannel.Length,
            FurDetailChannel.Thickness,
            FurDetailChannel.Color,
            FurDetailChannel.Comb,
        };

        [SerializeField] private FurController _target;

        // Which channels this stroke writes. Length alone by default: it is the one people
        // reach for first, and having all three armed out of the box would mean a stray
        // click changed three things at once.
        [SerializeField] private bool _paintLength = true;
        [SerializeField] private bool _paintThickness;
        [SerializeField] private bool _paintColor;

        // Combing is armed on its own rather than alongside the other three. The others
        // write a value the artist chose in advance; this one writes the DIRECTION OF THE
        // STROKE, so the same drag that lays down a length also means "comb this way" -
        // and a stroke meant to shorten a patch would silently rake it sideways too.
        [SerializeField] private bool _paintComb;

        // How hard a full-speed stroke combs, 0..1. Multiplies the direction, whose length
        // the shader reads as the strength - so this is genuinely "how much comb", not a
        // second flow control.
        [SerializeField] private float _combStrength = 1f;

        // Held in the MATERIAL's own units - meters for length, strand-width units for
        // thickness - not as the 0..1 value the map stores. A raw map value is the wrong
        // thing to ask an artist for: the number they are thinking in is the one on the
        // material slider they just tuned.
        [SerializeField] private float _lengthValue;
        [SerializeField] private float _thicknessValue;
        [SerializeField] private Color _colorValue = Color.white;

        // Brush. Radius is in meters because the brush is a world-space sphere - a pixel
        // radius would change what it covers every time the camera moved.
        [SerializeField] private float _radius = 0.08f;
        [SerializeField] private float _strength = 0.5f;
        [SerializeField] private float _hardness = 0.5f;

        // Distance between stamps along a stroke, as a fraction of the brush diameter.
        // A quarter is the value every other paint tool defaults to: each stamp overlaps
        // its neighbour by three quarters, which is dense enough to read as a continuous
        // line without piling flow up so fast that a slow drag saturates.
        [SerializeField] private float _spacing = 0.25f;

        [SerializeField] private FurPaintMode _mode = FurPaintMode.Solid;

        [SerializeField] private Vector2 _scroll;

        // Rebuilt lazily and thrown away with the window. A hidden object rather than a
        // MeshCollider on the user's GameObject: painting must not leave a component
        // behind on a prefab, and a temporary one would fight anything already there.
        private const string RaycastProxyName = "__FurPaintRaycastProxy";

        private GameObject _raycastProxy;
        private MeshCollider _raycastCollider;
        private Mesh _raycastMesh;

        // A summary of the pose the collider was last cooked for. See EnsureRaycastProxy:
        // a skinned coat reuses one Mesh object, so this is the only thing that can tell
        // the collider its data has gone stale.
        private int _raycastPoseHash;

        private bool _stroking;

        // Where the last stamp of the current stroke landed, in world space. The stroke is
        // laid out from here rather than from the cursor, which is what keeps its spacing
        // independent of the event rate - see StampAlongTo.
        private Vector3 _lastStampPoint;
        private bool _hasLastStamp;

        private bool _hasHit;
        private Vector3 _hitPoint;
        private Vector3 _hitNormal;
        private Vector2 _hitUv0;
        private Vector2 _hitUv1;

        // ------------------------------------------------------------------ //
        //  Lifecycle
        // ------------------------------------------------------------------ //
        public static void Open(FurController target)
        {
            var window = GetWindow<FurPaintWindow>(utility: false, title: "Draw Fur", focus: true);
            window._target = target;
            window.minSize = new Vector2(320f, 420f);
            window.Show();
        }

        private void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGUI;
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            DestroyRaycastProxy();

            // GPU surfaces, and nothing else is holding them. Leaving them behind would
            // leak the whole history every time the window closes.
            ClearHistory();
        }

        // ------------------------------------------------------------------ //
        //  Window
        // ------------------------------------------------------------------ //
        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUI.BeginChangeCheck();
            _target = (FurController)EditorGUILayout.ObjectField("Target", _target, typeof(FurController), true);
            if (EditorGUI.EndChangeCheck())
            {
                DestroyRaycastProxy();

                // The history belongs to the maps it was copied from. Carrying it to a new
                // target would offer to undo one model's strokes onto another's.
                ClearHistory();
            }

            if (_target == null)
            {
                EditorGUILayout.HelpBox("Select a Fur Controller to paint on.", MessageType.Info);
                EditorGUILayout.EndScrollView();
                return;
            }

            DrawUnwrapWarning();
            DrawChannels();
            DrawBrushSection();
            DrawOperationsSection();
            DrawHistorySection();
            DrawPersistenceSection();
            DrawDiagnostics();

            EditorGUILayout.EndScrollView();
        }

        private void DrawChannels()
        {
            Section("Channels");
            Note("Every ticked channel is written by the same stroke.");

            EditorGUILayout.Space(2f);
            _paintLength = DrawScalarChannel(FurDetailChannel.Length, _paintLength,
                ref _lengthValue, "Length", "m");
            _paintThickness = DrawScalarChannel(FurDetailChannel.Thickness, _paintThickness,
                ref _thicknessValue, "Thickness", null);
            _paintColor = DrawColorChannel();
            DrawCombChannel();

            if (!AnyChannelActive())
            {
                EditorGUILayout.HelpBox(
                    "No channel is ticked, so strokes have nowhere to go.", MessageType.Warning);
            }
        }

        /// <summary>
        /// One scalar channel: its tick box, and while ticked, the value in the MATERIAL's
        /// own units.
        ///
        /// The slider runs to <see cref="FurController.DetailScaleMax"/> times the material
        /// value, not to it. The map is a scale with its neutral point in the MIDDLE of its
        /// range, so there is headroom above the material's own setting - which is what
        /// lets a stroke grow fur as well as cut it.
        /// </summary>
        private bool DrawScalarChannel(FurDetailChannel channel, bool enabled, ref float value,
            string label, string units)
        {
            enabled = EditorGUILayout.ToggleLeft(label, enabled, EditorStyles.boldLabel);
            if (!enabled)
                return false;

            float materialValue = MaterialScalar(channel);

            EditorGUI.indentLevel++;
            if (materialValue <= 1e-6f)
            {
                string name = channel == FurDetailChannel.Length ? "Fur Length" : "Strand Thickness";
                EditorGUILayout.HelpBox(
                    $"The material's {name} is zero, so there is nothing for this map to " +
                    "scale. Raise it on the material first.", MessageType.Warning);
                EditorGUI.indentLevel--;
                return true;
            }

            float max = materialValue * FurController.DetailScaleMax;
            string suffix = string.IsNullOrEmpty(units) ? "" : $" ({units})";
            value = EditorGUILayout.Slider($"Value{suffix}", value, 0f, max);

            Note($"0 removes it, {materialValue:0.###} is the material's own value, " +
                 $"{max:0.###} is the most this map can reach.");

            WarnIfNeutral(Mathf.Approximately(value, materialValue), $"{materialValue:0.###}");
            EditorGUI.indentLevel--;

            return true;
        }

        /// <summary>
        /// The comb, which is unlike the other three and says so.
        ///
        /// They each write a VALUE the artist chose before the stroke; this writes the
        /// stroke itself. There is no direction field here and there should not be one -
        /// the direction is which way the hand went, which is the only interface for
        /// combing anybody has ever wanted. What is left to set is how hard.
        /// </summary>
        private void DrawCombChannel()
        {
            _paintComb = EditorGUILayout.ToggleLeft("Comb", _paintComb, EditorStyles.boldLabel);
            if (!_paintComb)
                return;

            EditorGUI.indentLevel++;
            _combStrength = EditorGUILayout.Slider("Strength", _combStrength, 0f, 1f);

            Note("Drag across the coat and the hair lies down the way you dragged. There is " +
                 "no direction to set - the stroke IS the direction.");

            Note("Combing the other way rakes it back out, and a region combed one way and " +
                 "then the other ends up flat again rather than at some third angle. 0 " +
                 "strength is the eraser.");

            if (_target.FurMaterial != null && _target.FurMaterial.HasProperty("_CombStrength")
                && _target.FurMaterial.GetFloat("_CombStrength") <= 0.001f)
            {
                EditorGUILayout.HelpBox(
                    "Comb Strength on the material is zero, so a fully combed region still " +
                    "leans nowhere. Strokes will land in the map but nothing will move.",
                    MessageType.Warning);
            }

            EditorGUI.indentLevel--;
        }

        private bool DrawColorChannel()
        {
            bool enabled = EditorGUILayout.ToggleLeft("Color", _paintColor, EditorStyles.boldLabel);
            if (!enabled)
                return false;

            EditorGUI.indentLevel++;
            _colorValue = EditorGUILayout.ColorField("Color", _colorValue);
            Note("Multiplies the albedo of the FUR only - the skin underneath keeps its own " +
                 "color. White leaves the coat as the material describes it, so white is also " +
                 "the eraser.");
            WarnIfNeutral(_colorValue == Color.white, "White");
            EditorGUI.indentLevel--;

            return true;
        }

        /// <summary>
        /// Says so when a channel's brush is loaded with the value that means "leave it
        /// alone".
        ///
        /// This is the failure the tool shipped with: a fresh map bakes neutral, and the
        /// value meaning neutral is the material's own - so the first thing anyone did was
        /// paint that value over a map already saying exactly it, watch nothing happen, and
        /// conclude the brush was broken. It is one slider drag away and completely
        /// invisible unless something names it.
        /// </summary>
        private static void WarnIfNeutral(bool neutral, string valueName)
        {
            if (!neutral)
                return;

            EditorGUILayout.HelpBox(
                $"{valueName} is the neutral value - it means \"leave this exactly as the " +
                "material says\". Strokes will land but nothing will look different.",
                MessageType.Warning);
        }

        private void DrawOperationsSection()
        {
            Section("Whole Map");

            using (new EditorGUI.DisabledScope(!AnyValueChannelActive()))
            {
                if (GUILayout.Button("Fill with the current value"))
                    FillArmedChannels();
            }

            Note("Covers every armed channel at full strength, in one undoable step. The way "
               + "to start from a base other than the material's own - a coat that is short "
               + "everywhere before the long patches are brushed back in.");

            if (_paintComb)
            {
                Note("Comb is skipped: a fill has no direction to it. Comb the whole coat by "
                   + "dragging across it instead.");
            }
        }

        /// <summary>
        /// Floods the armed channels with the brush's current value.
        ///
        /// Deliberately one enormous stamp through the ordinary paint path rather than a
        /// separate clear pass. The value written, the channels chosen, the undo step and
        /// the seam padding afterwards are then all the same code that a stroke uses, so
        /// Fill cannot drift away from what the brush does - which is exactly what a second
        /// implementation of "write this value" would eventually do.
        /// </summary>
        private void FillArmedChannels()
        {
            if (_target == null || !EnsureActiveMaps())
                return;

            var renderer = _target.GetComponent<Renderer>();
            if (renderer == null)
                return;

            CaptureUndoSnapshot();

            var brush = new FurBrush
            {
                PositionWS = renderer.bounds.center,

                // Wireframe: a fill has to reach the far side of the model as well as the
                // near one, and Solid mode exists precisely to stop a stroke doing that.
                Mode = FurPaintMode.Wireframe,

                // Twice the bounding radius, so the far corner is inside the sphere rather
                // than on it - the falloff reaches zero AT the edge.
                Radius = Mathf.Max(renderer.bounds.extents.magnitude * 2f, MinRadius),

                // Flat to the edge and at full flow: a fill that faded toward the outside
                // of the model would not be a fill.
                Strength = 1f,
                Hardness = 1f,
                DirectionWS = Vector3.forward,
            };

            if (_paintLength)
                _target.PaintLength(brush, MapValue(FurDetailChannel.Length, _lengthValue));

            if (_paintThickness)
                _target.PaintThickness(brush, MapValue(FurDetailChannel.Thickness, _thicknessValue));

            if (_paintColor)
                _target.PaintColor(brush, _colorValue);

            _target.FinishPaintStroke();
            SceneView.RepaintAll();
            Repaint();
        }

        private void DrawHistorySection()
        {
            Section("History");

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!CanUndo))
                    if (GUILayout.Button($"Undo ({_undo.Count})"))
                        PerformUndo();

                using (new EditorGUI.DisabledScope(!CanRedo))
                    if (GUILayout.Button($"Redo ({_redo.Count})"))
                        PerformRedo();
            }

            long bytes = HistoryByteSize();
            Note($"{bytes / (1024f * 1024f):0.#} MB of {_undoBudgetBytes / (1024 * 1024)} MB. "
               + "One step per stroke, holding only the channels that stroke wrote. Steps are "
               + "images of the maps, so the budget is in megabytes rather than in a count - "
               + "a step at 2048 costs sixty-four times one at 256.");

            Note("The history lives in this window and in graphics memory, so it does not "
               + "survive closing it, entering play mode, or a script recompile. Save before "
               + "any of those.");
        }

        /// <summary>
        /// Says, before a single stroke is laid down, whether this mesh can actually hold
        /// one.
        ///
        /// The maps store one value per piece of SURFACE, which needs every piece of
        /// surface to own a texel. A mirrored or overlapped UV0 does not give it one, and
        /// the symptom is unmistakable once you know it and baffling until then: paint a
        /// marking on one flank and it appears on the other, somewhere you never touched.
        ///
        /// The requirement was written into four separate tooltips and checked in exactly
        /// none of them, which is the same as not having it - nobody reads a tooltip about
        /// a property of their asset they have no reason to suspect. So it is measured
        /// here, the way the bake measures it: rasterise UV0 at the map's own resolution
        /// and count the texels more than one triangle claims. That number IS the
        /// artefact, and the fix - the UV1 detail set - is one control away.
        /// </summary>
        private void DrawUnwrapWarning()
        {
            Mesh mesh = _target.ActiveMesh;
            if (mesh == null || !mesh.isReadable)
                return;

            if (_target.DetailUvSet == FurDetailUvSet.UV1)
            {
                if (!mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord1))
                {
                    EditorGUILayout.HelpBox(
                        "This coat is set to the UV1 detail set, but the mesh has no second UV "
                      + "set - the maps have fallen back to UV0. Enable 'Generate Lightmap UVs' "
                      + "on the model importer.", MessageType.Error);
                }
                return;
            }

            float shared = FurUnwrapAnalysis.SharedTexelFraction(mesh, _target.DetailMapResolution);
            if (shared < 0.01f)
                return;

            bool hasUv1 = mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord1);

            EditorGUILayout.HelpBox(
                $"{shared * 100f:0.#}% of this map is shared between two or more places on the "
              + "model - its UV0 islands overlap or are mirrored. Painting any of those places "
              + "will paint the others too, and no brush setting can separate them: they are "
              + "one texel of storage.\n\n"
              + (hasUv1
                    ? "The mesh already has a second UV set. Switch this coat's Detail UV Set to "
                    + "UV1 on the Fur Controller."
                    : "Enable 'Generate Lightmap UVs' on the model importer, then switch this "
                    + "coat's Detail UV Set to UV1 on the Fur Controller."),
                shared > 0.1f ? MessageType.Error : MessageType.Warning);

            if (hasUv1 && GUILayout.Button("Switch to the UV1 detail set"))
            {
                Undo.RecordObject(_target, "Fur Detail UV Set");
                _target.DetailUvSet = FurDetailUvSet.UV1;
                EditorUtility.SetDirty(_target);

                // The maps are about to be rebuilt in a different layout, so every step in
                // the history describes an arrangement that will not exist a frame from now.
                ClearHistory();
            }
        }

        private void DrawBrushSection()
        {
            Section("Brush");

            // Meters, not pixels: the brush is a sphere in the world, so its size is a
            // property of the model rather than of how close the camera happens to be.
            _radius = EditorGUILayout.Slider("Radius (meters)", _radius, 0.005f, MaxRadius);
            _strength = EditorGUILayout.Slider("Strength (flow)", _strength, 0.01f, 1f);
            _hardness = EditorGUILayout.Slider("Hardness", _hardness, 0f, 1f);
            _spacing = EditorGUILayout.Slider("Spacing", _spacing, MinSpacing, 1f);

            Note("Strength is FLOW, not opacity: strokes build up, so a low value lets the " +
                 "value grow while the cursor lingers. Hold the mouse and drag in the Scene " +
                 "view; hold Alt / Ctrl / Shift to navigate instead.");

            Note("Spacing is how far the brush travels between stamps, as a fraction of its " +
                 "diameter. It is what makes a stroke a line rather than a row of blobs, and " +
                 "what keeps that line the same whether the hand moves fast or slow. Lower is " +
                 "smoother and builds flow faster; 1 leaves the stamps just touching.");

            Note("Shortcuts, in the Scene view:  [ and ]  resize the brush   ·   I  picks the " +
                 "value under the cursor into the brush   ·   Ctrl+Z / Ctrl+Shift+Z  undo and " +
                 "redo strokes.");

            EditorGUILayout.Space(4f);
            _mode = (FurPaintMode)EditorGUILayout.EnumPopup("Mode", _mode);

            if (_mode == FurPaintMode.Solid)
            {
                Note("Solid: only the surface you can actually see. Back faces are skipped, " +
                     "and so is anything standing behind the body - painting one leg leaves " +
                     "the far one alone.");
            }
            else
            {
                Note("Wireframe: everything the brush sphere contains, front and back, " +
                     "visible or hidden. For a band that has to go all the way through - " +
                     "a collar, a ring around a tail, a thin ear.");
            }

            DrawReachAdvice();
        }

        /// <summary>
        /// States the brush's diameter against the model's size.
        ///
        /// This is the number that decides whether the two modes can differ AT ALL, and
        /// nothing on screen used to say it. The brush is a sphere centred on the surface,
        /// so it only reaches the far side where the body is thinner than its diameter -
        /// which, at the default radius against a whole creature, is nowhere. Both modes
        /// then produce identical strokes and the mode control looks like it does nothing.
        /// </summary>
        private void DrawReachAdvice()
        {
            var renderer = _target.GetComponent<Renderer>();
            if (renderer == null)
                return;

            Vector3 size = renderer.bounds.size;
            float diameter = _radius * 2f;
            float thinnest = Mathf.Min(size.x, Mathf.Min(size.y, size.z));

            string sizeText = $"Brush diameter {diameter:0.###} m; the model is " +
                              $"{size.x:0.##} x {size.y:0.##} x {size.z:0.##} m.";

            if (diameter < thinnest)
            {
                EditorGUILayout.HelpBox(
                    sizeText + "\n\nThe brush is narrower than the model is thick, so it " +
                    "cannot reach the far side except on thin parts - an ear, the tip of a " +
                    "tail. Until it can, Solid and Wireframe produce the same stroke. Raise " +
                    "the radius to paint through.", MessageType.Info);
            }
            else
            {
                Note(sizeText + " Wide enough to reach through the model in places, so the " +
                     "two modes will differ there.");
            }
        }

        private void DrawPersistenceSection()
        {
            Section("Saving");

            bool unsaved = _target.HasUnsavedPaint;

            if (unsaved)
            {
                // Stated as consequences rather than as a generic "unsaved changes", because
                // the ways this work disappears are not obvious ones: entering play mode and
                // recompiling a script both do it.
                EditorGUILayout.HelpBox(
                    "Unsaved strokes. They live in render textures owned by the component, so " +
                    "they will be lost on entering play mode, recompiling scripts, or disabling " +
                    "the object. Save writes them into texture assets.", MessageType.Warning);
            }

            using (new EditorGUI.DisabledScope(!unsaved))
            {
                if (GUILayout.Button("Save Painted Maps...", GUILayout.Height(24f)))
                    SavePaintedMaps();
            }

            if (GUILayout.Button("Revert to Source Textures"))
            {
                if (EditorUtility.DisplayDialog("Revert fur paint",
                        "Discard every unsaved stroke on all four maps and rebake them from " +
                        "their source textures?\n\nThe undo history goes with them.",
                        "Revert", "Cancel"))
                {
                    _target.RebakeDetailMaps();

                    // The history describes strokes on maps that are about to be rebuilt
                    // from scratch. Keeping it would offer to undo its way back to a state
                    // this reverts past, which is not what the artist just asked for.
                    ClearHistory();
                    SceneView.RepaintAll();
                }
            }
        }

        private void DrawDiagnostics()
        {
            Section("Diagnostics");

            Mesh mesh = _target.ActiveMesh;
            if (mesh == null)
            {
                EditorGUILayout.HelpBox(
                    "No active mesh yet. The Fur Controller resolves it while rendering, so give " +
                    "the Scene view a moment - or check that the object is enabled and has a " +
                    "material assigned.", MessageType.Info);
                return;
            }

            // The one hard requirement, and the one whose failure is otherwise silent: a
            // non-readable mesh raycasts fine but reports no UV, so every stroke would land
            // in the same place.
            if (!mesh.isReadable)
            {
                EditorGUILayout.HelpBox(
                    $"'{mesh.name}' is not readable, so the brush cannot find the surface under " +
                    "the cursor. Enable Read/Write in the model's import settings.",
                    MessageType.Error);
                return;
            }

            EditorGUILayout.LabelField("Mesh", $"{mesh.name}  ({mesh.vertexCount:N0} verts)");
            EditorGUILayout.LabelField("Detail Map Bakes", _target.DetailMapBakeCount.ToString());

            DrawTexelSizeAdvice(mesh);
            DrawMapPreviews();
        }

        /// <summary>
        /// Shows the baked maps themselves, laid out in UV space.
        ///
        /// This is the one thing that splits "the brush is not writing" from "the brush is
        /// writing and the fur is not showing it", and there was no way to tell them apart
        /// from inside Unity. If a region looks unpainted on the model, look for it here:
        /// paint present in the map means the problem is downstream, in shading or in the
        /// unwrap; nothing there means the stroke never landed.
        ///
        /// Drawn without alpha, because these are render textures whose alpha carries
        /// COVERAGE rather than opacity - blending against it would make the padding
        /// around every UV island look like a hole.
        /// </summary>
        private void DrawMapPreviews()
        {
            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Baked Maps", EditorStyles.miniBoldLabel);

            bool any = false;
            foreach (FurDetailChannel channel in AllChannels)
            {
                if (!(_target.BakedDetailMap(channel) is RenderTexture rt) || !rt.IsCreated())
                    continue;

                any = true;

                EditorGUILayout.LabelField($"{channel}  ({rt.width}px)", EditorStyles.miniLabel);

                float size = Mathf.Min(EditorGUIUtility.currentViewWidth - 40f, 220f);
                Rect r = GUILayoutUtility.GetRect(size, size, GUILayout.ExpandWidth(false));
                r.x += 12f;
                r.width = size;

                EditorGUI.DrawPreviewTexture(r, rt, null, ScaleMode.ScaleToFit, 1f);
                EditorGUILayout.Space(2f);
            }

            if (!any)
            {
                Note("No maps baked yet - paint a stroke, or assign a source texture.");
                return;
            }

            Note("Mid-grey is neutral for Length and Thickness; white is neutral for Color. " +
                 "If a region reads as painted here but looks unchanged on the model, the " +
                 "stroke landed and the problem is elsewhere.");
        }

        /// <summary>
        /// States how much of the model's surface one texel of the detail maps covers.
        ///
        /// This single number decides whether a feature can be painted AT ALL, and nothing
        /// in the tool used to say it. A squirrel at a 256px map gets about nine
        /// millimetres of surface per texel - so the gap between two fingers, a few
        /// millimetres across, is narrower than one texel and simply cannot be
        /// represented. The brush is working perfectly and nothing appears, which reads as
        /// a broken tool rather than as a resolution that is too low.
        /// </summary>
        private void DrawTexelSizeAdvice(Mesh mesh)
        {
            float mm = SurfaceMillimetresPerTexel(mesh);
            if (mm <= 0f)
                return;

            EditorGUILayout.LabelField("Texel Size", $"~{mm:0.#} mm of surface");

            // Roughly the width of the tightest thing anyone tries to paint on a creature:
            // the gap between digits, the crease inside an ear.
            const float TightFeatureMm = 5f;

            if (mm > TightFeatureMm)
            {
                int suggested = Mathf.Min(FurController.MaxDetailMapResolution,
                    Mathf.NextPowerOfTwo(Mathf.CeilToInt(_target.DetailMapResolution * (mm / TightFeatureMm))));

                EditorGUILayout.HelpBox(
                    $"One texel spans about {mm:0.#} mm of this model. Anything narrower than " +
                    "that - the gap between fingers, the crease inside an ear - is thinner " +
                    "than a single texel and cannot be painted at any brush size.\n\n" +
                    $"Raise Baked Resolution to {suggested} to reach roughly {TightFeatureMm:0} mm.",
                    MessageType.Info);

                if (suggested > _target.DetailMapResolution &&
                    GUILayout.Button($"Set Baked Resolution to {suggested}"))
                {
                    Undo.RecordObject(_target, "Set Fur Detail Map Resolution");
                    _target.DetailMapResolution = suggested;
                    EditorUtility.SetDirty(_target);
                }
            }
        }

        /// <summary>
        /// How many millimetres of surface one texel spans, from the ratio of the mesh's
        /// world area to the fraction of the UV square its unwrap actually occupies.
        ///
        /// Measured rather than assumed, because the answer depends entirely on how
        /// tightly the model is packed: two models at the same map resolution can differ
        /// several-fold, and only the packed one can hold fine detail.
        /// </summary>
        private float SurfaceMillimetresPerTexel(Mesh mesh)
        {
            if (mesh == null || !mesh.isReadable)
                return 0f;

            Vector3[] verts = mesh.vertices;
            Vector2[] uv = mesh.uv;
            int[] tris = mesh.triangles;

            if (uv.Length != verts.Length || tris.Length < 3)
                return 0f;

            Vector3 scale = _target.transform.lossyScale;
            double worldArea = 0.0, uvArea = 0.0;

            for (int i = 0; i < tris.Length; i += 3)
            {
                Vector3 a = Vector3.Scale(verts[tris[i]], scale);
                Vector3 b = Vector3.Scale(verts[tris[i + 1]], scale);
                Vector3 c = Vector3.Scale(verts[tris[i + 2]], scale);
                worldArea += Vector3.Cross(b - a, c - a).magnitude * 0.5;

                Vector2 ta = uv[tris[i]], tb = uv[tris[i + 1]], tc = uv[tris[i + 2]];
                uvArea += Mathf.Abs((tb.x - ta.x) * (tc.y - ta.y) - (tc.x - ta.x) * (tb.y - ta.y)) * 0.5;
            }

            if (worldArea <= 0.0 || uvArea <= 0.0)
                return 0f;

            double resolution = _target.DetailMapResolution;
            double texels = uvArea * resolution * resolution;

            return (float)(System.Math.Sqrt(worldArea / texels) * 1000.0);
        }

        // ------------------------------------------------------------------ //
        //  Scene view
        // ------------------------------------------------------------------ //
        private void OnSceneGUI(SceneView sceneView)
        {
            if (_target == null)
                return;

            Event e = Event.current;

            // Shortcuts before every other gate in this method, and that ordering is load
            // bearing rather than tidy.
            //
            // Undo is a modifier chord, so any guard that returns early on a held Ctrl can
            // never see it - and the gates below return on conditions that have nothing to
            // do with whether there is a stroke to take back: no channel armed, a mesh not
            // resolved yet. Leaving Ctrl+Z unhandled in those states is not a missing
            // feature, it is a hazard: the keystroke falls through to the editor's own undo
            // stack, which knows nothing about the maps and will cheerfully take back
            // whatever was last done to the SCENE instead. Untick a channel to look at
            // something, press Ctrl+Z out of habit, and a light moves.
            if (HandleShortcuts(e))
                return;

            if (!_target.isActiveAndEnabled || !AnyChannelActive())
                return;

            Mesh mesh = _target.ActiveMesh;
            if (mesh == null || !mesh.isReadable)
                return;

            // Takes the default control so a drag paints instead of rubber-band selecting.
            // Only while no modifier is held, so the usual navigation keys still work and
            // the artist is never trapped in the tool.
            bool modifier = e.alt || e.control || e.command || e.shift;
            if (!modifier)
                HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));

            UpdateHit(e);
            DrawBrushCursor();

            if (modifier || e.button != 0)
                return;

            switch (e.type)
            {
                case EventType.MouseDown:
                    if (!_hasHit || !EnsureActiveMaps())
                        break;

                    // Once per stroke, not per stamp: the view is frozen while the cursor
                    // is held, so the depth picture Solid mode reads cannot go stale.
                    if (_mode == FurPaintMode.Solid)
                        _target.CaptureBrushOcclusion(sceneView.camera);

                    BeginStroke();
                    StampAt(_hitPoint);
                    _lastStampPoint = _hitPoint;
                    _hasLastStamp = true;
                    e.Use();
                    break;

                case EventType.MouseDrag:
                    if (!_stroking || !_hasHit)
                        break;
                    StampAlongTo(_hitPoint);
                    e.Use();
                    break;

                case EventType.MouseUp:
                    if (!_stroking)
                        break;
                    EndStroke();
                    e.Use();
                    break;
            }

            // A stroke must not be able to outlive the drag that started it.
            //
            // MouseUp is not guaranteed to arrive here. The button can be released over
            // another window, or with a modifier held - which the guard above returns on -
            // and either way the next MouseDown would then be treated as the continuation
            // of a stroke that visibly ended, painting a line across the model from
            // wherever the cursor happened to be. These are the events that say the drag
            // is over whatever route it took.
            if (_stroking && (e.type == EventType.MouseLeaveWindow
                           || e.rawType == EventType.MouseUp
                           || e.type == EventType.Ignore))
            {
                EndStroke();
            }

            if (e.type == EventType.MouseMove)
                sceneView.Repaint();
        }

        // ------------------------------------------------------------------ //
        //  Keyboard
        // ------------------------------------------------------------------ //
        /// <summary>
        /// The scene-view shortcuts, and whether one of them consumed the event.
        ///
        /// Undo is intercepted rather than left to Unity's own stack, and it has to be:
        /// the maps are render textures, so nothing a stroke does is visible to the
        /// editor's undo system at all. Ctrl+Z with this window open would otherwise
        /// walk back through whatever the artist last did to the SCENE - moving a light,
        /// renaming an object - while the strokes they were actually trying to take back
        /// stayed exactly where they were. Consuming the event is what stops that.
        ///
        /// The bracket keys are the size shortcut every paint tool has had for twenty
        /// years, and the reason it is universal is that brush size is the setting an
        /// artist changes most and the one it is most disruptive to cross the screen for.
        /// Geometric steps rather than fixed ones, because a brush is judged by ratio: a
        /// millimetre matters at 5mm and is invisible at half a metre.
        /// </summary>
        private bool HandleShortcuts(Event e)
        {
            if (e.type != EventType.KeyDown)
                return false;

            bool ctrl = e.control || e.command;

            if (ctrl && e.keyCode == KeyCode.Z)
            {
                // Ctrl+Shift+Z is the second redo binding, alongside Ctrl+Y.
                if (e.shift) PerformRedo();
                else PerformUndo();
                e.Use();
                return true;
            }

            if (ctrl && e.keyCode == KeyCode.Y)
            {
                PerformRedo();
                e.Use();
                return true;
            }

            if (!ctrl && !e.alt)
            {
                if (e.keyCode == KeyCode.LeftBracket)
                {
                    SetRadius(_radius / RadiusStep);
                    e.Use();
                    return true;
                }

                if (e.keyCode == KeyCode.RightBracket)
                {
                    SetRadius(_radius * RadiusStep);
                    e.Use();
                    return true;
                }

                if (e.keyCode == KeyCode.I)
                {
                    PickValueUnderCursor();
                    e.Use();
                    return true;
                }
            }

            return false;
        }

        // ------------------------------------------------------------------ //
        //  Eyedropper
        // ------------------------------------------------------------------ //
        /// <summary>
        /// Loads the brush with whatever the map already says under the cursor.
        ///
        /// The operation every paint tool has and the one whose absence is felt hardest
        /// here, because these channels are not colours: the brush is loaded in METRES of
        /// fur, and matching a patch you painted an hour ago by eye means guessing at a
        /// number rather than at a shade. Sampling it removes the guess entirely, which is
        /// the difference between blending a region into its surroundings and starting it
        /// again.
        ///
        /// Reads back through the same UV set the map is stored in, because that is the
        /// only one that addresses it - sampling a UV1 map by UV0 would return a value
        /// belonging to some other part of the body.
        /// </summary>
        private void PickValueUnderCursor()
        {
            if (!_hasHit || _target == null)
                return;

            Vector2 uv = _target.DetailUvSet == FurDetailUvSet.UV1 ? _hitUv1 : _hitUv0;
            bool picked = false;

            foreach (FurDetailChannel channel in AllChannels)
            {
                if (!ChannelActive(channel) || !TryReadMap(channel, uv, out Color stored))
                    continue;

                if (channel == FurDetailChannel.Comb)
                {
                    // Nothing to load: a comb's direction comes from the stroke, not from a
                    // control, so there is no brush setting for this to fill in.
                    continue;
                }

                if (channel == FurDetailChannel.Color)
                {
                    _colorValue = new Color(stored.r, stored.g, stored.b, 1f);
                }
                else
                {
                    // Back out of the map's 0..1 into the material units the sliders are
                    // in - the exact inverse of MapValue, so picking a value and painting
                    // it straight back changes nothing.
                    float material = MaterialScalar(channel);
                    float value = stored.r * material * FurController.DetailScaleMax;

                    if (channel == FurDetailChannel.Length) _lengthValue = value;
                    else _thicknessValue = value;
                }

                picked = true;
            }

            if (picked)
                Repaint();
        }

        /// <summary>
        /// One texel of a baked map, read back to the CPU.
        ///
        /// A readback stalls the graphics pipeline, which is why this is a key press
        /// rather than something the cursor does as it moves.
        /// </summary>
        private bool TryReadMap(FurDetailChannel channel, Vector2 uv, out Color value)
        {
            value = Color.black;

            if (!(_target.BakedDetailMap(channel) is RenderTexture map) || !map.IsCreated())
                return false;

            int x = Mathf.Clamp(Mathf.FloorToInt(uv.x * map.width), 0, map.width - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt(uv.y * map.height), 0, map.height - 1);

            RenderTexture previous = RenderTexture.active;
            var pixel = new Texture2D(1, 1, TextureFormat.RGBAFloat, false);

            try
            {
                RenderTexture.active = map;
                pixel.ReadPixels(new Rect(x, y, 1, 1), 0, 0);
                pixel.Apply();
                value = pixel.GetPixel(0, 0);
            }
            finally
            {
                RenderTexture.active = previous;
                DestroyImmediate(pixel);
            }

            return true;
        }

        private void SetRadius(float radius)
        {
            _radius = Mathf.Clamp(radius, MinRadius, MaxRadius);
            Repaint();
            SceneView.RepaintAll();
        }

        // ------------------------------------------------------------------ //
        //  Strokes
        // ------------------------------------------------------------------ //
        private void BeginStroke()
        {
            CaptureUndoSnapshot();
            _stroking = true;
            _hasLastStamp = false;
        }

        private void EndStroke()
        {
            _stroking = false;
            _hasLastStamp = false;

            // The padding around every UV island is rebuilt once here rather than after
            // each stamp - see FurAttributeMap.FinishStroke.
            if (_target != null)
                _target.FinishPaintStroke();

            Repaint();
        }

        /// <summary>
        /// Lays a continuous line of stamps from the last one to here.
        ///
        /// This is the difference between a brush and a rubber stamp, and it is not a
        /// refinement - it is what makes the tool usable at all. Mouse events arrive at
        /// whatever rate the editor happens to be running at, so a stroke used to be a
        /// row of discrete blobs whose spacing was set by how fast the hand moved: drag
        /// slowly and the value piled up into a dark smear, flick and the line broke into
        /// dots with bare model between them. The same gesture gave a different result
        /// every time, which is the one thing a paint tool may not do.
        ///
        /// Walking the gap in fixed steps of the brush's own size decouples the result
        /// from the frame rate entirely. Spacing is measured against the DIAMETER, which
        /// is the convention every other paint tool uses, so a quarter means each stamp
        /// overlaps its neighbour by three quarters.
        ///
        /// The step is a straight line through world space rather than a path over the
        /// surface. A chord between two points on a curved body passes slightly inside
        /// it, and that is harmless here: the brush is a SPHERE, so a centre a little
        /// under the surface still covers the surface. Tracing the mesh instead would
        /// cost a raycast per step to fix something no one can see.
        /// </summary>
        private void StampAlongTo(Vector3 target)
        {
            if (!_hasLastStamp)
            {
                StampAt(target);
                _lastStampPoint = target;
                _hasLastStamp = true;
                return;
            }

            float radius = Mathf.Max(_radius, MinRadius);
            float step = Mathf.Max(radius * 2f * Mathf.Max(_spacing, MinSpacing), 1e-5f);

            Vector3 delta = target - _lastStampPoint;
            float distance = delta.magnitude;

            if (distance < step)
                return;

            // A hard ceiling on the work one event may ask for. The cursor can jump the
            // width of the model in a single event - off the mesh and back on, or from one
            // limb to another - and stepping all the way across at a fine spacing would
            // both hang the editor and draw a line through everything in between. Past the
            // cap the gesture was a jump rather than a stroke, so it restarts from there.
            int steps = Mathf.FloorToInt(distance / step);
            if (steps > MaxStampsPerEvent)
            {
                // A jump, not a stroke - so no comb direction, for the same reason the
                // first stamp of a stroke carries none.
                StampAt(target);
                _lastStampPoint = target;
                return;
            }

            Vector3 direction = delta / distance;
            for (int i = 1; i <= steps; i++)
                StampAt(_lastStampPoint + direction * (step * i), direction);

            // Keep the remainder rather than snapping to the cursor: dropping it would
            // re-round the spacing on every event, which is the frame-rate dependence
            // this exists to remove.
            _lastStampPoint += direction * (step * steps);
        }

        private void UpdateHit(Event e)
        {
            _hasHit = false;

            if (!EnsureRaycastProxy())
                return;

            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);

            // Collider.Raycast rather than Physics.Raycast: this proxy is not part of the
            // scene's physics world and must not be, or it would show up in every other
            // query the project makes while the window is open.
            if (!_raycastCollider.Raycast(ray, out RaycastHit hit, float.MaxValue))
                return;

            _hasHit = true;
            _hitPoint = hit.point;
            _hitNormal = hit.normal;

            // Both sets, because which one addresses the maps is the coat's decision and
            // not this window's - see FurDetailUvSet. Only the eyedropper reads them, and
            // it needs whichever set the map it is sampling was actually stored in.
            _hitUv0 = hit.textureCoord;
            _hitUv1 = hit.textureCoord2;
        }

        /// <summary>
        /// Draws the brush at the cursor.
        ///
        /// As a SPHERE, not a disc. The brush has always been a sphere - that is what
        /// makes a stroke the same physical size wherever it lands - but drawing only the
        /// ring where it meets the surface hid its most important property: how far it
        /// reaches INTO the model. In Wireframe mode that reach is the entire point, and
        /// with a small radius against a thick body it reaches nothing at all, so the two
        /// modes produce identical results and the mode control looks broken.
        ///
        /// The surface ring is still drawn, brightest, because that is where a Solid
        /// stroke actually lands. The sphere behind it is what says why Wireframe would
        /// differ - or, just as usefully, why it would not.
        /// </summary>
        private void DrawBrushCursor()
        {
            if (!_hasHit)
                return;

            Color cursor = CursorColor();
            float radius = Mathf.Max(_radius, MinRadius);

            // The volume. Three orthogonal rings read as a sphere from any angle, and in
            // Wireframe mode they are drawn solidly because everything inside them is
            // going to be painted.
            Color volume = cursor;
            volume.a = _mode == FurPaintMode.Wireframe ? 0.55f : 0.18f;
            Handles.color = volume;
            Handles.DrawWireDisc(_hitPoint, Vector3.right,   radius);
            Handles.DrawWireDisc(_hitPoint, Vector3.up,      radius);
            Handles.DrawWireDisc(_hitPoint, Vector3.forward, radius);

            // Where the brush meets the surface - the part a Solid stroke marks.
            Handles.color = cursor;
            Handles.DrawWireDisc(_hitPoint, _hitNormal, radius);

            // Inner ring where the falloff has faded to about half. The outer circle
            // alone says nothing about hardness.
            Color inner = cursor;
            inner.a = 0.35f;
            Handles.color = inner;
            Handles.DrawWireDisc(_hitPoint, _hitNormal, radius * Mathf.Lerp(0.5f, 0.95f, _hardness));

            // What this stroke will write, and how far through. With one brush driving
            // three maps the tick boxes are in another window and easy to lose track of,
            // and a stroke that quietly changed a channel you had forgotten about is
            // exactly the kind of thing found much later.
            Handles.color = Color.white;
            Handles.Label(_hitPoint + _hitNormal * radius,
                $"{ActiveChannelLabel()}\n{_mode.ToString().ToLowerInvariant()}");
        }

        private void StampAt(Vector3 positionWS) => StampAt(positionWS, Vector3.zero);

        /// <param name="travelWS">
        /// The direction the stroke was moving when this stamp landed, in world space, or
        /// zero if it is not moving. Only the comb reads it - and reading it is the whole
        /// idea: combing is not a value you dial in and apply, it is the gesture itself.
        /// A barber does not select a direction from a list.
        /// </param>
        private void StampAt(Vector3 positionWS, Vector3 travelWS)
        {
            var brush = new FurBrush
            {
                PositionWS = positionWS,
                DirectionWS = SceneView.lastActiveSceneView != null
                    ? SceneView.lastActiveSceneView.camera.transform.forward
                    : -_hitNormal,
                Radius = Mathf.Max(_radius, MinRadius),
                Strength = _strength,
                Hardness = _hardness,
                Mode = _mode,
            };

            // One brush position, every armed channel - so the three maps share a single
            // edge rather than three hand-aligned ones.
            if (_paintLength)
                _target.PaintLength(brush, MapValue(FurDetailChannel.Length, _lengthValue));

            if (_paintThickness)
                _target.PaintThickness(brush, MapValue(FurDetailChannel.Thickness, _thicknessValue));

            if (_paintColor)
                _target.PaintColor(brush, _colorValue);

            // A stamp with nowhere to go carries no comb. The first stamp of a stroke is
            // exactly that: the button went down and the hand has not moved yet, so there
            // is no direction to record and guessing one would rake the fur on a click.
            if (_paintComb && travelWS.sqrMagnitude > 1e-12f)
                _target.PaintComb(brush, travelWS.normalized * Mathf.Clamp01(_combStrength));

            SceneView.RepaintAll();
        }

        // ------------------------------------------------------------------ //
        //  Channel plumbing
        // ------------------------------------------------------------------ //
        private bool ChannelActive(FurDetailChannel channel)
        {
            switch (channel)
            {
                case FurDetailChannel.Length:    return _paintLength;
                case FurDetailChannel.Thickness: return _paintThickness;
                case FurDetailChannel.Color:     return _paintColor;
                case FurDetailChannel.Comb:      return _paintComb;
                default:                         return false;
            }
        }

        // Comb counts. Leaving it out meant a comb-only brush was treated as "nothing armed",
        // so the Scene view never took the mouse and a comb could not be painted on its own.
        private bool AnyChannelActive()
        {
            return _paintLength || _paintThickness || _paintColor || _paintComb;
        }

        // Fill writes a value, and the comb has none - so Fill needs a value channel.
        private bool AnyValueChannelActive()
        {
            return _paintLength || _paintThickness || _paintColor;
        }

        private string ActiveChannelLabel()
        {
            string label = "";
            if (_paintLength) label += "length ";
            if (_paintThickness) label += "thickness ";
            if (_paintColor) label += "color ";
            if (_paintComb) label += "comb";
            return label.Trim();
        }

        /// <summary>Allocates a map for every armed channel, so strokes have somewhere to land.</summary>
        // ------------------------------------------------------------------ //
        //  Undo
        // ------------------------------------------------------------------ //
        /// <summary>
        /// One stroke's worth of "what the maps looked like before".
        ///
        /// A stroke is hundreds of blend operations against a render texture and not one
        /// of them can be run backwards, so the only route back is to have kept a copy.
        /// That is why this is images rather than a command log: replaying every stamp
        /// from the bake would be exact, but a long session is thousands of full-mesh
        /// rasterisations and undo has to be instant.
        ///
        /// Only the channels a stroke actually touched are held. Painting is usually one
        /// channel at a time, so remembering all three would triple the cost of the
        /// history to store two images nothing wrote to.
        /// </summary>
        private sealed class PaintUndoStep
        {
            public FurDetailChannel[] Channels;
            public RenderTexture[] Textures;

            public long ByteSize;

            public void Release()
            {
                if (Textures == null)
                    return;

                foreach (RenderTexture rt in Textures)
                    FurController.ReleaseDetailSnapshot(rt);

                Textures = null;
                Channels = null;
                ByteSize = 0;
            }
        }

        // Not serialized, and deliberately: these are GPU surfaces, so a domain reload
        // takes them whatever this field says. Pretending otherwise would leave an undo
        // stack full of dead handles, which is worse than an empty one.
        private readonly System.Collections.Generic.List<PaintUndoStep> _undo =
            new System.Collections.Generic.List<PaintUndoStep>();
        private readonly System.Collections.Generic.List<PaintUndoStep> _redo =
            new System.Collections.Generic.List<PaintUndoStep>();

        // What the history is allowed to cost, in bytes.
        //
        // A budget rather than a step count, because a step is not a fixed size: one
        // channel at 256 is a quarter of a megabyte and three at 2048 are a hundred. A
        // count that felt generous at the small end would be a gigabyte at the large one,
        // and the artist who set the resolution high is exactly the one who cannot afford
        // that. Bytes are the thing actually being spent, so bytes are what is bounded.
        [SerializeField] private long _undoBudgetBytes = 192L * 1024L * 1024L;

        private bool CanUndo => _undo.Count > 0;
        private bool CanRedo => _redo.Count > 0;

        /// <summary>
        /// Remembers the armed channels as they are now, before a stroke changes them.
        /// </summary>
        private void CaptureUndoSnapshot()
        {
            if (_target == null)
                return;

            var channels = new System.Collections.Generic.List<FurDetailChannel>();
            var textures = new System.Collections.Generic.List<RenderTexture>();
            long bytes = 0;

            foreach (FurDetailChannel channel in AllChannels)
            {
                if (!ChannelActive(channel))
                    continue;

                RenderTexture snapshot = _target.CaptureDetailSnapshot(channel);
                if (snapshot == null)
                    continue;

                channels.Add(channel);
                textures.Add(snapshot);
                bytes += _target.DetailSnapshotByteSize(channel);
            }

            if (textures.Count == 0)
                return;

            _undo.Add(new PaintUndoStep
            {
                Channels = channels.ToArray(),
                Textures = textures.ToArray(),
                ByteSize = bytes,
            });

            // A new stroke is a new branch of history, so anything that was undone is
            // now unreachable and its surfaces are dead weight.
            ClearRedo();
            TrimUndoToBudget();
        }

        private void TrimUndoToBudget()
        {
            long total = 0;
            foreach (PaintUndoStep step in _undo)
                total += step.ByteSize;

            // The most recent step is never dropped, whatever the budget says. A history
            // of nothing at all is the one outcome an artist cannot work around, and a
            // single step still buys back the stroke that just went wrong.
            while (_undo.Count > 1 && total > _undoBudgetBytes)
            {
                total -= _undo[0].ByteSize;
                _undo[0].Release();
                _undo.RemoveAt(0);
            }
        }

        private void PerformUndo()
        {
            if (!CanUndo || _target == null)
                return;

            PaintUndoStep step = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);

            // The current state has to be captured BEFORE the step is applied, or redo
            // would have nothing to go forward to.
            PaintUndoStep forward = CaptureStepFor(step.Channels);

            if (ApplyStep(step))
            {
                if (forward != null)
                    _redo.Add(forward);
                else
                    ClearRedo();
            }
            else
            {
                // The maps have been reallocated under the history - a resolution or UV
                // set change. Those steps describe a layout that no longer exists.
                forward?.Release();
                ClearHistory();
            }

            step.Release();
            _target.FinishPaintStroke();
            SceneView.RepaintAll();
            Repaint();
        }

        private void PerformRedo()
        {
            if (!CanRedo || _target == null)
                return;

            PaintUndoStep step = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);

            PaintUndoStep backward = CaptureStepFor(step.Channels);

            if (ApplyStep(step))
            {
                if (backward != null)
                    _undo.Add(backward);
            }
            else
            {
                backward?.Release();
                ClearHistory();
            }

            step.Release();
            _target.FinishPaintStroke();
            SceneView.RepaintAll();
            Repaint();
        }

        /// <summary>The current contents of exactly these channels, as a step.</summary>
        private PaintUndoStep CaptureStepFor(FurDetailChannel[] channels)
        {
            if (channels == null || channels.Length == 0)
                return null;

            var textures = new RenderTexture[channels.Length];
            long bytes = 0;

            for (int i = 0; i < channels.Length; i++)
            {
                textures[i] = _target.CaptureDetailSnapshot(channels[i]);
                if (textures[i] == null)
                {
                    foreach (RenderTexture rt in textures)
                        FurController.ReleaseDetailSnapshot(rt);
                    return null;
                }
                bytes += _target.DetailSnapshotByteSize(channels[i]);
            }

            return new PaintUndoStep { Channels = (FurDetailChannel[])channels.Clone(),
                                       Textures = textures, ByteSize = bytes };
        }

        /// <summary>
        /// Puts every channel of a step back. All or nothing: a step half applied would
        /// leave the three maps describing different moments in time.
        /// </summary>
        private bool ApplyStep(PaintUndoStep step)
        {
            if (step?.Channels == null || step.Textures == null)
                return false;

            for (int i = 0; i < step.Channels.Length; i++)
                if (!_target.RestoreDetailSnapshot(step.Channels[i], step.Textures[i]))
                    return false;

            return true;
        }

        private void ClearRedo()
        {
            foreach (PaintUndoStep step in _redo)
                step.Release();
            _redo.Clear();
        }

        private void ClearHistory()
        {
            foreach (PaintUndoStep step in _undo)
                step.Release();
            _undo.Clear();
            ClearRedo();
        }

        private long HistoryByteSize()
        {
            long total = 0;
            foreach (PaintUndoStep step in _undo)
                total += step.ByteSize;
            foreach (PaintUndoStep step in _redo)
                total += step.ByteSize;
            return total;
        }

        private bool EnsureActiveMaps()
        {
            bool any = false;
            foreach (FurDetailChannel channel in AllChannels)
            {
                if (ChannelActive(channel))
                    any |= _target.EnsureDetailMap(channel);
            }

            return any;
        }

        /// <summary>
        /// The material value this channel scales, read straight off the fur material.
        ///
        /// Read from the material rather than from the controller's cached copy because
        /// that copy is only refreshed while rendering, and this window is often the first
        /// thing touched after a material is assigned.
        /// </summary>
        private float MaterialScalar(FurDetailChannel channel)
        {
            Material material = _target != null ? _target.FurMaterial : null;
            if (material == null)
                return 0f;

            int id = channel == FurDetailChannel.Length ? FurLengthId : ThicknessId;
            return material.HasProperty(id) ? material.GetFloat(id) : 0f;
        }

        /// <summary>
        /// Converts a value in the material's units into the 0..1 the map stores.
        ///
        /// The map covers 0 .. DetailScaleMax times the material value, so the material
        /// value itself lands in the MIDDLE of that range - which is why an unpainted map
        /// bakes mid-grey rather than white, and why a stroke can lengthen fur as well as
        /// shorten it.
        /// </summary>
        private float MapValue(FurDetailChannel channel, float value)
        {
            float materialValue = MaterialScalar(channel);
            if (materialValue <= 1e-6f)
                return 0f;

            return Mathf.Clamp01(value / (materialValue * FurController.DetailScaleMax));
        }

        // Distinct colours so a glance at the Scene view says what is being painted.
        private Color CursorColor()
        {
            if (_paintColor && !_paintLength && !_paintThickness)
                return _colorValue;

            if (_paintLength && !_paintThickness && !_paintColor)
                return new Color(0.4f, 1f, 0.5f);

            if (_paintThickness && !_paintLength && !_paintColor)
                return new Color(1f, 0.75f, 0.3f);

            if (_paintComb && !_paintLength && !_paintThickness && !_paintColor)
                return new Color(0.85f, 0.55f, 1f);

            return new Color(0.6f, 0.85f, 1f);
        }

        // ------------------------------------------------------------------ //
        //  Raycast proxy
        // ------------------------------------------------------------------ //
        private bool EnsureRaycastProxy()
        {
            Mesh mesh = _target.ActiveMesh;
            if (mesh == null || !mesh.isReadable)
                return false;

            if (_raycastProxy == null)
            {
                // Sweep first. This field is not serialized, so a script recompile drops
                // the reference while the native object - being HideAndDontSave - lives
                // on, orphaned and unreachable. Anything of this name that is not the
                // proxy we are about to make is exactly that, and nothing else will ever
                // come along to clean it up.
                SweepOrphanedProxies();

                _raycastProxy = new GameObject(RaycastProxyName)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                _raycastCollider = _raycastProxy.AddComponent<MeshCollider>();
                _raycastMesh = null;
            }

            // A skinned coat hands back the SAME Mesh object every frame, refilled by
            // BakeMesh - so a reference comparison can never notice the pose changing, and
            // a MeshCollider cooks its collision data once, when the mesh is assigned.
            //
            // Those two facts together were a silent aiming error: the collider kept
            // whatever pose the character was in when this window opened, while the coat on
            // screen went on following the skeleton. Move a bone and the cursor still
            // reported the old surface, so a stroke landed somewhere the model no longer
            // was - close enough to look like a brush that was merely inaccurate, and
            // wrong for a reason nothing on screen could reveal.
            //
            // The pose is cheap to summarise exactly, so it is summarised and compared
            // rather than trusted: any change re-cooks. Re-cooking is not free, which is
            // why it is gated on the pose rather than done every frame.
            int poseHash = ResolvePoseHash();

            if (!ReferenceEquals(_raycastMesh, mesh) || poseHash != _raycastPoseHash)
            {
                // Cleared first: assigning the same reference back is a no-op, so the
                // collider would keep the data it already cooked - which is the entire
                // thing being corrected here.
                _raycastCollider.sharedMesh = null;
                _raycastCollider.sharedMesh = mesh;
                _raycastMesh = mesh;
                _raycastPoseHash = poseHash;
            }

            // Kept in step every frame rather than parented: a skinned character rebakes
            // its mesh as it animates, and the matrix the fur is drawn with is the one the
            // strokes have to agree with.
            Matrix4x4 m = _target.ActiveObjectToWorld;
            _raycastProxy.transform.SetPositionAndRotation(m.GetColumn(3), m.rotation);
            _raycastProxy.transform.localScale = m.lossyScale;

            return true;
        }

        /// <summary>
        /// A number that changes whenever the baked mesh would come out in a different
        /// shape - which is the only thing the collider needs to be told about.
        ///
        /// The bones and the blend shapes are the whole of what BakeMesh reads, so hashing
        /// them is exact rather than approximate: no pose change can slip past it, and no
        /// unchanged pose can trigger a needless re-cook. Reading the vertices instead
        /// would be the obvious alternative and a bad one - mesh.vertices allocates a copy
        /// of the entire array, every frame, to answer a question sixty matrices already
        /// answer.
        ///
        /// Constant for an unskinned mesh, whose shape cannot change at all.
        /// </summary>
        private int ResolvePoseHash()
        {
            var skinned = _target.GetComponent<SkinnedMeshRenderer>();
            if (skinned == null)
                return 0;

            unchecked
            {
                int hash = 17;

                Transform[] bones = skinned.bones;
                if (bones != null)
                {
                    foreach (Transform bone in bones)
                    {
                        if (bone == null)
                            continue;

                        // Position and rotation together: a bone that only rotates moves
                        // every vertex it owns, and one that only translates moves them
                        // all equally. Either alone would miss half the poses.
                        hash = hash * 31 + bone.localPosition.GetHashCode();
                        hash = hash * 31 + bone.localRotation.GetHashCode();
                        hash = hash * 31 + bone.localScale.GetHashCode();
                    }
                }

                Mesh shared = skinned.sharedMesh;
                if (shared != null)
                {
                    int shapes = shared.blendShapeCount;
                    for (int i = 0; i < shapes; i++)
                        hash = hash * 31 + skinned.GetBlendShapeWeight(i).GetHashCode();
                }

                return hash;
            }
        }

        /// <summary>
        /// Destroys raycast proxies left behind by a previous incarnation of this window -
        /// see the note at the call site.
        /// </summary>
        private static void SweepOrphanedProxies()
        {
            foreach (GameObject candidate in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (candidate != null && candidate.name == RaycastProxyName)
                    DestroyImmediate(candidate);
            }
        }

        private void DestroyRaycastProxy()
        {
            if (_raycastProxy != null)
                DestroyImmediate(_raycastProxy);

            _raycastProxy = null;
            _raycastCollider = null;
            _raycastMesh = null;
        }

        // ------------------------------------------------------------------ //
        //  Saving
        // ------------------------------------------------------------------ //
        /// <summary>
        /// Writes every painted map out to a texture asset and assigns each as its
        /// channel's source, which is what makes the strokes outlive the render textures
        /// holding them.
        ///
        /// One folder prompt rather than one file prompt per channel: a stroke now writes
        /// up to three maps at once, so being asked to name three files in a row for what
        /// was a single act of painting is the wrong shape entirely.
        ///
        /// Assigning the results back matters as much as writing the files: without it the
        /// next rebake would return to the OLD sources and the artist would watch the work
        /// vanish immediately after saving it.
        /// </summary>
        private void SavePaintedMaps()
        {
            string absolute = EditorUtility.SaveFolderPanel("Save painted fur maps", "Assets", "");
            if (string.IsNullOrEmpty(absolute))
                return;

            string folder = FileUtil.GetProjectRelativePath(absolute.Replace('\\', '/'));
            if (string.IsNullOrEmpty(folder))
            {
                EditorUtility.DisplayDialog("Save fur maps",
                    "Pick a folder inside this project's Assets directory.", "OK");
                return;
            }

            Undo.RecordObject(_target, "Save Painted Fur Maps");

            int written = 0;
            foreach (FurDetailChannel channel in AllChannels)
            {
                if (!_target.HasUnsavedPaintIn(channel))
                    continue;

                if (!(_target.BakedDetailMap(channel) is RenderTexture rt))
                    continue;

                string path = Path.Combine(folder,
                    $"{_target.gameObject.name}_Fur{channel}.png").Replace('\\', '/');

                Texture2D saved = FurDetailMapExporter.WriteToAsset(
                    rt, path, channel == FurDetailChannel.Color);

                if (saved == null)
                    continue;

                switch (channel)
                {
                    case FurDetailChannel.Length:    _target.LengthMap = saved; break;
                    case FurDetailChannel.Thickness: _target.ThicknessMap = saved; break;
                    case FurDetailChannel.Color:     _target.ColorMap = saved; break;
                    case FurDetailChannel.Comb:      _target.CombMap = saved; break;
                }
                written++;
            }

            EditorUtility.SetDirty(_target);

            if (written == 0)
            {
                EditorUtility.DisplayDialog("Save fur maps",
                    "Nothing was written - no channel had unsaved strokes.", "OK");
            }

            // The assignments mark the maps dirty, so the next frame rebakes from the files
            // just written. That is the point: what is on screen afterwards is what was
            // actually saved, not the render textures it came from.
            SceneView.RepaintAll();
            Repaint();
        }

        // ------------------------------------------------------------------ //
        //  Layout helpers
        // ------------------------------------------------------------------ //
        private static void Section(string title)
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        }

        private static void Note(string message)
        {
            EditorGUILayout.LabelField(message, EditorStyles.wordWrappedMiniLabel);
        }
    }
}
