using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace FurRendering.EditorTools
{
    /// <summary>
    /// Strips properties a fur material still stores but the fur shader no longer has.
    ///
    /// Unity never forgets a material property. Values are kept in the material's
    /// m_SavedProperties by NAME, and a name the current shader does not declare is simply
    /// carried along - forever, through every save, into every build. That is deliberate
    /// and usually right: it means swapping a shader out and back does not lose an
    /// artist's work.
    ///
    /// The cost is paid by whoever comes later. This shader has been through several
    /// rounds of "that slider had exactly one correct value, so it is a constant now" and
    /// "that feature was replaced" - the distance-thickness knobs, the LOD dither, the card
    /// geometry, the masks. Each of those left its value behind in every material authored
    /// while it existed. They are invisible in the Inspector, because the Inspector only
    /// draws what the shader declares, so nothing warns anybody. But they are in the YAML,
    /// they hold texture references alive, and the moment somebody greps a .mat or reads it
    /// to work out how a coat was set up, they are reading settings that stopped doing
    /// anything releases ago.
    ///
    /// Deliberately scoped to materials using the fur shader. Running this over the whole
    /// project would be a broader claim than can be justified: another shader's stale
    /// property might be one a script writes at runtime, and this cannot know that.
    /// </summary>
    internal static class FurMaterialCleanup
    {
        // Every group m_SavedProperties splits values into. Missing one would leave that
        // kind of stale property behind while reporting success.
        private static readonly string[] Groups = { "m_TexEnvs", "m_Floats", "m_Colors", "m_Ints" };

        [MenuItem(FurfluxInfo.ToolsMenu + "Clean Stale Material Properties", priority = 41)]
        private static void Run()
        {
            List<Material> materials = FindFurMaterials(out Shader shader);

            if (shader == null || materials.Count == 0)
            {
                EditorUtility.DisplayDialog("Clean fur materials",
                    "No materials using the fur shader were found.", "OK");
                return;
            }

            var report = new StringBuilder();
            var counts = new Dictionary<Material, List<string>>();
            int total = 0;

            foreach (Material material in materials)
            {
                List<string> stale = FindStale(material, shader);
                if (stale.Count == 0)
                    continue;

                counts[material] = stale;
                total += stale.Count;
                report.AppendLine($"{material.name}  ({stale.Count})");
            }

            if (total == 0)
            {
                EditorUtility.DisplayDialog("Clean fur materials",
                    $"Checked {materials.Count} fur materials. Nothing stale.", "OK");
                return;
            }

            bool go = EditorUtility.DisplayDialog("Clean fur materials",
                $"{total} stored properties across {counts.Count} materials are not declared "
              + $"by {shader.name} and do nothing.\n\n{report}\nRemove them?",
                "Remove", "Cancel");

            if (!go)
                return;

            int removed = 0;
            foreach (KeyValuePair<Material, List<string>> entry in counts)
                removed += Strip(entry.Key, shader);

            AssetDatabase.SaveAssets();

            Debug.Log($"[FURFLUX] Removed {removed} stale properties from {counts.Count} materials.");
        }

        private static List<Material> FindFurMaterials(out Shader shader)
        {
            shader = null;
            var found = new List<Material>();

            foreach (string guid in AssetDatabase.FindAssets("t:Material"))
            {
                var material = AssetDatabase.LoadAssetAtPath<Material>(
                    AssetDatabase.GUIDToAssetPath(guid));

                if (material == null || material.shader == null)
                    continue;

                // Identified by the properties only this shader has, rather than by name:
                // the shader has been renamed once already, and a string match would have
                // silently stopped finding anything the day it was.
                //
                // Both are declared properties. _ShellCount would look like a stronger
                // test and is exactly the wrong one: it is a per-draw uniform declared
                // outside the material buffer, so HasProperty is false for it on every fur
                // material in the project.
                if (!material.HasProperty("_FurType") || !material.HasProperty("_Density"))
                    continue;

                shader = material.shader;
                found.Add(material);
            }

            return found;
        }

        /// <summary>The property names a shader actually declares.</summary>
        private static HashSet<string> DeclaredNames(Shader shader)
        {
            var declared = new HashSet<string>();
            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
                declared.Add(shader.GetPropertyName(i));

            return declared;
        }

        /// <summary>The stored property names this shader does not declare.</summary>
        private static List<string> FindStale(Material material, Shader shader)
        {
            HashSet<string> declared = DeclaredNames(shader);

            var stale = new List<string>();
            var serialized = new SerializedObject(material);
            SerializedProperty saved = serialized.FindProperty("m_SavedProperties");

            foreach (string group in Groups)
            {
                SerializedProperty array = saved?.FindPropertyRelative(group);
                if (array == null || !array.isArray)
                    continue;

                for (int i = 0; i < array.arraySize; i++)
                {
                    string name = array.GetArrayElementAtIndex(i)
                                       .FindPropertyRelative("first").stringValue;

                    if (!declared.Contains(name))
                        stale.Add(name);
                }
            }

            return stale;
        }

        private static int Strip(Material material, Shader shader)
        {
            HashSet<string> declared = DeclaredNames(shader);

            Undo.RecordObject(material, "Clean Stale Fur Material Properties");

            var serialized = new SerializedObject(material);
            SerializedProperty saved = serialized.FindProperty("m_SavedProperties");
            int removed = 0;

            foreach (string group in Groups)
            {
                SerializedProperty array = saved?.FindPropertyRelative(group);
                if (array == null || !array.isArray)
                    continue;

                // Backwards, because deleting an element renumbers everything after it.
                for (int i = array.arraySize - 1; i >= 0; i--)
                {
                    string name = array.GetArrayElementAtIndex(i)
                                       .FindPropertyRelative("first").stringValue;

                    if (declared.Contains(name))
                        continue;

                    // A texture entry deleted this way needs its object reference cleared
                    // first, or the asset stays referenced by the material's dependency
                    // list even though the entry is gone.
                    SerializedProperty second = array.GetArrayElementAtIndex(i)
                                                     .FindPropertyRelative("second");
                    if (second != null && second.propertyType == SerializedPropertyType.ObjectReference)
                        second.objectReferenceValue = null;

                    array.DeleteArrayElementAtIndex(i);
                    removed++;
                }
            }

            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(material);
            return removed;
        }
    }
}
