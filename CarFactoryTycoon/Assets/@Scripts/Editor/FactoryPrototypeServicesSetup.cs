#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class FactoryPrototypeServicesSetup
{
    public static string Apply()
    {
        if(Application.isPlaying)throw new InvalidOperationException("Edit mode required.");
        var p=UnityEngine.Object.FindFirstObjectByType<ProductionProgression>();
        if(p==null||!p.prototypeMode)throw new InvalidOperationException("Prototype factory scene required.");
        var supply=UnityEngine.Object.FindFirstObjectByType<SupplyManager>();
        var fuel=UnityEngine.Object.FindFirstObjectByType<FuelStation>(FindObjectsInactive.Include);
        var player=UnityEngine.Object.FindFirstObjectByType<FactoryPlayerController>();
        if(supply==null||fuel==null)throw new InvalidOperationException("Existing supply/fuel hardware missing.");
        var templates=UnityEngine.Object.FindObjectsByType<SupplyWorker>(FindObjectsInactive.Include,FindObjectsSortMode.None);
        if(templates.Length==0)throw new InvalidOperationException("Supply worker template missing.");
        Undo.RecordObject(supply,"Connect prototype supply");
        supply.prototypeMode=true;supply.hireTemplate=templates[0];supply.supplyWorkers.Clear();supply.verboseLog=false;
        foreach(var worker in templates){Undo.RecordObject(worker,"Configure supply template");worker.prototypeMode=true;worker.verboseLog=false;worker.gameObject.SetActive(false);EditorUtility.SetDirty(worker);}
        Undo.RecordObject(fuel,"Connect prototype fuel");
        fuel.prototypeMode=true;fuel.prototypeSettings=p.prototypeSettings;fuel.enabled=true;
        if(fuel.attendant!=null)fuel.attendant.gameObject.SetActive(false);
        if(fuel.automationRig!=null)fuel.automationRig.SetActive(false);
        var interaction=p.GetComponent<PrototypeServicesInteraction>();
        if(interaction==null)interaction=Undo.AddComponent<PrototypeServicesInteraction>(p.gameObject);
        interaction.player=player;interaction.progression=p;interaction.supply=supply;interaction.fuel=fuel;
        interaction.warehousePost=Post(p.transform,"PrototypeSupplyDesk",supply.LoadPoint+Vector3.right*1.3f,"SUPPLY");
        interaction.fuelPost=Post(p.transform,"PrototypeFuelDesk",fuel.homePoint.position+Vector3.left*1.3f+Vector3.back*.6f,"FUEL");
        var day=p.GetComponent<PrototypeDayCycle>();Undo.RecordObject(day,"Connect service wages");
        day.supply=supply;day.fuel=fuel;day.services=interaction;
        foreach(var item in new UnityEngine.Object[]{supply,fuel,interaction,day,p.prototypeSettings})EditorUtility.SetDirty(item);
        EditorSceneManager.MarkSceneDirty(p.gameObject.scene);AssetDatabase.SaveAssets();EditorSceneManager.SaveScene(p.gameObject.scene);
        return "Prototype supply/fuel desks, zero-staff templates and daily wages saved.";
    }
    static Transform Post(Transform parent,string name,Vector3 position,string title)
    {
        var root=parent.Find(name);
        if(root!=null)return root;
        var go=new GameObject(name);Undo.RegisterCreatedObjectUndo(go,"Prototype service desk");
        root=go.transform;root.SetParent(parent,true);root.position=position;
        var body=GameObject.CreatePrimitive(PrimitiveType.Cube);body.name="Desk";body.transform.SetParent(root,false);
        body.transform.localPosition=Vector3.up*.7f;body.transform.localScale=new Vector3(.55f,1.4f,.3f);
        body.GetComponent<Renderer>().sharedMaterial=AssetDatabase.LoadAssetAtPath<Material>("Assets/@Resource/Materials/MatFloorConcreteGrid.mat");
        var text=new GameObject("Label").AddComponent<TextMesh>();text.transform.SetParent(root,false);
        text.transform.localPosition=new Vector3(0,1.25f,-.17f);text.anchor=TextAnchor.MiddleCenter;text.alignment=TextAlignment.Center;
        text.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");text.GetComponent<MeshRenderer>().sharedMaterial=text.font.material;
        text.fontSize=40;text.characterSize=.035f;text.text=title+"\n[F]";text.color=new Color(1,.85f,.3f);
        return root;
    }
}
#endif
