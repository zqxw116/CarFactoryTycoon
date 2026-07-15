using UnityEngine;

/// <summary>
/// 경제(자금) 관리. 차량 판매 수익 획득과 업그레이드 비용 차감을 담당한다.
/// MonoSingleton: 최초 .Instance 접근 시 오브젝트가 자동 생성된다.
/// </summary>
public class EconomyManager : MonoSingleton<EconomyManager>
{
    public int currentMoney = 0;

    /// <summary>잔액 변동 시 호출. (newAmount=변동 후 총액, delta=증감분 +/-, sourceWorldPos=획득 발생 월드좌표(코인 연출 출발점, 없으면 null))</summary>
    public System.Action<int, int, Vector3?> OnMoneyChanged;

    public override void Init()
    {
        // 시작 자금 등 초기화 지점 (현재는 기본값 사용)
    }

    /// <summary>재화 획득 (파츠 체결 보상 등). 적립 후 변동 이벤트를 쏜다 → MoneyUI 카운트업/코인 연출.
    /// sourceWorldPos를 주면 코인이 그 위치(로봇팔 등)에서 재화 UI로 날아간다.</summary>
    public void Earn(int amount, Vector3? sourceWorldPos = null)
    {
        if (amount <= 0) return;
        currentMoney += amount;
        OnMoneyChanged?.Invoke(currentMoney, amount, sourceWorldPos);
    }

    /// <summary>차량 판매 수익 획득.</summary>
    public void SellCar(int price, Vector3? sourceWorldPos = null)
    {
        Earn(price, sourceWorldPos);
        Debug.Log($"[Economy] 차량 판매 +{price} → 잔액 {currentMoney}");
    }

    /// <summary>비용 지불 시도. 잔액이 충분하면 차감 후 true, 아니면 false.</summary>
    public bool TrySpend(int cost)
    {
        if (currentMoney < cost) return false;
        currentMoney -= cost;
        OnMoneyChanged?.Invoke(currentMoney, -cost, null);
        return true;
    }
}
