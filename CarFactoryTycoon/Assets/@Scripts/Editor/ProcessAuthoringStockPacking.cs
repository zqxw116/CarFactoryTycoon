#if UNITY_EDITOR
using System.Linq;
using UnityEngine;
using UnityEditor;

public sealed partial class ProcessAuthoringWindow
{
    int packingHandle;
    Pose PackingFrame(ProcessAuthoringPreview p)=>ProcessAuthoringLayout.Frame(p.progression,p.progression.processes[processIndex]);
    Vector3 PackingWorld(Vector3 local,ProcessStockLayout data,Pose frame)=>frame.position+frame.rotation*(local+Vector3.right*(data.side*draft.actorDistance));
    Vector3 PackingLocal(Vector3 world,ProcessStockLayout data,Pose frame)=>Quaternion.Inverse(frame.rotation)*(world-frame.position)-Vector3.right*(data.side*draft.actorDistance);
    static Vector3 PackingField(string label,Vector3 stored,Vector3 displayed,System.Func<Vector3,Vector3> convert)
    {
        EditorGUI.BeginChangeCheck();
        var edited=EditorGUILayout.Vector3Field(label,displayed);
        return EditorGUI.EndChangeCheck()?convert(edited):stored;
    }
    Vector3 PackingPointField(string label,Vector3 stored,ProcessStockLayout data,Pose frame)
        =>PackingField(label,stored,PackingWorld(stored,data,frame),world=>PackingLocal(world,data,frame));
    Vector3 PackingOffsetField(string label,Vector3 stored,Pose frame)
        =>PackingField(label,stored,frame.rotation*stored,world=>Quaternion.Inverse(frame.rotation)*world);
    Vector3 PackingRotationField(string label,Vector3 stored,Pose frame)
        =>PackingField(label,stored,(frame.rotation*Quaternion.Euler(stored)).eulerAngles,world=>(Quaternion.Inverse(frame.rotation)*Quaternion.Euler(world)).eulerAngles);
    void DrawStockPacking(ProcessAuthoringPreview p)
    {
        if(p.FuelMode){EditorGUILayout.HelpBox("부품 공정을 선택하세요.",MessageType.Info);return;}
        var record=draft.processes[processIndex];if(record.stocks.Count==0)return;
        stockIndex=EditorGUILayout.Popup("재고 부품",Mathf.Clamp(stockIndex,0,record.stocks.Count-1),record.stocks.Select(x=>x.part.ToString()).ToArray());
        var stock=GripStock(p,out var data);if(stock==null)return;if(data.packing==null)data.packing=new ProcessStockPacking();
        var cfg=data.packing;var frame=PackingFrame(p);
        if(!cfg.enabled)
        {
            EditorGUILayout.HelpBox("부품 한 개의 월드 회전과 실제 크기로 재고를 배치합니다. 기존 배치는 버튼을 누를 때 전환됩니다.",MessageType.Info);
            if(GUILayout.Button("월드 기준 재고 배치 시작",GUILayout.Height(28)))
            {
                Undo.RecordObject(draft,"Enable World Stock Packing");
                data.itemEuler=(Quaternion.Inverse(frame.rotation)*stock.GetTopSlotWorldRot()).eulerAngles;
                cfg.enabled=true;cfg.columns=Mathf.Max(1,Mathf.CeilToInt(stock.Capacity/2f));cfg.layers=2;
                cfg.end=data.inventory.position+Quaternion.Inverse(frame.rotation)*Vector3.right*3;
                cfg.control1=Vector3.Lerp(data.inventory.position,cfg.end,1f/3);cfg.control2=Vector3.Lerp(data.inventory.position,cfg.end,2f/3);
                data.followInventoryRotation=true;Changed();
            }
            return;
        }
        EditorGUILayout.HelpBox("끝점은 방향/곡선 기준입니다. 간격·개수대로 배치하므로 끝점을 넘거나 끝점 전에 끝날 수 있습니다. 모든 위치·각도·오프셋 입력은 월드 기준입니다.",MessageType.Info);
        EditorGUILayout.LabelField("부품 하나의 자세",EditorStyles.boldLabel);
        EditorGUI.BeginChangeCheck();Undo.RecordObject(draft,"Edit World Stock Packing");
        data.itemEuler=PackingRotationField("부품 월드 회전",data.itemEuler,frame);
        data.followInventoryRotation=EditorGUILayout.Toggle("대기 슬롯도 같은 방향",data.followInventoryRotation);
        if(!data.followInventoryRotation)
        {
            data.pickup.euler=PackingRotationField("슬롯 별도 월드 회전",data.pickup.euler,frame);
        }
        GUILayout.Space(10);EditorGUILayout.LabelField("행 · 열 · 높이",EditorStyles.boldLabel);
        cfg.layers=Mathf.Clamp(EditorGUILayout.IntField("높이 개수",cfg.layers),1,Mathf.Max(1,stock.Capacity));
        int rows=Mathf.CeilToInt(stock.Capacity/(float)(Mathf.Max(1,cfg.columns)*cfg.layers));
        int nextRows=Mathf.Max(1,EditorGUILayout.IntField("행 개수",rows));
        if(nextRows!=rows)cfg.columns=Mathf.Max(1,Mathf.CeilToInt(stock.Capacity/(float)(nextRows*cfg.layers)));
        cfg.columns=Mathf.Clamp(EditorGUILayout.IntField("한 행의 열 개수",cfg.columns),1,Mathf.Max(1,stock.Capacity));
        EditorGUILayout.LabelField("실제 배치",stock.Capacity+"개 = 높이 "+cfg.layers+" / 행 "+Mathf.CeilToInt(stock.Capacity/(float)(cfg.columns*cfg.layers))+" / 최대 열 "+cfg.columns);
        EditorGUILayout.HelpBox("총 수량 중 한 개는 집기 슬롯에 표시됩니다. 남은 재고는 위층부터 소비합니다. 행·열·높이는 기존 풀 수량을 재배치하며 수량을 생성하지 않습니다.",MessageType.None);
        cfg.centerSpacing=EditorGUILayout.Popup("간격 측정",cfg.centerSpacing?1:0,new[]{"부품 실제 크기 + 빈 간격","중심 간격 직접 지정"})==1;
        EditorGUILayout.LabelField("간격 X=열, Y=행, Z=높이",EditorStyles.miniLabel);
        cfg.gap=EditorGUILayout.Vector3Field(cfg.centerSpacing?"중심 간격 (m)":"빈 간격 (m)",cfg.gap);
        EditorGUILayout.LabelField("회전한 부품 월드 크기",stock.PackedWorldSize.ToString("F3")+" m");
        cfg.offset=PackingOffsetField("전체 월드 오프셋",cfg.offset,frame);
        cfg.rowOffset=PackingOffsetField("행마다 더할 오프셋",cfg.rowOffset,frame);
        cfg.layerOffset=PackingOffsetField("층마다 더할 오프셋",cfg.layerOffset,frame);
        GUILayout.Space(10);EditorGUILayout.LabelField("시작 · 끝 · 경로",EditorStyles.boldLabel);
        data.inventory.position=PackingPointField("시작점 월드 위치",data.inventory.position,data,frame);
        cfg.end=PackingPointField("끝점 월드 위치",cfg.end,data,frame);
        bool curve=EditorGUILayout.Toggle("베지에 경로 사용",cfg.curve);
        if(curve&&!cfg.curve){cfg.control1=Vector3.Lerp(data.inventory.position,cfg.end,1f/3);cfg.control2=Vector3.Lerp(data.inventory.position,cfg.end,2f/3);}cfg.curve=curve;
        if(cfg.curve)
        {
            cfg.control1=PackingPointField("시작 제어점 월드 위치",cfg.control1,data,frame);
            cfg.control2=PackingPointField("도착 제어점 월드 위치",cfg.control2,data,frame);
        }
        if(EditorGUI.EndChangeCheck())Changed();
        packingHandle=GUILayout.Toolbar(packingHandle,cfg.curve?new[]{"시작점","끝점","시작 제어점","도착 제어점"}:new[]{"시작점","끝점"});
        if(GUILayout.Button("대표 부품 한 개 선택 · Scene에서 회전/이동")&&stock.InventoryExample!=null)
        {Selection.activeGameObject=stock.InventoryExample.gameObject;Tools.current=Tool.Rotate;}
        if(GUILayout.Button("선택한 재고 보기"))SceneView.lastActiveSceneView?.Frame(new Bounds(stock.transform.position,Vector3.one*6),false);
    }
    void CapturePackingExample()
    {
        if(tabIndex!=5||Preview==null||Preview.FuelMode||draft==null)return;
        var stock=GripStock(Preview,out var data);if(stock==null||data.packing==null||!data.packing.enabled||stock.InventoryExample==null||Selection.activeTransform!=stock.InventoryExample)return;
        var frame=PackingFrame(Preview);var sample=stock.InventoryExample;
        var rotation=frame.rotation*Quaternion.Euler(data.itemEuler);
        var delta=sample.position-stock.PackedSlotWorld(stock.InventoryExampleIndex);
        if(Quaternion.Angle(sample.rotation,rotation)<.01f&&delta.sqrMagnitude<.000001f)return;
        Undo.RecordObject(draft,"Rotate Stock Example");
        data.itemEuler=(Quaternion.Inverse(frame.rotation)*sample.rotation).eulerAngles;
        data.packing.offset+=Quaternion.Inverse(frame.rotation)*delta;
        Changed();
    }
    void DrawStockPackingScene(ProcessAuthoringPreview p)
    {
        if(tabIndex!=5||p.FuelMode)return;var stock=GripStock(p,out var data);var cfg=data?.packing;if(stock==null||cfg==null||!cfg.enabled)return;
        var frame=PackingFrame(p);var a=PackingWorld(data.inventory.position,data,frame);var b=PackingWorld(cfg.end,data,frame);
        ProcessAuthoringMarkers.Point(a,"재고 시작 · 첫 부품 바닥",Color.green);ProcessAuthoringMarkers.Point(b,"방향 끝점 · 개수 제한 아님",Color.magenta);
        var c=PackingWorld(cfg.control1,data,frame);var d=PackingWorld(cfg.control2,data,frame);
        if(cfg.curve){Handles.DrawBezier(a,b,c,d,Color.yellow,null,2);ProcessAuthoringMarkers.Point(c,"재고 시작 제어점",Color.yellow);ProcessAuthoringMarkers.Point(d,"재고 도착 제어점",Color.yellow);}
        else {Handles.color=Color.yellow;Handles.DrawDottedLine(a,b,5);}
        for(int i=0;i<stock.Capacity;i++){Handles.color=new Color(.2f,1,.6f,.45f);Handles.DrawWireCube(stock.PackedSlotWorld(i),Vector3.one*.045f);}
        packingHandle=Mathf.Clamp(packingHandle,0,cfg.curve?3:1);
        var position=packingHandle==0?a:packingHandle==1?b:packingHandle==2?c:d;
        EditorGUI.BeginChangeCheck();var moved=Handles.PositionHandle(position,Quaternion.identity);
        if(EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(draft,"Move Stock Packing Point");var local=PackingLocal(moved,data,frame);
            if(packingHandle==0)data.inventory.position=local;else if(packingHandle==1)cfg.end=local;else if(packingHandle==2)cfg.control1=local;else cfg.control2=local;
            Changed();
        }
    }
}
#endif
