using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace FurRendering.EditorTools
{
    /// <summary>
    /// The first thing somebody sees after importing FURFLUX: what it is, the three steps
    /// to a furred object, and where everything else lives.
    ///
    /// Opens on its own ONCE PER VERSION - after the first import and after an update -
    /// and never again unless asked for from Tools > FURFLUX > Welcome. A window that pops
    /// up on every editor start is one people learn to close without reading.
    /// </summary>
    public sealed class FurWelcomeWindow : EditorWindow
    {
        private const string ShownVersionKey = "FURFLUX.WelcomeShownVersion";

        private Vector2 _scroll;
        private GUIStyle _title;
        private GUIStyle _subtitle;
        private GUIStyle _stepNumber;
        private GUIStyle _body;

        public static void ShowWindow()
        {
            var window = GetWindow<FurWelcomeWindow>(utility: true, title: "Welcome to FURFLUX", focus: true);
            window.minSize = new Vector2(460f, 560f);
            window.maxSize = new Vector2(640f, 900f);
            window.Show();
        }

        [InitializeOnLoadMethod]
        private static void ShowOncePerVersion()
        {
            // Never in batch mode or a CI build, where there is no one to read it and an
            // EditorWindow can stall the process.
            if (Application.isBatchMode)
                return;

            if (EditorPrefs.GetString(ShownVersionKey, "") == FurfluxInfo.Version)
                return;

            // Deferred: InitializeOnLoad runs mid-import, before the editor can host a window.
            EditorApplication.delayCall += () =>
            {
                EditorPrefs.SetString(ShownVersionKey, FurfluxInfo.Version);
                ShowWindow();
            };
        }

        private void OnSelectionChange() => Repaint();

        private void EnsureStyles()
        {
            if (_title != null)
                return;

            _title = new GUIStyle(EditorStyles.boldLabel) { fontSize = 22 };
            _subtitle = new GUIStyle(EditorStyles.label) { fontSize = 12 };
            _subtitle.normal.textColor = EditorStyles.centeredGreyMiniLabel.normal.textColor;

            _stepNumber = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 16,
                alignment = TextAnchor.UpperCenter,
            };

            _body = new GUIStyle(EditorStyles.wordWrappedLabel) { richText = true };
        }

        private void OnGUI()
        {
            EnsureStyles();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.Space(10f);

            DrawHeader();
            DrawProjectCheck();

            Section("Get started");
            DrawSteps();

            Section("Which fur type?");
            DrawTypeGuide();

            Section("Explore");
            DrawLinks();

            EditorGUILayout.Space(10f);
            EditorGUILayout.EndScrollView();
        }

        // ------------------------------------------------------------------ //
        private void DrawHeader()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(10f);

                Texture2D logo = FurfluxInfo.Logo;
                if (logo != null)
                    GUILayout.Label(logo, GUILayout.Width(64f), GUILayout.Height(64f));

                GUILayout.Space(8f);

                using (new EditorGUILayout.VerticalScope())
                {
                    GUILayout.Space(6f);
                    GUILayout.Label(FurfluxInfo.ProductName, _title);
                    GUILayout.Label($"{FurfluxInfo.Tagline}  ·  v{FurfluxInfo.Version}", _subtitle);
                }
            }
        }

        /// <summary>
        /// The two things that stop FURFLUX from rendering at all, checked up front rather
        /// than left to be discovered as an empty Scene view.
        /// </summary>
        private void DrawProjectCheck()
        {
            EditorGUILayout.Space(6f);

            if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset))
            {
                EditorGUILayout.HelpBox(
                    "No Universal Render Pipeline asset is active. FURFLUX draws through URP and " +
                    "renders nothing without it - assign a URP asset in Project Settings > Graphics.",
                    MessageType.Error);
                return;
            }

            if (!SystemInfo.supportsInstancing)
            {
                EditorGUILayout.HelpBox(
                    "This graphics device does not support GPU instancing, which every fur layer " +
                    "is drawn with.", MessageType.Error);
                return;
            }

            EditorGUILayout.HelpBox("URP is active - this project is ready for fur.", MessageType.Info);
        }

        private void DrawSteps()
        {
            bool canAdd = FurfluxMenu.AddFurValidate();

            Step(1, "Add fur to a mesh",
                "Select an object with a <b>MeshRenderer</b> or <b>SkinnedMeshRenderer</b> and add fur. " +
                "It gets a Fur Controller and its own material in <i>Assets/Fur Materials</i>. Also on " +
                "right-click in the Hierarchy, under FURFLUX.",
                () =>
                {
                    using (new EditorGUI.DisabledScope(!canAdd))
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Add Shell Fur"))
                            FurfluxMenu.AddFurToSelection(FurController.FurType.Shells);
                        if (GUILayout.Button("Add Strand Fur"))
                            FurfluxMenu.AddFurToSelection(FurController.FurType.Strands);
                    }

                    if (!canAdd)
                        EditorGUILayout.LabelField("Select a mesh in the scene first.", EditorStyles.miniLabel);
                });

            Step(2, "Tune the look on the material",
                "Fur Length, Density, Thickness and the root/tip colours live on the material. Every " +
                "value is <b>relative to the object's size</b>, so one material works on a mouse and a bear.",
                null);

            Step(3, "Paint regions and set the budget",
                "On the Fur Controller: shell/strand budget and distance LOD, motion, wetness and " +
                "collision. <b>Draw Fur...</b> opens a Scene-view brush for length, thickness, colour " +
                "and combing.",
                null);
        }

        private void DrawTypeGuide()
        {
            EditorGUILayout.LabelField(
                "<b>Shells</b> - stacked copies of the surface with a procedural hair pattern. Cheap per " +
                "hair, no readable mesh needed. Best for short, dense coats and for many creatures.",
                _body);

            EditorGUILayout.Space(2f);

            EditorGUILayout.LabelField(
                "<b>Strands</b> - one real ribbon per hair, skinned on the GPU. Holds up in close-ups " +
                "and silhouettes. Needs Read/Write enabled on the mesh. Best for hero characters.",
                _body);

            EditorGUILayout.Space(2f);
            EditorGUILayout.LabelField(
                "You can switch type at any time with the Fur Type dropdown at the top of the material.",
                EditorStyles.wordWrappedMiniLabel);
        }

        private void DrawLinks()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Demo Scene", GUILayout.Height(26f)))
                    FurfluxMenu.OpenDemoScene();
                if (GUILayout.Button("Showcase Scene", GUILayout.Height(26f)))
                    FurfluxMenu.OpenShowcaseScene();
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Manual"))
                    FurfluxInfo.OpenManual();
                if (GUILayout.Button("Read Me"))
                    FurfluxInfo.Open(FurfluxInfo.ReadmeGuid, "read-me");
                if (GUILayout.Button("License"))
                    FurfluxInfo.Open(FurfluxInfo.LicenseGuid, "license");
            }

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField(
                "Reopen this window any time from Tools > FURFLUX > Welcome.",
                EditorStyles.centeredGreyMiniLabel);
        }

        // ------------------------------------------------------------------ //
        private static void Section(string title)
        {
            EditorGUILayout.Space(12f);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);

            Rect line = GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(line, new Color(0.5f, 0.5f, 0.5f, 0.35f));
            EditorGUILayout.Space(4f);
        }

        private void Step(int number, string title, string body, System.Action extra)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(number.ToString(), _stepNumber, GUILayout.Width(24f));

                using (new EditorGUILayout.VerticalScope())
                {
                    EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
                    EditorGUILayout.LabelField(body, _body);
                    extra?.Invoke();
                }
            }

            EditorGUILayout.Space(6f);
        }
    }
}
