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
///
/// 이 컴포넌트는 카메라 오브젝트에 직접 붙여도 되고, 별도의 빈 오브젝트에 붙여도 된다.
/// 별도 오브젝트에 붙인 경우 <see cref="targetCamera"/>가 비어 있으면 Camera.main을 자동으로 잡는다.
/// </summary>
public class CameraController : MonoBehaviour
{
    [Header("대상 카메라")]
    [Tooltip("움직일 카메라의 Transform. 비워두면 Camera.main(태그 MainCamera)을 자동으로 찾는다.")]
    public Transform targetCamera;

    [Header("패닝")]
    [Tooltip("드래그 이동 속도 (줌 거리에 비례)")]
    public float panSpeed = 0.25f;

    [Header("줌 (Orthographic Size 기반)")]
    public float zoomSpeed = 4f;
    [Tooltip("orthographicSize 최솟값. Start()에서 카메라의 현재 orthographicSize를 기준으로 자동 추정한다 (인스펙터에서 직접 조정 가능).")]
    public float minOrthoSize = 3f;
    [Tooltip("orthographicSize 최댓값. Start()에서 카메라의 현재 orthographicSize를 기준으로 자동 추정한다 (인스펙터에서 직접 조정 가능).")]
    public float maxOrthoSize = 80f;
    public float zoomSmoothing = 12f;

    [Header("줌 폴백 (Perspective 카메라용, orthographic이 아닐 때만 사용)")]
    public float minDistance = 3f;
    public float maxDistance = 80f;

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

    // 줌 대상 카메라 (orthographicSize 조절용, 캐시)
    private Camera targetCam;
    private bool useOrthoZoom;
    private float targetOrthoSize;
    private float currentOrthoSize;
    private bool warnedNoCamera;

    /// <summary>실제로 움직일 Transform. targetCamera가 없으면 자기 자신.</summary>
    private Transform Rig => targetCamera != null ? targetCamera : transform;

    // ─────────────────────────────────────────────
    // 초기화
    // ─────────────────────────────────────────────

    private void Start()
    {
        // 별도 오브젝트에 붙어 있고 대상이 지정되지 않았다면 메인 카메라를 자동으로 잡는다
        if (targetCamera == null)
        {
            Camera cam = GetComponent<Camera>();
            if (cam == null) cam = Camera.main;
            if (cam != null) targetCamera = cam.transform;
            else Debug.LogWarning("[CameraController] 움직일 카메라를 찾지 못했다 — targetCamera를 인스펙터에서 지정할 것.");
        }

        // 줌에 쓸 Camera 컴포넌트 확보 (targetCamera가 Transform이므로 GetComponent로 얻는다)
        if (targetCamera != null) targetCam = targetCamera.GetComponent<Camera>();
        if (targetCam == null) targetCam = Camera.main;
        if (targetCam == null && !warnedNoCamera)
        {
            warnedNoCamera = true;
            Debug.LogWarning("[CameraController] 줌에 사용할 Camera 컴포넌트를 찾지 못했다 — orthographicSize 줌이 동작하지 않는다.");
        }

        useOrthoZoom = targetCam != null && targetCam.orthographic;

        if (useOrthoZoom)
        {
            // 씬의 현재 orthographicSize를 기준으로 클램프 범위를 자동 추정 (0.3배~3배)
            float baseSize = targetCam.orthographicSize;
            if (baseSize <= 0f) baseSize = 10f;
            minOrthoSize = baseSize * 0.3f;
            maxOrthoSize = baseSize * 3f;
            targetOrthoSize = baseSize;
            currentOrthoSize = baseSize;
        }

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

        if (useOrthoZoom)
            currentOrthoSize = Mathf.Lerp(currentOrthoSize, targetOrthoSize, Time.deltaTime * zoomSmoothing);
        else
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

        Transform rig = Rig;
        Vector3 right   = new Vector3(rig.right.x,   0f, rig.right.z).normalized;
        Vector3 forward = new Vector3(rig.forward.x, 0f, rig.forward.z).normalized;

        float scale = currentDistance * panSpeed * 0.01f;
        pivot -= right   * delta.x * scale;
        pivot -= forward * delta.y * scale;
    }

    // ─────────────────────────────────────────────
    // 줌 (마우스 휠)
    // ─────────────────────────────────────────────

    private void HandleZoom(Mouse mouse)
    {
        // ★ 스케일 주의: Input System 1.x의 scroll.y는 프로젝트 설정
        //   (Input System Package ▸ Scroll Delta Behavior)에 따라 두 가지 스케일로 들어온다.
        //     · Uniform Across All Platforms (기본값) → 노치당 ±1
        //     · Kept At Platform Specific Range      → Windows에서 노치당 ±120
        //   무조건 120으로 나누면 기본 설정에서 노치당 0.008이 되어 줌이 사실상 안 먹는다.
        //   그래서 값의 크기를 보고 120 스케일일 때만 나눠 노치당 ±1로 맞춘다.
        float raw = mouse.scroll.ReadValue().y;
        float scroll = Mathf.Abs(raw) >= 10f ? raw / 120f : raw;
        if (Mathf.Abs(scroll) < 0.001f) return;

        if (useOrthoZoom)
        {
            // 휠을 위로(양수) 굴리면 확대(orthographicSize 감소)되도록 방향을 기존 거리줌과 동일하게 맞춘다
            targetOrthoSize -= scroll * zoomSpeed;
            targetOrthoSize = Mathf.Clamp(targetOrthoSize, minOrthoSize, maxOrthoSize);
        }
        else
        {
            // orthographic이 아닌 경우의 안전망: 기존 거리 기반 줌
            targetDistance -= scroll * zoomSpeed;
            targetDistance = Mathf.Clamp(targetDistance, minDistance, maxDistance);
        }
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
        Transform rig = Rig;
        Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
        rig.rotation = rotation;
        rig.position = pivot - rotation * Vector3.forward * currentDistance;

        if (useOrthoZoom && targetCam != null)
            targetCam.orthographicSize = currentOrthoSize;
    }
}
