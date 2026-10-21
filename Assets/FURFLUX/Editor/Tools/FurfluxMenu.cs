using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FurRendering.EditorTools
{
    /// <summary>
    /// Every FURFLUX entry point in the editor's menus.
    ///
    ///   Tools / FURFLUX /           welcome, manual, demo scenes, project setup
    ///   GameObject / FURFLUX /      add fur to the selected objects (also on right-click
    ///                               in the Hierarchy)
    ///   Assets / Create / FURFLUX / a new fur material of either type
    ///   Add Component / FURFLUX /   the components themselves
    ///
    /// Kept in one file so the whole surface the package exposes can be read at a glance,
    /// and so the menu priorities - which decide the order and the separators - are never
    /// spread across files where they drift out of step.
    /// </summary>
    internal static class FurfluxMenu
    {
        /// <summary>Where Add Fur puts the materials it creates. Outside the package on
        /// purpose: updating FURFLUX must never overwrite a coat somebody has tuned.</summary>
        private const string UserMaterialFolder = "Assets/Fur Materials";

        // ------------------------------------------------------------------ //
        //  Tools
        // ------------------------------------------------------------------ //
        [MenuItem(FurfluxInfo.ToolsMenu + "Welcome", priority = 0)]
        private static void Welcome() => FurWelcomeWindow.ShowWindow();

        [MenuItem(FurfluxInfo.ToolsMenu + "Documentation", priority = 1)]
        private static void Documentation() => FurfluxInfo.OpenManual();

        [MenuItem(FurfluxInfo.ToolsMenu + "Open Demo Scene", priority = 20)]
        public static void OpenDemoScene() => OpenScene(FurfluxInfo.DemoSceneGuid);

        [MenuItem(FurfluxInfo.ToolsMenu + "Open Showcase Scene", priority = 21)]
        public static void OpenShowcaseScene() => OpenScene(FurfluxInfo.ShowcaseSceneGuid);

        [MenuItem(FurfluxInfo.ToolsMenu + "Install Screen Fuzz Renderer Feature", priority = 40)]
        public static void InstallScreenFuzz()
        {
            if (FurScreenFuzzSetup.IsInstalled())
            {
                EditorUtility.DisplayDialog(FurfluxInfo.ProductName,
                    "The Fur Screen Fuzz feature is already on every renderer of the active URP asset.",
                    "OK");
                return;
            }

            FurScreenFuzzSetup.Install();
        }

        [MenuItem(FurfluxInfo.ToolsMenu + "Install Screen Fuzz Renderer Feature", validate = true)]
        private static bool InstallScreenFuzzValidate() => !EditorApplication.isPlaying;

        private static void OpenScene(string guid)
        {
            string path = FurfluxInfo.PathOf(guid);
            if (path == null)
            {
                EditorUtility.DisplayDialog(FurfluxInfo.ProductName,
                    "That demo scene is missing. Re-import the FURFLUX package to restore it.", "OK");
                return;
            }

            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                EditorSceneManager.OpenScene(path);
        }

        // ------------------------------------------------------------------ //
        //  GameObject - add fur to the selection
        // ------------------------------------------------------------------ //
        [MenuItem(FurfluxInfo.GameObjectMenu + "Add Fur (Shells)", priority = 10)]
        private static void AddShellFur() => AddFurToSelection(FurController.FurType.Shells);

        [MenuItem(FurfluxInfo.GameObjectMenu + "Add Fur (Strands)", priority = 11)]
        private static void AddStrandFur() => AddFurToSelection(FurController.FurType.Strands);

        [MenuItem(FurfluxInfo.GameObjectMenu + "Add Fur (Shells)", validate = true)]
        [MenuItem(FurfluxInfo.GameObjectMenu + "Add Fur (Strands)", validate = true)]
        public static bool AddFurValidate()
        {
            foreach (GameObject go in Selection.gameObjects)
                if (HasFurrableRenderer(go))
                    return true;

            return false;
        }

        private static bool HasFurrableRenderer(GameObject go)
        {
            return go != null &&
                   (go.GetComponent<SkinnedMeshRenderer>() != null || go.GetComponent<MeshFilter>() != null);
        }

        /// <summary>
        /// Adds a Fur Controller to every selected object that has a mesh, and gives each one
        /// its OWN material.
        ///
        /// Its own rather than the shipped default, because a fur material is a shared asset
        /// and tuning it changes every object using it. Handing out the package's default
        /// would mean the first slider somebody moved changed the demo scenes as well - and
        /// was overwritten by the next package update.
        /// </summary>
        public static void AddFurToSelection(FurController.FurType type)
        {
            int added = 0;
            var unreadable = new System.Text.StringBuilder();

            Undo.SetCurrentGroupName($"Add Fur ({type})");
            int group = Undo.GetCurrentGroup();

            foreach (GameObject go in Selection.gameObjects)
            {
                if (!HasFurrableRenderer(go))
                    continue;

                var fur = go.GetComponent<FurController>();
                if (fur == null)
                    fur = Undo.AddComponent<FurController>(go);

                if (fur.FurMaterial == null)
                {
                    Material material = CreateMaterial(type, go.name + "_Fur", UserMaterialFolder);
                    if (material != null)
                    {
                        Undo.RecordObject(fur, "Assign Fur Material");
                        fur.FurMaterial = material;
                        EditorUtility.SetDirty(fur);
                    }
                }

                if (type == FurController.FurType.Strands)
                {
                    Mesh mesh = go.GetComponent<SkinnedMeshRenderer>() != null
                        ? go.GetComponent<SkinnedMeshRenderer>().sharedMesh
                        : go.GetComponent<MeshFilter>().sharedMesh;

                    if (mesh != null && !mesh.isReadable)
                        unreadable.AppendLine("  - " + go.name + " (" + mesh.name + ")");
                }

                added++;
            }

            Undo.CollapseUndoOperations(group);

            if (unreadable.Length > 0)
            {
                EditorUtility.DisplayDialog(FurfluxInfo.ProductName,
                    "Strand fur places its hairs by reading the mesh, so these meshes need " +
                    "Read/Write enabled in their import settings before any fur appears:\n\n" +
                    unreadable + "\nShell fur has no such requirement.", "OK");
            }

            if (added > 0)
                Debug.Log($"[FURFLUX] Added {type.ToString().ToLowerInvariant()} fur to {added} object(s). " +
                          $"New materials are in '{UserMaterialFolder}'.");
        }

        // ------------------------------------------------------------------ //
        //  Assets / Create
        // ------------------------------------------------------------------ //
        [MenuItem(FurfluxInfo.CreateMenu + "Fur Material (Shells)", priority = 300)]
        private static void CreateShellMaterialAsset() => CreateMaterialInProjectWindow(FurController.FurType.Shells);

        [MenuItem(FurfluxInfo.CreateMenu + "Fur Material (Strands)", priority = 301)]
        private static void CreateStrandMaterialAsset() => CreateMaterialInProjectWindow(FurController.FurType.Strands);

        private static void CreateMaterialInProjectWindow(FurController.FurType type)
        {
            Material source = DefaultMaterial(type);
            if (source == null)
                return;

            var material = new Material(source) { enableInstancing = true };

            // Starts the rename field in whichever folder the Project window is showing,
            // exactly as the built-in Create > Material does.
            ProjectWindowUtil.CreateAsset(material, $"New Fur ({type}).mat");
        }

        /// <summary>A copy of the shipped default for a fur type, saved at a unique path.</summary>
        public static Material CreateMaterial(FurController.FurType type, string name, string folder)
        {
            Material source = DefaultMaterial(type);
            if (source == null)
                return null;

            EnsureFolder(folder);

            var material = new Material(source) { enableInstancing = true };
            string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{name}.mat");
            AssetDatabase.CreateAsset(material, path);
            Undo.RegisterCreatedObjectUndo(material, "Create Fur Material");
            return material;
        }

        private static Material DefaultMaterial(FurController.FurType type)
        {
            string guid = type == FurController.FurType.Strands
                ? FurfluxInfo.StrandsMaterialGuid
                : FurfluxInfo.ShellsMaterialGuid;

            var source = FurfluxInfo.Load<Material>(guid);
            if (source == null)
            {
                EditorUtility.DisplayDialog(FurfluxInfo.ProductName,
                    $"The default {type} material is missing from FURFLUX/Materials. " +
                    "Re-import the package to restore it.", "OK");
            }

            return source;
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
                return;

            string parent = System.IO.Path.GetDirectoryName(folder)?.Replace('\\', '/');
            string leaf = System.IO.Path.GetFileName(folder);

            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
                EnsureFolder(parent);

            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}
