#if UNITY_EDITOR
using System;
using System.Collections;
using UnityEngine;
using Object=UnityEngine.Object;

public static class FactoryWorldTextDepthChecks
{
    public static string Result="idle";
    public static void Start()
    {
        if(!Application.isPlaying)throw new InvalidOperationException("Play required");
        if(Result=="running")return;
        Result="running";
        Object.FindFirstObjectByType<FactoryPlayerController>().StartCoroutine(Run());
    }
    static IEnumerator Run()
    {
        var root=new GameObject("WorldTextDepthProbe");root.transform.position=new Vector3(900,1000,900);
        var cameraNode=new GameObject("Camera");cameraNode.transform.SetParent(root.transform,false);
        cameraNode.transform.localPosition=new Vector3(0,1,0);
        var camera=cameraNode.AddComponent<Camera>();camera.orthographic=true;camera.orthographicSize=2.3f;
        camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;camera.cullingMask=1<<30;
        camera.enabled=false;
        var target=new RenderTexture(256,256,24);camera.targetTexture=target;
        var pixels=new Texture2D(256,256,TextureFormat.RGB24,false);
        var labelNode=new GameObject("Index");labelNode.transform.SetParent(root.transform,false);labelNode.layer=30;
        labelNode.transform.localPosition=new Vector3(0,1,4);
        var label=labelNode.AddComponent<TextMesh>();label.font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.fontSize=64;label.characterSize=.2f;label.text="888888";label.anchor=TextAnchor.MiddleCenter;label.color=Color.white;
        label.font.RequestCharactersInTexture(label.text,label.fontSize,FontStyle.Normal);WorldTextDepth.Apply(label);
        var prefab=UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>("Assets/@Resource/Prefabs/Characters/Worker_Base.prefab");
        var actor=Object.Instantiate(prefab,root.transform);actor.name="CharacterOccluder";
        actor.transform.localPosition=new Vector3(0,0,2.7f);actor.transform.localRotation=Quaternion.Euler(0,180,0);
        foreach(var script in actor.GetComponentsInChildren<MonoBehaviour>(true))script.enabled=false;
        foreach(var animator in actor.GetComponentsInChildren<Animator>(true))animator.enabled=false;
        foreach(var node in actor.GetComponentsInChildren<Transform>(true))node.gameObject.layer=30;
        var block=new MaterialPropertyBlock();block.SetColor("_BaseColor",Color.red);
        foreach(var renderer in actor.GetComponentsInChildren<Renderer>(true))renderer.SetPropertyBlock(block);
        actor.SetActive(false);yield return null;
        Exception error=null;int before=0,after=0;
        try{before=ReadWhite(camera,target,pixels);}catch(Exception e){error=e;}
        actor.SetActive(true);yield return null;
        try{after=ReadWhite(camera,target,pixels);}catch(Exception e){error=e;}
        Result=error!=null?"FAIL "+error:before>0 && after>0 && after<before?
            "PASS actual character occludes font pixels: "+before+" -> "+after+"; uncovered text remains visible":
            "FAIL occlusion pixels before="+before+" after="+after;
        camera.targetTexture=null;Object.Destroy(root);Object.Destroy(target);Object.Destroy(pixels);
        Debug.Log(Result);
    }
    static int ReadWhite(Camera camera,RenderTexture target,Texture2D pixels)
    {
        camera.Render();var old=RenderTexture.active;RenderTexture.active=target;
        pixels.ReadPixels(new Rect(0,0,256,256),0,0);pixels.Apply();RenderTexture.active=old;
        int count=0;foreach(var c in pixels.GetPixels32())if(c.r>210 && c.g>210 && c.b>210)count++;
        return count;
    }
}
#endif
