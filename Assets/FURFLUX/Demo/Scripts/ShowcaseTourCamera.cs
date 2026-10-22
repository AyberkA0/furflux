using System;
using System.Collections.Generic;
using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace FurRendering.Demo
{
    /// <summary>
    /// Tour camera for the Fur Showcase: glides left to right across the nine stations at
    /// one fixed studio angle, with a small caption in the corner.
    /// Keys 1-9, arrows or A/D switch station; drag to orbit; scroll, W/S or +/- to zoom; H hides the caption.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    [AddComponentMenu("FURFLUX/Demo/Showcase Tour Camera")]
    public sealed class ShowcaseTourCamera : MonoBehaviour
    {
        [Serializable]
        public struct StationView
        {
            public string title;
            public string description;
            public Vector3 targetCenter;
            public float cameraDistanceZ;
            public float cameraHeightY;
            public float fieldOfView;

            public StationView(string title, string description, Vector3 targetCenter, float distanceZ = 3.2f, float heightY = 1.75f, float fov = 45f)
            {
                this.title = title;
                this.description = description;
                this.targetCenter = targetCenter;
                this.cameraDistanceZ = distanceZ;
                this.cameraHeightY = heightY;
                this.fieldOfView = fov;
            }
        }

        [Header("Transition Settings")]
        [Tooltip("How fast the camera translates smoothly to the next station.")]
        [SerializeField] private float positionSmoothTime = 0.45f;
        [SerializeField] private float fovSmoothTime = 0.35f;

        [Header("Fixed Studio Camera Angle")]
        [Tooltip("Fixed camera pitch angle (degrees) maintained across all stations for clean tracking.")]
        [SerializeField] private float fixedPitchAngle = 6.0f;

        [Header("Orbit & Zoom Controls")]
        [SerializeField] private float orbitSensitivity = 0.15f;
        [Tooltip("How much one wheel notch changes the distance, as a fraction of it.")]
        [SerializeField] private float zoomSensitivity = 0.15f;
        [Tooltip("Closest the camera gets to the point it is looking at, in meters.")]
        [SerializeField] private float minZoomDistance = 1.0f;
        [Tooltip("Furthest the camera pulls back from the station, in meters.")]
        [SerializeField] private float maxZoomDistance = 35f;

        [Header("UI Overlay")]
        [SerializeField] private bool showHud = true;

        [Header("Stations (Left to Right)")]
        [SerializeField] private StationView[] stations;

        private int _currentIndex = 0;
        private Vector3 _currentVelocity;
        private float _fovVelocity;

        private float _orbitYaw;
        private float _orbitPitch;
        private float _zoomFactor = 1f;     // camera distance as a multiple of the station's own
        private Vector3 _focusOffset;       // where zooming toward the cursor has moved the pivot
        private Camera _cam;

        private void Awake()
        {
            _cam = GetComponent<Camera>();
            BuildStationsLeftToRight();
        }

        private void Start()
        {
            // Start at Station 1 (Leftmost: Wind)
            _currentIndex = 0;
            _startedAt = Time.unscaledTime;
            if (stations != null && stations.Length > 0)
            {
                ApplyInstantViewpoint(_currentIndex);
            }
        }

        /// <summary>
        /// Builds the nine stations in left-to-right order, 12 m apart from X = -48 to +48:
        /// wind, stylised/realistic, wetness, motion, collision, patterned coat,
        /// short/long, sheen, distance.
        /// </summary>
        private void BuildStationsLeftToRight()
        {
            var list = new List<StationView>();

            Vector3 GetPos(string name, Vector3 fallback)
            {
                var go = GameObject.Find(name);
                return go != null ? go.transform.position : fallback;
            }

            // The copy says what to LOOK AT, in plain words. A visitor reads it once,
            // glances back at the fur, and should be able to find the thing described.

            // 1: Wind (far left)
            Vector3 windPos = GetPos("Station_Wind", new Vector3(-48.0f, 1.6f, 0.0f));
            list.Add(new StationView(
                "Wind",
                "Gusts lay the coat over and let it spring back. The side facing away from the wind moves less.",
                windPos,
                4.2f, 1.8f, 45f
            ));

            // 2: Stylised vs realistic
            Vector3 lpPos = GetPos("Station_LowPoly_Fur", new Vector3(-37.2f, 1.3f, 0.0f));
            Vector3 realPos = GetPos("Station_Realistic_Fur", new Vector3(-34.8f, 1.3f, 0.0f));
            Vector3 st2Center = (lpPos + realPos) * 0.5f;
            list.Add(new StationView(
                "Stylised and realistic",
                "The same shader with two settings: a few coarse layers on the left, many fine ones on the right.",
                new Vector3(st2Center.x, 1.35f, st2Center.z),
                4.6f, 1.8f, 46f
            ));

            // 3: Regional wetness
            Vector3 wetPos = GetPos("Station_Wetness", new Vector3(-24.0f, 1.6f, 0.0f));
            list.Add(new StationView(
                "Wetness",
                "Only the part that touches the water gets wet. It darkens, clumps and hangs heavier, then slowly dries.",
                new Vector3(wetPos.x, 1.5f, wetPos.z),
                4.4f, 1.8f, 45f
            ));

            // 4: Motion inertia
            Vector3 motPos = GetPos("Station_Motion", new Vector3(-12.0f, 1.4f, 0.0f));
            list.Add(new StationView(
                "Motion",
                "Watch the fur as the shape changes direction: it trails behind while moving and overshoots when it stops.",
                new Vector3(motPos.x, 1.4f, motPos.z),
                4.2f, 1.8f, 45f
            ));

            // 5: Collision
            Vector3 collPos = GetPos("Station_Collision", new Vector3(0.0f, 1.6f, 0.0f));
            list.Add(new StationView(
                "Collision",
                "A swinging arm brushes past. Where it touches, the fur is pushed aside instead of passing through.",
                collPos,
                4.2f, 1.8f, 45f
            ));

            // 6: Textured coat
            Vector3 texShavedPos = GetPos("Station_Textured_Shaved", new Vector3(10.8f, 1.3f, 0.0f));
            Vector3 texShortPos = GetPos("Station_Textured_Short", new Vector3(13.2f, 1.3f, 0.0f));
            Vector3 texCenter = (texShavedPos + texShortPos) * 0.5f;
            list.Add(new StationView(
                "Patterned coat",
                "The coat takes its colour from a texture. Left: clipped close. Right: a short coat over the same pattern.",
                new Vector3(texCenter.x, 1.35f, texCenter.z),
                4.6f, 1.8f, 46f
            ));

            // 7: Short vs long
            Vector3 dShortPos = GetPos("Station_Density_Short", new Vector3(22.8f, 1.3f, 0.0f));
            Vector3 dLongPos = GetPos("Station_Density_Long", new Vector3(25.2f, 1.3f, 0.0f));
            Vector3 densCenter = (dShortPos + dLongPos) * 0.5f;
            list.Add(new StationView(
                "Short and long",
                "A dense, velvety coat next to a long one. Same material, different length and density.",
                new Vector3(densCenter.x, 1.35f, densCenter.z),
                4.6f, 1.8f, 46f
            ));

            // 8: Sheen and backlight
            Vector3 sheenPos = GetPos("Station_Sheen", new Vector3(36.0f, 1.6f, 0.0f));
            list.Add(new StationView(
                "Sheen and backlight",
                "As it turns, a highlight runs along the hairs, and the edge glows where light comes from behind.",
                sheenPos,
                4.2f, 1.8f, 45f
            ));

            // 9: Distance LOD (far right)
            Vector3 lod0Pos = GetPos("Station_LOD_0", new Vector3(46.0f, 1.3f, 0.0f));
            list.Add(new StationView(
                "Distance",
                "The same coat at five distances. Further away it is drawn with fewer layers, and still reads as fur.",
                new Vector3(lod0Pos.x + 1.8f, 1.4f, lod0Pos.z),
                4.5f, 1.9f, 48f
            ));

            stations = list.ToArray();
        }

        private void ApplyInstantViewpoint(int index)
        {
            _currentIndex = Mathf.Clamp(index, 0, stations.Length - 1);
            StationView st = stations[_currentIndex];

            Vector3 defaultCamPos = new Vector3(st.targetCenter.x, st.cameraHeightY, st.targetCenter.z - st.cameraDistanceZ);
            transform.position = defaultCamPos;
            transform.rotation = Quaternion.Euler(fixedPitchAngle, 0f, 0f);
            if (_cam != null) _cam.fieldOfView = st.fieldOfView;

            _orbitYaw = 0f;
            _orbitPitch = 0f;
            ResetZoom();
        }

        private void ResetZoom()
        {
            _zoomFactor = 1f;
            _focusOffset = Vector3.zero;
        }

        private void Update()
        {
            HandleKeyboardInput();
            HandleOrbitAndZoom();
            UpdateCameraTransform();
        }

        private void HandleKeyboardInput()
        {
            // Number keys 1..9 (Alpha1-9 or Keypad1-9)
            for (int i = 0; i < 9; i++)
            {
                if (IsDigitPressed(i + 1))
                {
                    SwitchToStation(i);
                    return;
                }
            }

            // Arrow keys / A-D / Q-E for next / prev
            if (IsNextPressed())
            {
                SwitchToStation((_currentIndex + 1) % stations.Length);
            }
            else if (IsPrevPressed())
            {
                SwitchToStation((_currentIndex - 1 + stations.Length) % stations.Length);
            }

            // Toggle HUD with 'H'
            if (IsToggleHudPressed())
            {
                showHud = !showHud;
            }
        }

        public void SwitchToStation(int index)
        {
            if (stations == null || stations.Length == 0) return;
            _currentIndex = Mathf.Clamp(index, 0, stations.Length - 1);
            _orbitYaw = 0f;
            _orbitPitch = 0f;
            ResetZoom();
        }

        private void HandleOrbitAndZoom()
        {
            bool lookHeld = IsRightMouseHeld();
            Vector2 mouseDelta = ReadMouseDelta();
            float scroll = ReadScrollWheel();

            if (lookHeld)
            {
                _orbitYaw += mouseDelta.x * orbitSensitivity;
                _orbitPitch -= mouseDelta.y * orbitSensitivity;
                _orbitPitch = Mathf.Clamp(_orbitPitch, -35f, 35f);
            }

            // W/S or +/- zoom too, at about eight wheel notches a second.
            scroll += ReadZoomKeys() * 8f * Time.unscaledDeltaTime;

            if (Mathf.Abs(scroll) > 0.001f)
                Zoom(scroll);
        }

        /// <summary>
        /// Zoom in steps proportional to the distance, so a notch is as noticeable at 30 m as
        /// at 1 m, and toward whatever is under the cursor. Pulling back drifts the view
        /// home to the station's centre.
        /// </summary>
        private void Zoom(float scroll)
        {
            StationView st = stations[_currentIndex];
            Vector3 baseOffset = StationOffset(st);
            float baseDistance = Mathf.Max(baseOffset.magnitude, 1e-3f);

            float oldFactor = _zoomFactor;
            float newFactor = oldFactor * Mathf.Exp(-scroll * zoomSensitivity);
            newFactor = Mathf.Clamp(newFactor, minZoomDistance / baseDistance, maxZoomDistance / baseDistance);
            float ratio = newFactor / oldFactor;
            if (Mathf.Approximately(ratio, 1f))
                return;

            Vector3 pivot = st.targetCenter + _focusOffset;

            if (ratio < 1f && _cam != null)
            {
                // Scale the camera about the point under the cursor: that point then stays
                // where it is on screen while everything around it grows.
                // The object under the cursor if there is one, so a close-up lands on the
                // coat rather than in the gap between two exhibits.
                Ray ray = _cam.ScreenPointToRay(ReadMousePosition());
                var plane = new Plane(-transform.forward, pivot);
                Vector3 point;
                if (Physics.Raycast(ray, out RaycastHit surface, 200f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                    point = surface.point;
                else if (plane.Raycast(ray, out float hit))
                    point = ray.GetPoint(hit);
                else
                    point = pivot;
                pivot = point + (pivot - point) * ratio;
            }
            else
            {
                _focusOffset *= 1f / ratio;
                pivot = st.targetCenter + _focusOffset;
            }

            _focusOffset = pivot - st.targetCenter;
            _focusOffset.x = Mathf.Clamp(_focusOffset.x, -4f, 4f);
            _focusOffset.y = Mathf.Clamp(_focusOffset.y, -1.2f, 1.5f);
            _focusOffset.z = Mathf.Clamp(_focusOffset.z, -2f, 2f);
            _zoomFactor = newFactor;
        }

        /// <summary>Camera position relative to the station's target at the default framing.</summary>
        private static Vector3 StationOffset(StationView st)
        {
            return new Vector3(0f, st.cameraHeightY - st.targetCenter.y, -st.cameraDistanceZ);
        }

        private void UpdateCameraTransform()
        {
            if (stations == null || stations.Length == 0) return;

            StationView st = stations[_currentIndex];

            // The station's own framing, dollied along its line of sight by the zoom and
            // rotated about the pivot when the user orbits. The pitch is fixed, so moving
            // along that line keeps the pivot on the same spot of the screen.
            Vector3 pivot = st.targetCenter + _focusOffset;
            Quaternion orbitRot = Quaternion.Euler(_orbitPitch, _orbitYaw, 0f);
            Vector3 baseCamPos = pivot + orbitRot * (StationOffset(st) * _zoomFactor);

            // Smooth translation (glides horizontally across X axis)
            transform.position = Vector3.SmoothDamp(transform.position, baseCamPos, ref _currentVelocity, positionSmoothTime, Mathf.Infinity, Time.unscaledDeltaTime);

            // Camera rotation: fixed pitch looking straight forward (+ subtle orbit delta if user drags)
            Quaternion targetRotation = Quaternion.Euler(fixedPitchAngle + _orbitPitch, _orbitYaw, 0f);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, Time.unscaledDeltaTime * 12f);

            // Smooth FOV
            if (_cam != null)
            {
                _cam.fieldOfView = Mathf.SmoothDamp(_cam.fieldOfView, st.fieldOfView, ref _fovVelocity, fovSmoothTime, Mathf.Infinity, Time.unscaledDeltaTime);
            }
        }

        // ------------------------------------------------------------------ //
        //  Caption
        // ------------------------------------------------------------------ //
        //
        //  Written like a museum label rather than a game HUD: a small index, a title in
        //  sentence case, one or two plain sentences, all set flush-left in the corner and
        //  on no panel at all. The fur is the exhibit; the caption should be something you
        //  can stop noticing.

        private const float ReferenceHeight = 1080f;   // the layout below is in pixels at this height
        private const float Margin = 48f;
        private const float TextWidth = 460f;
        private const float CaptionFadeTime = 0.35f;
        private const float HintVisibleTime = 6f;
        private const float HintFadeTime = 1.5f;

        private static readonly Color Ink      = new Color(0.94f, 0.93f, 0.90f, 1f);
        private static readonly Color InkMuted = new Color(0.94f, 0.93f, 0.90f, 0.55f);
        private static readonly Color InkFaint = new Color(0.94f, 0.93f, 0.90f, 0.18f);
        private static readonly Color Shadow   = new Color(0f, 0f, 0f, 0.45f);

        private GUIStyle _indexStyle;
        private GUIStyle _titleStyle;
        private GUIStyle _bodyStyle;
        private GUIStyle _hintStyle;

        private int _captionIndex = -1;
        private float _captionShownAt;
        private float _startedAt;

        private void EnsureCaptionStyles()
        {
            if (_titleStyle != null)
                return;

            _indexStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = false };
            _titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 30, wordWrap = false };
            _bodyStyle  = new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true };
            _hintStyle  = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                wordWrap = false,
                alignment = TextAnchor.LowerRight,
            };

            foreach (var style in new[] { _indexStyle, _titleStyle, _bodyStyle, _hintStyle })
            {
                style.padding = new RectOffset(0, 0, 0, 0);
                style.margin = new RectOffset(0, 0, 0, 0);
                style.richText = false;
            }
        }

        private void OnGUI()
        {
            if (!showHud || stations == null || stations.Length == 0) return;

            EnsureCaptionStyles();

            float now = Time.unscaledTime;
            if (_captionIndex != _currentIndex)
            {
                _captionIndex = _currentIndex;
                _captionShownAt = now;
            }

            // Everything is laid out at 1080p and scaled, so the caption keeps its
            // proportions on any window size instead of shrinking to a smudge at 4K.
            float scale = Mathf.Clamp(Screen.height / ReferenceHeight, 0.6f, 2.5f);
            Matrix4x4 previousMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));

            float width = Screen.width / scale;
            float height = Screen.height / scale;

            StationView st = stations[_currentIndex];

            // A short fade and a few pixels of rise when the station changes - enough to
            // say "this is new" without turning the caption into an animation.
            float t = Mathf.Clamp01((now - _captionShownAt) / CaptionFadeTime);
            float ease = 1f - (1f - t) * (1f - t);
            float rise = (1f - ease) * 8f;

            float bodyHeight = _bodyStyle.CalcHeight(new GUIContent(st.description), TextWidth);
            float progressY = height - Margin - 3f;
            float bodyY = progressY - 22f - bodyHeight + rise;
            float titleY = bodyY - 42f;
            float indexY = titleY - 22f;

            string index = $"{_currentIndex + 1:00} / {stations.Length:00}";

            DrawText(new Rect(Margin, indexY, TextWidth, 18f), index, _indexStyle, InkMuted, ease);
            DrawText(new Rect(Margin, titleY, TextWidth, 38f), st.title, _titleStyle, Ink, ease);
            DrawText(new Rect(Margin, bodyY, TextWidth, bodyHeight), st.description, _bodyStyle, InkMuted, ease);

            DrawProgress(Margin, progressY);
            DrawHint(width, height, now);

            GUI.matrix = previousMatrix;
        }

        /// <summary>One thin segment per station; the current one is lit. Each is clickable.</summary>
        private void DrawProgress(float x, float y)
        {
            const float segment = 22f;
            const float gap = 5f;
            const float thickness = 2f;

            Color previous = GUI.color;

            for (int i = 0; i < stations.Length; i++)
            {
                var rect = new Rect(x + i * (segment + gap), y, segment, thickness);

                GUI.color = i == _currentIndex ? Ink : InkFaint;
                GUI.DrawTexture(rect, Texture2D.whiteTexture);

                // A generous invisible hit area around a 2px line.
                var hit = new Rect(rect.x, rect.y - 8f, rect.width + gap, rect.height + 16f);
                if (GUI.Button(hit, GUIContent.none, GUIStyle.none))
                    SwitchToStation(i);
            }

            GUI.color = previous;
        }

        /// <summary>
        /// The controls, said once and quietly: visible for the first few seconds of the
        /// tour, then faded out for good.
        /// </summary>
        private void DrawHint(float width, float height, float now)
        {
            float alpha = Mathf.Clamp01((HintVisibleTime - (now - _startedAt)) / HintFadeTime);
            if (alpha <= 0f)
                return;

            const string hint = "← →  stations      drag  orbit      scroll / W S  zoom      H  hide";
            DrawText(new Rect(width - Margin - 600f, height - Margin - 14f, 600f, 18f),
                     hint, _hintStyle, InkMuted, alpha);
        }

        private static void DrawText(Rect rect, string text, GUIStyle style, Color color, float alpha)
        {
            // A soft one-pixel shadow keeps light text legible over a bright coat without
            // needing a panel behind it.
            style.normal.textColor = new Color(Shadow.r, Shadow.g, Shadow.b, Shadow.a * alpha);
            GUI.Label(new Rect(rect.x, rect.y + 1f, rect.width, rect.height), text, style);

            style.normal.textColor = new Color(color.r, color.g, color.b, color.a * alpha);
            GUI.Label(rect, text, style);
        }

        // ------------------------------------------------------------------ //
        //  Input reading
        // ------------------------------------------------------------------ //

#if ENABLE_INPUT_SYSTEM
        private static bool IsDigitPressed(int digit)
        {
            var kb = Keyboard.current;
            if (kb == null) return false;
            switch (digit)
            {
                case 1: return kb.digit1Key.wasPressedThisFrame || kb.numpad1Key.wasPressedThisFrame;
                case 2: return kb.digit2Key.wasPressedThisFrame || kb.numpad2Key.wasPressedThisFrame;
                case 3: return kb.digit3Key.wasPressedThisFrame || kb.numpad3Key.wasPressedThisFrame;
                case 4: return kb.digit4Key.wasPressedThisFrame || kb.numpad4Key.wasPressedThisFrame;
                case 5: return kb.digit5Key.wasPressedThisFrame || kb.numpad5Key.wasPressedThisFrame;
                case 6: return kb.digit6Key.wasPressedThisFrame || kb.numpad6Key.wasPressedThisFrame;
                case 7: return kb.digit7Key.wasPressedThisFrame || kb.numpad7Key.wasPressedThisFrame;
                case 8: return kb.digit8Key.wasPressedThisFrame || kb.numpad8Key.wasPressedThisFrame;
                case 9: return kb.digit9Key.wasPressedThisFrame || kb.numpad9Key.wasPressedThisFrame;
                default: return false;
            }
        }

        private static bool IsNextPressed()
        {
            var kb = Keyboard.current;
            return kb != null && (kb.rightArrowKey.wasPressedThisFrame || kb.dKey.wasPressedThisFrame || kb.eKey.wasPressedThisFrame);
        }

        private static bool IsPrevPressed()
        {
            var kb = Keyboard.current;
            return kb != null && (kb.leftArrowKey.wasPressedThisFrame || kb.aKey.wasPressedThisFrame || kb.qKey.wasPressedThisFrame);
        }

        private static bool IsToggleHudPressed()
        {
            var kb = Keyboard.current;
            return kb != null && kb.hKey.wasPressedThisFrame;
        }

        private static bool IsRightMouseHeld()
        {
            var mouse = Mouse.current;
            return mouse != null && (mouse.rightButton.isPressed || mouse.leftButton.isPressed);
        }

        private static Vector2 ReadMouseDelta()
        {
            var mouse = Mouse.current;
            return mouse != null ? mouse.delta.ReadValue() : Vector2.zero;
        }

        private static float ReadScrollWheel()
        {
            var mouse = Mouse.current;
            return mouse != null ? mouse.scroll.ReadValue().y * 0.01f : 0f;
        }

        private static float ReadZoomKeys()
        {
            var kb = Keyboard.current;
            if (kb == null) return 0f;
            float zoom = 0f;
            if (kb.wKey.isPressed || kb.equalsKey.isPressed || kb.numpadPlusKey.isPressed) zoom += 1f;
            if (kb.sKey.isPressed || kb.minusKey.isPressed || kb.numpadMinusKey.isPressed) zoom -= 1f;
            return zoom;
        }

        private static Vector2 ReadMousePosition()
        {
            var mouse = Mouse.current;
            return mouse != null ? mouse.position.ReadValue() : new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        }
#else
        private static bool IsDigitPressed(int digit)
        {
            switch (digit)
            {
                case 1: return Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1);
                case 2: return Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2);
                case 3: return Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3);
                case 4: return Input.GetKeyDown(KeyCode.Alpha4) || Input.GetKeyDown(KeyCode.Keypad4);
                case 5: return Input.GetKeyDown(KeyCode.Alpha5) || Input.GetKeyDown(KeyCode.Keypad5);
                case 6: return Input.GetKeyDown(KeyCode.Alpha6) || Input.GetKeyDown(KeyCode.Keypad6);
                case 7: return Input.GetKeyDown(KeyCode.Alpha7) || Input.GetKeyDown(KeyCode.Keypad7);
                case 8: return Input.GetKeyDown(KeyCode.Alpha8) || Input.GetKeyDown(KeyCode.Keypad8);
                case 9: return Input.GetKeyDown(KeyCode.Alpha9) || Input.GetKeyDown(KeyCode.Keypad9);
                default: return false;
            }
        }

        private static bool IsNextPressed()
        {
            return Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D) || Input.GetKeyDown(KeyCode.E);
        }

        private static bool IsPrevPressed()
        {
            return Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A) || Input.GetKeyDown(KeyCode.Q);
        }

        private static bool IsToggleHudPressed()
        {
            return Input.GetKeyDown(KeyCode.H);
        }

        private static bool IsRightMouseHeld()
        {
            return Input.GetMouseButton(1) || Input.GetMouseButton(0);
        }

        private static Vector2 ReadMouseDelta()
        {
            return new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y")) * 10f;
        }

        private static float ReadScrollWheel()
        {
            return Input.GetAxisRaw("Mouse ScrollWheel") * 10f;
        }

        private static float ReadZoomKeys()
        {
            float zoom = 0f;
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.Equals) || Input.GetKey(KeyCode.KeypadPlus)) zoom += 1f;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.Minus) || Input.GetKey(KeyCode.KeypadMinus)) zoom -= 1f;
            return zoom;
        }

        private static Vector2 ReadMousePosition()
        {
            return Input.mousePosition;
        }
#endif
    }
}
