using UnityEditor;
using UnityEngine;

namespace FurRendering.EditorTools
{
    /// <summary>
    /// Collapsible sections shared by the fur component inspector and the fur material
    /// inspector.
    ///
    /// Why this exists at all: between them these two inspectors carry well over a
    /// hundred controls. Every one earns its place for SOME coat, and almost none is
    /// needed for the coat in front of you right now. A flat list makes the reader scan
    /// all of it to find the three sliders they came for and - worse - makes every
    /// setting look equally likely to be the one that matters.
    ///
    /// Each header carries a SUMMARY on its right. That is what makes collapsing safe
    /// rather than merely tidy: a row of shut drawers with no labels hides the settings
    /// and the fact that they are set to something unusual. "Collision - off" or
    /// "3 maps, 512px" belongs on the closed drawer.
    ///
    /// Open/closed state lives in EditorPrefs, not on the object. It is a property of
    /// how a particular person works, not of the asset, so it must not dirty a material,
    /// must not differ between two otherwise identical objects, and must not reach
    /// version control.
    /// </summary>
    internal static class FurInspectorSections
    {
        private const string PrefixKey = "FURFLUX.Section.";
        private const float HeaderHeight = 22f;

        /// <summary>
        /// Draws a section header and returns whether its body should be drawn.
        ///
        /// <paramref name="openByDefault"/> is consulted only the first time a given key
        /// is seen; after that the reader's own choice wins.
        /// </summary>
        public static bool Header(string key, string title, string summary = null, bool openByDefault = false)
        {
            string prefKey = PrefixKey + key;
            bool open = EditorPrefs.GetBool(prefKey, openByDefault);

            EditorGUILayout.Space(4f);

            Rect rect = GUILayoutUtility.GetRect(1f, HeaderHeight, GUILayout.ExpandWidth(true));

            // Drawn manually rather than through BeginFoldoutHeaderGroup because that
            // control owns its whole row and leaves nowhere to put the summary.
            if (Event.current.type == EventType.Repaint)
            {
                var background = new Rect(rect.x - 2f, rect.y, rect.width + 6f, rect.height);
                EditorGUI.DrawRect(background, HeaderColor());
            }

            var labelRect = new Rect(rect.x + 14f, rect.y + 3f, rect.width - 18f, EditorGUIUtility.singleLineHeight);
            EditorGUI.LabelField(labelRect, title, EditorStyles.boldLabel);

            if (!string.IsNullOrEmpty(summary))
            {
                var summaryStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    alignment = TextAnchor.MiddleRight,
                };
                summaryStyle.normal.textColor = EditorStyles.centeredGreyMiniLabel.normal.textColor;

                var summaryRect = new Rect(rect.x + 14f, rect.y + 3f, rect.width - 20f,
                    EditorGUIUtility.singleLineHeight);
                EditorGUI.LabelField(summaryRect, summary, summaryStyle);
            }

            var arrowRect = new Rect(rect.x + 1f, rect.y + 3f, 13f, EditorGUIUtility.singleLineHeight);
            bool next = EditorGUI.Foldout(arrowRect, open, GUIContent.none, toggleOnLabelClick: false);

            // The whole bar is the hit target, not just the little triangle - a 13-pixel
            // arrow is a poor thing to ask someone to hit repeatedly.
            Event e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && rect.Contains(e.mousePosition))
            {
                next = !open;
                e.Use();
            }

            if (next != open)
            {
                EditorPrefs.SetBool(prefKey, next);
                open = next;
            }

            if (open)
                EditorGUILayout.Space(2f);

            return open;
        }

        private static Color HeaderColor()
        {
            return EditorGUIUtility.isProSkin
                ? new Color(1f, 1f, 1f, 0.06f)
                : new Color(0f, 0f, 0f, 0.06f);
        }

        /// <summary>
        /// The strip across the top of both FURFLUX inspectors: whose component this is,
        /// which version, and the two places help lives.
        ///
        /// Small on purpose. It is an identity line, not a splash - it has to earn the
        /// vertical space it takes on an inspector people scroll every day.
        /// </summary>
        public static void Banner(string what)
        {
            const float height = 26f;
            Rect rect = GUILayoutUtility.GetRect(1f, height, GUILayout.ExpandWidth(true));

            if (Event.current.type == EventType.Repaint)
            {
                var background = new Rect(rect.x - 2f, rect.y, rect.width + 6f, rect.height);
                EditorGUI.DrawRect(background, EditorGUIUtility.isProSkin
                    ? new Color(0.92f, 0.55f, 0.22f, 0.16f)
                    : new Color(0.92f, 0.55f, 0.22f, 0.22f));
            }

            Texture2D icon = FurfluxInfo.ControllerIcon;
            float x = rect.x + 4f;
            if (icon != null)
            {
                GUI.DrawTexture(new Rect(x, rect.y + 4f, 18f, 18f), icon, ScaleMode.ScaleToFit);
                x += 24f;
            }

            var label = new Rect(x, rect.y + 4f, rect.width - 150f, EditorGUIUtility.singleLineHeight);
            EditorGUI.LabelField(label, $"FURFLUX  ·  {what}", EditorStyles.boldLabel);

            var version = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };
            version.normal.textColor = EditorStyles.centeredGreyMiniLabel.normal.textColor;

            float buttonsX = rect.xMax - 112f;
            EditorGUI.LabelField(new Rect(buttonsX - 50f, rect.y + 4f, 46f, EditorGUIUtility.singleLineHeight),
                "v" + FurfluxInfo.Version, version);

            if (GUI.Button(new Rect(buttonsX, rect.y + 4f, 50f, 18f), "Manual", EditorStyles.miniButtonLeft))
                FurfluxInfo.OpenManual();

            if (GUI.Button(new Rect(buttonsX + 50f, rect.y + 4f, 58f, 18f), "Welcome", EditorStyles.miniButtonRight))
                FurWelcomeWindow.ShowWindow();

            EditorGUILayout.Space(2f);
        }

        /// <summary>A quiet explanatory line, indented under whatever it belongs to.</summary>
        public static void Note(string message)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField(message, EditorStyles.wordWrappedMiniLabel);
            EditorGUI.indentLevel--;
        }

        /// <summary>Formats an on/off summary, which is most of what these headers say.</summary>
        public static string OnOff(bool enabled, string whenOn = "on", string whenOff = "off")
        {
            return enabled ? whenOn : whenOff;
        }
    }
}
