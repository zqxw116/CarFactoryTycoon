#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object=UnityEngine.Object;

public static class FactoryExpansionSetup
{
    static T[] All<T>() where T:Object=>Object.FindObjectsByType<T>(FindObjectsInactive.Include,FindObjectsSortMode.None);
    static Transform Find(string path)=>GameObject.Find(path).transform;
    public static string Apply()
    {
        if(Application.isPlaying)throw new InvalidOperationException("Edit mode required.");
        var p=All<ProductionProgression>().Single();
        if(p.GetComponent<FactoryExpansion>()!=null)throw new InvalidOperationException("Expansion already authored.");
        Undo.RegisterFullObjectHierarchyUndo(p.gameObject,"Growing factory footprint");
        var growth=p.gameObject.AddComponent<FactoryExpansion>();growth.progression=p;
        var shift=new Vector3(0,0,20.65f);
        foreach(string path in new[]{"GameObjectGroup/Warehouse","Decor/WarehouseBuilding","SupplyManager/WarehouseLoadPoint","SupplyWorker_01"}){var t=Find(path);Undo.RecordObject(t,"Warehouse by supply tunnel");t.position+=shift;}
        var load=Find("SupplyManager/WarehouseLoadPoint");
        foreach(Transform t in Find("Decor/Warehouse")){
            Undo.RecordObject(t,"Compact initial warehouse stock");var pos=t.position;
            pos.z=Mathf.Lerp(29,42,Mathf.InverseLerp(1.2f,26.5f,pos.z));
            if(t.name=="indicator-special-area")pos=load.position+Vector3.down*.095f;
            t.position=pos;
        }
        // Old stretched walls are retained disabled for authoring history. The moving boundary is real geometry.
        Find("Decor/Walls").gameObject.SetActive(false);Find("Decor/WallsUpper").gameObject.SetActive(false);
        foreach(var cover in p.constructionRows)if(cover!=null)cover.SetActive(false);
        p.constructionRows=new GameObject[0];EditorUtility.SetDirty(p);
        growth.floor=Find("Object/Plane").GetComponent<MeshFilter>();growth.floorCollider=growth.floor.GetComponent<MeshCollider>();growth.finishedFloor=growth.floor.sharedMesh;
        var wallRoot=new GameObject("GrowingFactoryBoundary");Undo.RegisterCreatedObjectUndo(wallRoot,"Growing boundary");wallRoot.transform.SetParent(p.transform,false);
        var wallMat=AssetDatabase.LoadAssetAtPath<Material>("Assets/@Resource/Materials/FactoryFlow/SupplySteel.mat");
        var capMat=AssetDatabase.LoadAssetAtPath<Material>("Assets/@Resource/Materials/FactoryFlow/SafetyYellow.mat");
        growth.walls=new Transform[4];growth.wallCaps=new Transform[4];
        for(int i=0;i<4;i++){growth.walls[i]=Box(wallRoot.transform,"Boundary_"+i,wallMat);growth.wallCaps[i]=Box(wallRoot.transform,"Cap_"+i,capMat);}
        var unavailable=new GameObject("FutureAreaNavigation");unavailable.transform.SetParent(p.transform,false);growth.futureArea=unavailable.AddComponent<UnityEngine.AI.NavMeshObstacle>();growth.futureArea.shape=UnityEngine.AI.NavMeshObstacleShape.Box;growth.futureArea.carving=true;growth.futureArea.carveOnlyStationary=false;
        var props=new List<FactoryExpansion.Prop>();
        foreach(string group in new[]{"Machines","Warehouse","Outbound","Inbound","Safety"})foreach(Transform t in Find("Decor/"+group)){
            var renderers=t.GetComponentsInChildren<Renderer>(true);float min=renderers.Length==0?t.position.z:renderers.Min(r=>r.bounds.min.z);
            props.Add(new FactoryExpansion.Prop{root=t.gameObject,minZ=min,authoredActive=t.gameObject.activeSelf});
        }
        growth.props=props.ToArray();growth.ApplyPrefix(23);
        var log=new StringBuilder();FactoryLayoutRevisionSetup.BakeCompleteLayout(log);
        foreach(Transform t in Find("Decor/Crosswalks"))props.Add(new FactoryExpansion.Prop{root=t.gameObject,minZ=t.position.z-2.5f,authoredActive=true});
        var departure=Find("DepartureStation");props.Add(new FactoryExpansion.Prop{root=departure.gameObject,minZ=-15,authoredActive=departure.gameObject.activeSelf});growth.props=props.ToArray();
        growth.ApplyPrefix(1);SaveInitialFloor(growth);
        var camera=All<CameraController>().Single();camera.factory=growth;camera.initialPivot=new Vector3(45.5f,.7f,35);camera.initialDistance=25;camera.initialYaw=225;camera.initialPitch=45;camera.minDistance=8;camera.maxDistance=100;camera.overviewPitch=55;camera.overviewYaw=35;
        camera.SetOverview(false);Camera.main.clearFlags=CameraClearFlags.SolidColor;Camera.main.backgroundColor=new Color(168f/255,178f/255,177f/255);EditorUtility.SetDirty(camera);EditorUtility.SetDirty(Camera.main);EditorUtility.SetDirty(growth);
        EditorSceneManager.MarkSceneDirty(p.gameObject.scene);EditorSceneManager.SaveScene(p.gameObject.scene);
        return "Growing footprint authored; warehouse 80.6/35; default perspective25m. "+log;
    }
    public static void SaveInitialFloor(FactoryExpansion growth)
    {
        const string path="Assets/@Resource/Materials/FactoryFlow/InitialFactoryFloor.asset";
        var mesh=AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if(mesh==null){mesh=Object.Instantiate(growth.floor.sharedMesh);mesh.hideFlags=HideFlags.None;AssetDatabase.CreateAsset(mesh,path);}
        else {EditorUtility.CopySerialized(growth.floor.sharedMesh,mesh);mesh.hideFlags=HideFlags.None;EditorUtility.SetDirty(mesh);AssetDatabase.SaveAssetIfDirty(mesh);}
        growth.floor.sharedMesh=mesh;if(growth.floorCollider!=null)growth.floorCollider.sharedMesh=mesh;
    }
    static Transform Box(Transform parent,string name,Material material){var g=GameObject.CreatePrimitive(PrimitiveType.Cube);g.name=name;g.transform.SetParent(parent,false);Object.DestroyImmediate(g.GetComponent<Collider>());g.GetComponent<Renderer>().sharedMaterial=material;return g.transform;}
}
#endif



