#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Splines;
using Object=UnityEngine.Object;

public sealed partial class ProcessAuthoringWindow : EditorWindow
{
    ProcessAuthoringProfile source,draft;
    int processIndex,actorIndex,stockIndex,vehicleIndex,partIndex;
    bool vehicleSettings;
    int tabIndex,targetIndex;
    bool wasEditable,selectionSync,needsDraftApply,wasIdle;
    ProcessAuthoringSceneEditing sceneEditing;
    bool Editable=>Preview!=null;
    bool dirty,robot;
    Vector2 scroll;
    float previewSpeed=1f;
    readonly BoxBoundsHandle boxHandle = new BoxBoundsHandle();
    [MenuItem("Tools/CarFactory/Process Authoring/Open Workbench")]
    public static void Open()=>GetWindow<ProcessAuthoringWindow>("공정 편집");
    ProcessAuthoringPreview Preview=>Object.FindFirstObjectByType<ProcessAuthoringPreview>();
    void OnEnable()
    {
        minSize=new Vector2(430,560);
        SceneView.duringSceneGui+=DrawScene;Undo.undoRedoPerformed+=OnUndo;EditorApplication.update+=EditorTick;Selection.selectionChanged+=OnSceneSelection;AssemblyReloadEvents.beforeAssemblyReload+=BackupDraft;EditorApplication.playModeStateChanged+=OnPlayMode;ProcessAuthoringPreview.BeforeSessionStart+=ConnectDraftBeforeStart;
        Load();
        if(SessionState.GetBool("ProcessAuthoring.RecoveryDirty",false)&&draft!=null)
        {EditorJsonUtility.FromJsonOverwrite(SessionState.GetString("ProcessAuthoring.RecoveryDraft","{}"),draft);dirty=true;hasUnsavedChanges=true;needsDraftApply=true;SessionState.SetBool("ProcessAuthoring.RecoveryDirty",false);}
        WheelProcessMigration.ExpandProfile(draft);
        var current=Preview;if(current!=null)WheelProcessMigration.ExpandScene(current.progression,current);
        if(current!=null&&draft!=null){processIndex=Mathf.Clamp(current.selectedProcess,0,current.fuelStation!=null?draft.processes.Count:draft.processes.Count-1);vehicleIndex=Mathf.Clamp(current.selectedVehicle,0,draft.vehicles.Count-1);robot=current.robotMode;}
        if(needsDraftApply&&Editable&&Preview!=null){ApplyLayout();}else RebuildBindings();
    }
    void ConnectDraftBeforeStart(ProcessAuthoringPreview preview)
    {
        if(draft==null||preview!=Preview)return;
        preview.progression.authoringProfile=draft;sceneEditing=null;needsDraftApply=true;
    }
    void OnPlayMode(PlayModeStateChange state)
    {
        if(state==PlayModeStateChange.ExitingEditMode||state==PlayModeStateChange.ExitingPlayMode)BackupDraft();
        if(state==PlayModeStateChange.EnteredPlayMode||state==PlayModeStateChange.EnteredEditMode){sceneEditing=null;needsDraftApply=true;}
    }
    void BackupDraft(){if(dirty&&draft!=null){SessionState.SetString("ProcessAuthoring.RecoveryDraft",EditorJsonUtility.ToJson(draft));SessionState.SetBool("ProcessAuthoring.RecoveryDirty",true);}}
    void OnDisable()
    {
        DisposeRigPreview();
        if(Preview!=null){if(Application.isPlaying)Preview.CloseTool();if(Preview.progression.authoringProfile==draft)Preview.progression.authoringProfile=source;}SceneView.duringSceneGui-=DrawScene;Undo.undoRedoPerformed-=OnUndo;EditorApplication.update-=EditorTick;Selection.selectionChanged-=OnSceneSelection;AssemblyReloadEvents.beforeAssemblyReload-=BackupDraft;EditorApplication.playModeStateChanged-=OnPlayMode;ProcessAuthoringPreview.BeforeSessionStart-=ConnectDraftBeforeStart;
        if(draft!=null)DestroyImmediate(draft);
    }
    void RebuildBindings()
    {
        var p=Preview;if(p!=null)WheelProcessMigration.ExpandScene(p.progression,p);
        if(p==null||draft==null){sceneEditing=null;return;}
        Transform selected=sceneEditing!=null&&targetIndex<sceneEditing.bindings.Count?sceneEditing.bindings[targetIndex].target:null;
        sceneEditing=new ProcessAuthoringSceneEditing(p,draft,processIndex,robot);
        int found=sceneEditing.Find(selected);targetIndex=found>=0?found:Mathf.Clamp(targetIndex,0,Mathf.Max(0,sceneEditing.bindings.Count-1));
    }
    void EditorTick()
    {
        CapturePackingExample();
        var current=Preview;
        UpdateRigPreview();
        if(current==null){sceneEditing=null;wasEditable=false;Repaint();return;}
        if(wasIdle!=current.Idle){wasIdle=current.Idle;RebuildBindings();wasEditable=Editable;Repaint();return;}
        if(sceneEditing==null||sceneEditing.Owner!=current)
        {if(Editable)ApplyLayout();else RebuildBindings();wasEditable=Editable;Repaint();return;}
        if(!Editable){wasEditable=false;Repaint();return;}
        if(needsDraftApply){ApplyLayout();wasEditable=true;}
        else if(!wasEditable||sceneEditing==null){RebuildBindings();wasEditable=true;}
        else if(sceneEditing.Pull(draft)){dirty=true;hasUnsavedChanges=true;ApplyLayout();}
        Repaint();
    }
    void OnSceneSelection()
    {
        if(selectionSync||sceneEditing==null||!Editable)return;
        var selected=Selection.activeTransform;
        if(tabIndex==5&&Preview!=null&&!Preview.FuelMode)
        {
            var pack=GripStock(Preview,out var packData);var example=pack!=null?pack.InventoryExample:null;
            if(example!=null&&selected!=null&&selected.IsChildOf(example))
            {if(selected!=example){selectionSync=true;Selection.activeGameObject=example.gameObject;selectionSync=false;}return;}
        }
        if(rigPreview?.root!=null&&selected!=null&&selected.IsChildOf(rigPreview.root.transform))
        {
            bool isPart=rigPreview.part!=null&&selected.IsChildOf(rigPreview.part);
            var target=isPart?rigPreview.part:rigPreview.root.transform;rigControl=isPart?5:4;
            if(selected!=target){selectionSync=true;Selection.activeGameObject=target.gameObject;selectionSync=false;}
            Repaint();return;
        }
        int index=sceneEditing.Find(Selection.activeTransform);if(index<0)return;targetIndex=index;
        var item=sceneEditing.bindings[index];UpdateBindingContext(item);
        if(item.target!=null&&Selection.activeTransform!=item.target){selectionSync=true;Selection.activeGameObject=item.target.gameObject;selectionSync=false;}
        Repaint();SceneView.RepaintAll();
    }
    void UpdateBindingContext(ProcessAuthoringSceneEditing.Binding item)
    {
        if(draft==null||processIndex>=draft.processes.Count)return;
        var record=draft.processes[processIndex];
        if(item.stock!=null)stockIndex=Mathf.Max(0,record.stocks.IndexOf(item.stock));
        if(item.actor!=null)actorIndex=Mathf.Max(0,Array.IndexOf(record.actors.Where(a=>(a.kind==ProcessActorKind.Robot)==robot).ToArray(),item.actor));
    }
    void OnUndo(){dirty=true;hasUnsavedChanges=true;ApplyLayout();Repaint();}
    void Load()
    {
        source=AssetDatabase.LoadAssetAtPath<ProcessAuthoringProfile>(ProcessAuthoringCapture.AssetPath);
        if(draft!=null)DestroyImmediate(draft);
        if(source!=null){draft=Instantiate(source);draft.hideFlags=HideFlags.HideAndDontSave;WheelProcessMigration.ExpandProfile(draft);}dirty=false;hasUnsavedChanges=false;
    }
    bool ResolveDirty()
    {
        if(!dirty)return true;
        int choice=EditorUtility.DisplayDialogComplex("미저장 공정 설정","변경을 저장할까요?","저장","취소","버리기");
        if(choice==1)return false;if(choice==0)Save();else Load();return true;
    }
    public override void SaveChanges(){Save();base.SaveChanges();}
    public override void DiscardChanges(){SessionState.SetBool("ProcessAuthoring.RecoveryDirty",false);Load();ApplyLayout();base.DiscardChanges();}
    void Save()
    {
        if(draft==null||source==null)return;
        if(sceneEditing!=null&&sceneEditing.Pull(draft))dirty=true;
        Undo.RecordObject(source,"Save Process Authoring");string assetName=System.IO.Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(source));EditorUtility.CopySerialized(draft,source);source.name=assetName;source.hideFlags=HideFlags.None;
        EditorUtility.SetDirty(source);AssetDatabase.SaveAssets();dirty=false;hasUnsavedChanges=false;SessionState.SetBool("ProcessAuthoring.RecoveryDirty",false);
        var preview=Preview;if(preview!=null){preview.progression.authoringProfile=source;ApplyLayout();}
    }
    void Changed(){dirty=true;hasUnsavedChanges=true;ApplyLayout();SceneView.RepaintAll();}
    void ApplyLayout()
    {
        var preview=Preview;if(preview==null||draft==null||!Editable)return;
        WheelProcessMigration.ExpandScene(preview.progression,preview);
        if(Application.isPlaying&&(!Application.IsPlaying(preview.gameObject)||!preview.gameObject.scene.isLoaded||!SceneManager.GetActiveScene().IsValid()||!SceneManager.GetActiveScene().isLoaded)){needsDraftApply=true;return;}
        needsDraftApply=false;
        var changedActors=sceneEditing!=null?sceneEditing.bindings.Where(x=>x.role=="Actor"&&x.actor!=null&&x.actor.kind==ProcessActorKind.Worker&&(x.DataChanged||Mathf.Abs(x.distance-draft.actorDistance)>.00001f||x.side!=x.actor.side)).ToArray():Array.Empty<ProcessAuthoringSceneEditing.Binding>();
        preview.progression.authoringProfile=Application.isPlaying?draft:source;
        ProcessAuthoringLayout.Apply(preview.progression,draft,!Application.isPlaying&&!preview.Idle);
        if(Application.isPlaying)
        {
            var entry=processIndex<preview.progression.processes.Length?preview.progression.processes[processIndex]:null;
            foreach(var item in changedActors)if(entry?.workers!=null)
                foreach(var worker in entry.workers)if(worker!=null&&ProcessAuthoringLayout.Key(worker)==item.actor.id)
                    ProcessAuthoringLayout.WithDistance(item.Pose,item.actor.side,draft.actorDistance).Apply(worker.transform,item.frame);
            foreach(var crew in preview.progression.GetComponents<PrototypeProcessCrew>())crew.RefreshAuthoredLayout();
        }
        if(preview.Car!=null&&vehicleIndex<draft.vehicles.Count){ProcessAuthoringLayout.ApplyVehicle(preview.Car,draft,draft.vehicles[vehicleIndex].id);ProcessPartGrip.ApplyToCar(preview.progression,preview.Car);}
        if(preview.Idle)preview.IdleAtStationWithProfile(draft);
        RebuildBindings();wasEditable=true;wasIdle=preview.Idle;
        if(!Application.isPlaying)EditorSceneManager.MarkSceneDirty(preview.gameObject.scene);
        SceneView.RepaintAll();
    }
    void Select()
    {
        var preview=Preview;if(preview==null)return;
        WheelProcessMigration.ExpandScene(preview.progression,preview);
        if(Application.isPlaying){bool paused=preview.Paused;preview.progression.authoringProfile=draft;preview.Select(processIndex,robot,vehicleIndex);ProcessAuthoringLayout.Apply(preview.progression,draft);if(paused)preview.Pause(true);RebuildBindings();wasEditable=Editable;return;}
        preview.selectedProcess=processIndex;preview.robotMode=robot;preview.selectedVehicle=vehicleIndex;
        for(int i=0;i<preview.processRoots.Length;i++)preview.processRoots[i].SetActive(i==processIndex);
        var armGroup=processIndex<preview.processRoots.Length?preview.processRoots[processIndex].transform.Find("RobotArms"):null;if(armGroup!=null)armGroup.gameObject.SetActive(robot);
        if(preview.fuelStation!=null)preview.fuelStation.gameObject.SetActive(processIndex==preview.progression.processes.Length);
        if(processIndex==preview.progression.processes.Length){ApplyLayout();return;}
        var entry=preview.progression.processes[processIndex];
        if(entry.humanRoot!=null)entry.humanRoot.SetActive(!robot);
        if(entry.workers!=null)foreach(var w in entry.workers)if(w!=null)w.gameObject.SetActive(!robot);
        if(entry.robots!=null)foreach(var r in entry.robots)if(r!=null)r.gameObject.SetActive(robot);
        for(int i=0;i<preview.vehicleInstances.Length;i++)if(preview.vehicleInstances[i]!=null)preview.vehicleInstances[i].gameObject.SetActive(i==vehicleIndex);
        ApplyLayout();SceneView.RepaintAll();
    }
    void OnGUI()
    {
        EditorGUIUtility.labelWidth=135;
        if(draft==null){EditorGUILayout.HelpBox("메인 씬에서 공정 배치를 가져오세요.",MessageType.Info);if(GUILayout.Button("메인 배치 가져오기")){ProcessAuthoringCapture.ImportMain();Load();}return;}
        var preview=Preview;
        if(preview==null)
        {
            EditorGUILayout.HelpBox("Scene에서 배치하고 Game에서 동작을 관찰합니다. 공정 편집 창은 두 화면과 같은 설정을 편집합니다.",MessageType.Info);
            if(GUILayout.Button("공정 편집 씬 열기",GUILayout.Height(30))&&ResolveDirty()&&EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()){EditorSceneManager.OpenScene(ProcessAuthoringSceneSetup.ScenePath,OpenSceneMode.Single);Select();}
            return;
        }
        using(new EditorGUILayout.VerticalScope()){GUILayout.Space(8);EditorGUILayout.LabelField("공정 편집",new GUIStyle(EditorStyles.boldLabel){fontSize=16});
        EditorGUILayout.LabelField(Application.isPlaying?(preview.Paused?"일시정지 · 배치 편집 가능":"실행 중 · 배치 편집·저장 가능"):"편집 모드 · Scene에서 배치",EditorStyles.miniLabel);
        int next=EditorGUILayout.Popup("공정",processIndex,(preview.fuelStation!=null?draft.processes.Select(x=>x.displayName).Concat(new[]{"주유"}):draft.processes.Select(x=>x.displayName)).ToArray());
        if(next!=processIndex){processIndex=next;actorIndex=stockIndex=targetIndex=0;Select();}
        next=EditorGUILayout.Popup("차종",vehicleIndex,draft.vehicles.Select(x=>x.displayName).ToArray());if(next!=vehicleIndex){vehicleIndex=next;Select();}
        int mode=GUILayout.Toolbar(robot?1:0,new[]{"작업자","로봇팔"},GUILayout.Height(25));if(mode!=(robot?1:0)){robot=mode==1;actorIndex=targetIndex=0;Select();}
        GUILayout.Space(6);DrawPlayback(preview);GUILayout.Space(8);
        tabIndex=GUILayout.Toolbar(tabIndex,new[]{"배치","잡기","동선 · IK","차종","공통","재고 배치"},GUILayout.Height(25));
        scroll=EditorGUILayout.BeginScrollView(scroll);
        using(new EditorGUI.DisabledScope(!Editable))
        {
            GUILayout.Space(10);
            if(tabIndex==0)DrawPlacement(preview);
            else if(tabIndex==1)DrawGripPanel(preview);
            else if(tabIndex==2)DrawMotion(preview);
            else if(tabIndex==3){vehicleSettings=true;DrawVehicleSettings(preview);}
            else if(tabIndex==4)DrawCommon(preview);
            else DrawStockPacking(preview);
        }
        GUILayout.Space(8);EditorGUILayout.EndScrollView();
        if(Application.isPlaying)
        {
            if(preview.FuelMode)DrawHandDiagnostics(preview.fuelStation.handIK,"주유원");
            else foreach(var worker in preview.progression.processes[processIndex].workers)if(worker!=null&&worker.gameObject.activeInHierarchy)DrawHandDiagnostics(worker.handIK,worker.name);
        }
        GUILayout.Space(6);EditorGUILayout.LabelField(dirty?"저장하지 않은 설정이 있습니다":"저장된 설정과 일치합니다",EditorStyles.miniLabel);
        using(new EditorGUILayout.HorizontalScope())
        {
            if(GUILayout.Button("설정 저장",GUILayout.Height(28)))Save();
            if(GUILayout.Button("다시 불러오기",GUILayout.Height(28))&&ResolveDirty()){Load();ApplyLayout();}
        }
        using(new EditorGUI.DisabledScope(Application.isPlaying))if(GUILayout.Button("저장하고 메인 배치에 적용",GUILayout.Height(27))){Save();ShowNotification(new GUIContent(ProcessAuthoringMainApply.ApplySaved()));}
        GUILayout.Space(6);}
    }
    void DrawPlacement(ProcessAuthoringPreview preview)
    {
        if(sceneEditing==null)RebuildBindings();if(sceneEditing==null||sceneEditing.bindings.Count==0)return;
        targetIndex=EditorGUILayout.Popup("편집 대상",Mathf.Clamp(targetIndex,0,sceneEditing.bindings.Count-1),sceneEditing.bindings.Select(x=>x.label).ToArray());
        var item=sceneEditing.bindings[targetIndex];UpdateBindingContext(item);GUILayout.Space(6);
        using(new EditorGUILayout.HorizontalScope())
        {
            if(GUILayout.Button("Scene에서 선택",GUILayout.Height(26))&&item.target!=null){Selection.activeGameObject=item.target.gameObject;Tools.current=Tool.Move;SceneView.RepaintAll();}
            if(GUILayout.Button("선택 대상 보기",GUILayout.Height(26))&&item.target!=null){Selection.activeGameObject=item.target.gameObject;SceneView.lastActiveSceneView?.FrameSelected();}
        }
        EditorGUILayout.HelpBox("Scene 뷰에서 W(이동) / E(회전)로 드래그하거나 아래 수치를 바꾸세요. 두 편집은 같은 설정에 즉시 반영됩니다.",MessageType.Info);
        EditorGUI.BeginChangeCheck();Undo.RecordObject(draft,"Edit Selected Layout");var pose=item.Pose;
        if(item.role=="Stop")pose.position.z=EditorGUILayout.FloatField("진입 후 정차 거리 (m)",pose.position.z);
        else DrawPoseFields(item.label,ref pose);
        if(item.stock!=null&&item.role=="Stock")item.stock.itemEuler=EditorGUILayout.Vector3Field("부품 자체 회전",item.stock.itemEuler);
        if(EditorGUI.EndChangeCheck()){item.Pose=pose;Changed();}
        GUILayout.Space(6);EditorGUILayout.HelpBox("라인 좌표(m): X = 좌우 보정, Y = 높이, Z = 진행 방향. 공통 거리 보정이 별도로 더해집니다. 집기 슬롯과 재고 더미는 독립된 위치입니다.",MessageType.None);
        if(item.target!=null)EditorGUILayout.LabelField("실제 월드 위치",item.target.position.ToString("F2"));
    }
    void DrawMotion(ProcessAuthoringPreview preview)
    {
        if(preview.FuelMode)
        {
            EditorGUI.BeginChangeCheck();Undo.RecordObject(draft,"Edit Fuel Work Pose");draft.fuel.useWorkStand=EditorGUILayout.Toggle("주유 작업 위치 지정",draft.fuel.useWorkStand);if(EditorGUI.EndChangeCheck())Changed();
            EditorGUILayout.HelpBox("대기·노즐·호스·작업 위치는 배치 탭에서 선택합니다.",MessageType.Info);return;
        }
        var actors=draft.processes[processIndex].actors.Where(a=>(a.kind==ProcessActorKind.Robot)==robot).ToArray();if(actors.Length==0)return;
        actorIndex=EditorGUILayout.Popup("동작 주체",Mathf.Clamp(actorIndex,0,actors.Length-1),actors.Select((a,i)=>(i+1)+" · "+(robot?a.part.ToString():"작업자")).ToArray());
        var stocks=draft.processes[processIndex].stocks;if(stocks.Count>1)stockIndex=EditorGUILayout.Popup("경로에서 볼 집기 슬롯",Mathf.Clamp(stockIndex,0,stocks.Count-1),stocks.Select(x=>x.part.ToString()).ToArray());
        var actor=actors[actorIndex];EditorGUI.BeginChangeCheck();Undo.RecordObject(draft,"Edit Actor Motion");
        if(robot){GUILayout.Label("차량 감지 박스",EditorStyles.boldLabel);actor.detectionCenter=EditorGUILayout.Vector3Field("로컬 중심",actor.detectionCenter);actor.detectionSize=EditorGUILayout.Vector3Field("크기",actor.detectionSize);}
        else
        {
            bool wasDirect=actor.hasWorkPose;
            actor.hasWorkPose=EditorGUILayout.Toggle("작업 위치 직접 지정",actor.hasWorkPose);
            if(!wasDirect&&actor.hasWorkPose)InitializeWorkPose(preview,actor);
            if(actor.hasWorkPose&&GUILayout.Button("작업 위치를 자동 기준으로 맞추기")){InitializeWorkPose(preview,actor);Changed();}
            actor.useAnimationRigging=EditorGUILayout.Toggle("Animation Rigging 손 IK",actor.useAnimationRigging);actor.bodySupport=EditorGUILayout.Toggle("상체 · 발 지지 보정",actor.bodySupport);
            GUILayout.Space(10);DrawCurveFields("일반 대기 → 집기",actor.fetchPath);DrawCurveFields("집기 → 체결 대기",actor.approachPath);
        }
        DrawCurveFields("체결 대기 → 작업 위치",actor.walkPath);DrawCurveFields("부품 운반",actor.carryPath);
        if(EditorGUI.EndChangeCheck())Changed();
        GUILayout.Space(10);
        if(GUILayout.Button("선택 작업자의 모든 경로 사용"))
        {
            Undo.RecordObject(draft,"Enable Actor Paths");
            actor.fetchPath.enabled=actor.approachPath.enabled=actor.walkPath.enabled=actor.carryPath.enabled=true;Changed();
        }
        EditorGUILayout.HelpBox("활성화한 곡선은 Scene 뷰에 표시됩니다. 일반 대기는 작업 후 복귀, 체결 대기는 차량 정지 전 대기하는 위치입니다.",MessageType.None);
    }
    void DrawCommon(ProcessAuthoringPreview preview)
    {
        EditorGUI.BeginChangeCheck();Undo.RecordObject(draft,"Edit Common Layout");GUILayout.Label("전체 공정에 적용",EditorStyles.boldLabel);
        draft.actorDistance=Mathf.Max(0,EditorGUILayout.FloatField("라인 → 주체 거리 (m)",draft.actorDistance));draft.hiringPostDistance=Mathf.Max(0,EditorGUILayout.FloatField("라인 → 고용 표지 (m)",draft.hiringPostDistance));draft.railWidth=Mathf.Max(.1f,EditorGUILayout.FloatField("레일 폭 (m)",draft.railWidth));
        GUILayout.Space(10);if(!preview.FuelMode){var actor=draft.processes[processIndex].actors.Where(x=>(x.kind==ProcessActorKind.Robot)==robot).ElementAtOrDefault(actorIndex);if(actor!=null)actor.side=EditorGUILayout.Popup("주체가 서는 쪽",actor.side<0?0:1,new[]{"진행 기준 왼쪽","진행 기준 오른쪽"})==0?-1:1;}
        else {draft.fuel.carPort=EditorGUILayout.Vector3Field("공통 차량 로컬 주유구",draft.fuel.carPort);draft.fuel.stopOffsetMeters=EditorGUILayout.FloatField("주유 정차 보정 (m)",draft.fuel.stopOffsetMeters);}
        if(EditorGUI.EndChangeCheck())Changed();
    }
    void InitializeWorkPose(ProcessAuthoringPreview preview,ProcessActorLayout actor)
    {
        if(preview.Car==null)return;
        var entry=preview.progression.processes[processIndex];var record=draft.processes[processIndex];var frame=ProcessAuthoringLayout.Frame(preview.progression,entry);
        var home=ProcessAuthoringLayout.WithDistance(actor.home,actor.side,draft.actorDistance).WorldPosition(frame);
        var type=record.stocks.Count>0?record.stocks[Mathf.Clamp(stockIndex,0,record.stocks.Count-1)].part:entry.parts[0];
        var part=preview.Car.GetComponentsInChildren<AssemblyPart>(true).FirstOrDefault(x=>x.myType==type);if(part==null)return;
        float meters=record.stopOffsetMeters;var model=draft.vehicles[vehicleIndex];
        foreach(var data in model.overrides)if(data.processId==entry.authoringId&&data.overrideStop){meters=data.stopOffsetMeters;break;}
        float stop=entry.gateProgress+meters/Mathf.Max(.01f,preview.progression.mainSpline.CalculateLength());
        var parked=ProcessAuthoringLayout.FrameAt(preview.progression.mainSpline,Mathf.Clamp01(stop));
        var oldPosition=preview.Car.transform.position;var oldRotation=preview.Car.transform.rotation;
        try
        {
            preview.Car.transform.SetPositionAndRotation(parked.position,parked.rotation);
            var point=PrototypeProcessCrew.DefaultWorkStandFor(preview.Car,part.AttachmentWorldPosition,home);
            var worker=entry.workers?.FirstOrDefault(x=>x!=null&&ProcessAuthoringLayout.Key(x)==actor.id);
            var grip=record.stocks.FirstOrDefault(x=>x.part==type)?.grip;
            if(worker!=null&&worker.handIK!=null&&grip!=null&&grip.configured)
            {
                var reach=WorkerReachEnvelope.Ensure(worker);var scale=ProcessPartGrip.SourceScale(preview.progression,type);
                var a=ProcessPartGrip.Contact(part.AttachmentWorldPosition,part.AttachmentWorldRotation,scale,grip.left);
                var b=ProcessPartGrip.Contact(part.AttachmentWorldPosition,part.AttachmentWorldRotation,scale,grip.right);
                var finger=Vector3.ProjectOnPlane(part.AttachmentWorldRotation*Vector3.Scale(scale,grip.finger),b-a);
                if(finger.sqrMagnitude<.00001f)finger=Vector3.ProjectOnPlane((a+b)*.5f-home,Vector3.up);finger.Normalize();
                a-=finger*worker.handIK.palmOffset;b-=finger*worker.handIK.palmOffset;
                point=reach.StandFor(a,b,home-(a+b)*.5f,home.y,grip.reachFraction);
                var existingHull=preview.Car.GetComponentInChildren<CarBodyCollision>(true);
                var hull=CarBodyCollision.EnsureCreated(preview.Car.gameObject,false);
                if(!Application.isPlaying&&existingHull==null&&hull!=null){hull.gameObject.hideFlags=HideFlags.DontSave;hull.gameObject.tag="EditorOnly";}
                Physics.SyncTransforms();point+=CarBodyCollision.ConstrainActorStep(point,Vector3.zero,reach.TorsoRadius);
                var outside=Vector3.ProjectOnPlane(point-preview.Car.transform.position,Vector3.up);
                if(outside.sqrMagnitude>.001f)point+=outside.normalized*.03f;
            }
            actor.workPose=new AuthoringPose{position=Quaternion.Inverse(frame.rotation)*(point-frame.position)-Vector3.right*(actor.side*draft.actorDistance),euler=actor.home.euler};
            actor.hasWorkPose=true;
        }
        finally{preview.Car.transform.SetPositionAndRotation(oldPosition,oldRotation);Physics.SyncTransforms();}
    }
    static AuthoringCurve CopyCurve(AuthoringCurve c)=>new AuthoringCurve{enabled=c.enabled,control1=c.control1,control2=c.control2};
    void DrawVehicleSettings(ProcessAuthoringPreview preview)
    {
        vehicleSettings=EditorGUILayout.Foldout(vehicleSettings,"차종 등록·체결점·그립",true);
        if(!vehicleSettings)return;
        EditorGUI.BeginChangeCheck();Undo.RecordObject(draft,"Edit Vehicle Authoring");
        var model=draft.vehicles[Mathf.Clamp(vehicleIndex,0,draft.vehicles.Count-1)];
        model.displayName=EditorGUILayout.TextField("표시 이름",model.displayName);
        model.prefab=(GameObject)EditorGUILayout.ObjectField("차량 프리팹",model.prefab,typeof(GameObject),false);
        model.assemblyData=(AssemblyPartDataSO)EditorGUILayout.ObjectField("차종 조립 데이터",model.assemblyData,typeof(AssemblyPartDataSO),false);
        int defaultIndex=Mathf.Max(0,draft.vehicles.FindIndex(x=>x.id==draft.defaultVehicleId));
        defaultIndex=EditorGUILayout.Popup("메인 기본 차량",defaultIndex,draft.vehicles.Select(x=>x.displayName).ToArray());draft.defaultVehicleId=draft.vehicles[defaultIndex].id;
        model.overrideFuelPort=EditorGUILayout.Toggle("차종 전용 주유구",model.overrideFuelPort);
        if(model.overrideFuelPort)model.fuelPort=EditorGUILayout.Vector3Field("차량 로컬 주유구",model.fuelPort);
        if(processIndex<draft.processes.Count)
        {
            var types=preview.progression.processes[processIndex].parts;
            if(types.Length>0)
            {
                partIndex=Mathf.Clamp(partIndex,0,types.Length-1);partIndex=EditorGUILayout.Popup("체결점 부품",partIndex,types.Select(x=>x.ToString()).ToArray());
                var data=model.parts.Find(x=>x.type==types[partIndex]);
                if(data==null && GUILayout.Button("이 부품의 차종별 보정 추가"))
                {
                    var template=model.prefab!=null?model.prefab.GetComponentsInChildren<AssemblyPart>(true).FirstOrDefault(x=>x.myType==types[partIndex]):null;
                    if(template!=null)
                    {
                        data=new ProcessPartOverride{type=template.myType,attachment=new AuthoringPose{position=template.assembledPos,euler=template.assembledRot}};
                        WorkerHandIK.TryComputeGrip(template.transform,.3f,.05f,.02f,out data.leftGrip,out data.rightGrip,out data.finger);model.parts.Add(data);dirty=true;hasUnsavedChanges=true;
                    }
                }
                if(data!=null)
                {
                    data.overridePose=EditorGUILayout.Toggle("체결점 보정 사용",data.overridePose);if(data.overridePose)DrawPoseFields("차량 로컬 체결점",ref data.attachment);
                    data.overrideGrip=EditorGUILayout.Toggle("양손 그립 보정 사용",data.overrideGrip);
                    if(data.overrideGrip){data.leftGrip=EditorGUILayout.Vector3Field("부품 로컬 그립 A",data.leftGrip);data.rightGrip=EditorGUILayout.Vector3Field("부품 로컬 그립 B",data.rightGrip);data.finger=EditorGUILayout.Vector3Field("손가락 방향",data.finger);}
                }
                var record=draft.processes[processIndex];var actors=record.actors.Where(a=>a.kind==(robot?ProcessActorKind.Robot:ProcessActorKind.Worker)).ToArray();
                if(actors.Length>0)
                {
                    var actor=actors[Mathf.Clamp(actorIndex,0,actors.Length-1)];
                    var tuning=model.overrides.Find(x=>x.processId==record.id && x.actorId==actor.id && x.part==types[partIndex]);
                    if(tuning==null && GUILayout.Button("이 차종·주체·부품의 동작 보정 추가"))
                    {
                        tuning=new ProcessVehicleOverride{processId=record.id,actorId=actor.id,part=types[partIndex],assemblyWait=actor.assemblyWait,workPose=actor.workPose,stopOffsetMeters=record.stopOffsetMeters,
                            fetchPath=CopyCurve(actor.fetchPath),approachPath=CopyCurve(actor.approachPath),walkPath=CopyCurve(actor.walkPath),carryPath=CopyCurve(actor.carryPath)};
                        model.overrides.Add(tuning);dirty=true;hasUnsavedChanges=true;
                    }
                    if(tuning!=null)
                    {
                        tuning.overrideStop=EditorGUILayout.Toggle("차종별 정차 보정",tuning.overrideStop);if(tuning.overrideStop)tuning.stopOffsetMeters=EditorGUILayout.FloatField("정차 거리",tuning.stopOffsetMeters);
                        tuning.overrideWait=EditorGUILayout.Toggle("차종별 체결 대기",tuning.overrideWait);if(tuning.overrideWait)DrawPoseFields("체결 대기",ref tuning.assemblyWait);
                        tuning.overrideWork=EditorGUILayout.Toggle("차종별 작업 위치",tuning.overrideWork);if(tuning.overrideWork)DrawPoseFields("작업 위치",ref tuning.workPose);
                        tuning.overridePaths=EditorGUILayout.Toggle("차종별 경로",tuning.overridePaths);
                        if(tuning.overridePaths){DrawCurveFields("집기 경로",tuning.fetchPath);DrawCurveFields("체결 대기 경로",tuning.approachPath);DrawCurveFields("작업 접근 경로",tuning.walkPath);DrawCurveFields("운반 경로",tuning.carryPath);}
                    }
                }
            }
        }
        if(EditorGUI.EndChangeCheck())Changed();
        if(GUILayout.Button("차종 추가")){Undo.RecordObject(draft,"Add Vehicle");draft.vehicles.Add(new ProcessVehicleProfile{id=Guid.NewGuid().ToString("N"),displayName="새 차량"});vehicleIndex=draft.vehicles.Count-1;Changed();}
        using(new EditorGUI.DisabledScope(Application.isPlaying))
            if(GUILayout.Button("저장하고 등록 차종을 씬에 준비")){Save();ProcessAuthoringVehicles.Sync(preview,source);Select();}
    }

    static void DrawHandDiagnostics(WorkerHandIK hands,string label)
    {
        if(hands==null||!hands.IsHolding||!hands.TryGetWristTargets(out var left,out var right,out var lr,out var rr))return;
        var animator=hands.GetComponent<Animator>();if(animator==null||!animator.isHuman)return;
        float error=Mathf.Max(Vector3.Distance(animator.GetBoneTransform(HumanBodyBones.LeftHand).position,left),Vector3.Distance(animator.GetBoneTransform(HumanBodyBones.RightHand).position,right));
        EditorGUILayout.LabelField(label+" 손목 목표 오차",error.ToString("0.000")+" m");
        if(hands.CurrentWeight>.9f&&error>.15f)EditorGUILayout.HelpBox("손 목표와 실제 손목의 간격이 큽니다. 그립·작업 위치·운반 경로를 조정하세요. 체결 완료는 자세 품질의 합격 판정이 아닙니다.",MessageType.Warning);
    }
    void DrawPlayback(ProcessAuthoringPreview preview)
    {
        if(GUILayout.Button("Idle · 차량 정차 / 작업자 일반 대기",GUILayout.Height(26))){ApplyLayout();preview.IdleAtStationWithProfile(draft);ResetRigPlacement(false);RebuildBindings();wasIdle=preview.Idle;SceneView.RepaintAll();}
        if(!Application.isPlaying)
        {
            if(GUILayout.Button("관찰 시작 (Play)",GUILayout.Height(27))&&ResolveDirty()){EditorSceneManager.SaveScene(preview.gameObject.scene);EditorApplication.isPlaying=true;}
        }
        else
        {
            using(new EditorGUILayout.HorizontalScope())
            {
                if(GUILayout.Button("1회 실행",GUILayout.Height(25)))preview.PlayOnce();
                bool auto=GUILayout.Toggle(preview.autoRun,"자동 반복","Button",GUILayout.Height(25));if(auto!=preview.autoRun){preview.autoRun=auto;if(auto)preview.PlayOnce();}
                if(GUILayout.Button(preview.Paused?"재개":"일시정지",GUILayout.Height(25)))preview.Pause(!preview.Paused);
            }
            using(new EditorGUILayout.HorizontalScope())
            {
                if(GUILayout.Button("체결 재시도",GUILayout.Height(24)))preview.Retry();
                if(GUILayout.Button("정지",GUILayout.Height(24)))preview.Stop();
                if(GUILayout.Button("재고 초기화",GUILayout.Height(24)))preview.RestoreStock();
            }
        }
        using(new EditorGUILayout.HorizontalScope())
        {
            if(Application.isPlaying)previewSpeed=preview.speed;
            GUILayout.Label("실행 속도",GUILayout.Width(75));
            if(GUILayout.Button("−",GUILayout.Width(35)))previewSpeed=ProcessAuthoringProfile.StepSpeed(previewSpeed,-1);
            GUILayout.Label(previewSpeed.ToString("0.0")+" 배",GUILayout.Width(55));
            if(GUILayout.Button("+",GUILayout.Width(35)))previewSpeed=ProcessAuthoringProfile.StepSpeed(previewSpeed,1);
            if(Application.isPlaying&&Mathf.Abs(preview.speed-previewSpeed)>.001f)preview.SetSpeed(previewSpeed);
        }
        if(Application.isPlaying)
        {
            EditorGUILayout.HelpBox(preview.Status,MessageType.None);
            var worker = RigSource(preview);
            if(worker!=null && worker.WorkTargetHeight.HasValue)
            {
                float height = worker.WorkTargetHeight.Value;
                worker.EvaluateWorkPosture(height, 0f, out float weight, out float knee);
                string pose = weight < .05f ? "선 자세" : knee > .5f ? "무릎 작업" : "허리 숙이기";
                EditorGUILayout.LabelField("작업 높이 · 도착 자세", $"{height:F2} m · {pose}");
            }
        }
        EditorGUILayout.LabelField("Game: 우클릭 + WASD / Q·E 이동 · Shift 가속 · 휠 앞뒤",EditorStyles.miniLabel);
    }
    static void DrawPoseFields(string label,ref AuthoringPose pose)
    {GUILayout.Label(label,EditorStyles.miniBoldLabel);pose.position=EditorGUILayout.Vector3Field("위치",pose.position);pose.euler=EditorGUILayout.Vector3Field("회전",pose.euler);}
    static void DrawCurveFields(string label,AuthoringCurve curve)
    {
        GUILayout.Space(12);
        using(new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            curve.enabled=EditorGUILayout.ToggleLeft(label,curve.enabled,EditorStyles.boldLabel);
            if(curve.enabled){GUILayout.Space(5);EditorGUI.indentLevel++;curve.control1=EditorGUILayout.Vector3Field("시작 제어점",curve.control1);curve.control2=EditorGUILayout.Vector3Field("도착 제어점",curve.control2);EditorGUI.indentLevel--;GUILayout.Space(5);}
        }
    }
    void DrawScene(SceneView view)
    {
        var preview=Preview;if(preview==null||draft==null||!Editable)return;
        WheelProcessMigration.ExpandScene(preview.progression,preview);
        if(sceneEditing==null)RebuildBindings();if(sceneEditing==null||sceneEditing.bindings.Count==0)return;
        ProcessAuthoringMarkers.Begin();ProcessAuthoringMarkers.Draw(sceneEditing);DrawStockPackingScene(preview);
        targetIndex=Mathf.Clamp(targetIndex,0,sceneEditing.bindings.Count-1);var selected=sceneEditing.bindings[targetIndex];
        Undo.RecordObject(draft,"Move Selected Layout Handle");EditorGUI.BeginChangeCheck();Handles.color=Color.cyan;
        if(tabIndex==0&&selected.target!=null)
        {
            if(Selection.activeTransform==selected.target)Handles.Label(selected.target.position+Vector3.up*.2f,selected.label);
            else{var pose=selected.Pose;HandlePose(ref pose,selected.frame,selected.side,selected.distance,selected.label);selected.Pose=pose;}
        }
        if(tabIndex==2&&processIndex<draft.processes.Count)
        {
            var record=draft.processes[processIndex];var actors=record.actors.Where(x=>(x.kind==ProcessActorKind.Robot)==robot).ToArray();
            if(actors.Length>0)
            {
                var actor=actors[Mathf.Clamp(actorIndex,0,actors.Length-1)];var frame=ProcessAuthoringLayout.Frame(preview.progression,preview.progression.processes[processIndex]);
                if(robot)
                {
                    var root=ProcessAuthoringLayout.WithDistance(actor.root,actor.side,draft.actorDistance);
                    using(new Handles.DrawingScope(Matrix4x4.TRS(root.WorldPosition(frame),root.WorldRotation(frame),Vector3.one))){boxHandle.center=actor.detectionCenter;boxHandle.size=actor.detectionSize;boxHandle.SetColor(Handles.color);boxHandle.DrawHandle();actor.detectionCenter=boxHandle.center;actor.detectionSize=boxHandle.size;}
                }
                else
                {
                    var pickup=record.stocks.Count>0?record.stocks[Mathf.Clamp(stockIndex,0,record.stocks.Count-1)].pickup:actor.home;
                    DrawCurve(actor.fetchPath,actor.home,pickup,frame,actor.side,draft.actorDistance,"일반 대기 → 집기",Color.cyan);DrawCurve(actor.approachPath,pickup,actor.assemblyWait,frame,actor.side,draft.actorDistance,"집기 → 체결 대기",new Color(1,.7f,.2f));DrawCurve(actor.walkPath,actor.assemblyWait,actor.hasWorkPose?actor.workPose:actor.assemblyWait,frame,actor.side,draft.actorDistance,"체결 대기 → 작업 위치",new Color(1,.4f,.8f));
                }
                if(actor.carryPath.enabled&&preview.Car!=null)
                {
                    var part=preview.Car.GetComponentsInChildren<AssemblyPart>(true).FirstOrDefault(x=>robot?x.myType==actor.part:preview.progression.processes[processIndex].parts.Contains(x.myType));
                    if(part!=null){var from=ProcessAuthoringLayout.WithDistance(robot?actor.robotPickup:actor.assemblyWait,actor.side,draft.actorDistance).WorldPosition(frame);if(!robot)from+=Vector3.up;var end=part.AttachmentWorldPosition;var a=from+frame.rotation*actor.carryPath.control1;var b=end+frame.rotation*actor.carryPath.control2;ProcessAuthoringMarkers.Point(from,"부품 운반 · 시작",Color.magenta);ProcessAuthoringMarkers.Point(end,"부품 운반 · 도착",Color.magenta);ProcessAuthoringMarkers.Point(a,"부품 운반 · 시작 제어점",Color.magenta);ProcessAuthoringMarkers.Point(b,"부품 운반 · 도착 제어점",Color.magenta);Handles.DrawBezier(from,end,a,b,Color.magenta,null,3);var movedA=Handles.PositionHandle(a,frame.rotation);var movedB=Handles.PositionHandle(b,frame.rotation);if(movedA!=a)actor.carryPath.control1=Quaternion.Inverse(frame.rotation)*(movedA-from);if(movedB!=b)actor.carryPath.control2=Quaternion.Inverse(frame.rotation)*(movedB-end);}
                }
            }
        }
        if(EditorGUI.EndChangeCheck())Changed();
        DrawGripScene(preview);DrawRigScene();
    }
    static void HandlePose(ref AuthoringPose pose,Pose frame,int side,float distance,string label)
    {
        var world=ProcessAuthoringLayout.WithDistance(pose,side,distance);var position=world.WorldPosition(frame);var rotation=world.WorldRotation(frame);
        Handles.Label(position+Vector3.up*.15f,label);
        var p=Tools.current==Tool.Rotate?position:Handles.PositionHandle(position,rotation);var r=Tools.current==Tool.Rotate?Handles.RotationHandle(rotation,p):rotation;
        if(p==position&&r.x==rotation.x&&r.y==rotation.y&&r.z==rotation.z&&r.w==rotation.w)return;
        pose.position=Quaternion.Inverse(frame.rotation)*(p-frame.position)-Vector3.right*(side*distance);
        pose.euler=(Quaternion.Inverse(frame.rotation)*r).eulerAngles;
    }
    static void DrawCurve(AuthoringCurve curve,AuthoringPose from,AuthoringPose to,Pose frame,int side,float distance,string label,Color color)
    {
        if(!curve.enabled)return;
        var a=ProcessAuthoringLayout.WithDistance(from,side,distance).WorldPosition(frame);
        var b=ProcessAuthoringLayout.WithDistance(to,side,distance).WorldPosition(frame);
        var c=a+frame.rotation*curve.control1;var d=b+frame.rotation*curve.control2;
        Handles.color=color;
        ProcessAuthoringMarkers.Point(a,label+" · 시작",color);ProcessAuthoringMarkers.Point(b,label+" · 도착",color);
        ProcessAuthoringMarkers.Point(c,label+" · 시작 제어점",color);ProcessAuthoringMarkers.Point(d,label+" · 도착 제어점",color);
        Handles.DrawBezier(a,b,c,d,color,null,3);
        Handles.DrawDottedLine(a,c,4);Handles.DrawDottedLine(d,b,4);
        var movedC=Handles.PositionHandle(c,frame.rotation);var movedD=Handles.PositionHandle(d,frame.rotation);
        if(movedC!=c)curve.control1=Quaternion.Inverse(frame.rotation)*(movedC-a);
        if(movedD!=d)curve.control2=Quaternion.Inverse(frame.rotation)*(movedD-b);
    }
}
#endif
