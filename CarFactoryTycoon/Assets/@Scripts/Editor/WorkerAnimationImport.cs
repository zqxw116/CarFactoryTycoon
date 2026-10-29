#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

// Only animation downloads are configured here. Authored character model importers are untouched.
public static class WorkerAnimationImport
{
    public const string Folder = "Assets/@Resource/Animations/Characters/Worker/";
    public static string Apply()
    {

        int count=0;
        foreach(var guid in AssetDatabase.FindAssets("t:Model",new[]{Folder.TrimEnd('/')}))
        {
            string path=AssetDatabase.GUIDToAssetPath(guid);
            var importer=AssetImporter.GetAtPath(path) as ModelImporter;
            if(importer==null || !System.IO.Path.GetFileName(path).StartsWith("Worker_"))continue;
            importer.importAnimation=true; importer.animationType=ModelImporterAnimationType.Human;
            importer.sourceAvatar=null; importer.avatarSetup=ModelImporterAvatarSetup.CreateFromThisModel;
            importer.importCameras=false; importer.importLights=false;
            var clips=importer.clipAnimations.Length>0?importer.clipAnimations:importer.defaultClipAnimations;
            foreach(var clip in clips)
            {
                clip.name=System.IO.Path.GetFileNameWithoutExtension(path);
                clip.loopTime=!(path.Contains("Hammer") || path.Contains("Takeoff") || path.Contains("Land"));
                clip.lockRootRotation=true; clip.lockRootHeightY=!path.Contains("Jump_"); clip.lockRootPositionXZ=true;
                clip.keepOriginalOrientation=true; clip.heightFromFeet=true;
                clip.events=Array.Empty<AnimationEvent>();
            }
            importer.clipAnimations=clips; importer.SaveAndReimport(); count++;
        }
        return "Imported "+count+" Worker clips; Humanoid source Avatar; character retains Worker Avatar for retargeting; loop/root bake configured.";
    }
}
#endif
