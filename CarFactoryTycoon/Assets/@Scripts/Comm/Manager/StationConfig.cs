using UnityEngine;

/// <summary>
/// 모든 StationController(로봇팔)가 공유하는 전역 체결 설정값.
/// 로봇팔이 수십 개여도 이 한 곳의 값만 바꾸면 일괄 적용된다(테스트/튜닝용).
/// 플레이 중 하이러키에서 이 오브젝트를 선택해 값을 바꾸면 모든 스테이션에 즉시 반영된다.
/// </summary>
public class StationConfig : MonoSingleton<StationConfig>
{
    [Header("체결 전역 설정")]
    [Tooltip("1초당 처리하는 작업량(work/초). 완성 시간 = 부품 requiredWork / assembleSpeed.")]
    public float assembleSpeed = 6f;

    /*  거리별 체결 속도 (소프트 게이트):

  - threshold 이내          → 전속(assembleSpeed 100%)
  - threshold ~ +margin 사이 → 거리에 비례해 선형 감속 (100% → 0%)
  - threshold + margin 이상  → 정지

  이진 ON/OFF 게이트는 파츠가 팔보다 빠를 때 전진→정지→전진 스텝이 반복되며 덜덜거리지만,
  감속 구간을 두면 파츠가 팔이 따라오는 속도에 자동 수렴해 연속적으로 부드럽게 움직입니다.
     */
    [Tooltip("로봇팔 끝(Rig_End)이 파츠 ArmLookTarget에 이 거리(m) 이내면 전속 체결")]
    public float assembleReachThreshold = 0.3f;

    [Tooltip("threshold부터 (threshold+margin)까지 체결 속도가 선형으로 0까지 감속되는 완충 구간(m). 팔이 처지면 파츠가 자동으로 느려져 스텝 끊김 없이 수렴")]
    public float reachReleaseMargin = 0.1f;

    [Tooltip("로봇팔이 추적하는 가상 타겟(trackingTarget)의 이동 속도(유닛/초). 타겟을 순간이동시키지 않고" +
        " 목표(파츠/EndPos)까지 미끄러뜨려 복귀/전환 시 팔끝 경로를 통제한다." +
        " 팔의 실제 추적 속도보다 넉넉히 빠르게 — 너무 느리면 팔이 파츠에 늦게 도달해 누락 밸런스에 영향을 준다.")]
    public float trackingTargetSpeed = 4f;

    [Header("보상")]
    [Tooltip("파츠 하나를 체결 완료할 때마다 적립되는 재화. 미완료 포기(ResetStation 경로)는 지급되지 않는다.")]
    public int partReward = 10;

    public override void Init() { }
}
