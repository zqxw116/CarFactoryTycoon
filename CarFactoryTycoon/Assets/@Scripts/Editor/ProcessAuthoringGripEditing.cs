#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
using UnityEngine;

public sealed partial class ProcessAuthoringWindow
{
    int gripPoint;
    PartStack GripStock(ProcessAuthoringPreview preview,out ProcessStockLayout data)
    {
        data=null;if(processIndex>=draft.processes.Count)return null;
        var record=draft.processes[processIndex];if(record.stocks.Count==0)return null;
        stockIndex=Mathf.Clamp(stockIndex,0,record.stocks.Count-1);data=record.stocks[stockIndex];
        var entry=preview.progression.processes[processIndex];var stocks=entry.humanStation!=null?entry.humanStation.partStacks:entry.wheel?.humanPartStacks;
        string id=data.id;return stocks?.FirstOrDefault(x=>x!=null&&ProcessAuthoringLayout.Key(x)==id);
    }
    void InitializeGrip(PartStack stock,ProcessStockLayout data)
    {
        if(data.grip==null)data.grip=new ProcessGripSettings();
        var display=stock.ReadyDisplay;if(display==null)return;
        WorkerHandIK.TryComputeGrip(display.transform,.3f,.05f,.02f,out data.grip.left,out data.grip.right,out data.grip.finger);
        var workers=Preview.progression.processes[processIndex].workers;var worker=workers?.FirstOrDefault(x=>x!=null);
        if(worker!=null&&(Quaternion.Euler(worker.carryEuler)*data.grip.left).x>(Quaternion.Euler(worker.carryEuler)*data.grip.right).x)
        {var swap=data.grip.left;data.grip.left=data.grip.right;data.grip.right=swap;}
        data.grip.workerCarryEuler=ProcessPartGrip.DefaultCarryEuler(data.grip.left,data.grip.right);
        data.grip.robot=(data.grip.left+data.grip.right)*.5f;data.grip.configured=true;
        data.grip.anatomicalWrist=true;data.grip.reachFraction=.98f;
        data.grip.leftElbow=new Vector3(-.12f,-.25f,.08f);data.grip.rightElbow=new Vector3(.12f,-.25f,.08f);
    }
    void DrawGripPanel(ProcessAuthoringPreview preview)
    {
        if(preview.FuelMode){EditorGUILayout.HelpBox("주유는 기존 노즐 손 IK를 사용합니다. 부품 잡기는 부품 공정에서 편집합니다.",MessageType.Info);return;}
        var record=draft.processes[processIndex];if(record.stocks.Count==0)return;
        stockIndex=EditorGUILayout.Popup("잡을 부품",Mathf.Clamp(stockIndex,0,record.stocks.Count-1),record.stocks.Select(x=>x.part.ToString()).ToArray());
        var stock=GripStock(preview,out var data);if(stock==null)return;
        if(data.grip==null)data.grip=new ProcessGripSettings();
        EditorGUILayout.HelpBox("대기 슬롯의 부품 로컬 좌표입니다. 더미에는 표시하지 않습니다. 작업자는 양손, 로봇팔은 한 접촉점을 잡고 운반합니다.",MessageType.Info);
        if(!data.grip.configured)
        {
            if(!robot)
            {
                showRigTwin=EditorGUILayout.ToggleLeft("체결 중 리깅 작업자 함께 보기",showRigTwin);
                if(rigPreview?.Replaying==true && GUILayout.Button("리깅 작업자 가까이 보기"))FocusRigPreview();
            }
            if(GUILayout.Button("현재 부품에서 잡기 위치 만들기",GUILayout.Height(28))){Undo.RecordObject(draft,"Initialize Part Contacts");InitializeGrip(stock,data);Changed();}
            return;
        }
        if(!robot)
        {
            DrawRigFields();
            if(GUILayout.Button("양팔 앞으로 · 기본 집기 자세 (접촉점 유지)"))ResetRigPlacement(true);
            if(showRigTwin&&GUILayout.Button("리깅 작업자 가까이 보기"))FocusRigPreview();
            rigControl=GUILayout.Toolbar(rigControl,new[]{"왼손","오른손","왼 팔꿈치","오른 팔꿈치","작업자 이동","부품 배치"});
            if(rigControl==5&&rigPreview?.part!=null&&GUILayout.Button("Scene에서 대기 부품 이동 / 회전"))
            {Selection.activeGameObject=rigPreview.part.gameObject;Tools.current=Tool.Rotate;}
            if(GUILayout.Button("반대 면에서 잡기 · 접촉 표면/양손/방향 반전"))ReverseGripSide();
            gripPoint=rigControl==1?1:0;
        }
        EditorGUI.BeginChangeCheck();Undo.RecordObject(draft,"Edit Part Contacts");
        if(robot)data.grip.robot=EditorGUILayout.Vector3Field("팔끝 접촉 위치",data.grip.robot);
        else
        {
            data.grip.left=EditorGUILayout.Vector3Field("왼손 접촉 위치",data.grip.left);
            data.grip.right=EditorGUILayout.Vector3Field("오른손 접촉 위치",data.grip.right);
            data.grip.finger=EditorGUILayout.Vector3Field(new GUIContent("손끝이 향하는 방향","부품 로컬 방향. 손목 회전과 손바닥-손목 거리 보정에 사용합니다. Scene 녹색 화살표를 확인하세요."),data.grip.finger);
            EditorGUILayout.HelpBox("녹색 화살표는 손끝이 향할 방향입니다. 위치 값이 아니라 방향이며, 손목 회전과 손바닥 접촉 보정에 사용합니다.",MessageType.None);
            GUILayout.Space(12);EditorGUILayout.LabelField("손목 회전 · 팔꿈치 굽힘",EditorStyles.boldLabel);
            data.grip.anatomicalWrist=EditorGUILayout.Toggle("모델 손목 자동 정렬",data.grip.anatomicalWrist);
            data.grip.leftWristEuler=EditorGUILayout.Vector3Field("왼손 손목 회전",data.grip.leftWristEuler);
            data.grip.rightWristEuler=EditorGUILayout.Vector3Field("오른손 손목 회전",data.grip.rightWristEuler);
            data.grip.leftElbow=EditorGUILayout.Vector3Field(new GUIContent("왼팔 굽힘 보조점","어깨 기준 오프셋(m). 팔꿈치가 굽는 면을 정합니다."),data.grip.leftElbow);
            data.grip.rightElbow=EditorGUILayout.Vector3Field(new GUIContent("오른팔 굽힘 보조점","어깨 기준 오프셋(m). 팔꿈치가 굽는 면을 정합니다."),data.grip.rightElbow);
            data.grip.reachFraction=EditorGUILayout.Slider(new GUIContent("집을 때 팔 뻗기 (%)","팔 길이에 맞춘 접근 거리입니다. 관절이 잠기지 않도록 여유를 둡니다."),data.grip.reachFraction*100f,50f,98f)/100f;
            GUILayout.Space(12);
            data.grip.workerCarryEuler=EditorGUILayout.Vector3Field(new GUIContent("들고 걸을 때 부품 방향","작업자가 집은 뒤 운반하는 동안의 회전입니다. 슬롯 배치·재고 적층 회전과 별개이며 작업자 몸을 기준으로 합니다."),data.grip.workerCarryEuler);
        }
        if(EditorGUI.EndChangeCheck())Changed();
        if(GUILayout.Button("자동 잡기 위치로 되돌리기")){Undo.RecordObject(draft,"Reset Part Contacts");data.grip.configured=false;Changed();}
        EditorGUILayout.HelpBox("잡기 위치는 공정 부품의 공통 설정입니다. 이 설정을 사용하면 차종 탭의 양손 그립보다 우선합니다. 차종마다 형상이 다르면 공정 잡기를 끄고 차종 그립을 사용하세요.",MessageType.None);
    }
    void DrawGripScene(ProcessAuthoringPreview preview)
    {
        if(tabIndex!=1||preview.FuelMode)return;
        var stock=GripStock(preview,out var data);
        if(stock==null||data?.grip==null||!data.grip.configured||!stock.HasReadyPart||stock.ReadyDisplay==null||!stock.ReadyDisplay.activeInHierarchy)return;
        var target=stock.ReadyDisplay.transform;var g=data.grip;
        if(!robot){DrawContact(target,g.left,"왼손",Color.cyan);DrawContact(target,g.right,"오른손",Color.yellow);}
        else DrawContact(target,g.robot,"팔끝 접촉",Color.green);
        if(!robot&&showRigTwin)return;
        var local=robot?g.robot:gripPoint==0?g.left:g.right;
        EditorGUI.BeginChangeCheck();var moved=Handles.PositionHandle(target.TransformPoint(local),target.rotation);
        if(EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(draft,"Move Part Contact");var point=target.InverseTransformPoint(moved);
            if(robot)g.robot=point;else if(gripPoint==0)g.left=point;else g.right=point;Changed();
        }
    }
    static void DrawContact(Transform target,Vector3 point,string label,Color color)
    {
        var world=target.TransformPoint(point);Handles.color=color;
        Handles.SphereHandleCap(0,world,Quaternion.identity,HandleUtility.GetHandleSize(world)*.07f,EventType.Repaint);
        Handles.Label(world+Vector3.up*.08f,label);
    }
}
#endif
