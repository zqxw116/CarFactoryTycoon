using UnityEngine;

/// <summary>
/// 기능 업그레이드 관리. 라인 / 로봇 / 차량의 업그레이드 현재값(배율)을 보관하고,
/// 다른 시스템(스포너·스테이션·차량)이 이 값을 참조한다.
/// 실제 구매(비용 차감)는 EconomyManager.TrySpend를 통해 처리한다.
/// </summary>
public class UpgradeManager : MonoSingleton<UpgradeManager>
{
    [Header("라인 업그레이드")]
    [Tooltip("컨베이어 속도 배율 (CarSpawner / CarController가 참조)")]
    public float lineSpeedMultiplier = 1.0f;

    [Header("로봇 업그레이드")]
    [Tooltip("로봇팔 체결 속도 배율 (StationController.assembleSpeed에 곱해 사용)")]
    public float robotAssembleMultiplier = 1.0f;

    [Header("차량 업그레이드")]
    [Tooltip("차량 판매가 배율 (CarController 출고 시 적용)")]
    public float carSellPriceMultiplier = 1.0f;

    public override void Init() { }

    // 각 업그레이드는 EconomyManager.TrySpend로 비용 차감 성공 시 배율을 증가시킨다.
    public bool UpgradeLineSpeed(float add, int cost)
    {
        if (EconomyManager.Instance == null || !EconomyManager.Instance.TrySpend(cost)) return false;
        lineSpeedMultiplier += add;
        return true;
    }

    public bool UpgradeRobotAssemble(float add, int cost)
    {
        if (EconomyManager.Instance == null || !EconomyManager.Instance.TrySpend(cost)) return false;
        robotAssembleMultiplier += add;
        return true;
    }

    public bool UpgradeCarSellPrice(float add, int cost)
    {
        if (EconomyManager.Instance == null || !EconomyManager.Instance.TrySpend(cost)) return false;
        carSellPriceMultiplier += add;
        return true;
    }
}
