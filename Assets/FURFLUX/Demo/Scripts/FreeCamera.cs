using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace FurRendering.Demo
{
    /// <summary>
    /// A Scene-view style fly camera for looking at the demo coats in Play Mode.
    ///
    /// Controls:
    ///   Right mouse (hold)  look around
    ///   W A S D             move forward / left / back / right
    ///   Q / E               move down / up
    ///   Shift               faster
    ///   Ctrl                slower, for fine positioning
    ///   Mouse wheel         change the base speed
    ///
    /// Works with both the Input System package and the legacy Input Manager.
    /// </summary>
    [AddComponentMenu("FURFLUX/Demo/Free Camera")]
    [RequireComponent(typeof(Camera))]
    public sealed class FreeCamera : MonoBehaviour
    {
        [Header("Movement")]
        [Tooltip("Base movement speed in m/s. The mouse wheel changes it in Play Mode.")]
        [SerializeField] private float moveSpeed = 5f;

        [Tooltip("Speed multiplier while Shift is held.")]
        [SerializeField] private float boostMultiplier = 4f;

        [Tooltip("Speed multiplier while Ctrl is held, for fine positioning.")]
        [SerializeField] private float slowMultiplier = 0.2f;

        [Tooltip("Movement smoothing. 0 starts and stops instantly; higher is smoother.")]
        [Range(0f, 1f)]
        [SerializeField] private float movementSmoothing = 0.15f;

        [Header("Look")]
        [Tooltip("Mouse sensitivity, in degrees per pixel.")]
        [SerializeField] private float lookSensitivity = 0.12f;

        [Tooltip("How far up or down the camera may look, in degrees.")]
        [Range(1f, 90f)]
        [SerializeField] private float pitchLimit = 89f;

        [Tooltip("Look smoothing.")]
        [Range(0f, 1f)]
        [SerializeField] private float lookSmoothing = 0.1f;

        [Header("Other")]
        [Tooltip("Lock and hide the cursor while the right mouse button is held.")]
        [SerializeField] private bool lockCursorWhileLooking = true;

        [Tooltip("How much one mouse-wheel step changes the base speed.")]
        [SerializeField] private float scrollSpeedStep = 0.15f;

        [SerializeField] private float minSpeed = 0.05f;
        [SerializeField] private float maxSpeed = 200f;

        private float _yaw;
        private float _pitch;
        private float _smoothedYaw;
        private float _smoothedPitch;
        private Vector3 _currentVelocity;
        private bool _cursorLocked;

        private void OnEnable()
        {
            // Start from the rotation the camera was left at in the scene.
            Vector3 euler = transform.eulerAngles;
            _yaw = _smoothedYaw = euler.y;
            _pitch = _smoothedPitch = NormalizeAngle(euler.x);
        }

        private void OnDisable()
        {
            SetCursorLocked(false);
        }

        private void Update()
        {
            // Unscaled, so the camera still flies while the game is paused.
            float deltaTime = Time.unscaledDeltaTime;

            bool looking = IsLookButtonHeld();
            SetCursorLocked(lockCursorWhileLooking && looking);

            UpdateSpeedFromScroll();
            UpdateRotation(looking, deltaTime);
            UpdateMovement(deltaTime);
        }

        // ------------------------------------------------------------------ //
        //  Rotation
        // ------------------------------------------------------------------ //
        private void UpdateRotation(bool looking, float deltaTime)
        {
            if (looking)
            {
                Vector2 mouseDelta = ReadMouseDelta();
                _yaw += mouseDelta.x * lookSensitivity;
                _pitch -= mouseDelta.y * lookSensitivity;
                _pitch = Mathf.Clamp(_pitch, -pitchLimit, pitchLimit);
            }

            // Frame-rate independent exponential smoothing.
            float lookLerp = SmoothingToLerp(lookSmoothing, deltaTime);
            _smoothedYaw = Mathf.LerpAngle(_smoothedYaw, _yaw, lookLerp);
            _smoothedPitch = Mathf.Lerp(_smoothedPitch, _pitch, lookLerp);

            transform.rotation = Quaternion.Euler(_smoothedPitch, _smoothedYaw, 0f);
        }

        // ------------------------------------------------------------------ //
        //  Movement
        // ------------------------------------------------------------------ //
        private void UpdateMovement(float deltaTime)
        {
            Vector3 input = ReadMoveInput();

            float speed = moveSpeed;
            if (IsBoostHeld()) speed *= boostMultiplier;
            if (IsSlowHeld()) speed *= slowMultiplier;

            Vector3 targetVelocity = transform.TransformDirection(input.normalized) * speed;

            float moveLerp = SmoothingToLerp(movementSmoothing, deltaTime);
            _currentVelocity = Vector3.Lerp(_currentVelocity, targetVelocity, moveLerp);

            transform.position += _currentVelocity * deltaTime;
        }

        private void UpdateSpeedFromScroll()
        {
            float scroll = ReadScroll();
            if (Mathf.Approximately(scroll, 0f))
                return;

            // Multiplicative, so steps are fine at low speeds and coarse at high ones.
            moveSpeed = Mathf.Clamp(moveSpeed * (1f + scroll * scrollSpeedStep), minSpeed, maxSpeed);
        }

        /// <summary>Turns a 0..1 smoothing amount into a frame-rate independent lerp factor.</summary>
        private static float SmoothingToLerp(float smoothing, float deltaTime)
        {
            if (smoothing <= 0f)
                return 1f;

            float halfLife = Mathf.Lerp(0.001f, 0.25f, smoothing);
            return 1f - Mathf.Exp(-deltaTime / halfLife);
        }

        private static float NormalizeAngle(float angle)
        {
            angle %= 360f;
            if (angle > 180f) angle -= 360f;
            return angle;
        }

        private void SetCursorLocked(bool locked)
        {
            if (_cursorLocked == locked)
                return;

            _cursorLocked = locked;
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        // ------------------------------------------------------------------ //
        //  Input - the one place that differs per input backend
        // ------------------------------------------------------------------ //
#if ENABLE_INPUT_SYSTEM

        private static bool IsLookButtonHeld()
        {
            var mouse = Mouse.current;
            return mouse != null && mouse.rightButton.isPressed;
        }

        private static Vector2 ReadMouseDelta()
        {
            var mouse = Mouse.current;
            return mouse != null ? mouse.delta.ReadValue() : Vector2.zero;
        }

        private static float ReadScroll()
        {
            var mouse = Mouse.current;
            return mouse != null ? mouse.scroll.ReadValue().y * 0.01f : 0f;
        }

        private static bool IsBoostHeld()
        {
            var kb = Keyboard.current;
            return kb != null && (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed);
        }

        private static bool IsSlowHeld()
        {
            var kb = Keyboard.current;
            return kb != null && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed);
        }

        private static Vector3 ReadMoveInput()
        {
            var kb = Keyboard.current;
            if (kb == null)
                return Vector3.zero;

            Vector3 move = Vector3.zero;
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed)    move.z += 1f;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed)  move.z -= 1f;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) move.x += 1f;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed)  move.x -= 1f;
            if (kb.eKey.isPressed)                               move.y += 1f;
            if (kb.qKey.isPressed)                               move.y -= 1f;
            return move;
        }

#else // Legacy Input Manager

        private static bool IsLookButtonHeld()  { return Input.GetMouseButton(1); }
        private static Vector2 ReadMouseDelta() { return new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y")) * 10f; }
        private static float ReadScroll()       { return Input.GetAxisRaw("Mouse ScrollWheel") * 10f; }
        private static bool IsBoostHeld()       { return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift); }
        private static bool IsSlowHeld()        { return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl); }

        private static Vector3 ReadMoveInput()
        {
            Vector3 move = Vector3.zero;
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow))    move.z += 1f;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow))  move.z -= 1f;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) move.x += 1f;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow))  move.x -= 1f;
            if (Input.GetKey(KeyCode.E))                                     move.y += 1f;
            if (Input.GetKey(KeyCode.Q))                                     move.y -= 1f;
            return move;
        }

#endif
    }
}
