using System.Collections.Generic;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// 라인 끝 출고 공정. 라인을 완주한 차량을 넘겨받아(TryDepart) 즉시 사라지게 하는 대신:
///
///   부릉부릉(제자리 레브 진동) → 가속 주행(도착 지점을 향해) → 도착 → 풀 반환
///
/// - 씬에 배치하고 arrivalPoint(도착 지점)를 지정한다. 미지정 시 이 오브젝트 위치가 도착 지점.
/// - 씬에 이 공정이 없으면 CarController가 기존대로 즉시 풀 반환한다 (TryDepart=false).
/// - 넘겨받는 즉시 LeaveLine()으로 트래픽 관리에서 해제한다 — 출고 중인 차가
///   라인 끝(progress 1.0)의 장애물로 남아 뒷차를 막는 것을 방지.
/// - 판매 대금($100)/불량 판정은 CarController가 라인 끝에서 이미 처리했고, 여기는 이동 연출만 담당.
/// </summary>
public class DepartureStation : MonoBehaviour
{
    /// <summary>씬에 배치된 현재 출고 공정 (없으면 null → CarController가 즉시 풀 반환).</summary>
    public static DepartureStation Current { get; private set; }

    [Header("도착 지점")]
    [Tooltip("차량이 주행해 갈 도착 지점. 비워두면 이 오브젝트 위치를 사용한다.")]
    public Transform arrivalPoint;
    [Tooltip("도착 지점에서 이 거리(m) 이내로 들어오면 도착 판정 → 풀 반환")]
    public float arriveRadius = 0.6f;

    [Header("부릉부릉 (레브 연출)")]
    [Tooltip("출발 전 제자리에서 차체가 떨리는 시간(초)")]
    public float revDuration = 0.6f;
    [Tooltip("레브 시 앞뒤 흔들림 각도(도)")]
    public float revPitchAngle = 1.5f;
    [Tooltip("레브 시 좌우 흔들림 각도(도)")]
    public float revRollAngle = 2.5f;
    [Tooltip("레브 진동 속도")]
    public float revFrequency = 28f;

    [Header("출고 주행")]
    [Tooltip("가속도(m/s²). 0에서 시작해 점점 빨라지며 튀어나간다.")]
    public float acceleration = 8f;
    public float maxSpeed = 12f;
    [Tooltip("도착 지점을 향해 차체가 회전하는 속도(도/초)")]
    public float turnSpeed = 360f;

    private class Departing
    {
        public CarController car;
        public float timer;
        public float speed;
        public Quaternion baseRot; // 레브 진동의 기준 회전 (캡처 시점)
    }

    private readonly List<Departing> departing = new List<Departing>();

    private void OnEnable() { Current = this; }

    private void OnDisable()
    {
        if (Current == this) Current = null;
        // 출고 중이던 차량은 그대로 얼지 않도록 전부 풀로 반환하고 정리
        for (int i = 0; i < departing.Count; i++)
            ReturnToPool(departing[i].car);
        departing.Clear();
    }

    /// <summary>씬에 출고 공정이 있으면 차량을 넘겨받고 true, 없으면 false(호출자가 즉시 풀 반환).</summary>
    public static bool TryDepart(CarController car)
    {
        if (car == null || Current == null || !Current.isActiveAndEnabled) return false;
        Current.Depart(car);
        return true;
    }

    private void Depart(CarController car)
    {
        car.LeaveLine(); // 트래픽 관리 해제 — 뒷차의 장애물로 남지 않게
        departing.Add(new Departing { car = car, baseRot = car.transform.rotation });
    }

    private Vector3 Destination => arrivalPoint != null ? arrivalPoint.position : transform.position;

    private void Update()
    {
        Vector3 dest = Destination;

        for (int i = departing.Count - 1; i >= 0; i--)
        {
            Departing d = departing[i];
            if (d.car == null || !d.car.gameObject.activeInHierarchy)
            {
                departing.RemoveAt(i); // 외부 요인으로 사라진 차량(풀 반환 등)은 목록에서만 정리
                continue;
            }

            d.timer += Time.deltaTime;
            Transform tf = d.car.transform;

            // 1) 부릉부릉: 제자리에서 차체가 미세하게 떨린다 (사인파 피치/롤 진동)
            if (d.timer < revDuration)
            {
                float pitch = Mathf.Sin(d.timer * revFrequency) * revPitchAngle;
                float roll = Mathf.Sin(d.timer * revFrequency * 1.31f) * revRollAngle;
                tf.rotation = d.baseRot * Quaternion.Euler(pitch, 0f, roll);
                continue;
            }

            // 2) 가속 주행: 도착 지점을 향해 회전하며 점점 빨라진다
            Vector3 to = dest - tf.position;
            if (to.sqrMagnitude <= arriveRadius * arriveRadius)
            {
                // 3) 도착: 풀로 반환 (파괴하지 않고 재사용)
                ReturnToPool(d.car);
                departing.RemoveAt(i);
                continue;
            }

            d.speed = Mathf.Min(maxSpeed, d.speed + acceleration * Time.deltaTime);
            tf.rotation = Quaternion.RotateTowards(
                tf.rotation, Quaternion.LookRotation(to.normalized), turnSpeed * Time.deltaTime);
            tf.position = Vector3.MoveTowards(tf.position, dest, d.speed * Time.deltaTime);
        }
    }

    private static void ReturnToPool(CarController car)
    {
        if (car == null || !car.gameObject.activeInHierarchy) return;
        if (CarPool.Instance != null) CarPool.Instance.Return(car);
        else car.gameObject.SetActive(false);
    }

    // 도착 지점(주황 구 + 도착 반경)과 공정 → 도착 연결선을 씬 뷰에 표시
    private void OnDrawGizmos()
    {
        Vector3 dest = Destination;

        Gizmos.color = new Color(1f, 0.6f, 0.1f, 1f);
        Gizmos.DrawWireSphere(dest, arriveRadius);
        Gizmos.DrawLine(transform.position, dest);
        Gizmos.DrawRay(dest, Vector3.up * 1.5f);

#if UNITY_EDITOR
        Handles.Label(dest + Vector3.up * 1.7f, $"출고 도착 지점 (r={arriveRadius:F1}m)");
#endif
    }
}
