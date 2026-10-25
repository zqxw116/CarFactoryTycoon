#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>Explicit authoring of the inspection pit. Preserves current scene edits.</summary>
public static class WaterInspectionVisualSetup
{
    const string MeshPath = "Assets/@Resource/Materials/FactoryFlow/InspectionFloorOpening.asset";
    struct Vertex
    {
        public Vector3 p, n;
        public Vector2 uv, uv2;
        public static Vertex Lerp(Vertex a, Vertex b, float t) => new Vertex {
            p = Vector3.Lerp(a.p, b.p, t), n = Vector3.Lerp(a.n, b.n, t),
            uv = Vector2.Lerp(a.uv, b.uv, t), uv2 = Vector2.Lerp(a.uv2, b.uv2, t)
        };
    }

    public static string Apply()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (Application.isPlaying || scene.name != "Car_Factory 3")
            throw new InvalidOperationException("Open Car_Factory 3 in edit mode.");
        var station = Object.FindFirstObjectByType<WaterTestStation>();
        var floor = GameObject.Find("Object/Plane");
        if (station == null || !station.sharedRejectDeck || floor == null)
            throw new InvalidOperationException("Missing shared inspection deck or factory floor.");
        var hatch = station.transform.Find("RejectHatch");
        if (hatch == null) throw new InvalidOperationException("Missing hatch.");
        var filter = floor.GetComponent<MeshFilter>();
        if (filter == null || filter.sharedMesh == null || filter.sharedMesh.subMeshCount != 1)
            throw new InvalidOperationException("Expected single-material floor mesh.");
        Vector3 center = station.rejectPoint.position;
        Undo.IncrementCurrentGroup();
        int group = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Deep inspection reject pit");
        RebuildFloorOpenings();
        var oldDark = hatch.Find("HoleDark");
        if (oldDark != null) { Undo.RecordObject(oldDark.gameObject, "Remove flat hole cover"); oldDark.gameObject.SetActive(false); }

        var dark = AssetDatabase.LoadAssetAtPath<Material>("Assets/@Resource/Materials/FactoryFlow/SupplyDark.mat");
        var steel = AssetDatabase.LoadAssetAtPath<Material>("Assets/@Resource/Materials/FactoryFlow/SupplySteel.mat");
        var black = oldDark != null ? oldDark.GetComponent<Renderer>().sharedMaterial : dark;
        const string lampPath="Assets/@Resource/Materials/FactoryFlow/PitDepthLamp.mat";
        var lamp=AssetDatabase.LoadAssetAtPath<Material>(lampPath);
        if(lamp==null) {lamp=new Material(steel);lamp.name="PitDepthLamp";AssetDatabase.CreateAsset(lamp,lampPath);}
        lamp.SetColor("_BaseColor",new Color(.8f,.53f,.22f));lamp.EnableKeyword("_EMISSION");lamp.SetColor("_EmissionColor",new Color(1f,.55f,.15f)*1.5f);EditorUtility.SetDirty(lamp);AssetDatabase.SaveAssetIfDirty(lamp);
        var pit = hatch.Find("DepthPit");
        if (pit == null) { var go = new GameObject("DepthPit"); Undo.RegisterCreatedObjectUndo(go, "Create pit"); pit = go.transform; pit.SetParent(hatch, false); }
        // Positions are world-space to retain the existing hatch and leaf transforms.
        float top = center.y - .02f, bottom = -3.5f;
        float middle = (top + bottom) * .5f, height = top - bottom;
        Box(pit, "Wall_X0", new Vector3(center.x-2.78f,middle,center.z), new Vector3(.16f,height,2.92f), dark);
        Box(pit, "Wall_X1", new Vector3(center.x+2.78f,middle,center.z), new Vector3(.16f,height,2.92f), dark);
        Box(pit, "Wall_Z0", new Vector3(center.x,middle,center.z-1.38f), new Vector3(5.4f,height,.16f), dark);
        Box(pit, "Wall_Z1", new Vector3(center.x,middle,center.z+1.38f), new Vector3(5.4f,height,.16f), dark);
        Box(pit, "DarkBottom", new Vector3(center.x,bottom-.04f,center.z), new Vector3(5.56f,.08f,2.76f), black);
        for (int i=0;i<3;i++)
        {
            float y=-.85f-i*.85f;
            for (int side=-1;side<=1;side+=2)
                Box(pit,"DepthRib_"+i+"_"+side,new Vector3(center.x,y,center.z+side*1.315f),new Vector3(5.35f,.055f,.025f),steel);
        }
        for(int i=0;i<2;i++)
            Box(pit,"DepthLamp_"+i,new Vector3(center.x,-.75f-i*1.35f,center.z+1.31f),new Vector3(3.4f,.035f,.025f),lamp);
        var lightTransform=pit.Find("InteriorLight");
        if(lightTransform==null) {var go=new GameObject("InteriorLight");Undo.RegisterCreatedObjectUndo(go,"Pit light");lightTransform=go.transform;lightTransform.SetParent(pit,false);}
        lightTransform.position=new Vector3(center.x,-.8f,center.z);
        var light=lightTransform.GetComponent<Light>();if(light==null)light=Undo.AddComponent<Light>(lightTransform.gameObject);
        Undo.RecordObject(light,"Pit light");light.type=LightType.Point;light.color=new Color(1f,.72f,.4f);light.intensity=4f;light.range=4.5f;light.shadows=LightShadows.None;light.enabled=false;
        Undo.RecordObject(station, "Inspection drop depth");
        station.rejectPitLight=light;
        station.rejectSinkDepth = 6.5f;
        station.rejectVanishDuration = 1.2f;
        EditorUtility.SetDirty(station);
        EditorSceneManager.MarkSceneDirty(scene);
        // Preserve the user's other pending edits; saving includes them, never reloads/discards them.
        if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("Could not save inspection scene.");
        Undo.CollapseUndoOperations(group);
        return "Floor opening 5.4 x 2.6m; visible walls to -3.5m, dark bottom; full-size drop 6.5m. Existing scene edits preserved and saved.";
    }

    static void Box(Transform parent, string name, Vector3 position, Vector3 scale, Material mat)
    {
        var t=parent.Find(name);
        if(t==null) {var go=GameObject.CreatePrimitive(PrimitiveType.Cube); go.name=name; Undo.RegisterCreatedObjectUndo(go,"Pit geometry"); t=go.transform; t.SetParent(parent,false); Object.DestroyImmediate(go.GetComponent<Collider>());}
        Undo.RecordObject(t,"Pit geometry");
        t.SetPositionAndRotation(position,Quaternion.identity); t.localScale=scale;
        t.GetComponent<Renderer>().sharedMaterial=mat;
    }

    // Rebuild from the original primitive, so moving a pit closes its old opening.
    public static void RebuildFloorOpenings()
    {
        var floor=GameObject.Find("Object/Plane");
        var filter=floor.GetComponent<MeshFilter>();
        var water=Object.FindFirstObjectByType<WaterTestStation>();
        var temporary=GameObject.CreatePrimitive(PrimitiveType.Plane);
        var source=temporary.GetComponent<MeshFilter>().sharedMesh;
        Object.DestroyImmediate(temporary);
        var generated=CutOpening(source,floor.transform,water.rejectPoint.position,new Vector2(2.7f,1.3f));
        var shake=Object.FindFirstObjectByType<ShakeTestStation>();
        if(shake!=null)
        {
            var both=CutOpening(generated,floor.transform,shake.transform.position,new Vector2(2.7f,1.6f));
            Object.DestroyImmediate(generated);generated=both;
        }
        var asset=AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
        if(asset==null){asset=generated;AssetDatabase.CreateAsset(asset,MeshPath);}
        else {EditorUtility.CopySerialized(generated,asset);Object.DestroyImmediate(generated);EditorUtility.SetDirty(asset);AssetDatabase.SaveAssetIfDirty(asset);}
        Undo.RecordObject(filter,"Inspection floor openings");filter.sharedMesh=asset;
        var collider=floor.GetComponent<MeshCollider>();
        if(collider!=null){Undo.RecordObject(collider,"Inspection floor collision");collider.sharedMesh=asset;}
    }

    static Mesh CutOpening(Mesh source, Transform floor, Vector3 center, Vector2 half)
    {
        var positions=source.vertices; var normals=source.normals; var uv=source.uv; var uv2=source.uv2;
        var triangles=source.triangles;
        var vertices=new List<Vertex>();
        for(int i=0;i<triangles.Length;i+=3)
        {
            var remaining=new List<Vertex>();
            for(int j=0;j<3;j++) {int k=triangles[i+j]; remaining.Add(new Vertex {p=positions[k],n=normals.Length>k?normals[k]:Vector3.up,uv=uv.Length>k?uv[k]:Vector2.zero,uv2=uv2.Length>k?uv2[k]:Vector2.zero});}
            // Partition a triangle into its outside pieces, retaining UV interpolation at each cut.
            for(int edge=0;edge<4 && remaining.Count>=3;edge++)
            {
                AddPolygon(vertices, Clip(remaining,floor,center,half,edge,false));
                remaining=Clip(remaining,floor,center,half,edge,true);
            }
        }
        var p=new List<Vector3>(); var n=new List<Vector3>(); var u=new List<Vector2>(); var u2=new List<Vector2>(); var indices=new List<int>();
        foreach(var v in vertices) {indices.Add(p.Count);p.Add(v.p);n.Add(v.n.normalized);u.Add(v.uv);u2.Add(v.uv2);}
        var mesh=new Mesh {name="FactoryFloor_InspectionOpening"};
        mesh.SetVertices(p);mesh.SetNormals(n);mesh.SetUVs(0,u);mesh.SetUVs(1,u2);mesh.SetTriangles(indices,0);mesh.RecalculateBounds();mesh.RecalculateTangents();
        return mesh;
    }
    static float Distance(Vertex v, Transform floor, Vector3 c, Vector2 h, int edge)
    {
        var w=floor.TransformPoint(v.p);
        switch(edge) {case 0:return w.x-(c.x-h.x);case 1:return c.x+h.x-w.x;case 2:return w.z-(c.z-h.y);default:return c.z+h.y-w.z;}
    }
    static List<Vertex> Clip(List<Vertex> input, Transform floor, Vector3 c, Vector2 h, int edge, bool inside)
    {
        var output=new List<Vertex>();
        if(input.Count==0)return output;
        var previous=input[input.Count-1]; float dp=Distance(previous,floor,c,h,edge)*(inside?1:-1);
        foreach(var current in input)
        {
            float dc=Distance(current,floor,c,h,edge)*(inside?1:-1);
            bool a=dp>=0,b=dc>=0;
            if(a!=b)output.Add(Vertex.Lerp(previous,current,dp/(dp-dc)));
            if(b)output.Add(current);
            previous=current;dp=dc;
        }
        return output;
    }
    static void AddPolygon(List<Vertex> output, List<Vertex> polygon)
    {
        for(int i=1;i+1<polygon.Count;i++)
        {
            if(Vector3.Cross(polygon[i].p-polygon[0].p,polygon[i+1].p-polygon[0].p).sqrMagnitude<1e-12f)continue;
            output.Add(polygon[0]);output.Add(polygon[i]);output.Add(polygon[i+1]);
        }
    }
}
#endif
