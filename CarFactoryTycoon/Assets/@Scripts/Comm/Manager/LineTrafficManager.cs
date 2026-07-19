using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 라인 위 차량들의 차간 간격(정체)을 관리하는 트래픽 매니저 — 어큐뮬레이션 컨베이어 방식.
/// 뒷차는 앞차 꽁무니 minGap(m)까지 접근한 뒤 정지하고, 앞차가 빠지면 다시 흐른다.
///
/// - 활성 차량을 pathProgress 내림차순(선두=0번)으로 유지한다. 스폰은 항상 라인 시작(꼬리)이고
///   추월이 없으므로(뒷차는 앞차를 넘지 못하게 클램프) 등록 시 정렬 삽입만으로 순서가 유지된다.
/// - 이동 주도권을 이 매니저가 갖는다: 매 프레임 선두→후미 순서로 CarController.MoveStep을 호출해
///   같은 프레임에 이미 갱신된 앞차 값 기준으로 클램프한다(스크립트 실행순서/1프레임 지연 이슈 없음).
/// - 이 매니저가 씬에 없으면 CarController가 기존대로 자율 이동한다(TestPartsScene 등 무변경).
/// - WheelStation은 게이트로 등록된다: 게이트를 아직 통과하지 않은 차량은 게이트 지점을 넘지 못하고
///   감속 정지 → 리프트가 대기 차량을 캡처한다. 방출은 SetProgress로 게이트 너머로 넘겨 처리.
/// </summary>
public class LineTrafficManager : MonoSingleton<LineTrafficManager>
{

    [Header("차간 간격 (어큐뮬레이션)")]
    [Tooltip("앞차와 유지하는 최소 간격(m, 호길이 기준). 차량 길이 4.5 + 여유 0.5 = 5 권장.")]
    public float minGap = 5f;

    [Tooltip("최소 간격에 도달하기 전 선형 감속이 시작되는 완충 구간 폭(m)." +
        " 예: minGap 5 + decelBand 2 → 앞차와 7m부터 감속 시작, 5m에서 정지." +
        " 체결 소프트 게이트와 같은 패턴 — 하드 클램프의 급정거를 없앤다.")]
    public float decelBand = 2f;

    [Tooltip("감속 구간 안에서도 유지하는 최소 속도 배율(0~1). 순수 선형 감속은 목표 지점에" +
        " 지수적으로만 수렴해 영원히 도착하지 않는다 — 바닥 속도를 두면 유한 시간에 도착하고" +
        " 정지는 클램프가 처리한다(게이트/앞차 꽁무니에 스르륵 도킹).")]
    [Range(0f, 1f)] public float minApproachFactor = 0.15f;

    // 활성 차량 목록. pathProgress 내림차순(0번 = 선두). 같은 MainSpline 위라는 전제.
    private readonly List<CarController> cars = new List<CarController>();

    // 라인 위 정지 공정 게이트(WheelStation, WaterTestStation 등 ILineGate 구현체).
    // 게이트 앞 차량은 게이트 지점을 넘지 못한다.
    private readonly List<ILineGate> gates = new List<ILineGate>();

    // 게이트 정지 지점을 게이트 진행도보다 살짝 앞(m)에 둔다.
    // 정확히 게이트 위에 세우면 '통과(progress ≥ gate)'로 판정돼 캡처 없이 지나가버린다 —
    // 통과 판정은 방출 시 SetProgress(gate)로만 넘어갈 수 있게 경계를 벌려 둔다.
    private const float GateStopOffset = 0.01f;



    /// <summary>차량을 트래픽 관리 대상으로 등록한다 (pathProgress 내림차순 위치에 삽입).</summary>
    public void Register(CarController car)
    {
        if (car == null || cars.Contains(car)) return;

        int idx = cars.Count;
        while (idx > 0 && cars[idx - 1].pathProgress < car.pathProgress) idx--;
        cars.Insert(idx, car);
    }

    public void Unregister(CarController car) => cars.Remove(car);

    public void RegisterGate(ILineGate gate)
    {
        if (gate == null || gates.Contains(gate)) return;
        gates.Add(gate);
    }

    public void UnregisterGate(ILineGate gate) => gates.Remove(gate);

    private void Update()
    {
        // 풀로 반환됐거나 파괴된 차량 정리 (OnDisable에서 해제되지만 방어적으로 한 번 더)
        cars.RemoveAll(c => c == null || !c.gameObject.activeInHierarchy);
        // 파괴된 게이트 정리 (ILineGate는 인터페이스라 MonoBehaviour 캐스트로 Unity null 체크)
        gates.RemoveAll(g => (g as MonoBehaviour) == null);

        // 선두→후미 순서로 구동. 각 차는 '이번 프레임에 이미 이동을 마친 앞차' 기준으로 클램프된다.
        for (int i = 0; i < cars.Count; i++)
        {
            CarController car = cars[i];
            if (!car.IsDriving) continue; // 리프트 캡처 등으로 멈춘 차 — 이동은 안 하지만 뒷차의 장애물로는 남는다

            float len = car.SplineLength;
            if (len <= 0f) continue;

            float speedFactor = 1f;
            float maxProgress = 1f;

            // 앞차 제약: minGap까지 접근 후 정지, decelBand 구간에서 선형 감속
            if (i > 0)
            {
                CarController leader = cars[i - 1];
                float gap = (leader.pathProgress - car.pathProgress) * len;
                speedFactor = DecelFactor(gap - minGap);
                maxProgress = leader.pathProgress - minGap / len;
            }

            // 게이트 제약: 아직 통과하지 않은 게이트 지점에서 정지 (간격 0 — 게이트 위치에 도킹)
            for (int g = 0; g < gates.Count; g++)
            {
                ILineGate gate = gates[g];
                // ILineGate는 인터페이스라 Unity의 커스텀 == null 연산자가 동작하지 않음
                // → MonoBehaviour 캐스트 후 Unity null 체크 + GateEnabled로 활성 여부 확인
                MonoBehaviour mb = gate as MonoBehaviour;
                if (mb == null || !gate.GateEnabled) continue;

                float gateT = gate.GateProgress;
                if (car.pathProgress >= gateT) continue; // 이미 통과(방출 직후 포함)

                float gap = (gateT - car.pathProgress) * len;
                speedFactor = Mathf.Min(speedFactor, DecelFactor(gap));
                maxProgress = Mathf.Min(maxProgress, gateT - GateStopOffset / len);
            }

            car.MoveStep(speedFactor, maxProgress);
        }
    }

    /// <summary>남은 거리(m)에 대한 속도 배율. 0 이하=정지, decelBand 안=선형 감속(바닥 속도 보장).</summary>
    private float DecelFactor(float dist)
    {
        if (dist <= 0f) return 0f;
        if (decelBand <= 0f) return 1f;
        return Mathf.Max(minApproachFactor, Mathf.Clamp01(dist / decelBand));
    }

    /// <summary>시작점 앞 minGap 안에 차량이 있으면 false — 스포너가 스폰을 보류하는 데 사용.</summary>
    public bool CanSpawnAt(float startProgress)
    {
        if (cars.Count == 0) return true;

        // 꼬리 차량(가장 낮은 progress)이 스폰 지점에서 가장 가까운 차다
        CarController tail = cars[cars.Count - 1];
        if (tail == null || !tail.gameObject.activeInHierarchy) return true;

        float len = tail.SplineLength;
        if (len <= 0f) return true;

        return (tail.pathProgress - startProgress) * len >= minGap;
    }

    /// <summary>
    /// 게이트 지점 바로 앞(epsMeters 이내)까지 도착해 대기 중인 차량을 반환한다 (없으면 null).
    /// WheelStation이 캡처 대상을 찾을 때 사용. 통과한 차량(progress ≥ gate)은 제외.
    /// </summary>
    public CarController GetCarAtGate(float gateProgress, float epsMeters)
    {
        for (int i = 0; i < cars.Count; i++)
        {
            CarController car = cars[i];
            if (car == null || !car.gameObject.activeInHierarchy) continue;
            if (car.pathProgress >= gateProgress) continue; // 이미 통과한 차량

            // 내림차순 목록에서 게이트 앞 첫 차량이 곧 게이트에 가장 가까운 차 — 이 차만 보면 된다
            float len = car.SplineLength;
            if (len <= 0f) return null;
            return (gateProgress - car.pathProgress) * len <= epsMeters ? car : null;
        }
        return null;
    }
}
