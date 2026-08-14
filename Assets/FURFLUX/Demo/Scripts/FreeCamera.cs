using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Play Mode'da serbest dolaşan kamera (Scene View benzeri kontrol).
///
/// Kontroller:
///   Sağ mouse (basılı tut)  : bak / döndür
///   W A S D                 : ileri, sol, geri, sağ
///   Q / E                   : aşağı / yukarı
///   Shift                   : hızlan
///   Ctrl                    : yavaşla (hassas hareket)
///   Mouse tekerleği         : temel hızı değiştir
///
/// Not: Bu proje "Input System Package (New)" kullanıyor; script her iki
/// input backend'inde de derlenecek şekilde yazıldı.
/// </summary>
[AddComponentMenu("Camera/Free Camera")]
[RequireComponent(typeof(Camera))]
public sealed class FreeCamera : MonoBehaviour
{
    [Header("Hareket")]
    [Tooltip("Temel hareket hızı (m/s). Mouse tekerleği ile oyun içinde değiştirilebilir.")]
    [SerializeField] private float moveSpeed = 5f;

    [Tooltip("Shift basılıyken hız çarpanı.")]
    [SerializeField] private float boostMultiplier = 4f;

    [Tooltip("Ctrl basılıyken hız çarpanı (hassas hareket).")]
    [SerializeField] private float slowMultiplier = 0.2f;

    [Tooltip("Hareketin yumuşaması. 0 = anında dur/başla, yüksek = daha akıcı.")]
    [Range(0f, 1f)]
    [SerializeField] private float movementSmoothing = 0.15f;

    [Header("Bakış")]
    [Tooltip("Mouse hassasiyeti (derece / piksel).")]
    [SerializeField] private float lookSensitivity = 0.12f;

    [Tooltip("Yukarı/aşağı bakış sınırı (derece).")]
    [Range(1f, 90f)]
    [SerializeField] private float pitchLimit = 89f;

    [Tooltip("Bakışın yumuşaması.")]
    [Range(0f, 1f)]
    [SerializeField] private float lookSmoothing = 0.1f;

    [Header("Diğer")]
    [Tooltip("Sağ mouse basılıyken imleci kilitle ve gizle.")]
    [SerializeField] private bool lockCursorWhileLooking = true;

    [Tooltip("Mouse tekerleğinin temel hızı ne kadar değiştireceği.")]
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
        // Mevcut rotasyondan başla; kamerayı sahnede nasıl bıraktıysan oradan devam eder.
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
        // Zaman ölçeğinden bağımsız çalışsın (Time.timeScale = 0 olsa bile).
        float deltaTime = Time.unscaledDeltaTime;

        bool looking = IsLookButtonHeld();
        SetCursorLocked(lockCursorWhileLooking && looking);

        UpdateSpeedFromScroll();
        UpdateRotation(looking, deltaTime);
        UpdateMovement(deltaTime);
    }

    // ------------------------------------------------------------------ //
    //  Dönüş
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

        // Kare hızından bağımsız üstel yumuşatma
        float lookLerp = SmoothingToLerp(lookSmoothing, deltaTime);
        _smoothedYaw = Mathf.LerpAngle(_smoothedYaw, _yaw, lookLerp);
        _smoothedPitch = Mathf.Lerp(_smoothedPitch, _pitch, lookLerp);

        transform.rotation = Quaternion.Euler(_smoothedPitch, _smoothedYaw, 0f);
    }

    // ------------------------------------------------------------------ //
    //  Hareket
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

        // Çarpımsal artış: yavaş hızlarda ince, yüksek hızlarda kaba adımlar.
        moveSpeed = Mathf.Clamp(moveSpeed * (1f + scroll * scrollSpeedStep), minSpeed, maxSpeed);
    }

    /// <summary>Yumuşatma değerini kare hızından bağımsız lerp katsayısına çevirir.</summary>
    private static float SmoothingToLerp(float smoothing, float deltaTime)
    {
        if (smoothing <= 0f)
            return 1f;

        // smoothing 1'e yaklaştıkça sönümleme süresi uzar.
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
    //  Input okuma (backend'e göre ayrışan tek yer)
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

#else // Eski Input Manager

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
