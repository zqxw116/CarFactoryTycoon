using UnityEngine;

/// <summary>
/// 사람 작업자가 두 손으로 부품을 잡을 지점(부품 로컬)을 수동으로 지정한다 — 선택 사항.
///
/// 붙이지 않으면 WorkerHandIK.TryComputeAutoGrip이 부품 메시 바운즈의 긴 수평축 양끝(몸 쪽 면)을
/// 자동으로 잡는다(범퍼처럼 길쭉한 부품은 자동값으로 충분하다).
/// 자동 계산이 어색한 부품(문짝·바퀴처럼 손 위치가 특이한 것)만 AssemblyPart와 같은 오브젝트에 붙인다.
/// 좌/우 구분은 하지 않는다 — Worker가 운반 자세(carryEuler) 기준으로 몸 오른쪽에 오는 점을 오른손에 배정한다.
/// </summary>
public class PartGripOverride : MonoBehaviour
{
    [Tooltip("잡이 지점 A (부품 로컬, m).")]
    public Vector3 gripA = new Vector3(-0.3f, 0f, 0f);

    [Tooltip("잡이 지점 B (부품 로컬, m).")]
    public Vector3 gripB = new Vector3(0.3f, 0f, 0f);

    [Tooltip("손가락이 향할 방향(부품 로컬). 운반 시 몸에서 멀어지는 쪽 — 손목은 잡이 지점에서 이 반대쪽으로 물러난다.")]
    public Vector3 fingerDir = Vector3.forward;

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.2f, 1f, 0.4f, 1f);
        Vector3 a = transform.TransformPoint(gripA), b = transform.TransformPoint(gripB);
        Gizmos.DrawWireSphere(a, 0.04f);
        Gizmos.DrawWireSphere(b, 0.04f);
        Gizmos.DrawLine(a, b);
        Vector3 f = transform.TransformDirection(fingerDir.normalized) * 0.1f;
        Gizmos.DrawLine(a, a + f);
        Gizmos.DrawLine(b, b + f);
    }
}
