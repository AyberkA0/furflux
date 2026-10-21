using System.Collections.Generic;
using FurRendering;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace FurRendering.EditorTools
{
    /// <summary>
    /// Finds and installs the screen-space fuzz renderer feature.
    ///
    /// Why this is worth a file of its own: the feature is the one part of FURFLUX that
    /// does not travel with the material. Everything else about a coat is on the component
    /// and the material, so dropping either into a project is enough; a full-screen pass
    /// has to live on the URP renderer, and a renderer is project state. Somebody who ticks
    /// Screen Space in the material inspector and sees nothing change has no way to guess
    /// that, so the inspector asks this whether the feature is there and offers the button
    /// that puts it there.
    ///
    /// It installs on EVERY renderer in the active pipeline asset rather than on the first
    /// one, because the renderers are usually quality tiers of the same game - a project
    /// with a PC and a Mobile renderer wants the coat to look like itself on both, and a
    /// feature installed on one of them is a bug that only appears at one quality setting.
    /// </summary>
    internal static class FurScreenFuzzSetup
    {
        private const string FeatureName = "FurScreenFuzz";

        /// <summary>True when every renderer of the active pipeline asset carries the feature.</summary>
        public static bool IsInstalled()
        {
            List<ScriptableRendererData> renderers = ActiveRenderers();
            if (renderers.Count == 0)
                return false;

            foreach (ScriptableRendererData data in renderers)
            {
                if (!HasFeature(data))
                    return false;
            }

            return true;
        }

        /// <summary>Adds the feature to any renderer of the active pipeline asset missing it.</summary>
        public static void Install()
        {
            List<ScriptableRendererData> renderers = ActiveRenderers();
            if (renderers.Count == 0)
            {
                Debug.LogWarning(
                    "[FURFLUX] No Universal Render Pipeline asset is active, so there is no " +
                    "renderer to add the screen-space fuzz pass to. Assign a URP asset in " +
                    "Project Settings > Graphics first.");
                return;
            }

            foreach (ScriptableRendererData data in renderers)
            {
                if (HasFeature(data))
                    continue;

                AddFeature(data);
            }

            AssetDatabase.SaveAssets();
        }

        private static bool HasFeature(ScriptableRendererData data)
        {
            foreach (ScriptableRendererFeature feature in data.rendererFeatures)
            {
                if (feature is FurScreenFuzzFeature)
                    return true;
            }

            return false;
        }

        private static void AddFeature(ScriptableRendererData data)
        {
            var feature = ScriptableObject.CreateInstance<FurScreenFuzzFeature>();
            feature.name = FeatureName;

            // Serialized rather than left to the Shader.Find fallback in Create(). The
            // fallback keeps the editor working, but a shader only ever referenced by name
            // is not pulled into a build - so without this the coat would haze in the
            // editor and not in the game, which is the worst place to find out.
            var featureObject = new SerializedObject(feature);
            featureObject.FindProperty("shader").objectReferenceValue =
                Shader.Find("Hidden/FURFLUX/Screen Fuzz");
            featureObject.ApplyModifiedPropertiesWithoutUndo();

            AssetDatabase.AddObjectToAsset(feature, data);
            AssetDatabase.SaveAssets();

            // The list and the map are written together and have to stay the same length:
            // URP looks a feature up by the local file id stored in the map, and a feature
            // present in the list but missing from the map is dropped on the next reload.
            var dataObject = new SerializedObject(data);
            SerializedProperty features = dataObject.FindProperty("m_RendererFeatures");
            SerializedProperty map = dataObject.FindProperty("m_RendererFeatureMap");

            features.arraySize++;
            features.GetArrayElementAtIndex(features.arraySize - 1).objectReferenceValue = feature;

            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out string _, out long localId);
            map.arraySize++;
            map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;

            dataObject.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(data);
        }

        /// <summary>Every renderer the active pipeline asset can draw with.</summary>
        private static List<ScriptableRendererData> ActiveRenderers()
        {
            var result = new List<ScriptableRendererData>();

            // GraphicsSettings rather than QualitySettings: a quality level may override
            // the pipeline asset, but the one actually in force is the one to install on,
            // and this is the property that reports it.
            var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (pipeline == null)
                return result;

            // The list is not public API, so it is read the way the inspector reads it.
            var asset = new SerializedObject(pipeline);
            SerializedProperty list = asset.FindProperty("m_RendererDataList");
            if (list == null)
                return result;

            for (int i = 0; i < list.arraySize; i++)
            {
                if (list.GetArrayElementAtIndex(i).objectReferenceValue is ScriptableRendererData data)
                    result.Add(data);
            }

            return result;
        }
    }
}
