#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public static class FactoryPrototypeServicesChecks
{
    public static string Result="idle";
    static PrototypeServicesInteraction services;
    static PrototypeDayCycle day;
    static ProductionProgression p;
    static FactoryPlayerController player;
    public static void Start()
    {
        services=UnityEngine.Object.FindFirstObjectByType<PrototypeServicesInteraction>();
        day=PrototypeDayCycle.Current;p=ProductionProgression.Current;player=services.player;
        day.enabled=false;services.enabled=false;player.enabled=false;player.interactor.inputEnabled=false;
        PrototypeLineController.Current.SetLever(PrototypeLineController.LeverPosition.Stop);
        Result="running";player.StartCoroutine(Guard());
    }
    static IEnumerator Guard()
    {
        var stack=new Stack<IEnumerator>();stack.Push(Run());
        while(stack.Count>0)
        {
            object wait;
            try
            {
                if(!stack.Peek().MoveNext()){stack.Pop();continue;}
                wait=stack.Peek().Current;
                if(wait is IEnumerator nested){stack.Push(nested);continue;}
            }
            catch(Exception e){Result="FAIL: "+e.Message;yield break;}
            yield return wait;
        }
        Result="PASS: local service menus; zero initial staff; full physical stock eligibility; real cargo pickup/duty pause/resume/delivery; exclusive wheel reservations; player/human/automatic fueling and busy/debt guards; service wages and next day; actual work audio pulses. Diagnostic placement/time acceleration used APIs. Stop Play to reset.";
    }
    static IEnumerator Run()
    {
        var supply=services.supply;var fuel=services.fuel;var eco=EconomyManager.Instance;
        Require(supply.WorkerCount==0&&!fuel.AttendantHired&&!fuel.IsAutomated&&eco.currentMoney==0,"initial services must be unstaffed");
        var stocks=UnityEngine.Object.FindObjectsByType<PartStack>(FindObjectsSortMode.None);
        Require(supply.TargetCount==stocks.Length,"all physical stocks must be targets");
        player.SetMapMode(true);services.OpenService(false);Require(!services.PopupIsOpen,"map cannot hire remotely");
        player.SetMapMode(false);Cursor.lockState=CursorLockMode.Locked;Cursor.visible=false;
        player.Teleport(services.warehousePost.position+Vector3.right);services.OpenService(false);
        Require(services.PopupIsOpen&&player.InteractionUIOpen,"local warehouse popup");services.ClosePopup();
        foreach(var s in stocks)s.Fill_Full();
        var target=stocks.Where(s=>s.partType!=PartType.None&&s.partType!=PartType.Frame_1).OrderBy(s=>(s.transform.position-supply.LoadPoint).sqrMagnitude).First();
        for(int i=0;i<3;i++)Require(target.TryConsume(),"consume test stock");
        int before=target.Count;
        Require(services.TryHireSupply()&&supply.WorkerCount==1&&day.HumanCount==1,"free supply hire/payroll count");
        var worker=supply.supplyWorkers.First(w=>w!=null&&w.gameObject.activeInHierarchy);
        yield return Until(()=>worker.Carrying>0,30,"real warehouse pickup");
        int cargo=worker.Carrying;Vector3 pausedAt=worker.transform.position;
        int expectedRegular=Mathf.CeilToInt((20-day.GameHour)*day.settings.dailyWage/12f-.0001f);
        day.AdvanceClock((20-day.GameHour)*day.settings.dayRealSeconds/14f);
        Require(!worker.WorkersOnDuty&&!supply.IsReserved(target),"off-duty releases reservation");
        yield return new WaitForSeconds(.4f);
        Require(worker.Carrying==cargo&&Vector3.Distance(worker.transform.position,pausedAt)<.05f&&target.Count==before,"off-duty preserves cargo and stops delivery");
        day.SetOvertime(true);
        yield return Until(()=>target.Count>before,60,"real navigation and stock unloading");
        Require(target.Count<=target.Capacity,"no duplicate/over-capacity stock");
        Require(services.TryHireSupply()&&supply.WorkerCount==2,"second free supply hire");
        var wheel=p.processes.First(x=>x.wheel!=null).wheel.humanPartStacks;
        wheel[0].TryConsume();wheel[1].TryConsume();
        var second=supply.supplyWorkers.Last(w=>w!=null&&w.gameObject.activeInHierarchy);
        SupplyManager.SupplyTarget a=default,b=default;
        Require(supply.TryReserveNext(worker,out a)&&supply.TryReserveNext(second,out b)&&a.stack!=b.stack,"exclusive multi-worker reservation");
        Require(wheel.Contains(a.stack)&&wheel.Contains(b.stack),"wheel stocks eligible with legacy component disabled");
        supply.Release(a.stack,worker);supply.Release(b.stack,second);foreach(var s in stocks)s.Fill_Full();

        var car=CreateFuelCar();yield return Until(()=>fuel.CurrentState==FuelStation.FuelState.AwaitingPlayer,3,"unstaffed fuel capture");
        player.Teleport(car.transform.position+Vector3.up*20);Require(!fuel.TryBeginPlayerFueling(player),"fuel range check");
        player.Teleport(car.transform.TransformPoint(new Vector3(2.3f,0,fuel.carPortLocal.z)));Cursor.lockState=CursorLockMode.Locked;
        Require(fuel.TryBeginPlayerFueling(player),"player can fuel without staff");
        Require(!fuel.TryHireAttendant(),"busy fuel hire blocked");
        fuel.AdvancePlayerFueling(player,.7f);fuel.AdvancePlayerFueling(player,5f);fuel.AdvancePlayerFueling(player,.6f);
        Require(fuel.CompletedFuelCount==1&&fuel.CurrentCar==null&&car.isMoving&&eco.currentMoney==0,"manual fuel releases car without bonus");
        car.LeaveLine();CarPool.Instance.Return(car);
        day.SetOvertime(false);Require(fuel.TryHireAttendant()&&day.HumanCount==3,"fuel worker included in payroll");
        car=CreateFuelCar();yield return Until(()=>fuel.CurrentState==FuelStation.FuelState.AwaitingPlayer,3,"off-duty attendant cannot auto-fuel");
        day.SetOvertime(true);
        yield return Until(()=>fuel.CurrentState!=FuelStation.FuelState.AwaitingPlayer,3,"overtime attendant starts");
        eco.Earn(1000);Require(!fuel.TryPurchaseAutomation()&&eco.currentMoney==1000,"busy automation conversion cannot charge");
        yield return Until(()=>fuel.CompletedFuelCount==2,35,"real attendant nozzle movement and fuel cycle");
        car.LeaveLine();CarPool.Instance.Return(car);
        yield return Until(()=>fuel.CurrentState==FuelStation.FuelState.Idle,20,"attendant returns nozzle");
        Require(fuel.TryPurchaseAutomation()&&!fuel.TryPurchaseAutomation()&&eco.currentMoney==0&&day.HumanCount==2,"automation costs once and stops human wage accrual");
        day.SetOvertime(false);car=CreateFuelCar();
        yield return Until(()=>fuel.CompletedFuelCount==3,8,"automatic fuel works off-duty");
        car.LeaveLine();CarPool.Instance.Return(car);

        day.SetOvertime(true);day.AdvanceClock(day.settings.dayRealSeconds*2f/14f+.01f);
        Require(day.IsSettlement&&day.RegularWages==expectedRegular&&day.OvertimeWages==15&&eco.currentMoney==-(expectedRegular+15),"supply regular/overtime wages and debt");
        Require(!services.TryHireSupply(),"settlement/debt blocks hire");day.ContinueNextDay();
        Require(worker.WorkersOnDuty&&second.WorkersOnDuty&&!day.OvertimeEnabled&&!services.TryHireSupply(),"next day resumes workers; debt still blocks free hires");
        eco.Earn(expectedRegular+15);Require(services.TryHireSupply(),"debt repayment unlocks warehouse hire");

        var process=p.processes[0];Require(p.TryHireProcess(0),"audio worker hire");
        car=CarPool.Instance.Get();car.SetPath(p.mainSpline,0,process.gateProgress+.05f/p.mainSpline.CalculateLength());p.Stamp(car);
        var part=car.GetComponentsInChildren<AssemblyPart>(true).First(x=>x.myType==process.parts[0]);
        yield return Until(()=>part.IsAssembled,25,"real work and sound");
        var audio=UnityEngine.Object.FindFirstObjectByType<PrototypeWorkAudio>();
        Require(audio!=null&&audio.PlayedImpactCount>=2&&audio.PlayedCompletionCount>=1,"real strikes emit impact/completion sound");
        Require(audio.GetComponentsInChildren<AudioSource>().Length==6,"bounded audio emitter pool");
        car.LeaveLine();CarPool.Instance.Return(car);day.enabled=true;
    }
    static CarController CreateFuelCar()
    {
        var fuel=services.fuel;var car=CarPool.Instance.Get();
        car.SetPath(p.mainSpline,0,fuel.gateProgress-.02f/p.mainSpline.CalculateLength());p.Stamp(car);
        foreach(var part in car.GetComponentsInChildren<AssemblyPart>(true))part.SetAssembled();
        car.CloseRepairWindow();return car;
    }
    static IEnumerator Until(Func<bool> predicate,float timeout,string message)
    {
        float end=Time.time+timeout;
        while(!predicate()&&Time.time<end)yield return null;
        Require(predicate(),message);
    }
    static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
#endif
