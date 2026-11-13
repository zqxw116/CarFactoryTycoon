using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>Inspection camera. Never captures the cursor or uses simulation time.</summary>
[DefaultExecutionOrder(1000)]
public sealed class ProcessAuthoringCamera : MonoBehaviour
{
    public Camera view;
    public float moveSpeed=5f,lookSensitivity=.15f;
    public static bool AcceptsInput(bool focused,bool inside,bool dragging)=>focused&&inside&&dragging;
    void Awake(){if(view==null)view=Camera.main;ReleaseCursor();}
    public void Step(Vector2 look,Vector3 motion,float seconds,bool fast=false)
    {
        if(view==null)view=Camera.main;if(view==null)return;
        var angles=view.transform.eulerAngles;float pitch=Mathf.DeltaAngle(0,angles.x);
        view.transform.rotation=Quaternion.Euler(Mathf.Clamp(pitch-look.y*lookSensitivity,-85,85),angles.y+look.x*lookSensitivity,0);
        view.transform.position+=(view.transform.right*motion.x+Vector3.up*motion.y+view.transform.forward*motion.z)*moveSpeed*(fast?3f:1f)*Mathf.Max(0,seconds);
    }
    void LateUpdate()
    {
        ReleaseCursor();if(!Application.isPlaying||Mouse.current==null||Keyboard.current==null)return;
        bool focused=Application.isFocused,inside=true;
#if UNITY_EDITOR
        focused=UnityEditor.EditorWindow.focusedWindow!=null&&UnityEditor.EditorWindow.focusedWindow.GetType().Name=="GameView";
        inside=UnityEditor.EditorWindow.mouseOverWindow!=null&&UnityEditor.EditorWindow.mouseOverWindow.GetType().Name=="GameView";
#endif
        var mouse=Mouse.current;var keys=Keyboard.current;
        if(!AcceptsInput(focused,inside,mouse.rightButton.isPressed))return;
        var motion=new Vector3((keys.dKey.isPressed?1:0)-(keys.aKey.isPressed?1:0),(keys.eKey.isPressed?1:0)-(keys.qKey.isPressed?1:0),(keys.wKey.isPressed?1:0)-(keys.sKey.isPressed?1:0));
        Step(mouse.delta.ReadValue(),Vector3.ClampMagnitude(motion,1),Time.unscaledDeltaTime,keys.leftShiftKey.isPressed||keys.rightShiftKey.isPressed);
        float wheel=mouse.scroll.ReadValue().y;if(Mathf.Abs(wheel)>.01f)Step(Vector2.zero,new Vector3(0,0,wheel*.002f),1);
    }
    public void ReleaseCursor(){Cursor.lockState=CursorLockMode.None;Cursor.visible=true;}
    void OnApplicationFocus(bool focused){ReleaseCursor();}
    void OnDisable(){ReleaseCursor();}
}
