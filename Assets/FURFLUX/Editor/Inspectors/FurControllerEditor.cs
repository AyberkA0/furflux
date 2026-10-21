using UnityEditor;
using UnityEngine;

namespace FurRendering.EditorTools
{
    /// <summary>
    /// Inspector for <see cref="FurController"/>.
    ///
    /// The component carries around fifty settings across six independent subsystems, and
    /// a given coat needs perhaps eight of them. So the layout answers one question: what
    /// does somebody setting a creature up actually touch?
    ///
    ///   * SOURCE, QUALITY and MOTION are open by default and hold about ten controls
    ///     between them. Assign a material, choose what it may cost, decide whether the
    ///     fur reacts to movement, done.
    ///   * WETNESS, DETAIL MAPS and COLLISION are whole features most coats never switch
    ///     on. Shut, and each one says what turning it on would cost.
    ///   * ADVANCED holds the tuning behind all of the above - the spring constants, the
    ///     LOD's inner thresholds, the shader references. Shut, and most projects never
    ///     open it.
    ///   * LIVE reports what is actually being drawn, which is the fastest way to tell a
    ///     coat that is too sparse from one that is being held back by its budget.
    ///
    /// Within a section, a subsystem that is switched off hides its settings entirely and
    /// says what switching it on would cost. Leaving them visible invites tuning
    /// parameters that nothing is reading - and this package has already seen exactly that
    /// happen.
    /// </summary>
    [CustomEditor(typeof(FurController))]
    [CanEditMultipleObjects]
    public sealed class FurControllerEditor : Editor
    {
        // Source
        private SerializedProperty _furMaterial;
        private SerializedProperty _preset;

        // Shadows
        private SerializedProperty _shadowMode;
        private SerializedProperty _receiveShadows;

        // LOD
        private SerializedProperty _enableLod;
        private SerializedProperty _shellCount;
        private SerializedProperty _shellCountByDistance;
        private SerializedProperty _hardCullDistance;

        // Strand fur
        private SerializedProperty _maxStrandCount;
        private SerializedProperty _ribbonSegments;

        private SerializedProperty _ribbonDensityByDistance;
        private SerializedProperty _ribbonSeed;

        // Motion
        private SerializedProperty _motionInertia;
        private SerializedProperty _inertiaMode;
        private SerializedProperty _inertiaBones;
        private SerializedProperty _boneInfluenceRadius;
        private SerializedProperty _dragStrength;
        private SerializedProperty _accelerationResponse;
        private SerializedProperty _inertiaDamping;
        private SerializedProperty _inertiaDampingRatio;
        private SerializedProperty _angularInertiaScale;

        // Wetness
        private SerializedProperty _wetness;
        private SerializedProperty _enableWetnessMap;
        private SerializedProperty _waterLayers;
        private SerializedProperty _wetnessMapResolution;
        private SerializedProperty _enableDrying;
        private SerializedProperty _wettingRate;
        private SerializedProperty _dryingRate;
        private SerializedProperty _contactFeather;
        private SerializedProperty _wetnessSplatShader;

        // Detail maps
        private SerializedProperty _lengthMap;
        private SerializedProperty _thicknessMap;
        private SerializedProperty _colorMap;
        private SerializedProperty _combMap;
        private SerializedProperty _detailMapResolution;
        private SerializedProperty _detailUvSet;
        private SerializedProperty _useBandGradient;
        private SerializedProperty _bandGradient;
        private SerializedProperty _colorMapScaleOffset;
        private SerializedProperty _colorMapFill;
        private SerializedProperty _colorMapTint;
        private SerializedProperty _colorMapStrength;
        private SerializedProperty _attributeBakeShader;

        // Collision
        private SerializedProperty _enableCollision;
        private SerializedProperty _collisionLayers;
        private SerializedProperty _collisionStrength;
        private SerializedProperty _collisionMargin;
        private SerializedProperty _collisionSpread;
        private SerializedProperty _maxSimultaneousColliders;

        private void OnEnable()
        {
            _furMaterial = serializedObject.FindProperty("furMaterial");
            _preset = serializedObject.FindProperty("preset");

            _shadowMode = serializedObject.FindProperty("shadowMode");
            _receiveShadows = serializedObject.FindProperty("receiveShadows");

            _enableLod = serializedObject.FindProperty("enableLod");
            _shellCount = serializedObject.FindProperty("shellCount");
            _shellCountByDistance = serializedObject.FindProperty("shellCountByDistance");
            _hardCullDistance = serializedObject.FindProperty("hardCullDistance");

            _maxStrandCount = serializedObject.FindProperty("maxStrandCount");
            _ribbonSegments = serializedObject.FindProperty("ribbonSegments");
            _ribbonDensityByDistance = serializedObject.FindProperty("ribbonDensityByDistance");
            _ribbonSeed = serializedObject.FindProperty("ribbonSeed");

            _motionInertia = serializedObject.FindProperty("motionInertia");
            _inertiaMode = serializedObject.FindProperty("inertiaMode");
            _inertiaBones = serializedObject.FindProperty("inertiaBones");
            _boneInfluenceRadius = serializedObject.FindProperty("boneInfluenceRadius");
            _dragStrength = serializedObject.FindProperty("dragStrength");
            _accelerationResponse = serializedObject.FindProperty("accelerationResponse");
            _inertiaDamping = serializedObject.FindProperty("inertiaDamping");
            _inertiaDampingRatio = serializedObject.FindProperty("inertiaDampingRatio");
            _angularInertiaScale = serializedObject.FindProperty("angularInertiaScale");

            _wetness = serializedObject.FindProperty("wetness");
            _enableWetnessMap = serializedObject.FindProperty("enableWetnessMap");
            _waterLayers = serializedObject.FindProperty("waterLayers");
            _wetnessMapResolution = serializedObject.FindProperty("wetnessMapResolution");
            _enableDrying = serializedObject.FindProperty("enableDrying");
            _wettingRate = serializedObject.FindProperty("wettingRate");
            _dryingRate = serializedObject.FindProperty("dryingRate");
            _contactFeather = serializedObject.FindProperty("contactFeather");
            _wetnessSplatShader = serializedObject.FindProperty("wetnessSplatShader");

            _lengthMap = serializedObject.FindProperty("lengthMap");
            _thicknessMap = serializedObject.FindProperty("thicknessMap");
            _colorMap = serializedObject.FindProperty("colorMap");
            _combMap = serializedObject.FindProperty("combMap");
            _detailMapResolution = serializedObject.FindProperty("detailMapResolution");
            _detailUvSet = serializedObject.FindProperty("detailUvSet");
            _useBandGradient = serializedObject.FindProperty("useBandGradient");
            _bandGradient = serializedObject.FindProperty("bandGradient");
            _colorMapScaleOffset = serializedObject.FindProperty("colorMapScaleOffset");
            _colorMapFill = serializedObject.FindProperty("colorMapFill");
            _colorMapTint = serializedObject.FindProperty("colorMapTint");
            _colorMapStrength = serializedObject.FindProperty("colorMapStrength");
            _attributeBakeShader = serializedObject.FindProperty("attributeBakeShader");

            _enableCollision = serializedObject.FindProperty("enableCollision");
            _collisionLayers = serializedObject.FindProperty("collisionLayers");
            _collisionStrength = serializedObject.FindProperty("collisionStrength");
            _collisionMargin = serializedObject.FindProperty("collisionMargin");
            _collisionSpread = serializedObject.FindProperty("collisionSpread");
            _maxSimultaneousColliders = serializedObject.FindProperty("maxSimultaneousColliders");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            FurInspectorSections.Banner("Fur Controller");
            DrawSource();
            DrawPreset();
            DrawQuality();
            DrawMotion();
            DrawWetness();
            DrawDetailMaps();
            DrawHairBanding();
            DrawCollision();
            DrawAdvanced();
            DrawLive();

            serializedObject.ApplyModifiedProperties();
        }

        // ------------------------------------------------------------------ //
        //  Source
        // ------------------------------------------------------------------ //
        private void DrawSource()
        {
            var material = _furMaterial.objectReferenceValue as Material;
            string summary = material != null ? material.name : "no material";

            if (!FurInspectorSections.Header("Source", "Source", summary, openByDefault: true))
                return;

            EditorGUILayout.PropertyField(_furMaterial, new GUIContent("Fur Material"));

            if (material == null && !_furMaterial.hasMultipleDifferentValues)
            {
                EditorGUILayout.HelpBox(
                    "No fur material, so nothing is drawn. Assign one using the " +
                    "FURFLUX/Fur Lit shader.", MessageType.Warning);
                return;
            }

            FurInspectorSections.Note(
                "How the coat LOOKS is on that material. What lives here is how this object is " +
                "driven: what it may cost, how it moves, what it reacts to.");

            DrawResolvedSize(material);
            WarnIfMeshUnreadable();
        }

        // ------------------------------------------------------------------ //
        //  Preset
        // ------------------------------------------------------------------ //
        /// <summary>
        /// A starting point for the coat, chosen and then explicitly applied.
        ///
        /// The two steps are separate because the write is not confined to this object.
        /// A preset covers the component AND the material, a material is a shared asset,
        /// and so pressing Apply can change the look of objects that are not the one
        /// selected. A dropdown somebody is browsing must not be able to do that; a
        /// button they pressed after reading what it says may.
        /// </summary>
        private void DrawPreset()
        {
            var material = _furMaterial.objectReferenceValue as Material;
            var selected = (FurPresetId)_preset.enumValueIndex;
            FurPreset chosen = FurPreset.Get(selected);

            string summary = chosen != null ? chosen.DisplayName : "none";
            if (!FurInspectorSections.Header("Preset", "Preset", summary))
                return;

            EditorGUILayout.PropertyField(_preset, new GUIContent("Preset"));

            FurInspectorSections.Note(
                "A coat is authored across the material and this component, tuned against each " +
                "other. A preset is the pair, so applying one writes both.");

            if (chosen == null)
            {
                EditorGUILayout.HelpBox(
                    "No preset selected. Choose one and press Apply to seed this coat; " +
                    "nothing is written until you do.", MessageType.None);
                return;
            }

            // The material is where half the preset lands, so no material means half an
            // apply - which would leave the component describing a coat nothing is
            // drawing. Refused rather than half-done.
            if (material == null)
            {
                EditorGUILayout.HelpBox(
                    "No fur material assigned, so half of this preset has nowhere to go. " +
                    "Assign one first.", MessageType.Warning);
                return;
            }

            // Worth naming every time rather than once in a tooltip: this is the only
            // button in the package whose effect can leave the selected object.
            int sharing = CountControllersSharing(material);
            if (sharing > 1)
            {
                EditorGUILayout.HelpBox(
                    $"'{material.name}' is used by {sharing} fur objects in this scene. " +
                    "Applying rewrites the material, so all of them change. Duplicate the " +
                    "material first if only this one should move.", MessageType.Warning);
            }

            EditorGUILayout.Space(2f);
            using (new EditorGUI.DisabledScope(serializedObject.isEditingMultipleObjects))
            {
                if (GUILayout.Button($"Apply Preset  -  {chosen.DisplayName}", GUILayout.Height(24f)))
                    ApplySelectedPreset(chosen, material);
            }

            FurInspectorSections.Note(
                "Leaves alone what a preset cannot know: the material reference, painted maps, " +
                "inertia bones and layer masks.");
        }

        /// <summary>
        /// How many enabled fur controllers in the open scenes point at this material.
        /// Used only to warn, so an approximate answer from the loaded scenes is the
        /// right scope - assets in unopened scenes are exactly the case the warning
        /// cannot cover and does not claim to.
        /// </summary>
        private static int CountControllersSharing(Material material)
        {
            var all = UnityEngine.Object.FindObjectsByType<FurController>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            int count = 0;
            foreach (var c in all)
                if (c.FurMaterial == material)
                    count++;

            return count;
        }

        /// <summary>
        /// Applies to every selected controller, as one undo step covering the
        /// components and the material together.
        ///
        /// Both halves are registered BEFORE either is written. Registering the material
        /// after the component would leave an undo that restores the component's numbers
        /// while the material keeps the preset's, which is a state neither the user nor
        /// the preset ever asked for.
        /// </summary>
        private void ApplySelectedPreset(FurPreset chosen, Material material)
        {
            Undo.SetCurrentGroupName("Apply Fur Preset - " + chosen.DisplayName);
            int group = Undo.GetCurrentGroup();

            Undo.RecordObject(material, "Apply Fur Preset");

            foreach (var t in targets)
            {
                var controller = t as FurController;
                if (controller == null)
                    continue;

                Undo.RecordObject(controller, "Apply Fur Preset");
                controller.ApplyPresetToComponent(chosen);
                EditorUtility.SetDirty(controller);
            }

            chosen.ApplyTo(material);
            EditorUtility.SetDirty(material);

            Undo.CollapseUndoOperations(group);

            // The serialized object is holding values from before the write; without this
            // the inspector redraws the old ones and ApplyModifiedProperties at the end
            // of OnInspectorGUI puts them straight back.
            serializedObject.Update();
        }

        /// <summary>
        /// What the material's relative settings come to on THIS object, in millimeters.
        ///
        /// Relative settings are what make a material portable - the same one clothes a
        /// mouse and a bear - but they cost the artist the one thing an absolute number
        /// gave for free: knowing how long the fur actually is. This is that number, and
        /// it belongs here rather than on the material because the material cannot know
        /// which object is asking.
        /// </summary>
        private void DrawResolvedSize(Material material)
        {
            if (serializedObject.isEditingMultipleObjects || material == null
                || !material.HasProperty("_FurLength") || !material.HasProperty("_Density"))
                return;

            var controller = target as FurController;
            Mesh mesh = controller != null ? ResolveRootMesh(controller) : null;
            if (controller == null)
                return;

            Vector3 s = controller.transform.lossyScale;
            float areaScale = (Mathf.Abs(s.x * s.y) + Mathf.Abs(s.y * s.z) + Mathf.Abs(s.z * s.x)) / 3f;
            float linear = Mathf.Sqrt(Mathf.Max(areaScale, 1e-8f));

            float size = mesh != null
                ? mesh.bounds.extents.magnitude * linear
                : controller.GetComponent<Renderer>() != null
                    ? controller.GetComponent<Renderer>().bounds.extents.magnitude
                    : 1f;

            float lengthMm = material.GetFloat("_FurLength") * size * 1000f;
            float spacingMm = size / Mathf.Max(material.GetFloat("_Density"), 1e-4f) * 1000f;

            FurInspectorSections.Note(
                $"On this object: {size * 100f:0.#} cm across, so the material gives fur " +
                $"{lengthMm:0.##} mm long with {spacingMm:0.##} mm between strands.");
        }

        /// <summary>
        /// The one setup mistake that produces no fur and no explanation.
        ///
        /// Both ribbon types sample the mesh on the CPU to place hair roots, so a mesh with
        /// Read/Write off draws nothing at all. It is worth an error rather than a note:
        /// nothing in the scene view says why, and the import setting is three panels away.
        /// </summary>
        private void WarnIfMeshUnreadable()
        {
            if (serializedObject.isEditingMultipleObjects || ResolveFurType() == FurController.FurType.Shells)
                return;

            var controller = target as FurController;
            Mesh mesh = controller != null ? ResolveRootMesh(controller) : null;

            if (mesh == null || mesh.isReadable)
                return;

            EditorGUILayout.HelpBox(
                $"'{mesh.name}' has Read/Write disabled. Strand fur samples the mesh " +
                "on the CPU to place hair roots, so nothing will be drawn until Read/Write is " +
                "turned on in the mesh's import settings. Shells fur does not need it.",
                MessageType.Error);
        }

        // ------------------------------------------------------------------ //
        //  Quality - what this object is allowed to cost
        // ------------------------------------------------------------------ //
        private void DrawQuality()
        {
            bool ribbons = ResolveFurType() != FurController.FurType.Shells;

            string summary = _enableLod.hasMultipleDifferentValues ? "-"
                : _enableLod.boolValue ? "LOD by distance"
                : ribbons ? "fixed" : $"fixed, {_shellCount.intValue} shells";

            if (!FurInspectorSections.Header("Quality", "Quality", summary, openByDefault: true))
                return;

            if (ribbons)
            {
                EditorGUILayout.PropertyField(
                    _maxStrandCount, new GUIContent("Max Strands"));

                EditorGUILayout.PropertyField(_ribbonSegments, new GUIContent("Ribbon Segments"));

                FurInspectorSections.Note(
                    "The ceiling on what this object may spend. How DENSE the coat is lives on the " +
                    "material as Strand Density - whichever asks for fewer wins.");
            }
            else if (!_enableLod.boolValue)
            {
                EditorGUILayout.IntSlider(_shellCount, 1, FurController.MaxShellCount,
                    new GUIContent("Shell Count"));
            }

            EditorGUILayout.Space(2f);
            EditorGUILayout.PropertyField(_enableLod, new GUIContent("Reduce With Distance"));

            if (_enableLod.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(
                    ribbons ? _ribbonDensityByDistance : _shellCountByDistance,
                    new GUIContent(ribbons ? "Ribbons by Distance" : "Shells by Distance"));
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(2f);
            EditorGUILayout.PropertyField(_shadowMode, new GUIContent("Shadows"));
            EditorGUILayout.PropertyField(_receiveShadows);

            WarnIfBudgetIsBinding();
        }

        /// <summary>
        /// Says when the ceiling is what is holding the coat back.
        ///
        /// A coat pinned to its budget looks exactly like a coat whose density is too low,
        /// and the two want opposite fixes - so which one is happening has to be stated
        /// rather than worked out from the render.
        /// </summary>
        private void WarnIfBudgetIsBinding()
        {
            if (serializedObject.isEditingMultipleObjects)
                return;

            var controller = target as FurController;
            if (controller == null || controller.ActiveRibbonCount <= 0
                || controller.RequestedRibbonCount <= controller.ActiveRibbonCount)
                return;

            EditorGUILayout.HelpBox(
                $"The material's density asks for {controller.RequestedRibbonCount:n0} over this " +
                $"object's surface, and the ceiling above is holding it to " +
                $"{controller.ActiveRibbonCount:n0}. Raise the ceiling for a fuller coat, or " +
                "lower Strand Density on the material so the two agree.",
                MessageType.Info);
        }

        // ------------------------------------------------------------------ //
        //  Motion
        // ------------------------------------------------------------------ //
        private void DrawMotion()
        {
            string summary = _motionInertia.hasMultipleDifferentValues ? "-"
                : FurInspectorSections.OnOff(_motionInertia.boolValue, "reacts to movement", "still");

            if (!FurInspectorSections.Header("Motion", "Motion", summary, openByDefault: true))
                return;

            EditorGUILayout.PropertyField(_motionInertia, new GUIContent("React To Movement"));

            if (!_motionInertia.boolValue)
            {
                FurInspectorSections.Note(
                    "Off, the coat holds its rest pose and answers only gravity and wind. On, it " +
                    "streams backward while the object travels and overshoots when it stops.");
                return;
            }

            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(_dragStrength, new GUIContent("Drag (while moving)"));
            EditorGUILayout.PropertyField(_accelerationResponse, new GUIContent("Throw (on start and stop)"));
            EditorGUILayout.PropertyField(_inertiaDamping, new GUIContent("Recovery Speed"));
            EditorGUI.indentLevel--;

            FurInspectorSections.Note(
                "Drag follows speed, so it holds fur swept back while the object moves. Throw " +
                "follows acceleration - zero at constant speed however fast, largest on a start or " +
                "stop - which is why its useful values are far smaller.");

            if (_inertiaMode.enumValueIndex == (int)FurController.FurInertiaMode.TransformAndBones)
                DrawInertiaBones();
        }

        private void DrawInertiaBones()
        {
            EditorGUILayout.Space(2f);
            EditorGUILayout.PropertyField(_inertiaBones, new GUIContent("Motion Bones"), true);
            EditorGUILayout.PropertyField(_boneInfluenceRadius, new GUIContent("Bone Reach"));
        }

        // ------------------------------------------------------------------ //
        //  Wetness
        // ------------------------------------------------------------------ //
        private void DrawWetness()
        {
            string summary;
            if (_wetness.hasMultipleDifferentValues)
                summary = "-";
            else if (_enableWetnessMap.boolValue)
                summary = "reacts to water";
            else
                summary = _wetness.floatValue > 0f ? $"{_wetness.floatValue:0.##} wet" : "dry";

            if (!FurInspectorSections.Header("Wetness", "Wetness", summary))
                return;

            EditorGUILayout.PropertyField(_wetness, new GUIContent("Global Wetness"));
            EditorGUILayout.PropertyField(_enableWetnessMap, new GUIContent("React To Water"));

            if (!_enableWetnessMap.boolValue)
            {
                FurInspectorSections.Note(
                    "Off, no map is allocated and the shader skips a fetch that runs per vertex per " +
                    "shell; Global Wetness above still works. On, only what actually touches water gets " +
                    "wet, and stays wet after leaving.");
                return;
            }

            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(_waterLayers);
            EditorGUILayout.PropertyField(_wettingRate, new GUIContent("Soaks In"));
            EditorGUILayout.PropertyField(_enableDrying, new GUIContent("Dries Out"));
            using (new EditorGUI.DisabledScope(!_enableDrying.boolValue))
                EditorGUILayout.PropertyField(_dryingRate, new GUIContent("Drying Rate"));
            EditorGUI.indentLevel--;

            if (_waterLayers.intValue == 0 && !_waterLayers.hasMultipleDifferentValues)
            {
                EditorGUILayout.HelpBox(
                    "Water Layers is empty, so nothing can wet this fur. Put your water " +
                    "volumes on a layer and select it here.", MessageType.Warning);
            }
        }

        // ------------------------------------------------------------------ //
        //  Detail maps
        // ------------------------------------------------------------------ //
        // One heading for all three maps. They are the same mechanism reached three ways -
        // a texture in this object's own unwrap multiplied into a material value - and
        // splitting them apart would hide that the cost model, the UV requirement and the
        // rebake rules are identical for each.
        private void DrawDetailMaps()
        {
            int active = 0;
            if (_lengthMap.objectReferenceValue != null) active++;
            if (_thicknessMap.objectReferenceValue != null) active++;
            if (_colorMap.objectReferenceValue != null) active++;
            if (_combMap.objectReferenceValue != null) active++;

            string summary = active == 0 ? "none" : $"{active} of 4";

            if (!serializedObject.isEditingMultipleObjects && ((FurController)target).HasUnsavedPaint)
                summary += " - unsaved paint";

            if (!FurInspectorSections.Header("Detail Maps", "Detail Maps", summary))
                return;

            FurInspectorSections.Note(
                "Vary the coat ACROSS THE SURFACE, where the material states one value for the " +
                "whole object. Each is a multiplier: an empty slot costs nothing and leaves the " +
                "material's value alone.");

            EditorGUILayout.Space(2f);
            DrawMapSlot(_lengthMap, "Length (R)", FurDetailChannel.Length,
                "1 = the material's Fur Length, 0 = shaved bare.");
            DrawMapSlot(_thicknessMap, "Thickness (R)", FurDetailChannel.Thickness,
                "Strand WIDTH, not count. 1 = the material's thickness, 0 = no strands.");
            DrawMapSlot(_colorMap, "Color (RGB)", FurDetailChannel.Color,
                "Multiplies the albedo, alongside the material's Root/Tip gradient.");
            DrawMapSlot(_combMap, "Comb (RG)", FurDetailChannel.Comb,
                "Which way the hair LIES, as a direction in the surface's own frame. " +
                "Flat grey is uncombed. Painted with the Comb brush in Draw Fur - drag " +
                "across the coat and it lies down the way you dragged.");

            EditorGUILayout.Space(2f);
            EditorGUILayout.PropertyField(_colorMapScaleOffset,
                new GUIContent("Color Tiling / Offset", "x,y scale and z,w offset for the colour map's source."));
            EditorGUILayout.PropertyField(_colorMapFill,
                new GUIContent("Color Fill", "What the colour map holds where no texture reaches."));
            EditorGUILayout.PropertyField(_colorMapTint, new GUIContent("Color Tint"));
            EditorGUILayout.PropertyField(_colorMapStrength, new GUIContent("Color Strength"));

            DrawMatchMeshColorsButton();

            if (active > 0)
                DrawUnwrapAdvice();

            DrawPaintButton();
        }


        // ------------------------------------------------------------------ //
        //  Hair banding
        // ------------------------------------------------------------------ //
        // Its own section rather than a corner of Detail Maps, because it is the OTHER
        // axis. The maps vary the coat across the surface - where on the animal. This
        // varies it along one hair - where on the strand. Filing them together reads as
        // though banding were a fourth map, and people then look for a texture slot.
        private void DrawHairBanding()
        {
            bool on = _useBandGradient.boolValue;

            if (!FurInspectorSections.Header("Hair Banding", "Hair Banding",
                    on ? "gradient" : "root / tip"))
                return;

            EditorGUILayout.PropertyField(_useBandGradient, new GUIContent("Use Band Gradient"));

            if (!_useBandGradient.boolValue)
            {
                FurInspectorSections.Note(
                    "Off, the hair takes the material's Root and Tip Color - a two-stop ramp. On, it " +
                    "gets as many stops as you like: a dark root with a pale ring through the middle is " +
                    "most of what makes an animal read as that animal.");
                return;
            }

            EditorGUILayout.PropertyField(_bandGradient, new GUIContent("Band Gradient"));

            FurInspectorSections.Note(
                "Left is the root, right is the tip. RGB multiplies the albedo, so white is neutral " +
                "and above 1 brightens. Alpha is how solid the hair is at that height; fading the " +
                "tip removes the cut-off edge that gives procedural fur away.");

            FurInspectorSections.Note(
                "While this is on, the material's Root Color and Tip Color do nothing.");
        }

        /// <summary>
        /// Takes the colours off the model's OWN material and gives them to the coat.
        ///
        /// The fur's colour map is a per-region multiplier over the same UV0 the model is
        /// textured on, so the model's albedo is already in the right space: assigning it is
        /// all "match the mesh" needs. Nothing is baked or copied, so this costs no memory
        /// and stays live if the source texture is re-imported.
        ///
        /// The material's base COLOUR goes to the tint rather than being multiplied into the
        /// texture, for the same reason: the two together are what the model's albedo is, and
        /// keeping them apart leaves both adjustable afterwards.
        /// </summary>
        private void DrawMatchMeshColorsButton()
        {
            EditorGUILayout.Space(2f);

            using (new EditorGUI.DisabledScope(serializedObject.isEditingMultipleObjects))
            {
                if (GUILayout.Button(new GUIContent("Match Mesh Colours",
                        "Point the fur's colour map at the model's own albedo, so the coat " +
                        "takes the colours the mesh already has.")))
                    MatchMeshColors();
            }

            if (serializedObject.isEditingMultipleObjects)
                return;

            // The coat's albedo is the material's Base Colour times this map. Anything other
            // than white on the material tints the result, so "matches the mesh" would not be
            // true - said here rather than fixed silently, because the material is a shared
            // asset and every other object using it would change too.
            var furMat = _furMaterial.objectReferenceValue as Material;
            if (furMat != null && furMat.HasProperty("_BaseColor"))
            {
                Color b = furMat.GetColor("_BaseColor");
                if (b.r < 0.99f || b.g < 0.99f || b.b < 0.99f)
                {
                    EditorGUILayout.HelpBox(
                        $"The material's Base Colour is {b.r:0.##}, {b.g:0.##}, {b.b:0.##} - the " +
                        "coat is tinted by it on top of whatever this map holds, so it will not " +
                        "match the mesh exactly. Setting it to white changes the MATERIAL, and " +
                        "so every object using it.", MessageType.Info);

                    if (GUILayout.Button("Set Material Base Colour to White"))
                    {
                        Undo.RecordObject(furMat, "Fur Base Colour to White");
                        furMat.SetColor("_BaseColor", Color.white);
                        EditorUtility.SetDirty(furMat);
                    }
                }
            }
        }

        /// <summary>
        /// Painting writes into a render texture the component owns, and re-baking the map
        /// throws those strokes away. Never silently - the same rule the Clear button and
        /// Baked Resolution already follow.
        /// </summary>
        private static bool ConfirmOverwritingStrokes(FurController controller)
        {
            return !controller.HasUnsavedPaintIn(FurDetailChannel.Color)
                   || EditorUtility.DisplayDialog("Match Mesh Colours",
                       "The colour map has painted strokes that are not saved to a texture. " +
                       "Matching the mesh rebakes the map and discards them.",
                       "Discard and match", "Cancel");
        }

        private void MatchMeshColors()
        {
            var controller = (FurController)target;

            Renderer source = controller.GetComponent<Renderer>()
                              ?? controller.GetComponentInChildren<Renderer>();

            if (source == null)
            {
                EditorUtility.DisplayDialog("Match Mesh Colours",
                    "No renderer on this object to read colours from.", "OK");
                return;
            }

            // The first material that actually carries an albedo texture. A model split over
            // several materials has one UV set between them, so any of their maps lands in the
            // right place - but only one can be the colour map, and the first with a texture
            // is the one worth guessing at.
            Texture albedo = null;
            // The tiling matters as much as the texture: sampled at 1x1 while the model shows
            // it at 3x3, the coat carries the same pattern at the wrong frequency and reads as
            // matching in some places and drifting in others.
            Vector4 sourceST = new Vector4(1f, 1f, 0f, 0f);
            foreach (Material m in source.sharedMaterials)
            {
                if (m == null)
                    continue;

                Texture t = m.HasProperty("_BaseMap") ? m.GetTexture("_BaseMap") : null;
                if (t == null && m.HasProperty("_MainTex"))
                    t = m.GetTexture("_MainTex");

                if (t == null)
                    continue;

                albedo = t;
                string stName = m.HasProperty("_BaseMap") && m.GetTexture("_BaseMap") != null ? "_BaseMap" : "_MainTex";
                Vector2 scale = m.GetTextureScale(stName);
                Vector2 offset = m.GetTextureOffset(stName);
                sourceST = new Vector4(scale.x, scale.y, offset.x, offset.y);
                break;
            }

            // No texture: the model's colour is a single value, so the map is filled with it.
            // Still a real map rather than a shortcut through the tint, because that leaves a
            // surface the brush can work on - starting from the mesh's own colour and painting
            // markings over it is the useful thing to do next.
            if (albedo == null)
            {
                Color flat = Color.white;
                foreach (Material m in source.sharedMaterials)
                {
                    if (m == null) continue;
                    if (m.HasProperty("_BaseColor")) { flat = m.GetColor("_BaseColor"); break; }
                    if (m.HasProperty("_Color")) { flat = m.GetColor("_Color"); break; }
                }

                if (!ConfirmOverwritingStrokes(controller))
                    return;

                // Both carry the colour. Note that the shader multiplies them - the map holds
                // the fill, and the tint scales it - so the coat lands on the colour SQUARED
                // and reads darker than the mesh. That is the asked-for behaviour; either one
                // of them alone is what makes the two match exactly.
                Undo.RecordObject(controller, "Match Mesh Colours");
                _colorMap.objectReferenceValue = null;
                _colorMapScaleOffset.vector4Value = new Vector4(1f, 1f, 0f, 0f);
                _colorMapFill.colorValue = flat;
                _colorMapTint.colorValue = flat;
                _colorMapStrength.floatValue = 1f;
                serializedObject.ApplyModifiedProperties();
                controller.RebakeColorMap();
                return;
            }

            // Painting writes into a render texture the component owns, and re-pointing the
            // slot rebakes from the new source - which throws those strokes away. Never
            // silently: the same rule the Clear button and Baked Resolution already follow.
            if (!ConfirmOverwritingStrokes(controller))
                return;

            // Texture assigned: it carries every colour there is, so both flat values are
            // reset out of its way. The source material's own Base Colour is deliberately not
            // folded into the tint - a texture is the whole answer here.
            Undo.RecordObject(controller, "Match Mesh Colours");
            _colorMap.objectReferenceValue = albedo;
            _colorMapScaleOffset.vector4Value = sourceST;
            _colorMapFill.colorValue = Color.white;
            _colorMapTint.colorValue = Color.white;
            _colorMapStrength.floatValue = 1f;
            serializedObject.ApplyModifiedProperties();
            controller.RebakeColorMap();
        }

        /// <summary>
        /// One texture slot, with a Clear button beside it while that channel is holding a
        /// render texture.
        ///
        /// Clear is offered separately from emptying the slot because the two mean
        /// different things: emptying the slot rebakes the map neutral and KEEPS it, which
        /// is the state you paint from, while Clear gives the memory back. Conflating them
        /// would mean a texture slot being emptied silently discarded someone's strokes.
        /// </summary>
        private void DrawMapSlot(SerializedProperty map, string label, FurDetailChannel channel, string note)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(map, new GUIContent(label));

            bool allocated = !serializedObject.isEditingMultipleObjects
                          && ((FurController)target).HasDetailMap(channel);

            using (new EditorGUI.DisabledScope(!allocated))
            {
                if (GUILayout.Button("Clear", GUILayout.Width(48f)))
                {
                    Undo.RecordObject(target, "Clear Fur Detail Map");
                    ((FurController)target).ClearDetailMap(channel);
                    EditorUtility.SetDirty(target);
                }
            }
            EditorGUILayout.EndHorizontal();

            FurInspectorSections.Note(note);
        }

        // ------------------------------------------------------------------ //
        //  Collision
        // ------------------------------------------------------------------ //
        private void DrawCollision()
        {
            string summary = _enableCollision.hasMultipleDifferentValues ? "-"
                : FurInspectorSections.OnOff(_enableCollision.boolValue);

            if (!FurInspectorSections.Header("Collision", "Collision", summary))
                return;

            EditorGUILayout.PropertyField(_enableCollision, new GUIContent("Enable Collision"));

            if (!_enableCollision.boolValue)
            {
                FurInspectorSections.Note(
                    "Off, no physics query runs and the shader's contact solve is skipped. Contact is " +
                    "resolved per strand tip per shell, so this is the most expensive feature here.");
                return;
            }

            EditorGUI.indentLevel++;
            EditorGUILayout.PropertyField(_collisionLayers);
            EditorGUILayout.PropertyField(_collisionStrength);
            EditorGUILayout.PropertyField(_collisionMargin);
            EditorGUI.indentLevel--;

            FurInspectorSections.Note(
                "Box, Sphere and Capsule colliders are exact. Mesh and Terrain are approximated by " +
                "their bounding box - give anything the fur should hug a primitive collider.");

            if (_collisionLayers.intValue == 0 && !_collisionLayers.hasMultipleDifferentValues)
            {
                EditorGUILayout.HelpBox(
                    "Collision Layers is empty, so nothing can deflect this fur.",
                    MessageType.Warning);
            }
        }

        // ------------------------------------------------------------------ //
        //  Advanced
        // ------------------------------------------------------------------ //
        private void DrawAdvanced()
        {
            if (!FurInspectorSections.Header("Advanced", "Advanced",
                    "tuning behind the settings above"))
                return;

            EditorGUILayout.LabelField("Level of detail", EditorStyles.miniBoldLabel);
            EditorGUILayout.PropertyField(_hardCullDistance);
            EditorGUILayout.PropertyField(_ribbonSeed, new GUIContent("Ribbon Seed"));

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Motion", EditorStyles.miniBoldLabel);
            EditorGUILayout.PropertyField(_inertiaMode, new GUIContent("Inertia Source"));
            EditorGUILayout.PropertyField(_inertiaDampingRatio);
            EditorGUILayout.PropertyField(_angularInertiaScale);

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Wetness", EditorStyles.miniBoldLabel);
            EditorGUILayout.PropertyField(_wetnessMapResolution);
            EditorGUILayout.PropertyField(_contactFeather);

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Detail maps", EditorStyles.miniBoldLabel);
            EditorGUILayout.PropertyField(_detailMapResolution, new GUIContent("Baked Resolution"));
            EditorGUILayout.PropertyField(_detailUvSet, new GUIContent("Detail UV Set"));
            FurInspectorSections.Note(
                "Which unwrap the per-object maps live in. UV0 is the texturing unwrap and is often " +
                "mirrored or overlapped, which makes two places share one texel: paint one and the " +
                "other changes too. UV1 is the lightmap unwrap, which Unity guarantees not to " +
                "overlap - enable 'Generate Lightmap UVs' on the importer to build one.");

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Collision", EditorStyles.miniBoldLabel);
            EditorGUILayout.PropertyField(_collisionSpread);
            EditorGUILayout.PropertyField(_maxSimultaneousColliders);

            EditorGUILayout.Space(4f);
            EditorGUILayout.LabelField("Shader references", EditorStyles.miniBoldLabel);
            FurInspectorSections.Note(
                "Auto-resolved in the editor. The serialized reference is what pulls a Hidden/ " +
                "shader into a player build.");
            EditorGUILayout.PropertyField(_wetnessSplatShader);
            EditorGUILayout.PropertyField(_attributeBakeShader);
        }

        // ------------------------------------------------------------------ //
        //  Live
        // ------------------------------------------------------------------ //
        private void DrawLive()
        {
            if (serializedObject.isEditingMultipleObjects)
                return;

            var controller = (FurController)target;
            bool ribbons = ResolveFurType() != FurController.FurType.Shells;

            string summary = ribbons
                ? $"{controller.ActiveRibbonCount:n0} ribbons"
                : $"{controller.ActiveShellCount} shells";

            if (!FurInspectorSections.Header("Live", "Live", summary, openByDefault: true))
                return;

            if (ribbons)
            {
                string drawn = controller.ActiveRibbonCount.ToString("n0");
                if (controller.RequestedRibbonCount > controller.ActiveRibbonCount)
                    drawn += $"  (density asked for {controller.RequestedRibbonCount:n0})";

                EditorGUILayout.LabelField(
                    "Strands Drawn",
                    drawn);
            }
            else
            {
                EditorGUILayout.LabelField("Shells Drawn", controller.ActiveShellCount.ToString());
            }

            EditorGUILayout.LabelField("Colliders Resolved", controller.ActiveColliderCount.ToString());

            // A running total rather than a per-frame rate because the number that means
            // something is that it STOPS. One bake per enabled map and then silence is
            // correct; a figure climbing with the frame counter means something is
            // dirtying the maps every frame.
            EditorGUILayout.LabelField("Detail Map Bakes", controller.DetailMapBakeCount.ToString());
        }

        // ------------------------------------------------------------------ //
        //  Shared helpers
        // ------------------------------------------------------------------ //
        /// <summary>The fur type the assigned material is set to.</summary>
        private FurController.FurType ResolveFurType()
        {
            var material = _furMaterial.objectReferenceValue as Material;
            if (material == null || !material.HasProperty("_FurType"))
                return FurController.FurType.Shells;

            return (FurController.FurType)Mathf.Clamp(
                Mathf.RoundToInt(material.GetFloat("_FurType")), 0, 1);
        }

        /// <summary>The mesh hair roots would be planted on - the SHARED one, never a bake.</summary>
        private static Mesh ResolveRootMesh(FurController controller)
        {
            var skinned = controller.GetComponent<SkinnedMeshRenderer>();
            if (skinned != null)
                return skinned.sharedMesh;

            var filter = controller.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }

        /// <summary>
        /// Opens the scene-view brush.
        ///
        /// A button rather than an inline brush section because painting takes over the
        /// Scene view's left mouse button. That has to be something the artist turns on
        /// and off deliberately, with an obvious way out - closing a window - rather than
        /// a mode a component can leave them stuck in.
        /// </summary>
        private void DrawPaintButton()
        {
            EditorGUILayout.Space(4f);

            using (new EditorGUI.DisabledScope(serializedObject.isEditingMultipleObjects))
            {
                if (GUILayout.Button("Draw Fur...", GUILayout.Height(26f)))
                    FurPaintWindow.Open((FurController)target);
            }

            if (serializedObject.isEditingMultipleObjects)
            {
                FurInspectorSections.Note("Select a single Fur Controller to paint.");
                return;
            }

            var controller = (FurController)target;
            if (Application.isPlaying || !controller.HasUnsavedPaint)
                return;

            EditorGUILayout.HelpBox(
                "This object has unsaved painted strokes. They live in a render texture and " +
                "will be lost on entering play mode or recompiling. Open Draw Fur and save them " +
                "to a texture asset.", MessageType.Warning);
        }

        /// <summary>
        /// States the one requirement all three maps share, once, where somebody who has
        /// just switched one on will read it.
        ///
        /// Worth calling out explicitly because the failure is silent and looks like a bug
        /// in the fur rather than in the asset: on a mirrored unwrap the map applies
        /// correctly and the far side of the model simply inherits the near side's length,
        /// which reads as the map "leaking" rather than as shared texels.
        /// </summary>
        private static void DrawUnwrapAdvice()
        {
            FurInspectorSections.Note(
                "The maps are stored in the Detail UV Set (Advanced), so that unwrap must not " +
                "overlap. Mirrored islands share texels, and one side inherits the other's paint - " +
                "switch to UV1 (lightmap UVs) if UV0 is mirrored.");
        }
    }
}
