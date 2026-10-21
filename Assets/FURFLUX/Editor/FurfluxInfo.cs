using UnityEditor;
using UnityEngine;

namespace FurRendering.EditorTools
{
    /// <summary>
    /// The package's identity and the assets the editor tools point at.
    ///
    /// Every asset is found by GUID rather than by path. Users move the FURFLUX folder -
    /// into a Plugins folder, under Third Party, anywhere - and a path written into a menu
    /// item would break the moment they did. A GUID travels with the asset.
    /// </summary>
    internal static class FurfluxInfo
    {
        public const string ProductName = "FURFLUX";
        public const string Version = "1.1.0";
        public const string Tagline = "Shell and strand fur for URP";

        // Menu roots, in one place so every entry lands in the same tree.
        public const string ToolsMenu = "Tools/FURFLUX/";
        public const string GameObjectMenu = "GameObject/FURFLUX/";
        public const string CreateMenu = "Assets/Create/FURFLUX/";

        // --- Assets the tools open or copy ---
        public const string ManualGuid = "2c937e0a9a36c704c8bf51816433c29f";
        public const string ReadmeGuid = "43b213def7c5e564881e565c3f6370da";
        public const string LicenseGuid = "bd4fa8e8cdb98314488aa915aeb33233";
        public const string DemoSceneGuid = "74e2eb7cb1e76ba4093fe28b1535726d";
        public const string ShowcaseSceneGuid = "521ec1870ddb885458ab16c5df356df4";
        public const string ShellsMaterialGuid = "9bf6c575910e26f49b8962084e6a0da2";
        public const string StrandsMaterialGuid = "6ebc7998bc20fad4e920a3c3f6d1bc75";
        public const string LogoGuid = "75cb9b39a6ab424da86f982f031614fd";
        public const string ControllerIconGuid = "b473bd48fdd74915b0a68eeea4c7434f";

        /// <summary>Project path of an asset by GUID, or null if it has been deleted.</summary>
        public static string PathOf(string guid)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            return string.IsNullOrEmpty(path) ? null : path;
        }

        public static T Load<T>(string guid) where T : Object
        {
            string path = PathOf(guid);
            return path != null ? AssetDatabase.LoadAssetAtPath<T>(path) : null;
        }

        private static Texture2D _logo;
        private static Texture2D _controllerIcon;

        public static Texture2D Logo => _logo != null ? _logo : (_logo = Load<Texture2D>(LogoGuid));

        public static Texture2D ControllerIcon =>
            _controllerIcon != null ? _controllerIcon : (_controllerIcon = Load<Texture2D>(ControllerIconGuid));

        /// <summary>Opens an asset the way double-clicking it in the Project window would.</summary>
        public static void Open(string guid, string what)
        {
            Object asset = Load<Object>(guid);
            if (asset == null)
            {
                EditorUtility.DisplayDialog(ProductName,
                    $"The {what} is missing from the FURFLUX folder. Re-import the package to restore it.",
                    "OK");
                return;
            }

            AssetDatabase.OpenAsset(asset);
        }

        /// <summary>Opens the manual in the system's default viewer for Markdown.</summary>
        public static void OpenManual()
        {
            string path = PathOf(ManualGuid);
            if (path == null)
            {
                Open(ManualGuid, "manual");
                return;
            }

            EditorUtility.OpenWithDefaultApp(path);
        }
    }
}
