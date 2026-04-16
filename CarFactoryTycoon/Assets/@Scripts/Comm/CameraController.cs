using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 공장 뷰 전용 카메라 컨트롤러 (New Input System).
///
/// 조작법:
///   우클릭 드래그 / 중클릭 드래그  → 패닝 (XZ 평면 이동)
///   마우스 휠                        → 줌 인/아웃
///   Alt + 좌클릭 드래그             → 피벗 기준 오빗(궤도) 회전
///   F 키                             → 초기 뷰로 리셋
/// </summary>
public class CameraController : MonoBehaviour
{
    [Header("패닝")]
    [Tooltip("드래그 이동 속도 (줌 거리에 비례)")]
    public float panSpeed = 0.25f;

    [Header("줌")]
    public float zoomSpeed = 4f;
    public float minDistance = 3f;
    public float maxDistance = 80f;
    public float zoomSmoothing = 12f;

    [Header("오빗 회전 (Alt + 좌클릭 드래그)")]
    public float orbitSpeed = 200f;
    [Tooltip("수직 회전 최솟값 (도)")]
    public float minPitch = 10f;
    [Tooltip("수직 회전 최댓값 (도)")]
    public float maxPitch = 85f;

    [Header("초기 뷰")]
    public Vector3 initialPivot = Vector3.zero;
    public float initialYaw = 45f;
    public float initialPitch = 50f;
    public float initialDistance = 20f;

    // 내부 상태
    private Vector3 pivot;
    private float yaw;
    private float pitch;
    private float targetDistance;
    private float currentDistance;

    // ─────────────────────────────────────────────
    // 초기화
    // ─────────────────────────────────────────────

    private void Start()
    {
        ResetView();
        currentDistance = targetDistance;
        ApplyTransform();
    }

    // ─────────────────────────────────────────────
    // 매 프레임
    // ─────────────────────────────────────────────

    private void Update()
    {
        var mouse    = Mouse.current;
        var keyboard = Keyboard.current;

        if (mouse == null) return;

        HandlePan(mouse, keyboard);
        HandleZoom(mouse);
        HandleOrbit(mouse, keyboard);

        if (keyboard != null && keyboard.fKey.wasPressedThisFrame)
            ResetView();

        currentDistance = Mathf.Lerp(currentDistance, targetDistance, Time.deltaTime * zoomSmoothing);
        ApplyTransform();
    }

    // ─────────────────────────────────────────────
    // 패닝 (우/중 클릭 드래그)
    // ─────────────────────────────────────────────

    private void HandlePan(Mouse mouse, Keyboard keyboard)
    {
        bool isPanButton = mouse.rightButton.isPressed || mouse.middleButton.isPressed;
        bool isOrbiting  = keyboard != null && keyboard.leftAltKey.isPressed && mouse.leftButton.isPressed;

        if (!isPanButton || isOrbiting) return;

        Vector2 delta = mouse.delta.ReadValue();
        if (delta.sqrMagnitude < 0.001f) return;

        Vector3 right   = new Vector3(transform.right.x,   0f, transform.right.z).normalized;
        Vector3 forward = new Vector3(transform.forward.x, 0f, transform.forward.z).normalized;

        float scale = currentDistance * panSpeed * 0.01f;
        pivot -= right   * delta.x * scale;
        pivot -= forward * delta.y * scale;
    }

    // ─────────────────────────────────────────────
    // 줌 (마우스 휠)
    // ─────────────────────────────────────────────

    private void HandleZoom(Mouse mouse)
    {
        // New Input System의 scroll.y 는 Windows 기준 노치당 ±120
        // 120으로 나눠 노치당 ±1 로 정규화
        float scroll = mouse.scroll.ReadValue().y / 120f;
        if (Mathf.Abs(scroll) < 0.001f) return;

        targetDistance -= scroll * zoomSpeed;
        targetDistance = Mathf.Clamp(targetDistance, minDistance, maxDistance);
    }

    // ─────────────────────────────────────────────
    // 오빗 (Alt + 좌클릭 드래그)
    // ─────────────────────────────────────────────

    private void HandleOrbit(Mouse mouse, Keyboard keyboard)
    {
        if (keyboard == null) return;
        if (!keyboard.leftAltKey.isPressed || !mouse.leftButton.isPressed) return;

        Vector2 delta = mouse.delta.ReadValue();
        if (delta.sqrMagnitude < 0.001f) return;

        yaw   += delta.x * orbitSpeed * Time.deltaTime;
        pitch -= delta.y * orbitSpeed * Time.deltaTime;
        pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
    }

    // ─────────────────────────────────────────────
    // 뷰 리셋 (F 키)
    // ─────────────────────────────────────────────

    private void ResetView()
    {
        pivot          = initialPivot;
        yaw            = initialYaw;
        pitch          = initialPitch;
        targetDistance = initialDistance;
    }

    // ─────────────────────────────────────────────
    // 최종 Transform 적용
    // ─────────────────────────────────────────────

    private void ApplyTransform()
    {
        Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
        transform.rotation = rotation;
        transform.position = pivot - rotation * Vector3.forward * currentDistance;
    }
}
