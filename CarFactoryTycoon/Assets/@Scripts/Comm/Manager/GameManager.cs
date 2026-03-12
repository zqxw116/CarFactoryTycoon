using UnityEngine;

public class GameManager : MonoBehaviour
{
    public static GameManager Instance;

    [Header("Game Data")]
    public int currentMoney = 0;
    public float lineSpeedMultiplier = 1.0f; // 컨베이어 속도 배율
    public float assemblySuccessRate = 0.8f; // 로봇팔 기본 체결 성공률 (80%)

    private void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    // 차량 판매 성공 시 호출
    public void SellCar(int price)
    {
        currentMoney += price;
        Debug.Log($"차량 판매! 현재 잔액: {currentMoney}");
        // TODO: UI 업데이트 이벤트 호출
    }

    // 업그레이드 버튼에서 호출할 함수들
    public void UpgradeLineSpeed() { /* TODO: 돈 차감 및 속도 증가 로직 */ }
    public void UpgradeAssemblyRate() { /* TODO: 돈 차감 및 체결률 증가 로직 */ }
}