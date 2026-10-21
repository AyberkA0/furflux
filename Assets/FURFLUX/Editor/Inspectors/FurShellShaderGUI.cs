using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace FurRendering.EditorTools
{
    /// <summary>
    /// Material inspector for the fur shader.
    ///
    /// The shader carries around fifty properties across two fur types, and on any given
    /// coat perhaps a dozen of them matter. Unity's default inspector lays all fifty out
    /// flat, which means scrolling past forty controls to reach Fur Length and no signal at
    /// all about which of them the coat in front of you is actually using.
    ///
    /// So the layout is built around one question: what does somebody tuning a coat touch?
    ///   * FUR TYPE sits above everything, under no header. It is not one setting among
    ///     the others - it decides which of the others exist at all.
    ///   * Four short sections carry what gets tuned: the surface, the fur, how it moves,
    ///     how it is lit. Those are open by default and hold about a dozen controls
    ///     between them.
    ///   * WETNESS is a self-contained response that most coats never use, so it is shut.
    ///   * ADVANCED holds everything technical: the corrections with one right answer, the
    ///     wind's inner shape, the second half of the wetness response, the render state.
    ///     Shut, and most projects never open it.
    ///
    /// Anything the chosen fur type cannot use is DISABLED IN PLACE, never hidden, with a
    /// line underneath saying which types it does work in. Hiding it would be tidier and
    /// much worse: somebody who tuned a setting on a shell coat and then switched to
    /// strands needs to see that it is still there, still set, and no longer doing
    /// anything.
    ///
    /// Properties are looked up BY NAME rather than by walking the array in order, which
    /// is what stops this file from silently rearranging itself when a property is added
    /// to the shader, and lets a missing one be reported plainly instead of drawing the
    /// wrong slider under the right label.
    /// </summary>
    public sealed class FurShellShaderGUI : ShaderGUI
    {
        /// <summary>Which fur types a property does something in.</summary>
        [Flags]
        private enum Types
        {
            None = 0,
            Shells = 1 << 0,
            Strands = 1 << 1,
            All = Shells | Strands,
        }

        private static readonly GUIContent[] TypeNames =
        {
            new GUIContent("Shells"),
            new GUIContent("Strands"),
        };

        // One line each, shown under the selector. Deliberately about the TRADE rather than
        // about the technique: what somebody picking between them needs to know is what
        // each one costs and where each one falls apart.
        private static readonly string[] TypeSummaries =
        {
            "Concentric copies of the surface, one per layer, with the hair drawn as a " +
            "procedural pattern. Cheap per hair, expensive per layer. Needs no readable mesh " +
            "and follows blend shapes - but the hair is an illusion, so it thins out in a " +
            "close-up and at a silhouette.",

            "One ribbon per hair, planted on the surface and skinned on the GPU. " +
            "Real geometry: it holds up close and along a silhouette, and it costs per hair " +
            "instead of per layer. Needs a readable mesh (Read/Write enabled); follows the " +
            "skeleton and, with Follow Blend Shapes on the Fur Controller, the face rig too.",
        };

        /// <summary>
        /// Which types each property is live in. Anything absent is live in both.
        ///
        /// This table is the inspector's whole knowledge of the support matrix, and it is
        /// deliberately one flat list rather than a flag threaded through each Draw call:
        /// a property's support is a fact about the property, so it belongs somewhere it
        /// can be read off in one go and checked against the shader.
        /// </summary>
        private static readonly Dictionary<string, Types> Support = new Dictionary<string, Types>
        {
            // The strand PATTERN, resolved in the fragment stage. Shells resolve it across
            // the surface; a strand ribbon simply IS the hair, so it has no pattern to grow
            // on contact or to clump when wet.
            { "_WetClumpSize", Types.Shells },
            { "_WetClumpStrength", Types.Shells },
        };

        private readonly Dictionary<string, MaterialProperty> _properties =
            new Dictionary<string, MaterialProperty>();

        private MaterialEditor _editor;
        private Types _current = Types.Shells;

        public override void OnGUI(MaterialEditor materialEditor, MaterialProperty[] properties)
        {
            _editor = materialEditor;

            _properties.Clear();
            foreach (MaterialProperty property in properties)
                _properties[property.name] = property;

            FurInspectorSections.Banner("Fur Material");
            DrawFurType(materialEditor);

            DrawSurface();
            DrawFur();
            DrawMotion();
            DrawLighting();
            DrawWetness();
            DrawAdvanced(materialEditor);
        }

        // ------------------------------------------------------------------ //
        //  Fur type - alone at the top, under no header
        // ------------------------------------------------------------------ //
        private void DrawFurType(MaterialEditor materialEditor)
        {
            MaterialProperty type = Get("_FurType");
            if (type == null)
            {
                _current = Types.Shells;
                return;
            }

            int index = Mathf.Clamp(Mathf.RoundToInt(type.floatValue), 0, TypeNames.Length - 1);
            _current = IndexToType(index);

            EditorGUILayout.Space(4f);

            EditorGUI.showMixedValue = type.hasMixedValue;
            EditorGUI.BeginChangeCheck();

            int next = EditorGUILayout.Popup(
                new GUIContent("Fur Type",
                    "How this coat is built. It decides which of the settings below do " +
                    "anything at all - the ones the chosen type cannot use stay visible but " +
                    "are greyed out."),
                index, TypeNames);

            if (EditorGUI.EndChangeCheck())
            {
                materialEditor.RegisterPropertyChangeUndo("Fur Type");
                type.floatValue = next;
                index = next;
                _current = IndexToType(index);
            }

            EditorGUI.showMixedValue = false;

            // Keywords are what the shader actually branches on, and they can fall out of
            // step with the float - a material built by script, or one whose property was
            // set through Material.SetFloat, carries the value without the keyword.
            // Reconciled on every draw rather than only on change, because the drift is
            // exactly the case where nobody is about to touch the popup.
            if (!type.hasMixedValue)
            {
                ApplyKeywords(materialEditor, index);
                FurInspectorSections.Note(TypeSummaries[index]);
            }
        }

        private static Types IndexToType(int index)
        {
            switch (index)
            {
                case 1: return Types.Strands;
                default: return Types.Shells;
            }
        }

        private static void ApplyKeywords(MaterialEditor materialEditor, int index)
        {
            foreach (UnityEngine.Object target in materialEditor.targets)
            {
                if (target is not Material material)
                    continue;

                bool strands = index == 1;

                // Checked before writing. Enabling a keyword that is already on still
                // dirties the material, which would put every fur material in the project
                // into the next commit just for having been looked at.
                if (material.IsKeywordEnabled("_FURTYPE_STRANDS") != strands)
                    Toggle(material, "_FURTYPE_STRANDS", strands);
            }
        }

        private static void Toggle(Material material, string keyword, bool enabled)
        {
            if (enabled)
                material.EnableKeyword(keyword);
            else
                material.DisableKeyword(keyword);
        }

        // ------------------------------------------------------------------ //
        //  The four sections a coat is actually tuned in
        // ------------------------------------------------------------------ //
        private void DrawSurface()
        {
            if (!FurInspectorSections.Header("Mat.Surface", "Surface",
                    "the fur's own colour", openByDefault: true))
                return;

            Texture("_BaseMap", "_BaseColor");
            Property("_Smoothness");

            Property("_NormalMapToggle");
            if (IsOn("_NormalMapToggle"))
            {
                EditorGUI.indentLevel++;
                Texture("_BumpMap");
                Property("_NormalScale");
                EditorGUI.indentLevel--;
            }

            FurInspectorSections.Note(
                "This is the FUR's albedo, not the body's. The object's own renderer draws "
              + "its surface with its own material; this coat only ever draws hair.");
        }

        private void DrawFur()
        {
            string summary = Has("_FurLength")
                ? $"{Get("_FurLength").floatValue:0.###} m, density {Get("_Density").floatValue:0}"
                : null;

            if (!FurInspectorSections.Header("Mat.Fur", "Fur", summary, openByDefault: true))
                return;

            Property("_FurLength");
            Property("_Density", DensityLabel());
            Property("_Thickness");
            Property("_LengthRandom");
            Property("_Curl");
            DrawCurlNote();

            EditorGUILayout.Space(2f);
            Property("_RootColor");
            Property("_TipColor");
            DrawColorAlphaNote();
            DrawBandingPointer();

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Fuzz layer", EditorStyles.miniBoldLabel);
            Property("_FuzzAmount");
            DrawFuzzControls();

            EditorGUILayout.Space(2f);
            DrawDistanceNotes();

            FurInspectorSections.Note(DensityNote());
            WarnIfDensityIsZero();

            FurInspectorSections.Note(
                "Everything here is RELATIVE: length is a fraction of the object's size, density is " +
                "strands ACROSS it, thickness is a fraction of the spacing that implies. So one " +
                "material clothes a mouse and a bear at any import scale, and raising density makes " +
                "a coat finer rather than matted.");

            FurInspectorSections.Note(
                "Per-region variation - long tail, short muzzle, markings - lives on the Fur " +
                "Controller as Detail Maps, because it belongs to the object's unwrap rather than " +
                "to the material.");
        }

        private void DrawMotion()
        {
            string summary = Has("_WindStrength") && Get("_WindStrength").floatValue > 0f
                ? $"wind {Get("_WindStrength").floatValue:0.##}"
                : "still";

            if (!FurInspectorSections.Header("Mat.Motion", "Motion", summary, openByDefault: true))
                return;

            Property("_Stiffness");
            Property("_Gravity");

            EditorGUILayout.Space(2f);
            Property("_WindStrength");
            Property("_WindDirection");

            if (_current != Types.Shells)
            {
                FurInspectorSections.Note(
                    "A ribbon only bends where it has a joint. If gravity and wind leave hair looking " +
                    "stiff, raise Ribbon Segments on the Fur Controller.");
            }
        }

        private void DrawLighting()
        {
            if (!FurInspectorSections.Header("Mat.Lighting", "Lighting", null, openByDefault: true))
                return;

            Property("_Occlusion");
            Property("_SheenIntensity");
            Property("_RimIntensity");

            FurInspectorSections.Note(
                "Root Darkening is the shading toward the skin, which is most of what makes fur " +
                "read as deep. Sheen is the lengthways highlight along a strand; Backlight is what " +
                "a coat does lit from behind.");
        }

        private void DrawWetness()
        {
            if (!FurInspectorSections.Header("Mat.Wetness", "Wetness Response",
                    "what this coat does when soaked"))
                return;

            FurInspectorSections.Note(
                "The WET end of values whose dry end is set above. These describe what the coat is " +
                "MADE OF; how wet a creature is right now lives on its Fur Controller.");

            Property("_WetDarkening");
            Property("_WetLengthScale");
            Property("_WetThinning");
            Property("_WetClumpStrength");
            Property("_WetSmoothness");

            EditorGUILayout.Space(2f);
            Property("_GravityMultiplierOnWet");
            Property("_StiffnessMultiplierOnWet");
        }

        // ------------------------------------------------------------------ //
        //  Advanced
        // ------------------------------------------------------------------ //
        private void DrawAdvanced(MaterialEditor materialEditor)
        {
            if (!FurInspectorSections.Header("Mat.Advanced", "Advanced",
                    "corrections, render state, the rest of the response"))
                return;

            EditorGUILayout.LabelField("Strand shape", EditorStyles.miniBoldLabel);
            Property("_AlphaCutout");
            WarnIfCutoutErasesCoat();
            Property("_StrandJitter", JitterLabel());

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Wind shape", EditorStyles.miniBoldLabel);
            Property("_WindFrequency");
            Property("_WindTurbulence");
            Property("_WindGustiness");
            Property("_WindShelter");

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Lighting detail", EditorStyles.miniBoldLabel);
            Property("_OcclusionCurve");
            Property("_StrandNormalStrength");
            Property("_SheenColor");
            Property("_SheenExponent");
            Property("_RimColor");
            Property("_RimPower");

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Wetness detail", EditorStyles.miniBoldLabel);
            Property("_WetClumpSize");
            Property("_WetWindResponse");
            Property("_WetRimScale");
            Property("_WetSheenScale");
            Property("_WetOcclusionScale");

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Render state", EditorStyles.miniBoldLabel);
            DrawRenderState(materialEditor);
        }

        /// <summary>
        /// A cutout high enough that the coat stops rendering.
        ///
        /// Worth its own warning because the symptom - no fur, on a material that is
        /// otherwise set up correctly and is still submitting every hair - looks like a
        /// broken package rather than a setting. The slider stops at 0.9 for the same
        /// reason; this catches the range just below it.
        /// </summary>
        private void WarnIfCutoutErasesCoat()
        {
            if (!Has("_AlphaCutout") || Get("_AlphaCutout").floatValue < 0.8f)
                return;

            EditorGUILayout.HelpBox(
                "High enough to erase the coat. Coverage is discarded below this, and a strand " +
                "thinner than a pixel never reaches it - so the fur stops rendering while every " +
                "hair is still being drawn. Default is 0.35.",
                MessageType.Warning);

            if (GUILayout.Button("Reset to 0.35"))
                Get("_AlphaCutout").floatValue = 0.35f;
        }

        /// <summary>
        /// The two settings that only mean something once the fuzz layer is on.
        ///
        /// Disabled rather than hidden, the same way an unsupported property is: a slider
        /// that vanishes takes its value with it as far as the reader is concerned, and
        /// somebody who tuned the softness and then turned the amount to zero should be
        /// able to see that the softness is still where they left it.
        /// </summary>
        /// <summary>
        /// Says what the two colours' ALPHA does, but only when somebody has moved it.
        ///
        /// Silent at the default, because a note explaining a channel nobody has touched is
        /// noise in a panel this file is otherwise trying to keep short.
        /// </summary>
        /// <summary>
        /// Says, next to the two colours, that there is a version of them with more than
        /// two stops - and where it is.
        ///
        /// It is not here, and that is a wart worth signposting rather than hiding. Root
        /// and Tip are a two-stop gradient, and the thing that replaces them is a Gradient
        /// with as many stops as you like - which Unity can only serialize on an object,
        /// never in a shader property block. So it lives on the Fur Controller while its
        /// own two-stop ancestor lives here, and anybody looking for "more colours along
        /// the hair" looks in this panel first, finds two colour swatches, and concludes
        /// that two is all there is.
        /// </summary>
        /// <summary>
        /// Curl is the one strand value whose cost is not obvious, so it says so.
        /// </summary>
        private void DrawCurlNote()
        {
            if (!Has("_Curl") || Get("_Curl").floatValue <= 0f)
                return;

            FurInspectorSections.Note(
                _current == Types.Shells
                    ? "Each hair coils on its own phase. A shell coat resolves the coil from "
                    + "its layers, so a tight curl needs shells to resolve it - raise Shell "
                    + "Count on the Fur Controller if the coil breaks into steps."
                    : "Each hair coils on its own phase. A ribbon needs joints to coil "
                    + "through, so raise Ribbon Segments on the Fur Controller if the curl "
                    + "reads as a straight quad turned sideways.");
        }

        private void DrawBandingPointer()
        {
            if (BandingIsOverriding())
            {
                EditorGUILayout.HelpBox(
                    "The Fur Controller on this object is using a Band Gradient, so these two colours " +
                    "do nothing. Edit the hair's colour under Fur Controller > Hair Banding.",
                    MessageType.Info);
                return;
            }

            FurInspectorSections.Note(
                "Two stops: root and tip. For a banded hair - dark root, pale ring, dark tip - turn " +
                "on Hair Banding on the Fur Controller and author as many stops as you like. A " +
                "Gradient cannot live on a material, which is the only reason it is over there.");
        }

        // Whether any Fur Controller driving one of the selected materials has banding on.
        //
        // Cached on a timer rather than resolved per repaint: this runs from OnGUI, which
        // fires constantly, and the answer changes only when somebody ticks a box.
        private static double _bandingCheckedAt = -1.0;
        private static bool _bandingCached;

        private bool BandingIsOverriding()
        {
            const double refreshSeconds = 0.5;

            if (EditorApplication.timeSinceStartup - _bandingCheckedAt < refreshSeconds)
                return _bandingCached;

            _bandingCheckedAt = EditorApplication.timeSinceStartup;
            _bandingCached = false;

            if (_editor == null || _editor.targets == null)
                return false;

            var selected = new System.Collections.Generic.HashSet<UnityEngine.Object>(_editor.targets);

            foreach (FurController controller in UnityEngine.Object.FindObjectsByType<FurController>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (!controller.UseBandGradient)
                    continue;

                if (controller.FurMaterial != null && selected.Contains(controller.FurMaterial))
                {
                    _bandingCached = true;
                    break;
                }
            }

            return _bandingCached;
        }

        private void DrawColorAlphaNote()
        {
            bool rootFaded = Has("_RootColor") && Get("_RootColor").colorValue.a < 0.999f;
            bool tipFaded  = Has("_TipColor")  && Get("_TipColor").colorValue.a  < 0.999f;

            if (!rootFaded && !tipFaded)
                return;

            FurInspectorSections.Note(
                "Alpha is how solid the hair is at that height, interpolated along the strand like " +
                "the colour. Fading the TIP is the useful end: it removes the cut-off edge that " +
                "gives procedural fur away. Applies to shadows and depth too.");
        }

        private void DrawFuzzControls()
        {
            bool on = Has("_FuzzAmount") && Get("_FuzzAmount").floatValue > 0f;
            bool screen = Has("_FuzzMode") && Get("_FuzzMode").floatValue > 0.5f;

            using (new EditorGUI.DisabledScope(!on))
            {
                Property("_FuzzMode");

                // One slider, two jobs, and they are deliberately the same job: how far
                // past the coat the softness reaches, as a fraction of the fur's length.
                // The geometric layer gets there by growing its hairs; this one by
                // widening a blur. Relabelled rather than split so switching modes
                // compares like with like.
                Property("_FuzzExtent", screen
                    ? "Fuzz Reach (blur radius, of fur length)"
                    : "Fuzz Reach (of fur length)");

                // Strand widening is how the GEOMETRIC layer turns a hair into a blur
                // kernel. The screen-space layer blurs pixels and has no strand to widen,
                // so the slider would be a control that does nothing. Disabled rather than
                // hidden, for the reason given above DrawFuzzControls.
                using (new EditorGUI.DisabledScope(screen))
                    Property("_FuzzSoftness");
            }

            SyncFuzzStencil(screen);

            if (!on)
            {
                FurInspectorSections.Note(
                    "Off, and costing nothing while it is - the extra draw is not submitted at all.");
                return;
            }

            if (screen)
            {
                DrawScreenFuzzNotes();
                return;
            }

            FurInspectorSections.Note(
                "A second, softer copy of the coat over the sharp one: same hairs, longer and much " +
                "wider, blended instead of cut. What reaches past the coat is a soft edge a clipped " +
                "coat cannot have; what lands between hairs is fullness. Never casts shadows.");
        }

        /// <summary>
        /// Keeps the stencil write mask in step with the chosen mode.
        ///
        /// The mask is what actually switches the coat's stencil mark on and off, and it
        /// has to be a separate property from the mode because ShaderLab render state is
        /// resolved from a property and cannot read a mode enum. Nobody should have to
        /// know that, so the inspector keeps the pair consistent and never shows either.
        ///
        /// Written only when it is wrong, because writing every OnGUI would mark the
        /// material dirty on every repaint.
        /// </summary>
        private void SyncFuzzStencil(bool screen)
        {
            if (!Has("_FuzzStencilWriteMask") || !Has("_FuzzStencilRef"))
                return;

            MaterialProperty mask = Get("_FuzzStencilWriteMask");
            float wanted = screen ? Get("_FuzzStencilRef").floatValue : 0f;

            if (!Mathf.Approximately(mask.floatValue, wanted))
                mask.floatValue = wanted;
        }

        /// <summary>
        /// What the screen-space layer is, plus the one piece of project setup it needs.
        ///
        /// The setup check earns its place: with no renderer feature installed this mode
        /// is not subtly wrong, it does nothing at all, and there is nothing in the scene
        /// view to say so.
        /// </summary>
        private void DrawScreenFuzzNotes()
        {
            FurInspectorSections.Note(
                "The coat's own drawn PIXELS, blurred and laid back down only where the coat is not " +
                "- so the haze carries the hairs' real colours and lighting. The sharp hairs are " +
                "left as drawn. One full-screen pass instead of a second coat.");

            if (FurScreenFuzzSetup.IsInstalled())
                return;

            EditorGUILayout.HelpBox(
                "The Fur Screen Fuzz renderer feature is not on the active URP renderer, so this " +
                "mode is doing nothing. It is a full-screen pass and has to live on the renderer.", MessageType.Warning);

            if (GUILayout.Button("Add it to the active renderer"))
                FurScreenFuzzSetup.Install();
        }

        private void DrawDistanceNotes()
        {
            FurInspectorSections.Note(
                "Once hairs are too small to draw one by one, the coat is drawn as what they " +
                "average to: held to a minimum width, coverage kept where the count says, colour " +
                "handed down to the skin as the coat thins. Costs nothing close up.");
        }

        /// <summary>
        /// Unity's own material footer plus the cull mode.
        ///
        /// GPU Instancing is missing from it deliberately: the fur is drawn through
        /// Graphics.RenderMeshInstanced and does not render at all without it, so it is
        /// held on here rather than offered as a choice that has only one answer.
        /// </summary>
        private void DrawRenderState(MaterialEditor materialEditor)
        {
            Property("_Cull");

            bool culling = Has("_Cull") && !Mathf.Approximately(Get("_Cull").floatValue, 0f);
            if (culling)
            {
                EditorGUILayout.HelpBox(
                    "Fur on the far side sits on back-facing triangles and extends past the silhouette, " +
                    "so culling removes strands that should show over the shape's horizon. On strand " +
                    "fur it removes about half the hairs outright. Cull Off is recommended.",
                    MessageType.Warning);

                if (GUILayout.Button("Set Cull Mode to Off"))
                    Get("_Cull").floatValue = 0f;
            }

            // Not a setting, and it never was one. The fur is submitted through
            // Graphics.RenderMeshInstanced, which does not render at all without this - so
            // the checkbox had exactly one allowed position and an error box beside it
            // saying so. Held on here instead, and no longer drawn.
            var material = materialEditor.target as Material;
            if (material != null && !material.enableInstancing)
            {
                material.enableInstancing = true;
                EditorUtility.SetDirty(material);
            }

            EditorGUILayout.Space(2f);
            materialEditor.RenderQueueField();
            materialEditor.DoubleSidedGIField();
        }

        // ------------------------------------------------------------------ //
        //  Per-type wording
        // ------------------------------------------------------------------ //
        /// <summary>
        /// What _Density is counting, which is the one thing that changes with the type.
        ///
        /// The NUMBER means the same thing throughout - how finely the coat is divided -
        /// and every type squares it to get a count. What differs is the unit it is divided
        /// in, and that is a fact about the type rather than about the setting.
        /// </summary>
        private string DensityLabel()
        {
            return "Strand Density (strands across the object)";
        }

        private string JitterLabel()
        {
            // Shells have a cell to sit inside; a ribbon has only the direction its plane
            // faces. Same knob, same idea, and the label says which of the two it reaches.
            return _current == Types.Shells
                ? "Strand Placement Randomness"
                : "Strand Placement Randomness (ribbon twist)";
        }

        private string DensityNote()
        {
            switch (_current)
            {
                default:
                    return "How many strands lie ACROSS the object, squared for the count. " +
                           "Relative, like everything else about a strand, so the same material " +
                           "works on any size of creature at any import scale. Shell fur lays its " +
                           "pattern over the unwrap, scaled by the mesh's average meters per UV - " +
                           "so islands packed much tighter or looser than the average get finer " +
                           "or coarser strands.";
            }
        }

        /// <summary>
        /// Says so when the density has been left at zero.
        ///
        /// Zero is a legitimate value for the ribbon types - it is how a coat is switched
        /// off - but it is also what a half-finished material has in it, and the two are
        /// indistinguishable from the render: both are a bare object.
        /// </summary>
        private void WarnIfDensityIsZero()
        {
            MaterialProperty density = Get("_Density");
            if (_current == Types.Shells || density == null || density.hasMixedValue || density.floatValue > 0f)
                return;

            EditorGUILayout.HelpBox(
                "Density is zero, so no fur is drawn. That is a valid way to switch a coat off; if " +
                "it was not meant, this is the setting to raise.", MessageType.Warning);
        }

        // ------------------------------------------------------------------ //
        //  Support matrix
        // ------------------------------------------------------------------ //
        private static Types SupportOf(string name)
        {
            return Support.TryGetValue(name, out Types types) ? types : Types.All;
        }

        private bool IsLive(string name) => (SupportOf(name) & _current) != 0;

        private static string TypeName(Types type)
        {
            switch (type)
            {
                case Types.Strands: return "Strands";
                default: return "Shells";
            }
        }

        /// <summary>Human-readable list of the types a property is live in.</summary>
        private static string ListTypes(Types types)
        {
            var names = new List<string>(2);
            if ((types & Types.Shells) != 0) names.Add("Shells");
            if ((types & Types.Strands) != 0) names.Add("Strands");

            if (names.Count == 0)
                return "no fur type";
            if (names.Count == 1)
                return names[0];

            return string.Join(", ", names.GetRange(0, names.Count - 1)) + " and " + names[names.Count - 1];
        }

        /// <summary>
        /// The line that goes under a control the current type cannot use.
        ///
        /// It names both halves on purpose - what this type cannot do AND where the setting
        /// does work - because the second half is the actionable one. "Not available" alone
        /// leaves the reader to go and find out by switching types.
        /// </summary>
        private string UnsupportedNote(Types supported)
        {
            return $"Not available in {TypeName(_current)} fur. Works in: {ListTypes(supported)}.";
        }

        // ------------------------------------------------------------------ //
        //  Property plumbing
        // ------------------------------------------------------------------ //
        private bool Has(string name) => _properties.ContainsKey(name);

        private MaterialProperty Get(string name)
        {
            return _properties.TryGetValue(name, out MaterialProperty property) ? property : null;
        }

        private bool IsOn(string name)
        {
            MaterialProperty property = Get(name);
            return property != null && property.floatValue > 0.5f;
        }

        private void Property(string name) => Property(name, null);

        /// <summary>
        /// Draws one property by name, or says so when the shader no longer has it.
        ///
        /// Reporting rather than silently skipping: a property removed from the shader but
        /// still listed here is a bug in this file, and a section that quietly loses a
        /// slider is the kind of thing nobody notices for months.
        ///
        /// A property the current fur type cannot use is drawn DISABLED and annotated,
        /// never hidden - see the note at the top of the class. <paramref name="label"/>
        /// overrides the shader's display name, for the handful of settings that mean the
        /// same thing in every type but count it in a different unit.
        /// </summary>
        private void Property(string name, string label)
        {
            MaterialProperty property = Get(name);
            if (property == null)
            {
                EditorGUILayout.LabelField($"missing shader property: {name}", EditorStyles.miniLabel);
                return;
            }

            bool live = IsLive(name);

            using (new EditorGUI.DisabledScope(!live))
                _editor.ShaderProperty(property, label ?? property.displayName);

            if (!live)
                FurInspectorSections.Note(UnsupportedNote(SupportOf(name)));
        }

        /// <summary>Draws a texture slot, optionally with a colour swatch beside it.</summary>
        private void Texture(string textureName, string colorName = null)
        {
            MaterialProperty texture = Get(textureName);
            if (texture == null)
            {
                EditorGUILayout.LabelField($"missing shader property: {textureName}", EditorStyles.miniLabel);
                return;
            }

            MaterialProperty color = colorName != null ? Get(colorName) : null;
            bool live = IsLive(textureName);

            using (new EditorGUI.DisabledScope(!live))
            {
                if (color != null)
                    _editor.TexturePropertySingleLine(new GUIContent(texture.displayName), texture, color);
                else
                    _editor.TexturePropertySingleLine(new GUIContent(texture.displayName), texture);

                // Tiling/offset only where the shader actually declares it - the detail and
                // wetness maps are [NoScaleOffset] because they are indexed by raw UV0.
                if ((texture.propertyFlags & UnityEngine.Rendering.ShaderPropertyFlags.NoScaleOffset) == 0)
                {
                    EditorGUI.indentLevel++;
                    _editor.TextureScaleOffsetProperty(texture);
                    EditorGUI.indentLevel--;
                }
            }

            if (!live)
                FurInspectorSections.Note(UnsupportedNote(SupportOf(textureName)));
        }
    }
}
