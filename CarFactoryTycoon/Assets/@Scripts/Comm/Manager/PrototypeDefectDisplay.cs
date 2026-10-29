using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Missing slots use corners; attached unfinished parts keep their material with a thin outline.</summary>
public sealed class PrototypeDefectDisplay : MonoBehaviour
{
    public enum PartState { None, MissingSlot, AttachedPending }
    public ProductionProgression progression;
    public FactoryPlayerController player;
    public float partVisualRange=14f, workIconRange=10f, vehicleBadgeRange=60f;
    public int MissingPartCount { get; private set; }
    public int AffectedCarCount { get; private set; }
    public int SlotPartCount { get; private set; }
    public int OutlinePartCount { get; private set; }
    public int PreviewPartCount { get; private set; }
    public int VisibleWorkIconCount { get; private set; }
    public int VehicleBadgeCount { get; private set; }
    LineTrafficManager traffic;
    Material cornerMaterial,activeCornerMaterial,farCornerMaterial,outlineMaterial,previewMaterial;
    Mesh farCorners;
    MeshRenderer farRenderer;
    readonly List<Vector3> farVertices=new List<Vector3>(4096);
    readonly List<int> farIndices=new List<int>(4096);
    public int DistantPartMarkerCount {get;private set;}
    readonly Dictionary<CarController,AssemblyPart[]> partsByCar=new Dictionary<CarController,AssemblyPart[]>();
    readonly Dictionary<AssemblyPart,PartVisual> visuals=new Dictionary<AssemblyPart,PartVisual>();
    readonly HashSet<CarController> liveCars=new HashSet<CarController>();
    readonly HashSet<AssemblyPart> wanted=new HashSet<AssemblyPart>();
    readonly HashSet<PartType> overdue=new HashSet<PartType>();
    readonly List<AssemblyPart> staleParts=new List<AssemblyPart>();
    readonly List<CarController> staleCars=new List<CarController>();
    readonly List<VehicleBadge> badges=new List<VehicleBadge>();
    readonly Rect[] iconRects=new Rect[8];
    readonly RewardPopup[] rewards=new RewardPopup[8];
    PrototypeDayCycle rewardCycle;
    int nextReward;
    struct RewardPopup { public Vector3 point; public float expires; public string text; }
    struct VehicleBadge { public CarController car; public int count; }
    sealed class MeshCopy { public Transform node; public MeshRenderer renderer; public Mesh ownedMesh; public Material[] materials; public Material currentMaterial; }
    sealed class PartVisual
    {
        public Transform root;
        public MeshRenderer corners;
        public Mesh cornerMesh;
        public Bounds localBounds;
        public PartState state;
        public bool preview;
        public readonly List<MeshCopy> copies=new List<MeshCopy>();
    }
    void OnEnable()=>RefreshDisplay();
    void LateUpdate()=>RefreshDisplay();
    public PartState StateOf(AssemblyPart part)=>part!=null&&visuals.TryGetValue(part,out var v)?v.state:PartState.None;
    public bool PreviewOf(AssemblyPart part)=>part!=null&&visuals.TryGetValue(part,out var v)&&v.preview;
    public void RefreshDisplay()
    {
        if(rewardCycle!=PrototypeDayCycle.Current)
        {
            if(rewardCycle!=null)rewardCycle.OnAssemblyRewardPaid-=RewardPaid;
            rewardCycle=PrototypeDayCycle.Current;
            if(rewardCycle!=null)rewardCycle.OnAssemblyRewardPaid+=RewardPaid;
        }
        MissingPartCount=AffectedCarCount=SlotPartCount=OutlinePartCount=PreviewPartCount=0;
        badges.Clear();wanted.Clear();liveCars.Clear();farVertices.Clear();farIndices.Clear();DistantPartMarkerCount=0;
        if(progression==null||progression.mainSpline==null||progression.processes==null){ClearVisuals();return;}
        if(traffic==null)traffic=FindFirstObjectByType<LineTrafficManager>();
        if(player==null)player=FindFirstObjectByType<FactoryPlayerController>();
        if(traffic==null||!traffic.isActiveAndEnabled){ClearVisuals();return;}
        var interactor=player!=null?player.interactor:null;
        var focused=interactor!=null&&interactor.inputEnabled&&player!=null&&!player.MapMode&&player.ControlsActive?interactor.FocusedPart:null;
        if(focused!=null&&!interactor.IsAimingAt(focused))focused=null;
        foreach(var car in traffic.ActiveCars)
        {
            if(car==null||!car.gameObject.activeInHierarchy)continue;
            liveCars.Add(car);
            if(!car.CanRepair||car.targetSpline!=progression.mainSpline||car.SplineLength<=0)continue;
            if(!partsByCar.TryGetValue(car,out var parts)){parts=car.GetComponentsInChildren<AssemblyPart>(true);partsByCar.Add(car,parts);}
            overdue.Clear();
            float meters=progression.prototypeSettings!=null?progression.prototypeSettings.workZoneMeters:6f;
            foreach(var process in progression.processes)
            {
                if(process==null||process.parts==null||car.pathProgress<=process.gateProgress+meters/car.SplineLength)continue;
                foreach(var type in process.parts)if(type!=PartType.None&&type!=PartType.Frame_1)overdue.Add(type);
            }
            int count=0;
            foreach(var part in parts)
            {
                if(part==null||part.myType==PartType.None||part.myType==PartType.Frame_1||part.IsAssembled)continue;
                bool attached=part.IsPlayerAttached||part.currentWork>0f;
                bool missed=overdue.Contains(part.myType);
                bool selected=part==focused;
                bool nearbyPreview=interactor!=null&&interactor.PreviewEligible(part);
                if(attached||missed)count++;
                if(!attached&&!missed&&!selected&&!nearbyPreview)continue;
                float distance=player!=null?Vector3.Distance(player.transform.position,part.AttachmentWorldPosition):0;
                if(distance>partVisualRange&&!nearbyPreview)
                {
                    if(distance<=vehicleBadgeRange&&interactor!=null)
                    {AppendFarCorners(interactor.AttachmentBounds(part));DistantPartMarkerCount++;}
                    continue;
                }
                wanted.Add(part);
                if(!visuals.TryGetValue(part,out var visual)){visual=CreateVisual(part);visuals.Add(part,visual);}
                visual.state=attached?PartState.AttachedPending:PartState.MissingSlot;
                visual.preview=nearbyPreview&&!attached&&!part.IsPlayerAttaching;
                bool showSlot=!attached&&!part.IsPlayerAttaching;
                visual.corners.enabled=showSlot;
                visual.corners.sharedMaterial=visual.preview?GetActiveCornerMaterial():GetCornerMaterial();
                if(showSlot)SlotPartCount++;
                if(attached)OutlinePartCount++;
                if(visual.preview)PreviewPartCount++;
                visual.root.SetPositionAndRotation(attached?part.transform.position:part.AttachmentWorldPosition,attached?part.transform.rotation:part.AttachmentWorldRotation);
                visual.root.localScale=part.transform.lossyScale;
                foreach(var copy in visual.copies)
                {
                    copy.renderer.enabled=attached||visual.preview;
                    var material=attached?GetOutlineMaterial():GetPreviewMaterial();
                    if(copy.currentMaterial!=material)
                    {for(int m=0;m<copy.materials.Length;m++)copy.materials[m]=material;copy.renderer.sharedMaterials=copy.materials;copy.currentMaterial=material;}
                }
            }
            if(count>0){MissingPartCount+=count;AffectedCarCount++;badges.Add(new VehicleBadge{car=car,count=count});}
        }
        UpdateFarMesh();
        staleParts.Clear();foreach(var pair in visuals)if(pair.Key==null||!wanted.Contains(pair.Key))staleParts.Add(pair.Key);
        foreach(var part in staleParts)Release(part);
        staleCars.Clear();foreach(var pair in partsByCar)if(pair.Key==null||!liveCars.Contains(pair.Key))staleCars.Add(pair.Key);
        foreach(var car in staleCars)partsByCar.Remove(car);
    }
    Material Material(ref Material cache,string name,Color color,float pixels,CullMode cull)
    {
        if(cache!=null)return cache;
        var shader=Resources.Load<Shader>("PrototypeFeedback/PartOverlay");
        if(shader==null)return null;
        cache=new Material(shader){name=name,hideFlags=HideFlags.DontSave};
        cache.SetColor("_OverlayColor",color);cache.SetFloat("_OutlinePixels",pixels);cache.SetFloat("_Cull",(float)cull);
        return cache;
    }
    Material GetOutlineMaterial()=>Material(ref outlineMaterial,"UnfastenedOutline",new Color(1f,.12f,.05f,.92f),1.8f,CullMode.Front);
    Material GetPreviewMaterial()=>Material(ref previewMaterial,"AttachmentPreview",new Color(1f,.72f,.3f,.16f),0,CullMode.Off);
    Material GetCornerMaterial()=>Material(ref cornerMaterial,"MissingSlotCorners",new Color(1f,.22f,.08f,.30f),0,CullMode.Off);
    Material GetActiveCornerMaterial()=>Material(ref activeCornerMaterial,"ActiveSlotCorners",new Color(1f,.72f,.3f,.9f),0,CullMode.Off);
    void AppendFarCorners(Bounds bounds)
    {
        bounds.Expand(.04f);
        for(int corner=0;corner<8;corner++)for(int axis=0;axis<3;axis++)
        {
            Vector3 start=Corner(bounds,corner),end=Vector3.Lerp(start,Corner(bounds,corner^(1<<axis)),.16f);
            farIndices.Add(farVertices.Count);farVertices.Add(start);
            farIndices.Add(farVertices.Count);farVertices.Add(end);
        }
    }
    void UpdateFarMesh()
    {
        if(farRenderer==null)
        {
            var go=new GameObject("DistantDefectMarkers"){hideFlags=HideFlags.DontSave};
            farCorners=new Mesh{name="BatchedDistantDefectCorners",hideFlags=HideFlags.DontSave};
            farCorners.indexFormat=IndexFormat.UInt32;farCorners.MarkDynamic();
            go.AddComponent<MeshFilter>().sharedMesh=farCorners;
            farRenderer=go.AddComponent<MeshRenderer>();ConfigureRenderer(farRenderer);
            farRenderer.sharedMaterial=Material(ref farCornerMaterial,"DistantMissingCorners",new Color(1f,.22f,.08f,.22f),0,CullMode.Off);
        }
        farCorners.Clear();farCorners.SetVertices(farVertices);farCorners.SetIndices(farIndices,MeshTopology.Lines,0);farCorners.RecalculateBounds();
        farRenderer.enabled=farVertices.Count>0;
    }
    PartVisual CreateVisual(AssemblyPart part)
    {
        var visual=new PartVisual();
        visual.root=new GameObject("PartFeedback_"+part.myType){hideFlags=HideFlags.DontSave}.transform;
        bool boundsStarted=false;
        foreach(var renderer in part.GetComponentsInChildren<Renderer>(true))
        {
            if(renderer.GetComponentInParent<AssemblyPart>(true)!=part)continue;
            Mesh mesh=null,owned=null;
            var filter=renderer.GetComponent<MeshFilter>();
            if(filter!=null)mesh=filter.sharedMesh;
            else if(renderer is SkinnedMeshRenderer skin){owned=new Mesh{name="PartFeedbackSkin"};skin.BakeMesh(owned);mesh=owned;}
            if(mesh==null)continue;
            var relative=part.transform.worldToLocalMatrix*renderer.transform.localToWorldMatrix;
            for(int corner=0;corner<8;corner++)
            {
                var point=relative.MultiplyPoint3x4(Corner(mesh.bounds,corner));
                if(!boundsStarted){visual.localBounds=new Bounds(point,Vector3.zero);boundsStarted=true;}else visual.localBounds.Encapsulate(point);
            }
            var go=new GameObject("PartFeedbackMesh"){hideFlags=HideFlags.DontSave};go.transform.SetParent(visual.root,false);
            go.transform.localPosition=relative.GetColumn(3);
            Vector3 x=relative.GetColumn(0),y=relative.GetColumn(1),z=relative.GetColumn(2);
            var scale=new Vector3(x.magnitude,y.magnitude,z.magnitude);
            if(Vector3.Dot(Vector3.Cross(x,y),z)<0)scale.x=-scale.x;
            go.transform.localRotation=z.sqrMagnitude>1e-8f&&y.sqrMagnitude>1e-8f?Quaternion.LookRotation(z,y):Quaternion.identity;
            go.transform.localScale=scale;
            go.AddComponent<MeshFilter>().sharedMesh=mesh;
            var copy=go.AddComponent<MeshRenderer>();ConfigureRenderer(copy);copy.enabled=false;
            visual.copies.Add(new MeshCopy{node=go.transform,renderer=copy,ownedMesh=owned,materials=new Material[Mathf.Max(1,mesh.subMeshCount)]});
        }
        if(!boundsStarted)visual.localBounds=new Bounds(Vector3.zero,Vector3.one*.25f);
        var corners=new GameObject("MissingSlotCorners"){hideFlags=HideFlags.DontSave};corners.transform.SetParent(visual.root,false);
        visual.cornerMesh=CornerMesh(visual.localBounds);
        corners.AddComponent<MeshFilter>().sharedMesh=visual.cornerMesh;
        visual.corners=corners.AddComponent<MeshRenderer>();ConfigureRenderer(visual.corners);visual.corners.sharedMaterial=GetCornerMaterial();
        return visual;
    }
    static void ConfigureRenderer(MeshRenderer renderer)
    {renderer.shadowCastingMode=ShadowCastingMode.Off;renderer.receiveShadows=false;renderer.lightProbeUsage=LightProbeUsage.Off;renderer.reflectionProbeUsage=ReflectionProbeUsage.Off;}
    static Vector3 Corner(Bounds b,int n)=>b.center+new Vector3((n&1)==0?-b.extents.x:b.extents.x,(n&2)==0?-b.extents.y:b.extents.y,(n&4)==0?-b.extents.z:b.extents.z);
    static Mesh CornerMesh(Bounds source)
    {
        source.Expand(.08f);
        var vertices=new Vector3[48];var indices=new int[48];int index=0;
        for(int corner=0;corner<8;corner++)for(int axis=0;axis<3;axis++)
        {
            var start=Corner(source,corner);var end=Vector3.Lerp(start,Corner(source,corner^(1<<axis)),.18f);
            vertices[index]=start;indices[index]=index;index++;vertices[index]=end;indices[index]=index;index++;
        }
        var mesh=new Mesh{name="MissingSlotBrackets",hideFlags=HideFlags.DontSave};mesh.vertices=vertices;mesh.SetIndices(indices,MeshTopology.Lines,0);mesh.RecalculateBounds();return mesh;
    }
    void OnGUI()
    {
        VisibleWorkIconCount=VehicleBadgeCount=0;
        if(player==null||player.viewCamera==null||player.InteractionUIOpen||(PrototypeDayCycle.Current!=null&&PrototypeDayCycle.Current.IsSettlement))return;
        var camera=player.viewCamera;var focused=player.interactor!=null?player.interactor.FocusedPart:null;
        var color=GUI.color;
        if(!player.MapMode)
        {
            foreach(var pair in visuals)
            {
                if(pair.Value.state!=PartState.AttachedPending||pair.Key==focused)continue;
                var center=pair.Value.root.TransformPoint(pair.Value.localBounds.center);
                if(Vector3.Distance(player.transform.position,center)>workIconRange)continue;
                var screen=camera.WorldToScreenPoint(center);if(screen.z<=0||screen.x<20||screen.x>Screen.width-20||screen.y<24||screen.y>Screen.height-24)continue;
                var rect=new Rect(screen.x-9,Screen.height-screen.y-18,18,18);bool overlaps=false;
                for(int n=0;n<VisibleWorkIconCount;n++)if(iconRects[n].Overlaps(rect)){overlaps=true;break;}
                if(overlaps||VisibleWorkIconCount==iconRects.Length)continue;
                iconRects[VisibleWorkIconCount++]=rect;
                GUI.color=new Color(1f,.75f,.3f);GUI.DrawTexture(new Rect(rect.x+8,rect.y+5,3,11),Texture2D.whiteTexture);GUI.DrawTexture(new Rect(rect.x+3,rect.y+3,13,5),Texture2D.whiteTexture);
            }
        }
        GUI.color=color;
        if(!player.MapMode&&focused!=null&&focused.IsPlayerAttached&&!focused.IsAssembled&&!focused.IsPlayerAttaching)
        {
            var screen=camera.WorldToScreenPoint(player.interactor.InteractionPoint(focused));
            if(screen.z>0)
            {
                float x=Mathf.Clamp(screen.x-110,8,Mathf.Max(8,Screen.width-228));
                float y=Mathf.Clamp(Screen.height-screen.y+26,8,Mathf.Max(8,Screen.height-128));
                GUI.Box(new Rect(x,y,220,28),PlayerAssemblyInteractor.PartLabel(focused.myType)+" · 망치 "+focused.PlayerHitsRemaining(player.interactor.PlayerHammerHits)+"회");
            }
        }
        for(int n=0;n<rewards.Length;n++)
        {
            var reward=rewards[n];float remaining=reward.expires-Time.time;
            if(remaining<=0||reward.text==null||Vector3.Distance(player.transform.position,reward.point)>12f)continue;
            var screen=camera.WorldToScreenPoint(reward.point+Vector3.up*(.18f+(1.1f-remaining)*.35f));
            if(screen.z<=0)continue;
            GUI.color=new Color(1f,.85f,.45f,Mathf.Clamp01(remaining*2));
            GUI.Label(new Rect(screen.x-30,Screen.height-screen.y-18,80,24),reward.text);
        }
        GUI.color=color;
        foreach(var badge in badges)
        {
            if(badge.car==null)continue;
            float distance=Vector3.Distance(player.transform.position,badge.car.transform.position);
            if(distance>vehicleBadgeRange)continue;
            var screen=camera.WorldToScreenPoint(badge.car.transform.position+Vector3.up*2.15f);
            // The overhead anchor can leave the frustum while standing beside/inside a car.
            // Nearby defects keep a clamped top-edge badge even when its world anchor is behind the eye.
            if(screen.z<=0)
            {if(distance>workIconRange||player.MapMode)continue;screen=new Vector3(Screen.width*.5f,Screen.height-48f,1f);}
            float badgeX=Mathf.Clamp(screen.x-48,8,Mathf.Max(8,Screen.width-104));
            float badgeY=Mathf.Clamp(Screen.height-screen.y-12,36,Mathf.Max(36,Screen.height-40));
            VehicleBadgeCount++;GUI.Box(new Rect(badgeX,badgeY,96,25),"미체결 "+badge.count);
        }
    }
    void Release(AssemblyPart part)
    {
        if(!visuals.TryGetValue(part,out var visual))return;
        visuals.Remove(part);foreach(var copy in visual.copies)if(copy.ownedMesh!=null)Destroy(copy.ownedMesh);
        if(visual.cornerMesh!=null)Destroy(visual.cornerMesh);if(visual.root!=null)Destroy(visual.root.gameObject);
    }
    void ClearVisuals()
    {staleParts.Clear();foreach(var pair in visuals)staleParts.Add(pair.Key);foreach(var part in staleParts)Release(part);partsByCar.Clear();}
    void RewardPaid(Vector3 point,int amount){rewards[nextReward]=new RewardPopup{point=point,expires=Time.time+1.1f,text="+"+amount+"원"};nextReward=(nextReward+1)%rewards.Length;}
    void OnDisable(){if(rewardCycle!=null)rewardCycle.OnAssemblyRewardPaid-=RewardPaid;rewardCycle=null;System.Array.Clear(rewards,0,rewards.Length);ClearVisuals();if(farRenderer!=null)farRenderer.enabled=false;MissingPartCount=AffectedCarCount=SlotPartCount=OutlinePartCount=PreviewPartCount=VisibleWorkIconCount=VehicleBadgeCount=0;traffic=null;}
    void OnDestroy(){ClearVisuals();if(farRenderer!=null)Destroy(farRenderer.gameObject);if(farCorners!=null)Destroy(farCorners);if(activeCornerMaterial!=null)Destroy(activeCornerMaterial);if(farCornerMaterial!=null)Destroy(farCornerMaterial);if(cornerMaterial!=null)Destroy(cornerMaterial);if(outlineMaterial!=null)Destroy(outlineMaterial);if(previewMaterial!=null)Destroy(previewMaterial);}
}
