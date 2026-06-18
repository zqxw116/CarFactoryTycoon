using UnityEngine;

/// <summary>
/// 경제(자금) 관리. 차량 판매 수익 획득과 업그레이드 비용 차감을 담당한다.
/// MonoSingleton: 최초 .Instance 접근 시 오브젝트가 자동 생성된다.
/// </summary>
public class EconomyManager : MonoSingleton<EconomyManager>
{
    public int currentMoney = 0;

    /// <summary>잔액 변동 시 호출. (newAmount=변동 후 총액, delta=증감분 +/-)</summary>
    public System.Action<int, int> OnMoneyChanged;

    public override void Init()
    {
        // 시작 자금 등 초기화 지점 (현재는 기본값 사용)
    }

    /// <summary>차량 판매 수익 획득.</summary>
    public void SellCar(int price)
    {
        currentMoney += price;
        OnMoneyChanged?.Invoke(currentMoney, price);
        Debug.Log($"[Economy] 차량 판매 +{price} → 잔액 {currentMoney}");
    }

    /// <summary>비용 지불 시도. 잔액이 충분하면 차감 후 true, 아니면 false.</summary>
    public bool TrySpend(int cost)
    {
        if (currentMoney < cost) return false;
        currentMoney -= cost;
        OnMoneyChanged?.Invoke(currentMoney, -cost);
        return true;
    }
}
