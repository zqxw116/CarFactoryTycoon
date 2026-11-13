using UnityEngine;

/// <summary>Holds a newer contract at its endpoint while older vehicles finish using the previous outlet.</summary>
public sealed class PendingProductionOutletGate:MonoBehaviour,ILineGate,ITargetedLineGate
{
    FirstProductionLoop loop;
    CarController car;
    ProductionContract contract;
    LineTrafficManager traffic;
    int version;
    public float GateProgress=>contract!=null?contract.endProgress:1f;
    public bool GateEnabled=>isActiveAndEnabled&&loop!=null&&loop.isActiveAndEnabled&&car!=null&&
        car.gameObject.activeInHierarchy&&car.LifeVersion==version&&contract!=null&&!contract.isCompleteVehicle&&!contract.settled&&
        contract.processCount>loop.OutletPrefix;
    public bool Blocks(CarController candidate)=>GateEnabled&&candidate==car;
    public void Initialize(FirstProductionLoop owner,CarController vehicle,ProductionContract promise)
    {
        loop=owner;car=vehicle;contract=promise;version=vehicle.LifeVersion;
        if(traffic==null){traffic=LineTrafficManager.Instance;if(traffic!=null)traffic.RegisterGate(this);}
    }
    void OnDestroy(){if(traffic!=null)traffic.UnregisterGate(this);}
}
