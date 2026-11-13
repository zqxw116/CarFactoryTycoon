#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Object=UnityEngine.Object;

public static class FactoryProcessGateChecks
{
    public static string Result="idle";
    static PrototypeProcessCrew crew;
    static CarController car;
    static ProductionProgression p;
    static PrototypeLineController line;
    static PrototypeDayCycle day;
    static readonly Dictionary<PartStack,int> stock=new Dictionary<PartStack,int>();
    static void Need(bool ok,string message){if(!ok)throw new InvalidOperationException(message);Result+="\nOK "+message;}
    public static void Start()
    {
        if(!Application.isPlaying)throw new InvalidOperationException("Play required");
        if(Result.StartsWith("running"))return;
        p=ProductionProgression.Current;line=PrototypeLineController.Current;day=PrototypeDayCycle.Current;
        Object.FindFirstObjectByType<FactoryPlayerController>().StartCoroutine(Guard());
    }
    static IEnumerator Guard()
    {
        var entry=p.processes[0];bool hired=entry.hired,automated=entry.automated;
        bool lineEnabled=line.enabled,dayEnabled=day!=null&&day.enabled;
        int money=EconomyManager.Instance.currentMoney;
        Result="running";stock.Clear();
        var piles=entry.humanStation.partStacks;
        foreach(var s in piles){stock[s]=s.Count;s.Fill_Full();}
        line.enabled=false;if(day!=null)day.enabled=false;
        var oldLever=line.lever;line.SetLever(PrototypeLineController.LeverPosition.Stop);
        IEnumerator run=Run();Exception failure=null;
        while(true)
        {
            bool more=false;object next=null;
            try{more=run.MoveNext();if(more)next=run.Current;}catch(Exception e){failure=e;}
            if(failure!=null||!more)break;yield return next;
        }
        if(crew!=null)crew.CancelCycle();
        if(car!=null)CarPool.Instance.Return(car);car=null;
        entry.hired=hired;entry.automated=automated;
        typeof(ProductionProgression).GetMethod("ApplyProcess",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(p,new object[]{entry});
        foreach(var pair in stock){int delta=pair.Value-pair.Key.Count;if(delta>0)pair.Key.Add(delta);while(pair.Key.Count>pair.Value)pair.Key.TryConsume();}
        EconomyManager.Instance.currentMoney=money;line.SetLever(oldLever);line.enabled=lineEnabled;if(day!=null)day.enabled=dayEnabled;
        Result=(failure==null?"PASS":"FAIL "+failure)+"\n"+Result;Debug.Log(Result);
    }
    static IEnumerator Run()
    {
        var entry=p.processes[0];Need(!entry.hired,"fixture starts unhired");
        Need(p.TryHireProcess(0),"real hire path");crew=PrototypeProcessCrew.Ensure(p,0);crew.SetDutyStatus(true,false);
        var pile=entry.humanStation.partStacks[0];int count=pile.Count,completed=crew.CompletedPartCount;
        car=CarPool.Instance.Get();car.SetPath(p.mainSpline,1.2f,entry.gateProgress+.02f/p.mainSpline.CalculateLength());p.Stamp(car);
        float end=Time.time+12f;while(Time.time<end&&car.isMoving)yield return null;
        Need(!car.isMoving&&crew.GateEnabled,"work-ready vehicle stops at gate");
        Need(Mathf.Abs((crew.GateProgress-car.pathProgress)*car.SplineLength)<.06f,"stop pose within 6cm");
        end=Time.time+35f;while(Time.time<end&&crew.CompletedPartCount==completed)yield return null;
        Need(crew.CompletedPartCount>completed,"human assembly completes after stop");
        yield return null;
        Need(car.isMoving&&!crew.GateEnabled,"completed cycle releases traffic");
        Need(pile.Count==count-1,"one pickup consumes one stock");
        CarPool.Instance.Return(car);crew.CancelCycle();
        car=CarPool.Instance.Get();car.SetPath(p.mainSpline,1.2f,entry.gateProgress+.02f/p.mainSpline.CalculateLength());p.Stamp(car);
        end=Time.time+12f;while(Time.time<end&&car.isMoving)yield return null;
        Need(!car.isMoving,"second life can stop");crew.CancelCycle();
        Need(!crew.GateEnabled&&car.isMoving,"cancel releases owned stop");
        CarPool.Instance.Return(car);crew.CancelCycle();
        while(pile.Count>0)pile.TryConsume();
        car=CarPool.Instance.Get();car.SetPath(p.mainSpline,1.2f,entry.gateProgress+.02f/p.mainSpline.CalculateLength());p.Stamp(car);
        end=Time.time+2f;while(Time.time<end)yield return null;
        Need(car.isMoving&&!crew.GateEnabled&&car.pathProgress>crew.GateProgress,"empty stock passes without late stop");
        CarPool.Instance.Return(car);crew.CancelCycle();pile.Fill_Full();crew.SetDutyStatus(false,false);
        car=CarPool.Instance.Get();car.SetPath(p.mainSpline,1.2f,entry.gateProgress+.02f/p.mainSpline.CalculateLength());p.Stamp(car);
        end=Time.time+2f;while(Time.time<end)yield return null;
        Need(car.isMoving&&!crew.GateEnabled,"off-duty process passes traffic");
    }
}
#endif
