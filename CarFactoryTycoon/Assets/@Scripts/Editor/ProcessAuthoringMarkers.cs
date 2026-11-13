#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public static class ProcessAuthoringMarkers
{
    static readonly List<Rect> labels=new List<Rect>();
    public static void Begin(){labels.Clear();}
    public static Color ColorFor(string role)
    {
        switch(role){case "Home":return new Color(.35f,1,.45f);case "Wait":return new Color(1,.7f,.2f);case "Work":return new Color(1,.4f,.8f);case "Pickup":return Color.cyan;case "Stock":return new Color(.65f,.7f,1);case "Stop":return new Color(1,.45f,.3f);default:return Color.white;}
    }
    public static void Label(Vector3 point,string text,Color color)
    {
        var view=SceneView.currentDrawingSceneView;
        if(view==null||view.camera==null||view.camera.WorldToViewportPoint(point).z<0)return;
        var style=new GUIStyle(EditorStyles.miniBoldLabel);style.normal.textColor=color;
        var content=new GUIContent(text);var size=style.CalcSize(content);var screen=HandleUtility.WorldToGUIPoint(point);
        var rect=new Rect(screen.x+7,screen.y+3,size.x+10,19);
        for(int pass=0;pass<24;pass++)
        {
            bool overlap=false;foreach(var prior in labels)if(prior.Overlaps(rect)){overlap=true;break;}
            if(!overlap)break;rect.y+=20;
        }
        labels.Add(rect);Handles.BeginGUI();GUI.Box(rect,GUIContent.none,EditorStyles.helpBox);
        GUI.Label(new Rect(rect.x+5,rect.y+2,rect.width-8,rect.height-2),content,style);Handles.EndGUI();
    }
    public static void Point(Vector3 point,string text,Color color)
    {
        Handles.color=color;float size=HandleUtility.GetHandleSize(point)*.065f;
        Handles.SphereHandleCap(0,point,Quaternion.identity,size,EventType.Repaint);
        Label(point+Vector3.up*.1f,text,color);
    }
    public static void Draw(ProcessAuthoringSceneEditing editing)
    {
        if(editing==null)return;
        foreach(var item in editing.bindings)
        {
            if(item.target==null)continue;var color=ColorFor(item.role);
            if(!item.target.gameObject.activeInHierarchy)color*=.65f;
            Point(item.target.position,item.label+(!item.target.gameObject.activeInHierarchy?" (비활성)":""),color);
        }
    }
}
#endif
