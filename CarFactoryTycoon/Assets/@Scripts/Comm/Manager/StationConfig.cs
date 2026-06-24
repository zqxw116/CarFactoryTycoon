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

    /*  즉 들어올 때 기준(0.6)과 나갈 때 기준(1.0)이 다릅니다.

  왜 둘로 나누나

  기준이 하나(0.6)뿐이면, 팔 끝이 0.6 경계선에서 미세하게 흔들릴 때 매 프레임 ON/OFF가 토글되며 체결이
  덜덜거립니다. 그래서:

  - 가까워지면 0.6에서 물고
  - 한 번 물리면 1.0까지 벌어져야 풀어줌

  이 0.4 간격(밴드) 덕분에 경계 근처에서 깜빡임 없이 안정적으로 붙어 있습니다.
     */
    [Tooltip("로봇팔 끝(Rig_End)이 파츠 ArmLookTarget에 이 거리(m) 이내면 체결 시작(engage)")]
    public float assembleReachThreshold = 0.6f;

    [Tooltip("체결 중 거리가 (threshold + 이 여유) 를 넘어야 비로소 체결 중단. 경계선 ON/OFF 떨림 방지(히스테리시스)")]
    public float reachReleaseMargin = 0.4f;

    public override void Init() { }
}
