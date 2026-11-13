#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using Object=UnityEngine.Object;

public static class FactoryProcessRetryPickupChecks
{
    public static string Result="idle";
    static void Need(bool value,string text){if(!value)throw new InvalidOperationException(text);}
    public static void Start()
    {
        Need(Application.isPlaying,"Play required");
        var p=Object.FindFirstObjectByType<ProcessAuthoringPreview>();Need(p!=null,"authoring scene required");
        Result="running";p.StartCoroutine(Guard(p));
    }
    static IEnumerator Guard(ProcessAuthoringPreview p)
    {
        var counts=Object.FindObjectsByType<PartStack>(FindObjectsInactive.Include,FindObjectsSortMode.None).ToDictionary(x=>x,x=>x.Count);
        var run=Run(p);Exception failure=null;
        while(true){bool more=false;object next=null;try{more=run.MoveNext();if(more)next=run.Current;}catch(Exception e){failure=e;}if(failure!=null||!more)break;yield return next;}
        p.Stop();
        foreach(var pair in counts){if(pair.Key.Count<pair.Value)pair.Key.Add(pair.Value-pair.Key.Count);while(pair.Key.Count>pair.Value)pair.Key.TryConsume();pair.Key.ResetReadySlot();}
        Result=(failure==null?"PASS":"FAIL "+failure)+"\n"+Result;SessionState.SetString("FactoryProcessRetryPickupChecks",Result);Debug.Log(Result);
    }
    static IEnumerator Run(ProcessAuthoringPreview p)
    {
        string report="";
        foreach(bool robot in new[]{true,false})
        {
            p.Select(8,robot,0);var entry=p.progression.processes[8];var stock=entry.humanStation.partStacks[0];
            var crew=PrototypeProcessCrew.Ensure(p.progression,8);int before=stock.Count;
            Need(before>1,"fixture needs two parts");p.SetSpeed(5);p.Retry();
            Need(!crew.Detects(p.Car),"vehicle starts outside detection");
            float deadline=Time.realtimeSinceStartup+6;
            while(Time.realtimeSinceStartup<deadline&&stock.Count==before)
            {p.Car.isMoving=false;p.Car.SetProgress(0);yield return null;}
            Need(stock.Count==before-1,"Retry must pick before vehicle entry: "+(robot?"robot":"worker"));
            Need(!crew.Detects(p.Car),"pickup occurs before detection");
            float hold=Time.realtimeSinceStartup+.3f;
            while(Time.realtimeSinceStartup<hold){p.Car.isMoving=false;p.Car.SetProgress(0);yield return null;}
            Need(stock.Count==before-1,"waiting must not consume again");
            var part=p.Car.GetComponentsInChildren<AssemblyPart>(true).First(x=>x.myType==stock.partType);
            Need(part.IsPlayerAttaching&&!part.IsPlayerAttached&&!part.IsAssembled,"hold instead of early fastening");
            p.Retry();Need(stock.Count==before,"second Retry restores run-start stock");
            p.SetSpeed(20);deadline=Time.realtimeSinceStartup+20;
            while(Time.realtimeSinceStartup<deadline&&p.Status!="1회 완료"&&!p.Status.Contains("미완료")&&!p.Status.Contains("불가"))yield return null;
            Need(p.Status=="1회 완료","entry then fastening: "+p.Status);
            Need(stock.Count==before-1,"retry consumes exactly one");
            report+=(robot?"robot":"worker")+": pre-entry pickup / hold / repeat Retry / complete / exact stock PASS\n";
            Result="running\n"+report;
        }
        Result=report;
    }
}
#endif
