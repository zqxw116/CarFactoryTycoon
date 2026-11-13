#if UNITY_EDITOR
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;

/// <summary>Actual preview pickup/contact regression, without modifying the saved profile.</summary>
public static class FactoryProcessGripChecks
{
    public static string Result="idle";
    static readonly BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    static void Need(bool ok,string text){if(!ok)throw new InvalidOperationException(text);}
    public static string Geometry()
    {
        var across=Quaternion.Euler(ProcessPartGrip.DefaultCarryEuler(Vector3.back*.3f,Vector3.forward*.3f))*Vector3.forward;
        Need(Vector3.Distance(across,Vector3.right)<.0001f,"default carrying aligns depth-separated grips with shoulders");
        var ratio=ProcessPartGrip.ScaleRatio(Vector3.one,new Vector3(-1,1,1));
        Need(Vector3.Scale(new Vector3(-1,1,1),Vector3.Scale(new Vector3(.2f,.3f,.4f),ratio))==new Vector3(.2f,.3f,.4f),"mirrored held mesh preserves ready contact");
        var g=new Vector3(.23f,-.17f,.41f);var scale=new Vector3(.4f,2,1.3f);
        for(int i=0;i<24;i++)
        {
            var rotation=Quaternion.Euler(i*13,i*29,i*7);var root=new Vector3(4,-2,9);
            var contact=ProcessPartGrip.Contact(root,rotation,scale,g);
            Need(Vector3.Distance(ProcessPartGrip.RootAtContact(contact,rotation,scale,g),root)<.00001f,"part-local robot inverse contact");
        }
        var go=new GameObject("GripFallbackFixture");try
        {
            var part=go.AddComponent<AssemblyPart>();var native=go.AddComponent<PartGripOverride>();
            native.gripA=new Vector3(-.7f,.1f,0);native.gripB=new Vector3(.6f,.2f,0);native.explicitHands=false;
            var before=native.gripA;ProcessPartGrip.Apply(part,new ProcessGripSettings{configured=true,left=Vector3.up,right=Vector3.down});
            ProcessPartGrip.Apply(part,null);
            Need(native.enabled&&!native.authoredByProcess&&!native.explicitHands&&native.gripA==before,"reset must restore native grip and hand assignment");
        }finally{Object.DestroyImmediate(go);}
        return "PASS 24 rotated/nonuniform-scale contact inverses + native fallback";
    }
    public static void Start()
    {
        Need(Application.isPlaying,"Play required");
        var preview=Object.FindFirstObjectByType<ProcessAuthoringPreview>();
        Need(preview!=null,"authoring scene required");Need(Resources.FindObjectsOfTypeAll<ProcessAuthoringWindow>().Length==0,"close editor window for isolated fixture");
        Need(preview.progression.authoringProfile!=null,"saved shared profile reference required");
        Result="running";preview.StartCoroutine(Guard(preview));
    }
    static IEnumerator Guard(ProcessAuthoringPreview preview)
    {
        var original=preview.progression.authoringProfile;var fixture=Object.Instantiate(original);
        preview.progression.authoringProfile=fixture;var run=Run(preview,fixture);Exception failure=null;
        var allStocks=Object.FindObjectsByType<PartStack>(FindObjectsInactive.Include,FindObjectsSortMode.None);
        var counts=allStocks.ToDictionary(x=>x,x=>x.Count);
        while(true)
        {
            bool more=false;object next=null;try{more=run.MoveNext();if(more)next=run.Current;}catch(Exception e){failure=e;}
            if(!more||failure!=null)break;yield return next;
        }
        preview.Stop();preview.progression.authoringProfile=original;
        foreach(var pair in counts){if(pair.Key.Count<pair.Value)pair.Key.Add(pair.Value-pair.Key.Count);while(pair.Key.Count>pair.Value)pair.Key.TryConsume();pair.Key.ResetReadySlot();}
        ProcessAuthoringLayout.Apply(preview.progression,original);preview.Select(0,false,0);
        Object.Destroy(fixture);Result=(failure==null?"PASS":"FAIL "+failure)+"\n"+Result;
        Debug.Log(Result);SessionState.SetString("FactoryProcessGripChecks",Result);
    }
    static IEnumerator Run(ProcessAuthoringPreview p,ProcessAuthoringProfile profile)
    {
        Result=Geometry()+"\n";
        // Front door representative has existing reachable human poses.
        int index=8;var entry=p.progression.processes[index];var record=profile.Find(entry.authoringId);
        p.Select(index,false,0);
        var stock=entry.humanStation.partStacks[0];stock.ResetReadySlot();var layout=record.FindStock(ProcessAuthoringLayout.Key(stock));
        Need(stock.ReadyDisplay!=null,"ready mesh exists");var g=layout.grip;g.configured=true;
        WorkerHandIK.TryComputeGrip(stock.ReadyDisplay.transform,.3f,.05f,.02f,out g.left,out g.right,out g.finger);
        var workerDefault=entry.workers.First(x=>x!=null);var carry=Quaternion.Euler(workerDefault.carryEuler);
        if((carry*g.left).x>(carry*g.right).x){var swap=g.left;g.left=g.right;g.right=swap;}
        g.workerCarryEuler=ProcessPartGrip.DefaultCarryEuler(g.left,g.right);
        // Arm-side surface derived from existing native contact, preserving reachable robot setup.
        g.robot=(g.left+g.right)*.5f;
        foreach(bool robotMode in new[]{false,true})
        {
            p.Select(index,robotMode,0);stock.ResetReadySlot();int before=stock.Count;
            p.SetSpeed(5);p.PlayOnce();float end=Time.realtimeSinceStartup+25;int samples=0;float maximum=0;
            bool empty=false,refilled=false;int rigSamples=0;float wristError=0,steadyWristError=0;string worstPhase="";
            while(Time.realtimeSinceStartup<end&&p.Status!="1회 완료"&&!p.Status.Contains("미완료")&&!p.Status.Contains("불가"))
            {
                if(stock.Count==before-1){empty|=!stock.HasReadyPart;refilled|=stock.HasReadyPart;}
                var crew=PrototypeProcessCrew.Ensure(p.progression,index);
                var list=(IEnumerable)typeof(PrototypeProcessCrew).GetField("slots",Flags).GetValue(crew);
                foreach(var slot in list)
                {
                    var t=slot.GetType();var held=(AssemblyPart)t.GetField("part").GetValue(slot);
                    if(held==null||!(bool)t.GetField("consumed").GetValue(slot)||(bool)t.GetField("attached").GetValue(slot))continue;
                    samples++;
                    if(robotMode)
                    {
                        var arm=(StationController)t.GetField("robot").GetValue(slot);
                        float error=Vector3.Distance(held.transform.TransformPoint((Vector3)t.GetField("robotGripLocal").GetValue(slot)),arm.robotArm.endEffector.position);
                        maximum=Mathf.Max(maximum,error);
                    }
                    else
                    {
                        var worker=(Worker)t.GetField("worker").GetValue(slot);var ov=held.GetComponent<PartGripOverride>();
                        Need(ov!=null&&ov.explicitHands&&Vector3.Distance(ov.gripA,Vector3.Scale(g.left,ProcessPartGrip.ScaleRatio((Vector3)t.GetField("sourceScale").GetValue(slot),held.transform.lossyScale)))<.0001f&&Vector3.Distance(ov.gripB,Vector3.Scale(g.right,ProcessPartGrip.ScaleRatio((Vector3)t.GetField("sourceScale").GetValue(slot),held.transform.lossyScale)))<.0001f,"explicit left/right targets copied to held part");
                        if(worker.handIK.TryGetWristTargets(out var left,out var right,out var lr,out var rr))
                        {
                            var a=worker.handIK.GetComponent<Animator>();
                            var lh=a.GetBoneTransform(HumanBodyBones.LeftHand);var rh=a.GetBoneTransform(HumanBodyBones.RightHand);
                            float error=Mathf.Max(Vector3.Distance(lh.position,left),Vector3.Distance(rh.position,right));wristError=Mathf.Max(wristError,error);rigSamples++;
                            string phase=t.GetField("phase").GetValue(slot).ToString();if(error>=wristError)worstPhase=phase;if(phase!="Approach"||(float)t.GetField("elapsed").GetValue(slot)>.4f)steadyWristError=Mathf.Max(steadyWristError,error);
                        }
                    }
                }
                yield return new WaitForEndOfFrame();
            }
            Need(p.Status=="1회 완료","configured contacts complete "+(robotMode?"robot":"worker")+" status "+p.Status);
            Need(stock.Count==before-1,"consume exactly one");Need(empty&&refilled,"slot empties and refills");Need(samples>0,"sampled real transport");
            if(robotMode)Need(maximum<.002f,"robot contact drift "+maximum);
            else Need(steadyWristError<.15f,"worker steady wrist contact error "+steadyWristError);
            Result+=(robotMode?"robot":"worker")+" samples="+samples+" maxRobotContact="+maximum+" wristMax="+wristError+" worstPhase="+worstPhase+" steadyWristMax="+steadyWristError+" wristSamples="+rigSamples+" exactStock/slot PASS\n";
        }
    }
}
#endif
