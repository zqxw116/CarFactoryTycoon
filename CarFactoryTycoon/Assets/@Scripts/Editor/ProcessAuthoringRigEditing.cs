#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
using UnityEngine;

public sealed partial class ProcessAuthoringWindow
{
    bool showRigTwin=true;
    bool forceRigReach;
    string forceRigActor;
    int rigControl;
    ProcessAuthoringRigPreview rigPreview;
    readonly ProcessGripSettings automaticPreviewGrip = new ProcessGripSettings { configured = true };
    void DisposeRigPreview(){rigPreview?.Dispose();rigPreview=null;}
    Worker RigSource(ProcessAuthoringPreview p)
    {
        if(p==null||robot||p.FuelMode||draft==null||processIndex>=p.progression.processes.Length)return null;
        var workers=p.progression.processes[processIndex].workers;
        return workers!=null?workers.Where(x=>x!=null).ElementAtOrDefault(actorIndex):null;
    }
    void UpdateRigPreview()
    {
        var p=Preview;
        if(Application.isPlaying&&p!=null&&(!Application.IsPlaying(p.gameObject)||!p.gameObject.scene.isLoaded||!UnityEngine.SceneManagement.SceneManager.GetActiveScene().IsValid()))return;
        var worker=RigSource(p);
        if(!showRigTwin||worker==null||tabIndex!=1){DisposeRigPreview();return;}
        var stock=GripStock(p,out var data);
        bool replay=Application.isPlaying&&p.ActiveAttempt&&!p.Idle;
        bool authored=data?.grip!=null&&data.grip.configured;
        if(stock==null||stock.ReadyDisplay==null||(!authored&&!replay)){DisposeRigPreview();return;}
        var previewGrip=authored?data.grip:automaticPreviewGrip;
        if(!authored && (rigPreview==null || rigPreview.stock!=stock))
            WorkerHandIK.TryComputeGrip(stock.ReadyDisplay.transform,worker.maxGripHalfSpan,worker.gripEndInset,worker.gripFaceInset,
                out previewGrip.left,out previewGrip.right,out previewGrip.finger);
        if(rigPreview==null)rigPreview=new ProcessAuthoringRigPreview();
        var frame=ProcessAuthoringLayout.Frame(p.progression,p.progression.processes[processIndex]);
        var actor=draft.processes[processIndex].FindActor(ProcessAuthoringLayout.Key(worker));
        var offset=frame.rotation*(Vector3.right*(actor!=null?actor.side:1)*3.5f-Vector3.forward*1.5f);
        var home=actor!=null?ProcessAuthoringLayout.WithDistance(actor.home,actor.side,draft.actorDistance).WorldPosition(frame):worker.transform.position;
        if(rigPreview.TryCapturePlacement(frame,out var moved)&&actor!=null)
        {
            Undo.RecordObject(draft,"Move Rig Preview Worker");actor.rigPreviewPose=moved;actor.hasRigPreviewPose=true;
            forceRigReach=false;dirty=true;hasUnsavedChanges=true;
        }
        if(rigPreview.TryCapturePart(out var partPose))
        {
            Undo.RecordObject(draft,"Move Ready Part From Rig Preview");
            data.followInventoryRotation=false;
            data.pickup=new AuthoringPose{position=Quaternion.Inverse(frame.rotation)*(partPose.position-frame.position),euler=(Quaternion.Inverse(frame.rotation)*partPose.rotation).eulerAngles};
            data.pickup.position.x-=data.side*draft.actorDistance;
            dirty=true;hasUnsavedChanges=true;
            ProcessAuthoringLayout.Apply(p.progression,draft,false);
        }
        var attemptPart=replay?PrototypeProcessCrew.Ensure(p.progression,processIndex).PreviewPartFor(worker):null;
        rigPreview.Tick(worker,stock,previewGrip,offset,home,actor,frame,replay,forceRigReach&&actor!=null&&forceRigActor==actor.id,attemptPart);
        if(actor!=null&&!actor.hasRigPreviewPose&&!replay&&rigPreview.root!=null)
        {
            actor.rigPreviewPose=AuthoringPose.Capture(rigPreview.root.transform,frame);actor.hasRigPreviewPose=true;
            dirty=true;hasUnsavedChanges=true;
        }
    }
    void DrawRigFields()
    {
        GUILayout.Space(10);showRigTwin=EditorGUILayout.ToggleLeft("리깅 작업자 함께 보기",showRigTwin,EditorStyles.boldLabel);
        if(!showRigTwin)DisposeRigPreview();
        else EditorGUILayout.HelpBox("옆 작업자는 자세 편집/동작 비교용입니다. Scene에서 작업자를 옮기면 멀리서는 Idle, 도달 범위 안에서는 집기 자세가 됩니다. 재시도 중에는 실제 작업자의 동작을 함께 재생합니다.",MessageType.Info);
        if(showRigTwin&&GUILayout.Button("Scene에서 리깅 작업자 이동 / 회전"))
        {UpdateRigPreview();if(rigPreview?.root!=null){rigControl=4;Selection.activeGameObject=rigPreview.root;Tools.current=Tool.Move;}}
        if(showRigTwin&&rigPreview!=null)EditorGUILayout.LabelField("리깅 미리보기",rigPreview.Replaying?"실제 체결 동작 재생":rigPreview.IsReaching?"팔 뻗어 집기":"Idle · 도달 범위 밖");
    }
    void ReverseGripSide()
    {
        var stock=GripStock(Preview,out var data);if(stock==null||data?.grip==null)return;
        Undo.RecordObject(draft,"Reverse Pickup Face");var g=data.grip;
        var swap=g.left;g.left=g.right;g.right=swap;g.finger=-g.finger;
        if(g.finger.sqrMagnitude>.000001f)
        {
            var n=g.finger.normalized;var b=stock.ItemLocalBounds;
            float extent=Mathf.Abs(n.x)*b.extents.x+Mathf.Abs(n.y)*b.extents.y+Mathf.Abs(n.z)*b.extents.z;
            float surface=Vector3.Dot(b.center,n)-Mathf.Max(0,extent-.02f);
            g.left+=n*(surface-Vector3.Dot(g.left,n));g.right+=n*(surface-Vector3.Dot(g.right,n));
        }
        g.workerCarryEuler=ProcessPartGrip.DefaultCarryEuler(g.left,g.right);
        ResetRigPlacement(false);
    }
    void ResetRigPlacement(bool resetHands)
    {
        var p=Preview;var worker=RigSource(p);if(worker==null)return;
        var actor=draft.processes[processIndex].FindActor(ProcessAuthoringLayout.Key(worker));
        Undo.RecordObject(draft,"Reset Forward Reach Pose");if(actor!=null)actor.hasRigPreviewPose=false;
        if(resetHands)
        {
            GripStock(p,out var data);var g=data?.grip;if(g==null)return;
            g.anatomicalWrist=true;g.leftWristEuler=g.rightWristEuler=Vector3.zero;
            g.leftElbow=new Vector3(-.12f,-.25f,.08f);g.rightElbow=new Vector3(.12f,-.25f,.08f);g.reachFraction=.98f;
        }
        forceRigReach=true;forceRigActor=actor?.id;DisposeRigPreview();Changed();UpdateRigPreview();
    }
    void FocusRigPreview()
    {
        UpdateRigPreview();if(rigPreview?.root==null)return;
        SceneView.lastActiveSceneView?.LookAt(rigPreview.root.transform.position+Vector3.up,Quaternion.Euler(15,135,0),2.2f);
    }
    void DrawRigScene()
    {
        if(rigPreview?.root==null||!rigPreview.root.activeInHierarchy)return;
        var stock=GripStock(Preview,out var data);if(data?.grip==null||!data.grip.configured)return;var g=data.grip;
        var root=rigPreview.root.transform;var target=rigPreview.part;
        ProcessAuthoringMarkers.Label(root.position+Vector3.up*2,rigPreview.Replaying?"리깅 작업자 · 체결 동작 재생":rigPreview.IsReaching?"리깅 작업자 · 집기 자세":"리깅 작업자 · Idle",Color.white);
        var source=RigSource(Preview);if(source!=null)ProcessAuthoringMarkers.Label(source.transform.position+Vector3.up*2,"실제 작업자 · 동작 확인",Color.white);
        var left=target.TransformPoint(g.left);var right=target.TransformPoint(g.right);
        ProcessAuthoringMarkers.Point(left,"리깅 · 왼손 접촉",Color.cyan);ProcessAuthoringMarkers.Point(right,"리깅 · 오른손 접촉",Color.yellow);
        rigPreview.hands.GetElbowTargets(out var lh,out var rh);
        ProcessAuthoringMarkers.Point(lh,"왼팔 굽힘 보조점",new Color(.4f,1,.5f));ProcessAuthoringMarkers.Point(rh,"오른팔 굽힘 보조점",new Color(1,.6f,.3f));
        Handles.color=Color.green;var origin=(left+right)*.5f;var finger=Vector3.ProjectOnPlane(target.TransformVector(g.finger),right-left);if(finger.sqrMagnitude<.00001f)finger=rigPreview.hands.transform.forward;finger.Normalize();
        Handles.ArrowHandleCap(0,origin,Quaternion.LookRotation(finger.sqrMagnitude>.001f?finger:Vector3.forward),.3f,EventType.Repaint);
        ProcessAuthoringMarkers.Label(origin+finger*.35f,"손끝이 향하는 방향",Color.green);
        var envelope=rigPreview.reach;
        DrawReach(envelope);
        if(source!=null)DrawReach(WorkerReachEnvelope.Ensure(source));
        if(rigControl>=4||rigPreview.Replaying)return;
        EditorGUI.BeginChangeCheck();
        if(rigControl<2)
        {
            bool isLeft=rigControl==0;var local=isLeft?g.left:g.right;var position=target.TransformPoint(local);
            if(Tools.current==Tool.Rotate)
            {
                var euler=isLeft?g.leftWristEuler:g.rightWristEuler;
                rigPreview.hands.TryGetWristTargets(out var wl,out var wr,out var lr,out var rr);var rotation=isLeft?lr:rr;var basis=rotation*Quaternion.Inverse(Quaternion.Euler(euler));var moved=Handles.RotationHandle(rotation,position);
                if(EditorGUI.EndChangeCheck()){Undo.RecordObject(draft,"Rotate Worker Wrist");var value=(Quaternion.Inverse(basis)*moved).eulerAngles;if(isLeft)g.leftWristEuler=value;else g.rightWristEuler=value;Changed();}return;
            }
            var p=Handles.PositionHandle(position,target.rotation);
            if(EditorGUI.EndChangeCheck()){Undo.RecordObject(draft,"Move Worker Contact");if(isLeft)g.left=target.InverseTransformPoint(p);else g.right=target.InverseTransformPoint(p);Changed();}
        }
        else
        {
            bool isLeft=rigControl==2;var shoulder=rigPreview.avatar.GetBoneTransform(isLeft?HumanBodyBones.LeftUpperArm:HumanBodyBones.RightUpperArm).position;
            var position=isLeft?lh:rh;var moved=Handles.PositionHandle(position,rigPreview.hands.transform.rotation);
            if(EditorGUI.EndChangeCheck()){Undo.RecordObject(draft,"Move Elbow Hint");var local=Quaternion.Inverse(rigPreview.hands.transform.rotation)*(moved-shoulder);if(isLeft)g.leftElbow=local;else g.rightElbow=local;Changed();}
        }
    }
    static void DrawReach(WorkerReachEnvelope reach)
    {
        if(reach==null||!reach.Ready)return;
        Handles.color=new Color(.2f,1,.5f,.5f);float scale=Mathf.Abs(reach.transform.lossyScale.x);
        var a=reach.transform.TransformPoint(reach.leftShoulder);var b=reach.transform.TransformPoint(reach.rightShoulder);
        Handles.DrawWireDisc(a,Vector3.up,reach.leftLength*scale*.98f);Handles.DrawWireDisc(a,reach.transform.right,reach.leftLength*scale*.98f);
        Handles.DrawWireDisc(b,Vector3.up,reach.rightLength*scale*.98f);Handles.DrawWireDisc(b,reach.transform.right,reach.rightLength*scale*.98f);
    }
}
#endif
