#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>Two-way boundary between saved line-local poses and ordinary Scene transforms.</summary>
public sealed class ProcessAuthoringSceneEditing
{
    public sealed class Binding
    {
        public string label,role;
        public Transform target,selectionTarget;
        public ProcessActorLayout actor;
        public ProcessStockLayout stock;
        public Pose frame;
        public int side;
        public float distance;
        public Func<AuthoringPose> read;
        public Action<AuthoringPose> write;
        Vector3 position;Quaternion rotation;AuthoringPose authored;
        public bool DataChanged=>(Pose.position-authored.position).sqrMagnitude>.00000001f||RotationChanged(Quaternion.Euler(Pose.euler),Quaternion.Euler(authored.euler));
        public AuthoringPose Pose {get=>read();set=>write(value);}
        public void Snapshot(){if(target!=null){position=target.position;rotation=target.rotation;authored=Pose;}}
        public bool Moved=>target!=null&&((target.position-position).sqrMagnitude>.00000001f||RotationChanged(target.rotation,rotation));
        static bool RotationChanged(Quaternion a,Quaternion b)
        {float sign=Quaternion.Dot(a,b)<0?-1:1;return Mathf.Abs(a.x-sign*b.x)+Mathf.Abs(a.y-sign*b.y)+Mathf.Abs(a.z-sign*b.z)+Mathf.Abs(a.w-sign*b.w)>.00001f;}
        public void Apply(){var pose=ProcessAuthoringLayout.WithDistance(Pose,side,distance);pose.Apply(target,frame);Snapshot();}
        public void Capture(){var p=AuthoringPose.Capture(target,frame);p.position.x-=side*distance;Pose=p;var previous=authored;Snapshot();authored=previous;}
    }
    public readonly List<Binding> bindings=new List<Binding>();
    public ProcessAuthoringPreview Owner=>preview;
    readonly ProcessAuthoringPreview preview;readonly ProcessAuthoringProfile profile;
    Transform group;Pose frame;
    public ProcessAuthoringSceneEditing(ProcessAuthoringPreview preview,ProcessAuthoringProfile profile,int index,bool robot)
    {
        this.preview=preview;this.profile=profile;
        var root=preview.transform.Find("편집 기준점");if(root==null){root=new GameObject("편집 기준점").transform;root.SetParent(preview.transform,false);root.tag="EditorOnly";}
        foreach(Transform child in root)child.gameObject.SetActive(false);
        string name="공정_"+index.ToString("D2");group=root.Find(name);if(group==null){group=new GameObject(name).transform;group.SetParent(root,false);group.tag="EditorOnly";}group.gameObject.SetActive(true);foreach(Transform child in group)child.gameObject.SetActive(false);
        if(index==preview.progression.processes.Length)
        {
            var f=profile.fuel;var station=preview.fuelStation;frame=ProcessAuthoringLayout.FrameAt(preview.progression.mainSpline,preview.progression.authoringFuelAnchorProgress);
            Add("주유 설비","Fuel",station.transform,()=>f.station,x=>f.station=x);
            Add("주유원 일반 대기","Home",station.homePoint,()=>f.home,x=>f.home=x);
            Add("노즐 거치","Nozzle",station.nozzleDock,()=>f.nozzleDock,x=>f.nozzleDock=x);
            Add("호스 시작","Hose",station.hoseAnchor,()=>f.hoseAnchor,x=>f.hoseAnchor=x);
            if(f.useWorkStand)Add("주유 작업 위치","Work",Marker("주유 작업 위치"),()=>f.workStand,x=>f.workStand=x,true);
            return;
        }
        var entry=preview.progression.processes[index];var record=profile.Find(entry.authoringId);frame=ProcessAuthoringLayout.Frame(preview.progression,entry);
        var stocks=entry.humanStation!=null?entry.humanStation.partStacks:entry.wheel?.humanPartStacks;
        foreach(var data in record.stocks)
        {
            var stock=stocks?.FirstOrDefault(x=>x!=null&&ProcessAuthoringLayout.Key(x)==data.id);if(stock==null)continue;
            string suffix=record.stocks.Count>1?" · "+data.part:"";
            var binding=Add("재고 더미"+suffix,"Stock",stock.transform,()=>data.inventory,x=>data.inventory=x,false,data.side,profile.actorDistance);binding.stock=data;
            binding=Add("집기 슬롯"+suffix,"Pickup",Marker("집기 슬롯_"+data.id.Substring(0,6)),()=>data.pickup,x=>{if(Quaternion.Angle(Quaternion.Euler(data.pickup.euler),Quaternion.Euler(x.euler))>.01f)data.followInventoryRotation=false;data.pickup=x;},true,data.side,profile.actorDistance);binding.stock=data;
        }
        bool staged=Application.isPlaying||preview.Idle;
        foreach(var actor in record.actors.Where(x=>(x.kind==ProcessActorKind.Robot)==robot))
        {
            Worker worker=entry.workers?.FirstOrDefault(x=>x!=null&&ProcessAuthoringLayout.Key(x)==actor.id);
            StationController arm=entry.robots?.FirstOrDefault(x=>x!=null&&ProcessAuthoringLayout.Key(x)==actor.id);
            string suffix=record.actors.Count(x=>x.kind==actor.kind)>1?" · "+(actor.kind==ProcessActorKind.Robot?actor.part.ToString():worker!=null?worker.name:actor.id.Substring(0,6)):"";
            Action<string,string,Transform,Func<AuthoringPose>,Action<AuthoringPose>,bool> bind=(label,role,target,read,write,marker)=>{var item=Add(label+suffix,role,target,read,write,marker,actor.side,profile.actorDistance);item.actor=actor;if(role=="Actor"&&staged)item.selectionTarget=robot?arm?.transform:worker?.transform;};
            bind(robot?"로봇팔 초기 배치":"작업자 초기 배치","Actor",staged?Marker("초기 배치_"+actor.id.Substring(0,6)):robot?arm?.transform:worker?.transform,()=>actor.root,x=>actor.root=x,staged);
            if(!robot)
            {
                bind("일반 대기","Home",Marker("일반 대기_"+actor.id.Substring(0,6)),()=>actor.home,x=>actor.home=x,true);
                bind("체결 전 대기","Wait",Marker("체결 전 대기_"+actor.id.Substring(0,6)),()=>actor.assemblyWait,x=>actor.assemblyWait=x,true);
                if(actor.hasWorkPose)bind("체결 작업 위치","Work",Marker("체결 작업 위치_"+actor.id.Substring(0,6)),()=>actor.workPose,x=>actor.workPose=x,true);
            }
            else if(arm!=null)
            {
                bind("팔 대기 목표","Rest",arm.endPos,()=>actor.robotRest,x=>actor.robotRest=x,false);
                bind("기존 부품 대기 목표","RobotPickup",arm.stationPilePos,()=>actor.robotPickup,x=>actor.robotPickup=x,false);
            }
        }
        if(record.hasHiringPost)Add("고용 표지","Hiring",entry.hiringPost,()=>record.hiringPost,x=>record.hiringPost=x,false,record.hiringPostSide,profile.hiringPostDistance);
        if(record.hasHardware&&entry.Platform!=null)Add("공정 설비","Hardware",entry.Platform,()=>record.hardware,x=>record.hardware=x);
        Add("차량 정차점","Stop",Marker("차량 정차점"),()=>new AuthoringPose{position=Vector3.forward*record.stopOffsetMeters},x=>record.stopOffsetMeters=Mathf.Clamp(x.position.z,.1f,preview.progression.prototypeSettings.workZoneMeters-.1f),true);
    }
    Transform Marker(string name)
    {var t=group.Find(name);if(t==null){t=new GameObject(name).transform;t.SetParent(group,false);t.tag="EditorOnly";}t.gameObject.SetActive(true);return t;}
    Binding Add(string label,string role,Transform target,Func<AuthoringPose> read,Action<AuthoringPose> write,bool apply=false,int side=0,float distance=0)
    {
        var b=new Binding{label=label,role=role,target=target,read=read,write=write,frame=frame,side=side,distance=distance};
        if(apply)b.Apply();else b.Snapshot();bindings.Add(b);return b;
    }
    public bool Pull(UnityEngine.Object undoTarget)
    {
        var changed=bindings.Where(x=>x.Moved).ToArray();if(changed.Length==0)return false;
        Undo.RecordObject(undoTarget,"Move Process Layout");
        foreach(var item in changed)
        {
            if(changed.Any(parent=>parent!=item&&parent.target!=null&&item.target.IsChildOf(parent.target)))continue;
            item.Capture();
        }
        return true;
    }
    public int Find(Transform selected)
    {
        if(selected==null)return -1;
        for(int i=0;i<bindings.Count;i++)if(bindings[i].target==selected)return i;
        for(int i=0;i<bindings.Count;i++)if(bindings[i].selectionTarget!=null&&(selected==bindings[i].selectionTarget||selected.IsChildOf(bindings[i].selectionTarget)))return i;
        for(int i=0;i<bindings.Count;i++)if(bindings[i].role=="Stock"&&bindings[i].target!=null)
        {
            var stack=bindings[i].target.GetComponent<PartStack>();
            if(stack!=null&&stack.ReadyDisplay!=null&&(selected==stack.ReadyDisplay.transform||selected.IsChildOf(stack.ReadyDisplay.transform)))
                for(int n=0;n<bindings.Count;n++)if(bindings[n].role=="Pickup"&&bindings[n].stock==bindings[i].stock)return n;
        }
        for(int i=0;i<bindings.Count;i++)if(bindings[i].target!=null&&selected.IsChildOf(bindings[i].target))return i;
        return -1;
    }
}
#endif
