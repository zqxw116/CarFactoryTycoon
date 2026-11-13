using UnityEngine;

/// <summary>First playable prices/pacing are provisional, isolated from authored process tuning.</summary>
[CreateAssetMenu(menuName="Car Factory/First Production Balance")]
public sealed class FirstProductionSettings:ScriptableObject
{
    public bool enabledInMain=true;
    public int deliveryBase=100,deliveryPerAdditionalProcess=60,firstHireCost=180,nextProcessBaseCost=200;
    [Min(0)] public int automaticInputCost = 400;
    public float washDistanceBeforeDashboard=10f,sprayRadius=.95f,sprayRange=4.5f;
    public int DeliveryPrice(int processes)=>deliveryBase+Mathf.Max(0,processes-1)*deliveryPerAdditionalProcess;
    public int UnlockPrice(int processes)=>nextProcessBaseCost*Mathf.Max(1,processes);
}
