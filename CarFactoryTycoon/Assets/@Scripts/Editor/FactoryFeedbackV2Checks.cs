#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;

public static class FactoryFeedbackV2Checks
{
    public static string Result="idle";
    static readonly StringBuilder log=new StringBuilder();
    static FactoryPlayerController player;
    static PlayerAssemblyInteractor interactor;
    static ProductionProgression progression;
    static PrototypeDayCycle day;
    static PrototypeDefectDisplay display;
    static CarController car;
    static readonly BindingFlags Private=BindingFlags.NonPublic|BindingFlags.Instance;
    static readonly MethodInfo Focus=typeof(PlayerAssemblyInteractor).GetMethod("UpdateFocus",Private);
    static void Need(bool value,string reason){if(!value)throw new InvalidOperationException(reason);}
    public static void Start()
    {
        Need(Application.isPlaying,"Play required");log.Clear();Result="running";
        player=Object.FindFirstObjectByType<FactoryPlayerController>();interactor=player.interactor;
        progression=ProductionProgression.Current;day=PrototypeDayCycle.Current;display=Object.FindFirstObjectByType<PrototypeDefectDisplay>();
        player.StartCoroutine(Guard());
    }
    static IEnumerator Guard()
    {
        var routines=new Stack<IEnumerator>();routines.Push(Run());bool success=true;
        while(routines.Count>0)
        {
            object wait=null;bool moved=false;
            try{moved=routines.Peek().MoveNext();if(moved)wait=routines.Peek().Current;}
            catch(Exception ex){log.AppendLine("FAIL "+ex);success=false;break;}
            if(!moved){routines.Pop();continue;}if(wait is IEnumerator nested){routines.Push(nested);continue;}yield return wait;
        }
        if(interactor!=null&&interactor.HeldPart!=null)interactor.TryThrow();
        if(car!=null&&car.gameObject.activeInHierarchy)CarPool.Instance.Return(car);
        if(player!=null){player.Teleport(player.SpawnPoint);player.enabled=true;interactor.enabled=true;interactor.inputEnabled=true;}
        if(day!=null)day.enabled=true;
        Result=(success?"PASS\n":"FAIL\n")+log;Debug.Log(Result);
    }
    static AssemblyPart Part(CarController c,int index)=>c.GetComponentsInChildren<AssemblyPart>(true).First(p=>p.myType==progression.processes[index].parts[0]);
    static CarController NewCar(float progress)
    {
        var c=CarPool.Instance.Get();c.SetPath(progression.mainSpline,0,progress);progression.Stamp(c);
        foreach(var p in c.GetComponentsInChildren<AssemblyPart>(true))p.SetAssembled();return c;
    }
    static void Aim(AssemblyPart part)
    {
        var target=interactor.AttachmentAimPoint(part);var eye=target+Vector3.forward*1.7f;
        player.Teleport(eye-Vector3.up*1.62f);player.viewCamera.transform.SetPositionAndRotation(eye,Quaternion.LookRotation(target-eye));Physics.SyncTransforms();
    }
    static void AttachFixture(AssemblyPart part)
    {
        Need(part.TryBeginPlayerAttachment(interactor,part.AttachmentWorldPosition,part.AttachmentWorldRotation),"fixture begin");
        Need(part.CompletePlayerAttachment(interactor),"fixture attach");
    }
    static IEnumerator Until(Func<bool> condition,float timeout,string reason)
    {float end=Time.time+timeout;while(!condition()&&Time.time<end)yield return null;Need(condition(),reason);}
    static IEnumerator Run()
    {
        day.enabled=false;player.enabled=false;interactor.enabled=false;interactor.inputEnabled=true;Cursor.lockState=CursorLockMode.Locked;
        PrototypeLineController.Current.SetLever(PrototypeLineController.LeverPosition.Stop);
        var economy=EconomyManager.Instance;Need(economy.currentMoney==0&&day.AssemblyIncome==0,"fresh economy expected");
        var shader=Resources.Load<Shader>("PrototypeFeedback/PartOverlay");Need(shader!=null&&!ShaderUtil.ShaderHasError(shader),"feedback shader");
        var entry=progression.processes[0];car=NewCar(entry.gateProgress+.05f/progression.mainSpline.CalculateLength());var part=Part(car,0);part.Reset();
        Need(economy.currentMoney==0,"fixture SetAssembled paid");
        var stock=Object.FindObjectsByType<PartStack>(FindObjectsSortMode.None).First(s=>s.partType==part.myType);
        player.Teleport(stock.transform.position-Vector3.up*.4f);Physics.SyncTransforms();Need(interactor.TryTake(stock),"real stock pickup");Aim(part);
        Need(interactor.TryAttach(car),"player attachment");interactor.AdvanceHeldAttachment(true,2.01f);
        Need(part.IsPlayerAttached&&economy.currentMoney==0&&day.AssemblyIncome==0,"attachment must not pay");
        for(int hit=0;hit<3;hit++)
        {yield return new WaitForSeconds(.31f);Aim(part);Need(interactor.TryHammer(part),"player hit");Need(economy.currentMoney==(hit==2?1:0),"payout before final hit");}
        Need(day.AssemblyIncome==1&&day.SoldCars==0&&day.SalesIncome==0&&day.MissionIncome==0&&day.PurchaseCosts==0&&day.NetIncome==1,"ledger separated");
        Need(!interactor.TryHammer(part)&&economy.currentMoney==1,"duplicate hammer");part.SetWork(0);part.AddWork(part.requiredWork);Need(economy.currentMoney==1,"repeat same-lifetime payout");
        var original=car;CarPool.Instance.Return(car);car=null;
        for(int n=0;n<6;n++){var c=CarPool.Instance.Get();if(c==original){car=c;break;}CarPool.Instance.Return(c);}
        Need(car==original,"actual pool reuse");car.SetPath(progression.mainSpline,0,entry.gateProgress);progression.Stamp(car);part=Part(car,0);AttachFixture(part);
        for(int n=0;n<3;n++)Need(part.TryPlayerHammer(3),"reused part work");Need(economy.currentMoney==2&&day.AssemblyIncome==2,"new vehicle lifetime reward");
        CarPool.Instance.Return(car);car=null;
        day.AdvanceClock(day.settings.dayRealSeconds+1);Need(day.IsSettlement&&day.AssemblyIncome==2&&day.NetIncome==2,"settlement assembly income");
        day.ContinueNextDay();Need(day.AssemblyIncome==0&&economy.currentMoney==2&&day.SoldCars==0,"next-day ledger reset");interactor.inputEnabled=true;Cursor.lockState=CursorLockMode.Locked;
        log.AppendLine("Real stock/player: E pays0; final hit+1; repeated work0; actual pool reuse+1; day ledger/reset, sales/mission unaffected.");
        Need(progression.TryHireProcess(0),"human hire");car=NewCar(entry.gateProgress+.05f/progression.mainSpline.CalculateLength());part=Part(car,0);part.Reset();
        int before=day.AssemblyIncome;yield return Until(()=>part.IsAssembled,28,"real human assembly");Need(day.AssemblyIncome==before+1,"human reward");
        CarPool.Instance.Return(car);car=null;yield return new WaitForSeconds(.3f);economy.Earn(1000);
        Need(progression.TryPurchaseRobot(0),"robot conversion");car=NewCar(entry.gateProgress+.05f/progression.mainSpline.CalculateLength());part=Part(car,0);part.Reset();
        before=day.AssemblyIncome;yield return Until(()=>part.IsAssembled,28,"real robot assembly");Need(day.AssemblyIncome==before+1,"robot reward");
        CarPool.Instance.Return(car);car=null;yield return new WaitForSeconds(.3f);
        log.AppendLine("Actual NPC3-hits and robot3-sparks each pay exactly1.");
        car=NewCar(entry.gateProgress+7f/progression.mainSpline.CalculateLength());
        var absent=Part(car,0);var attached=Part(car,1);var partial=Part(car,2);absent.Reset();attached.Reset();partial.Reset();AttachFixture(attached);partial.SetWork(partial.requiredWork/3);
        var source=attached.GetComponentInChildren<MeshRenderer>(true);var sourceMaterial=source.sharedMaterial;
        display.RefreshDisplay();Need(display.StateOf(absent)==PrototypeDefectDisplay.PartState.MissingSlot&&display.StateOf(attached)==PrototypeDefectDisplay.PartState.AttachedPending&&display.StateOf(partial)==PrototypeDefectDisplay.PartState.AttachedPending,"three states");
        Need(display.SlotPartCount>=1&&display.OutlinePartCount>=2&&display.PreviewPartCount==0,"no blanket preview");
        Need(!absent.gameObject.activeSelf&&source.sharedMaterial==sourceMaterial,"original part state/material modified");
        var roots=Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).Where(t=>t.name.StartsWith("PartFeedback_")).ToArray();
        Need(roots.All(t=>t.GetComponentsInChildren<Collider>(true).Length==0&&t.GetComponentsInChildren<AssemblyPart>(true).Length==0),"interactive visual clones");
        foreach(var renderer in roots.SelectMany(t=>t.GetComponentsInChildren<MeshRenderer>()).Where(r=>r.enabled&&r.sharedMaterial!=null&&r.sharedMaterial.name=="UnfastenedOutline"))
            Need(Mathf.Abs(renderer.sharedMaterial.GetFloat("_OutlinePixels")-1.8f)<.01f,"outline pixel width");
        stock=Object.FindObjectsByType<PartStack>(FindObjectsSortMode.None).First(s=>s.partType==absent.myType);player.Teleport(stock.transform.position-Vector3.up*.4f);Physics.SyncTransforms();Need(interactor.TryTake(stock),"preview stock");Aim(absent);Focus.Invoke(interactor,null);display.RefreshDisplay();
        Need(interactor.FocusedPart==absent&&display.PreviewOf(absent)&&display.PreviewPartCount==1,"focused preview only");
        player.viewCamera.transform.rotation=Quaternion.LookRotation(Vector3.up);Focus.Invoke(interactor,null);display.RefreshDisplay();Need(display.PreviewPartCount==0,"look-away hides preview");
        Aim(absent);Focus.Invoke(interactor,null);player.SetMapMode(true);display.RefreshDisplay();Need(display.PreviewPartCount==0,"map hides preview");player.SetMapMode(false);interactor.inputEnabled=true;
        interactor.TryThrow();player.Teleport(car.transform.position+Vector3.forward*22);display.RefreshDisplay();Need(display.OutlinePartCount==0&&display.SlotPartCount==0&&display.MissingPartCount>=3,"far grouping keeps counts");
        player.Teleport(car.transform.position+Vector3.forward*2);display.RefreshDisplay();attached.TryPlayerHammer(3);attached.TryPlayerHammer(3);attached.TryPlayerHammer(3);display.RefreshDisplay();Need(display.StateOf(attached)==PrototypeDefectDisplay.PartState.None,"completion removes outline");
        car.CloseRepairWindow();display.RefreshDisplay();Need(display.MissingPartCount==0&&display.OutlinePartCount==0&&display.SlotPartCount==0,"deadline cleanup");
        CarPool.Instance.Return(car);car=null;display.RefreshDisplay();yield return null;Need(!Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).Any(t=>t.name.StartsWith("PartFeedback_")),"pool cleanup");
        log.AppendLine("Missing corners/selected translucent preview, attached/partial outline, original materials untouched, no collision/scripts, far grouping, completion/deadline/pool cleanup passed.");
        int retainedIncome=day.AssemblyIncome,retainedBalance=economy.currentMoney;
        var shake=Object.FindFirstObjectByType<ShakeTestStation>();car=NewCar(shake.gateProgress-.0001f);
        Part(car,0).Reset();part=Part(car,1);part.Reset();AttachFixture(part);for(int n=0;n<3;n++)Need(part.TryPlayerHammer(3),"pre-inspection completion");
        yield return Until(()=>!car.gameObject.activeInHierarchy,12,"actual rejection return");
        Need(day.AssemblyIncome==retainedIncome+1&&economy.currentMoney==retainedBalance+1&&day.SoldCars==0&&day.MissionIncome==0,"rejection must keep work reward without sale/mission");car=null;
        log.AppendLine("Actual shake rejection retains earned part reward; no sale/mission reward.");
    }
}
#endif
