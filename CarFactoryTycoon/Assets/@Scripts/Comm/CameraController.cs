using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>Close perspective by default. RMB/MMB pan, wheel zoom, Alt+LMB orbit, F reset, 1 perspective, 2 overview, V toggle.</summary>
public class CameraController : MonoBehaviour
{
    public Transform targetCamera;
    public FactoryExpansion factory;
    public float panSpeed=.25f,zoomSpeed=4f,zoomSmoothing=12f;
    public float minOrthoSize=3,maxOrthoSize=90,minDistance=8,maxDistance=100;
    public float orbitSpeed=200,minPitch=10,maxPitch=85;
    public Vector3 initialPivot=new Vector3(45.5f,0,35);
    public float initialYaw=35,initialPitch=45,initialDistance=25;
    public float overviewPitch=55,overviewYaw=35,overviewPadding=1.10f;
    [SerializeField] bool overview;
    Vector3 pivot;float yaw,pitch,distance,targetDistance,ortho,targetOrtho;
    int lastPrefix=-1;float lastAspect;
    Camera cam;
    public bool IsOverview=>overview;
    public Bounds ActiveBounds=>factory!=null?factory.ActiveBounds:new Bounds(initialPivot,new Vector3(30,5,20));
    void Resolve(){if(cam!=null)return;if(targetCamera==null){cam=GetComponent<Camera>();if(cam==null)cam=Camera.main;if(cam!=null)targetCamera=cam.transform;}else cam=targetCamera.GetComponent<Camera>();if(factory==null)factory=FindFirstObjectByType<FactoryExpansion>();}
    void Start(){Resolve();SetOverview(PrototypeLineController.Current!=null);}
    public void SetOverview(bool enabled)
    {
        Resolve();if(cam==null)return;overview=enabled;cam.orthographic=enabled;
        if(enabled)FitOverview();else ResetView();
    }
    public void ToggleOverview()=>SetOverview(!overview);
    public void ResetView()
    {
        Resolve();if(cam==null)return;
        if(overview){FitOverview();return;}
        pivot=CurrentFocus();yaw=initialYaw;pitch=initialPitch;distance=targetDistance=initialDistance;
        cam.orthographic=false;cam.fieldOfView=50;ApplyTransform();
    }
    Vector3 CurrentFocus(){if(factory==null||factory.progression==null)return initialPivot;var p=factory.progression;int i=Mathf.Clamp(factory.VisiblePrefix-1,0,p.processes.Length-1);return p.processes[i].humanRoot.transform.position+Vector3.up*.7f;}
    public void FocusProcess(int index)
    {
        Resolve();if(factory==null)return;var p=factory.progression;index=Mathf.Clamp(index,0,p.processes.Length-1);
        SetOverview(false);pivot=p.processes[index].humanRoot.transform.position+Vector3.up*.7f;ApplyTransform();
    }
    void FitOverview()
    {
        var b=ActiveBounds;pivot=b.center;yaw=overviewYaw;pitch=overviewPitch;distance=targetDistance=100;
        ortho=targetOrtho=FitSize(b,Quaternion.Euler(pitch,yaw,0),cam.aspect,overviewPadding);
        lastPrefix=factory!=null?factory.VisiblePrefix:0;lastAspect=cam.aspect;ApplyTransform();
    }
    public static float FitSize(Bounds b,Quaternion rotation,float aspect,float padding=1.1f)
    {
        var right=rotation*Vector3.right;var up=rotation*Vector3.up;var e=b.extents;
        float halfWidth=Mathf.Abs(right.x)*e.x+Mathf.Abs(right.y)*e.y+Mathf.Abs(right.z)*e.z;
        float halfHeight=Mathf.Abs(up.x)*e.x+Mathf.Abs(up.y)*e.y+Mathf.Abs(up.z)*e.z;
        return Mathf.Max(halfHeight,halfWidth/Mathf.Max(.1f,aspect))*padding;
    }
    /// <summary>Deterministic render pose without changing live camera controls or save state.</summary>
    public void ApplyCapturePose(Camera output,bool useOverview,int prefix,float aspect=16f/9f)
    {
        Resolve();output.clearFlags=CameraClearFlags.SolidColor;output.backgroundColor=new Color(168f/255,178f/255,177f/255);output.aspect=aspect;output.orthographic=useOverview;output.nearClipPlane=.1f;output.farClipPlane=300;
        Quaternion q=Quaternion.Euler(useOverview?overviewPitch:initialPitch,useOverview?overviewYaw:initialYaw,0);
        Vector3 center=initialPivot;float d=initialDistance;
        if(factory!=null){var p=factory.progression;int i=Mathf.Clamp(prefix-1,0,p.processes.Length-1);center=useOverview?factory.GetBoundsForPrefix(prefix).center:p.processes[i].humanRoot.transform.position+Vector3.up*.7f;}
        if(useOverview){d=100;output.orthographicSize=FitSize(factory!=null?factory.GetBoundsForPrefix(prefix):ActiveBounds,q,aspect,overviewPadding);}else output.fieldOfView=50;
        output.transform.SetPositionAndRotation(center-q*Vector3.forward*d,q);
    }
    void Update()
    {
        Resolve();if(cam==null)return;
        if(overview&&((factory!=null&&lastPrefix!=factory.VisiblePrefix)||Mathf.Abs(lastAspect-cam.aspect)>.001f))FitOverview();
        var m=Mouse.current;var k=Keyboard.current;
        if(k!=null){if(k.digit1Key.wasPressedThisFrame)SetOverview(false);if(k.digit2Key.wasPressedThisFrame)SetOverview(true);if(k.vKey.wasPressedThisFrame)ToggleOverview();if(k.fKey.wasPressedThisFrame)ResetView();}
        if(m!=null)
        {
            bool alt=k!=null&&(k.leftAltKey.isPressed||k.rightAltKey.isPressed);var delta=m.delta.ReadValue();
            if(alt&&m.leftButton.isPressed){yaw+=delta.x*orbitSpeed*Time.unscaledDeltaTime;pitch=Mathf.Clamp(pitch-delta.y*orbitSpeed*Time.unscaledDeltaTime,minPitch,maxPitch);}
            else if(m.rightButton.isPressed||m.middleButton.isPressed){var right=Vector3.ProjectOnPlane(targetCamera.right,Vector3.up).normalized;var forward=Vector3.ProjectOnPlane(targetCamera.forward,Vector3.up).normalized;float scale=(overview?ortho:distance)*panSpeed*.01f;pivot-=(right*delta.x+forward*delta.y)*scale;}
            float raw=m.scroll.ReadValue().y;float scroll=Mathf.Abs(raw)>=10?raw/120:raw;
            if(overview)targetOrtho=Mathf.Clamp(targetOrtho-scroll*zoomSpeed,minOrthoSize,maxOrthoSize);else targetDistance=Mathf.Clamp(targetDistance-scroll*zoomSpeed,minDistance,maxDistance);
        }
        float t=1-Mathf.Exp(-zoomSmoothing*Time.unscaledDeltaTime);ortho=Mathf.Lerp(ortho,targetOrtho,t);distance=Mathf.Lerp(distance,targetDistance,t);ApplyTransform();
    }
    void ApplyTransform(){var q=Quaternion.Euler(pitch,yaw,0);targetCamera.SetPositionAndRotation(pivot-q*Vector3.forward*distance,q);if(overview)cam.orthographicSize=ortho;}
}
