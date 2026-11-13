#if UNITY_EDITOR
using System;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;

public static class FactoryProcessEditorUXChecks
{
    static readonly BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    static object Get(ProcessAuthoringWindow w,string key)=>typeof(ProcessAuthoringWindow).GetField(key,Flags).GetValue(w);
    static void Set(ProcessAuthoringWindow w,string key,object value)=>typeof(ProcessAuthoringWindow).GetField(key,Flags).SetValue(w,value);
    static void Call(ProcessAuthoringWindow w,string method)=>typeof(ProcessAuthoringWindow).GetMethod(method,Flags).Invoke(w,null);
    static void Need(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
    public static string CloseProfile()
    {
        var preview=Object.FindFirstObjectByType<ProcessAuthoringPreview>();
        ProcessAuthoringWindow.Open();var window=Resources.FindObjectsOfTypeAll<ProcessAuthoringWindow>().First();
        Call(window,"ApplyLayout");var source=(ProcessAuthoringProfile)Get(window,"source");window.Close();
        Need(source!=null&&preview.progression.authoringProfile==source,"closing tool must retain saved profile, never a destroyed draft");
        var external=Object.Instantiate(source);preview.progression.authoringProfile=external;
        try
        {
            ProcessAuthoringWindow.Open();Resources.FindObjectsOfTypeAll<ProcessAuthoringWindow>().First().Close();
            Need(preview.progression.authoringProfile==external,"window must not replace externally owned runtime profiles when it did not apply a draft");
        }finally{preview.progression.authoringProfile=source;Object.DestroyImmediate(external);}
        return "PASS closed tool retains saved profile + external ownership";
    }
    public static string Run()
    {
        var preview=Object.FindFirstObjectByType<ProcessAuthoringPreview>();Need(preview!=null,"authoring scene required");
        
        var window=Resources.FindObjectsOfTypeAll<ProcessAuthoringWindow>().First();
        var original=(ProcessAuthoringProfile)Get(window,"draft");var fixture=Object.Instantiate(original);
        int originalIndex=(int)Get(window,"processIndex"),originalTarget=(int)Get(window,"targetIndex");bool originalDirty=(bool)Get(window,"dirty"),originalUnsaved=window.hasUnsavedChanges;
        var log=new StringBuilder();
        try
        {
            Set(window,"draft",fixture);Set(window,"processIndex",0);Set(window,"targetIndex",0);Call(window,"RebuildBindings");Call(window,"ApplyLayout");
            var entry=preview.progression.processes[0];var stock=entry.humanStation.partStacks[0];var data=fixture.processes[0].FindStock(ProcessAuthoringLayout.Key(stock));var frame=ProcessAuthoringLayout.Frame(preview.progression,entry);
            var before=stock.transform.position;data.inventory.position.x+=1f;Call(window,"ApplyLayout");
            Need(Vector3.Distance(stock.transform.position,before+frame.rotation*Vector3.right)<.001f,"numeric position must update visible inventory immediately");log.AppendLine("OK numeric -> Scene "+(Application.isPlaying?(preview.Paused?"paused":"unpaused"):"edit"));
            stock.transform.position+=frame.rotation*Vector3.right*.7f;float expected=data.inventory.position.x+.7f;Call(window,"EditorTick");
            Need(Mathf.Abs(data.inventory.position.x-expected)<.001f,"Scene transform must update draft");log.AppendLine("OK Scene transform -> numeric draft");
            var editing=(ProcessAuthoringSceneEditing)Get(window,"sceneEditing");var pickup=editing.bindings.First(x=>x.stock==data&&x.role=="Pickup");Need(editing.bindings[editing.Find(stock.ReadyDisplay.transform)].role=="Pickup","clicking displayed ready part selects its pickup marker, not inventory");log.AppendLine("OK ready display selects pickup");var pickupBefore=stock.ReadyPosition;pickup.target.position+=Vector3.up*.3f;Call(window,"EditorTick");
            Need(Vector3.Distance(stock.ReadyPosition,pickupBefore+Vector3.up*.3f)<.001f,"pickup marker must update actual ready slot");log.AppendLine("OK ready slot marker -> live pickup");
            Undo.IncrementCurrentGroup();int group=Undo.GetCurrentGroup();Undo.RecordObject(stock.transform,"Drag inventory");var old=stock.transform.position;stock.transform.position+=Vector3.forward*.4f;Call(window,"EditorTick");Undo.FlushUndoRecordObjects();Undo.PerformUndo();
            Need(Vector3.Distance(stock.transform.position,old)<.001f,"Undo must restore transform and profile together");log.AppendLine("OK drag Undo");Undo.RevertAllDownToGroup(group);
            if(Application.isPlaying)
            {
                var editingLive=(ProcessAuthoringSceneEditing)Get(window,"sceneEditing");
                var liveActor=editingLive.bindings.First(x=>x.role=="Actor"&&x.actor.kind==ProcessActorKind.Worker);
                var actual=entry.workers.First(x=>ProcessAuthoringLayout.Key(x)==liveActor.actor.id);
                liveActor.target.position+=frame.rotation*Vector3.forward*.2f;var expectedRoot=liveActor.target.position;Call(window,"EditorTick");
                Need(Vector3.Distance(actual.transform.position,expectedRoot)<.001f,"drag initial marker must reposition live worker");
                log.AppendLine("OK worker live marker drag -> actual worker");
            }
            if(Application.isPlaying && preview.Paused)
            {
                var errors=new System.Collections.Generic.List<string>();
                var edit=(ProcessAuthoringSceneEditing)Get(window,"sceneEditing");var actor=edit.bindings.First(x=>x.role=="Actor"&&x.actor.kind==ProcessActorKind.Worker);var actorBefore=actor.target.position;
                Undo.IncrementCurrentGroup();int actorUndo=Undo.GetCurrentGroup();Undo.RecordObject(fixture,"Edit worker numeric pose");var pose=actor.Pose;pose.position.z+=.4f;actor.Pose=pose;Set(window,"targetIndex",edit.bindings.IndexOf(actor));Call(window,"ApplyLayout");Undo.FlushUndoRecordObjects();Set(window,"targetIndex",0);Undo.PerformUndo();
                if(Vector3.Distance(actor.target.position,actorBefore)>.001f)errors.Add("worker Undo after selecting stock");Undo.RevertAllDownToGroup(actorUndo);actor.target.position=actorBefore;
                var live=stock.transform.position;Set(window,"processIndex",1);Call(window,"Select");Set(window,"processIndex",0);Call(window,"Select");
                if(Vector3.Distance(stock.transform.position,live)>.001f||!preview.Paused)errors.Add("paused draft process roundtrip");
                Need(errors.Count==0,string.Join("; ",errors));log.AppendLine("OK worker Undo across selection + paused draft roundtrip");
            }
            string stable=EditorJsonUtility.ToJson(fixture);for(int i=0;i<100;i++)Call(window,"EditorTick");Need(stable==EditorJsonUtility.ToJson(fixture),"idle editor polling must not drift saved poses");log.AppendLine("OK 100 idle polls without pose drift");
            foreach(var root in preview.processRoots)foreach(var b in root.GetComponentsInChildren<MonoBehaviour>(true))Need(b.GetType().FullName!="StarterAssets.StarterAssetsInputs","NPC focus callback must be removed");
            Need(preview.GetComponent<ProcessAuthoringCamera>()!=null,"inspection camera attached");
            Need(!ProcessAuthoringCamera.AcceptsInput(false,true,true)&&!ProcessAuthoringCamera.AcceptsInput(true,false,true)&&!ProcessAuthoringCamera.AcceptsInput(true,true,false)&&ProcessAuthoringCamera.AcceptsInput(true,true,true),"camera must require focused hovered Game + RMB");
            var go=new GameObject("CameraUXFixture");float oldTime=Time.timeScale;
            try{var cam=go.AddComponent<Camera>();var control=go.AddComponent<ProcessAuthoringCamera>();control.view=cam;Time.timeScale=0;var start=go.transform.position;control.Step(new Vector2(5,3),Vector3.forward,.2f);Need(Vector3.Distance(start,go.transform.position)>.9f,"camera must move while paused");control.ReleaseCursor();Need(Cursor.lockState==CursorLockMode.None&&Cursor.visible,"cursor stays free");}
            finally{Time.timeScale=oldTime;Object.DestroyImmediate(go);}
            log.AppendLine("OK camera paused/unscaled + focus gate + free cursor");return "PASS\n"+log;
        }
        finally
        {
            Set(window,"draft",original);Set(window,"processIndex",originalIndex);Set(window,"targetIndex",originalTarget);Call(window,"ApplyLayout");Set(window,"dirty",originalDirty);typeof(EditorWindow).GetProperty("hasUnsavedChanges",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).SetValue(window,originalUnsaved);Object.DestroyImmediate(fixture);
        }
    }
}
#endif
