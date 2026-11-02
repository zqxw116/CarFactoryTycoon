#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Object=UnityEngine.Object;

public static class FactoryReadySlotIntegrationChecks
{
    public static string Result="idle";
    static readonly StringBuilder log=new StringBuilder();
    static void Need(bool ok,string message){if(!ok)throw new InvalidOperationException(message);log.AppendLine("OK "+message);}
    public static void Start()
    {
        if(!Application.isPlaying)throw new InvalidOperationException("Play required");
        if(Result=="running")return;
        Result="running";log.Clear();
        Object.FindFirstObjectByType<FactoryPlayerController>().StartCoroutine(Guard(Run()));
    }
    static IEnumerator Guard(IEnumerator action)
    {
        while(true)
        {
            bool more=false;object next=null;
            try{more=action.MoveNext();if(more)next=action.Current;}
            catch(Exception e){Result="FAIL\n"+log+e;Debug.LogError(Result);yield break;}
            if(!more)break;yield return next;
        }
        Result="PASS\n"+log;Debug.Log(Result);
    }
    static IEnumerator Run()
    {
        var p=ProductionProgression.Current;var line=PrototypeLineController.Current;
        line.SetLever(PrototypeLineController.LeverPosition.Stop);
        var day=PrototypeDayCycle.Current;day.enabled=false;
        var all=Object.FindObjectsByType<PartStack>(FindObjectsInactive.Include,FindObjectsSortMode.None);
        int configured=0,overlap=0;
        foreach(var stack in all)
        {
            if(!stack.IsReadySlotConfigured)continue;configured++;
            var display=stack.ReadyDisplay;if(display==null)continue;
            foreach(var renderer in stack.GetComponentsInChildren<Renderer>())
            {
                if(renderer.transform.IsChildOf(display.transform)||renderer.name=="ReadySlotStand" || renderer.GetComponentInParent<PortableAssemblyPart>()!=null)continue;
                if(stack.ReadyBounds.Intersects(renderer.bounds)){overlap++;break;}
            }
        }
        Need(configured==26,"26 production stock slots configured");
        Need(overlap==0,"ready parts separated from their backing piles");
        var entry=p.processes[0];var stock=entry.humanStation.partStacks[0];
        Need(!entry.hired,"fixture starts unhired");
        EconomyManager.Instance.currentMoney=1000000;
        Need(p.TryHireProcess(0),"hire automatic human worker");
        var crew=PrototypeProcessCrew.Ensure(p,0);int baseline=stock.Count;
        var car=CarPool.Instance.Get();car.SetPath(p.mainSpline,0,entry.gateProgress+.0002f);p.Stamp(car);
        float until=Time.time+30f;bool sawEmpty=false,sawRefill=false;
        while(Time.time<until && crew.CompletedPartCount<1)
        {
            if(stock.Count==baseline-1){if(!stock.HasReadyPart)sawEmpty=true;else sawRefill=true;}
            yield return null;
        }
        Need(crew.CompletedPartCount==1,"human completes a part taken from ready slot");
        Need(stock.Count==baseline-1 && sawEmpty && sawRefill,"human pickup empties then refills with exactly one stock consumed (count="+stock.Count+", baseline="+baseline+", empty="+sawEmpty+", refill="+sawRefill+")");
        CarPool.Instance.Return(car);yield return null;
        var doomed=CarPool.Instance.Get();doomed.SetPath(p.mainSpline,0,entry.gateProgress+.0002f);p.Stamp(doomed);
        yield return null;yield return null;
        int reservedCount=stock.Count;
        Object.Destroy(doomed.gameObject);yield return null;yield return null;
        var probe=new GameObject("ReservationAfterDestroyedTarget");
        bool recovered=stock.TryReserveReady(probe);
        Need(recovered && stock.Count==reservedCount,"destroyed target releases reservation without consuming or refunding stock");
        stock.ReleaseReadyReservation(probe);Object.Destroy(probe);
        Need(p.TryPurchaseRobot(0),"convert same process to robot");crew=PrototypeProcessCrew.Ensure(p,0);baseline=stock.Count;
        car=CarPool.Instance.Get();car.SetPath(p.mainSpline,0,entry.gateProgress+.0002f);p.Stamp(car);
        until=Time.time+35f;sawEmpty=false;sawRefill=false;
        while(Time.time<until && crew.CompletedPartCount<2)
        {
            if(stock.Count==baseline-1){if(!stock.HasReadyPart)sawEmpty=true;else sawRefill=true;}
            yield return null;
        }
        Need(crew.CompletedPartCount==2,"robot completes from the same fixed ready slot");
        Need(stock.Count==baseline-1 && sawEmpty && sawRefill,"robot pickup empties/refills independently of completion (count="+stock.Count+", baseline="+baseline+", empty="+sawEmpty+", refill="+sawRefill+")");
        CarPool.Instance.Return(car);yield return null;
        Need(crew.CurrentCar==null,"returned vehicle releases process target");
        while(stock.TryConsume()){}
        yield return null;
        Need(stock.Count==0 && !stock.HasReadyPart && stock.ReadyDisplay==null,"stock zero keeps ready slot empty");
        Need(stock.Add(1)==1,"existing supply Add replenishes inventory");
        until=Time.time+1.2f;while(!stock.HasReadyPart && Time.time<until)yield return null;
        Need(stock.HasReadyPart && stock.Count==1,"supply repopulates one staged part without duplicate count");
    }
}
#endif
