using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 유저 클릭/터치 라우팅. 관리형 게임의 "직접 개입" 수단을 한곳에서 처리한다.
///
/// 클릭 대상별 동작:
/// - <b>부품 파일(PartStack)</b> : 재고를 refillAmount만큼 응급 보충. 이후 전역 쿨타임이 돌아
///   다른 공정도 보충할 수 없다 — 클릭은 어디까지나 응급 수단이고, 상시 보충은 보급 작업자의 일이다.
/// - <b>창고(warehouse)</b> : 보충 쿨타임을 즉시 초기화한다. 대가가 '시간'이 아니라 '주의력'인 구조 —
///   카메라를 창고로 옮기는 동안 라인을 볼 수 없다. 라인이 커질수록 이 왕복이 부담스러워지고
///   자연스럽게 보급 작업자에게 일을 넘기게 된다.
/// - <b>작업자(Worker)</b> : 작업 중이면 체결량을 추가(참여형 개입), 유휴 + 컨디션이 깎여 있으면
///   선제 휴식을 시킨다(강제 휴식보다 짧고 라인이 멈추지 않는다).
///
/// 참고: 레이어 충돌 매트릭스 제약(Car 6 ↔ Station 7 / Robot 8만 허용)은 <b>트리거 감지에만</b>
/// 적용된다. Physics.Raycast는 매트릭스와 무관하므로 클릭 대상 레이어는 자유롭게 잡을 수 있다.
/// </summary>
public class InputManager : MonoBehaviour
{
    [Header("레이캐스트")]
    [Tooltip("클릭 판정에 사용할 레이어. 기본값(everything)이면 모든 레이어를 검사한다.")]
    public LayerMask clickMask = ~0;

    [Tooltip("레이캐스트 최대 거리.")]
    public float rayDistance = 500f;

    [Header("재고 응급 보충")]
    [Tooltip("파일을 한 번 클릭할 때 채워지는 재고 수.")]
    public int refillAmount = 3;

    [Tooltip("보충 후 다시 보충할 수 있게 되기까지의 전역 쿨타임(초). 창고를 클릭하면 즉시 초기화된다.")]
    public float refillCooldown = 8f;

    [Header("작업 참여")]
    [Tooltip("작업 중인 작업자를 클릭할 때마다 추가되는 작업량(work).")]
    public float clickWorkAmount = 2f;

    [Header("창고")]
    [Tooltip("이 오브젝트(또는 그 자식)를 클릭하면 보충 쿨타임이 초기화된다.")]
    public Transform warehouse;

    private float refillTimer = 0f;

    /// <summary>남은 보충 쿨타임(초). 0이면 즉시 보충 가능 — UI 표시용.</summary>
    public float RefillCooldownRemaining => Mathf.Max(0f, refillTimer);

    /// <summary>지금 재고를 보충할 수 있는지 — UI 표시용.</summary>
    public bool CanRefillNow => refillTimer <= 0f;

    private void Update()
    {
        if (refillTimer > 0f) refillTimer -= Time.deltaTime;

        // Input System 패키지 기준. Pointer는 마우스/터치를 모두 포괄하므로 모바일도 동일하게 작동한다.
        Pointer pointer = Pointer.current;
        if (pointer == null || !pointer.press.wasPressedThisFrame) return;

        Camera cam = Camera.main;
        if (cam == null) return;

        Ray ray = cam.ScreenPointToRay(pointer.position.ReadValue());
        if (!Physics.Raycast(ray, out RaycastHit hit, rayDistance, clickMask)) return;

        HandleClick(hit);
    }

    private void HandleClick(RaycastHit hit)
    {
        Transform hitTf = hit.transform;

        // 1) 창고 — 보충 쿨타임 초기화
        if (warehouse != null && (hitTf == warehouse || hitTf.IsChildOf(warehouse)))
        {
            refillTimer = 0f;
            Debug.Log("[Input] 창고 방문 — 보충 쿨타임 초기화");
            return;
        }

        // 2) 부품 파일 — 재고 응급 보충
        // 콜라이더가 자식(쌓인 부품 인스턴스 등)에 있을 수 있으므로 부모까지 올라가며 찾는다
        PartStack stack = hitTf.GetComponentInParent<PartStack>();
        if (stack != null)
        {
            TryRefill(stack);
            return;
        }

        // 3) 작업자 — 작업 가속 또는 선제 휴식
        Worker worker = hitTf.GetComponentInParent<Worker>();
        if (worker != null)
        {
            HandleWorkerClick(worker);
            return;
        }
    }

    private void TryRefill(PartStack stack)
    {
        if (!CanRefillNow)
        {
            Debug.Log($"[Input] 보충 쿨타임 {RefillCooldownRemaining:F1}초 남음 — 창고를 클릭하면 즉시 초기화된다");
            return;
        }

        if (stack.IsFull)
        {
            Debug.Log($"[Input] {stack.name} 재고가 이미 가득 찼다 ({stack.Count}/{stack.Capacity})");
            return;
        }

        int added = stack.Add(refillAmount);
        if (added <= 0) return;

        refillTimer = refillCooldown;
        Debug.Log($"[Input] {stack.name} 재고 +{added} → {stack.Count}/{stack.Capacity} (쿨타임 {refillCooldown}초)");
    }

    private void HandleWorkerClick(Worker worker)
    {
        // 작업 중이면 체결을 도와준다
        if (worker.TryBoostWork(clickWorkAmount)) return;

        // 유휴 + 컨디션이 깎여 있으면 미리 쉬게 한다(라인 정지 예방)
        if (worker.TryPreemptiveRest())
        {
            Debug.Log($"[Input] {worker.name} 선제 휴식 (컨디션 {worker.condition:F0}%)");
            return;
        }
    }
}
