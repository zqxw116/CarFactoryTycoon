using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;

/// <summary>Final preparation before dispatch: human nozzle handling, or a purchased automatic arm.</summary>
public class FuelStation : MonoBehaviour, ILineGate
{
    public enum FuelState { Idle, PickingUp, Approaching, Connecting, Fueling, Disconnecting, Returning, Parking, Resting }
    public SplineContainer mainLineSpline;
    [Range(0,1)] public float gateProgress;
    public Transform attendant;
    public NavMover attendantMover;
    public Animator attendantAnimator;
    public WorkerHandIK handIK;
    public Transform homePoint, nozzle, nozzleDock, hoseAnchor, hoseEnd;
    public LineRenderer hose;
    public GameObject automationRig;
    public Transform autoShoulder, upperArm, lowerArm, elbowJoint;
    public Transform fillBar;
    public Renderer statusLens;
    public Vector3 carPortLocal = new Vector3(1.13f,.95f,-1.1f);
    public float workerSideDistance = 1.95f;
    public float workerMoveSpeed = 1.8f;
    public float manualFuelSeconds = 4.5f;
    public float automaticFuelSeconds = 2f;
    public float forcedRestSeconds = 6f;
    public float conditionCostPerCar = 10f;
    [Range(0,100)] public float condition = 100f;
    [Min(1)] public int automationPrice = 1200;
    [Min(1)] public int speedBasePrice = 300;
    [SerializeField] bool automationPurchased;
    [SerializeField,Range(0,3)] int speedLevel;
    [SerializeField] FuelState currentState;
    [SerializeField] CarController currentCar;

    public FuelState CurrentState => currentState;
    public CarController CurrentCar => currentCar;
    public bool IsAutomated => automationPurchased;
    public int AutomationCost => Mathf.Max(1,automationPrice);
    public int SpeedLevel => speedLevel;
    public int MaxSpeedLevel => 3;
    public int SpeedUpgradeCost => Mathf.Max(1,Mathf.RoundToInt(Mathf.Max(1,speedBasePrice)*Mathf.Pow(1.7f,speedLevel)));
    public float FillProgress => currentState == FuelState.Fueling ? Mathf.Clamp01(1f-timer/Mathf.Max(.01f,duration)) :
        currentState == FuelState.Disconnecting ? 1f : 0f;
    public bool CanRest => !automationPurchased && currentState == FuelState.Idle && condition < 100f;
    public float GateProgress => gateProgress;
    public bool GateEnabled => isActiveAndEnabled;
    public string StatusText
    {
        get
        {
            if(automationPurchased && !automationActive) return "현재 작업 후 자동 전환";
            if(currentState == FuelState.Resting) return "작업자 휴식 중";
            if(currentState == FuelState.Fueling) return "주유 중 "+Mathf.RoundToInt(FillProgress*100)+"%";
            if(currentState != FuelState.Idle) return automationActive ? "자동 노즐 작동 중" : "작업자 노즐 처리 중";
            return automationActive ? "자동 주유 대기" : "작업자 대기 / 컨디션 "+Mathf.RoundToInt(condition)+"%";
        }
    }

    bool automationActive, cycleAutomated, visualsInitialized;
    float timer, duration, cycleSpeed, barWidth;
    Vector3 workPoint, moveFrom, barHome;
    Quaternion rotateFrom;
    readonly Vector3[] hosePoints=new Vector3[24];
    MaterialPropertyBlock block;
    static readonly int SpeedHash=Animator.StringToHash("Speed"),MotionHash=Animator.StringToHash("MotionSpeed"),GroundHash=Animator.StringToHash("Grounded");

    void OnEnable()
    {
        if(mainLineSpline==null){var go=GameObject.Find("MainSpline");if(go!=null)mainLineSpline=go.GetComponent<SplineContainer>();}
        if(mainLineSpline!=null){SplineUtility.GetNearestPoint(mainLineSpline.Spline,(float3)mainLineSpline.transform.InverseTransformPoint(transform.position),out _,out float t);gateProgress=t;}
        block=new MaterialPropertyBlock();
        if(!visualsInitialized && fillBar!=null){barWidth=fillBar.localScale.x;barHome=fillBar.localPosition;visualsInitialized=true;}
        currentState=FuelState.Idle;
        SetNozzleDock();ApplyAutomationIfReady();
        LineTrafficManager.Instance.RegisterGate(this);
    }

    void OnDisable()
    {
        CancelCycle();
        LineTrafficManager.Instance.UnregisterGate(this);
    }

    public bool TryPurchaseAutomation()
    {
        if(automationPurchased || !isActiveAndEnabled || !EconomyManager.Instance.TrySpend(AutomationCost))return false;
        automationPurchased=true;
        ApplyAutomationIfReady();
        return true;
    }

    public bool TryUpgradeSpeed()
    {
        if(speedLevel>=MaxSpeedLevel || !isActiveAndEnabled || !EconomyManager.Instance.TrySpend(SpeedUpgradeCost))return false;
        speedLevel++;
        return true;
    }

    public bool TryRestAttendant()
    {
        if(!CanRest)return false;
        Begin(FuelState.Resting,3f);
        return true;
    }

    void ApplyAutomationIfReady()
    {
        if(!automationPurchased || (currentState!=FuelState.Idle && currentState!=FuelState.Resting))return;
        automationActive=true;currentState=FuelState.Idle;timer=0;
        if(handIK!=null)handIK.Release();
        if(attendantMover!=null)attendantMover.Stop();
        if(attendant!=null)attendant.gameObject.SetActive(false);
        if(automationRig!=null)automationRig.SetActive(true);
        SetNozzleDock();
    }

    void Update()
    {
        bool handling=currentState>=FuelState.PickingUp && currentState<=FuelState.Disconnecting;
        if(handling && (currentCar==null || !currentCar.gameObject.activeInHierarchy)){CancelCycle();return;}
        timer-=Time.deltaTime;
        switch(currentState)
        {
            case FuelState.Idle:
                ApplyAutomationIfReady();Capture();break;
            case FuelState.PickingUp:
                FaceDock();BlendNozzle(CarryPosition(),CarryRotation());
                if(timer<=0)Begin(FuelState.Approaching,0);break;
            case FuelState.Approaching:
                CarryNozzle();
                if(MoveAttendant(workPoint)){FacePort();Begin(FuelState.Connecting,.65f/cycleSpeed);}break;
            case FuelState.Connecting:
                if(!cycleAutomated)FacePort();
                BlendNozzle(PortPosition(),PortRotation());
                if(timer<=0)Begin(FuelState.Fueling,(cycleAutomated?automaticFuelSeconds:manualFuelSeconds)/cycleSpeed);break;
            case FuelState.Fueling:
                if(!cycleAutomated)FacePort();
                if(nozzle!=null)nozzle.SetPositionAndRotation(PortPosition(),PortRotation());
                if(timer<=0)Begin(FuelState.Disconnecting,(cycleAutomated?.35f:.5f)/cycleSpeed);break;
            case FuelState.Disconnecting:
                BlendNozzle(cycleAutomated?nozzleDock.position:CarryPosition(),cycleAutomated?nozzleDock.rotation:CarryRotation());
                if(timer<=0)ReleaseCar();break;
            case FuelState.Returning:
                CarryNozzle();
                if(MoveAttendant(homePoint.position)){FaceDock();Begin(FuelState.Parking,.35f/cycleSpeed);}break;
            case FuelState.Parking:
                FaceDock();BlendNozzle(nozzleDock.position,nozzleDock.rotation);
                if(timer<=0)
                {
                    SetNozzleDock();if(handIK!=null)handIK.Release();
                    Begin(condition<=0&&!automationPurchased?FuelState.Resting:FuelState.Idle,forcedRestSeconds);
                    ApplyAutomationIfReady();
                }
                break;
            case FuelState.Resting:
                ApplyAutomationIfReady();
                if(currentState==FuelState.Resting && timer<=0){condition=100;Begin(FuelState.Idle,0);}break;
        }
        if(attendantAnimator!=null && attendant.gameObject.activeInHierarchy)
        {
            float speed=(currentState==FuelState.Approaching || currentState==FuelState.Returning) && attendantMover!=null?attendantMover.CurrentSpeed:0;
            attendantAnimator.SetFloat(SpeedHash,speed,.1f,Time.deltaTime);attendantAnimator.SetFloat(MotionHash,1);attendantAnimator.SetBool(GroundHash,true);
        }
        UpdateMeter();
    }

    void Capture()
    {
        if(mainLineSpline==null || nozzle==null || nozzleDock==null || homePoint==null)return;
        if(!automationActive && (attendant==null || attendantMover==null))return;
        var car=LineTrafficManager.Instance.GetCarAtGate(gateProgress,.1f);if(car==null)return;
        currentCar=car;car.isMoving=false;cycleAutomated=automationActive;cycleSpeed=1f+speedLevel*.25f;
        workPoint=car.transform.TransformPoint(new Vector3(workerSideDistance,0,carPortLocal.z));workPoint.y=homePoint.position.y;
        if(cycleAutomated)Begin(FuelState.Connecting,.4f/cycleSpeed);
        else
        {
            if(handIK!=null)handIK.Grip(nozzle,new Vector3(-.07f,-.07f,-.43f),new Vector3(.07f,-.06f,-.3f),Vector3.forward);
            Begin(FuelState.PickingUp,.35f/cycleSpeed);
        }
    }

    void Begin(FuelState state,float seconds)
    {
        currentState=state;timer=duration=Mathf.Max(.001f,seconds);
        if(nozzle!=null){moveFrom=nozzle.position;rotateFrom=nozzle.rotation;}
        if(attendantMover!=null && state!=FuelState.Approaching && state!=FuelState.Returning)attendantMover.Stop();
    }

    Vector3 PortPosition()=>currentCar.transform.TransformPoint(carPortLocal);
    Quaternion PortRotation()=>Quaternion.LookRotation(-currentCar.transform.right,Vector3.up);
    Vector3 CarryPosition()=>attendant.position+attendant.forward*.5f+Vector3.up*1.05f;
    Quaternion CarryRotation()=>Quaternion.LookRotation(attendant.forward,Vector3.up);
    void CarryNozzle(){if(nozzle!=null)nozzle.SetPositionAndRotation(CarryPosition(),CarryRotation());}
    void FacePort(){if(attendantMover!=null)attendantMover.FaceTowards(PortPosition());}
    void FaceDock(){if(attendantMover!=null && nozzleDock!=null)attendantMover.FaceTowards(nozzleDock.position);}
    bool MoveAttendant(Vector3 target){attendantMover.Speed=workerMoveSpeed*cycleSpeed;return attendantMover.MoveTo(target);}
    void BlendNozzle(Vector3 target,Quaternion rotation)
    {
        if(nozzle==null)return;float t=Mathf.SmoothStep(0,1,1-timer/Mathf.Max(.001f,duration));
        nozzle.SetPositionAndRotation(Vector3.Lerp(moveFrom,target,t),Quaternion.Slerp(rotateFrom,rotation,t));
    }
    void SetNozzleDock(){if(nozzle!=null && nozzleDock!=null)nozzle.SetPositionAndRotation(nozzleDock.position,nozzleDock.rotation);}

    void ReleaseCar()
    {
        if(currentCar!=null){currentCar.SetProgress(gateProgress);currentCar.isMoving=true;currentCar=null;}
        if(cycleAutomated){SetNozzleDock();Begin(FuelState.Idle,0);}
        else{condition=Mathf.Max(0,condition-conditionCostPerCar);Begin(FuelState.Returning,0);}
    }

    public void CancelCycle()
    {
        if(currentCar!=null && currentCar.gameObject.activeInHierarchy && currentCar.targetSpline!=null)
        {
            currentCar.SetProgress(gateProgress-.01f/Mathf.Max(.01f,currentCar.SplineLength));currentCar.isMoving=true;
        }
        currentCar=null;if(handIK!=null)handIK.Release();if(attendantMover!=null)attendantMover.Stop();
        SetNozzleDock();currentState=FuelState.Idle;timer=duration=0;
        if(attendant!=null && homePoint!=null)
        {
            var agent=attendant.GetComponent<UnityEngine.AI.NavMeshAgent>();
            if(agent!=null && agent.enabled && agent.isOnNavMesh)agent.Warp(homePoint.position);
            else attendant.position=homePoint.position;
        }
    }

    void LateUpdate()
    {
        if(hose!=null && hoseAnchor!=null && hoseEnd!=null)
        {
            Vector3 a=hoseAnchor.position,b=hoseEnd.position;
            for(int i=0;i<hosePoints.Length;i++)
            {
                float t=(float)i/(hosePoints.Length-1);float u=1-t;
                hosePoints[i]=u*u*u*a+3*u*u*t*(a+Vector3.up*.15f)+3*u*t*t*(b+Vector3.down*.3f)+t*t*t*b+Vector3.down*(Mathf.Sin(t*Mathf.PI)*.45f);
            }
            hose.positionCount=hosePoints.Length;hose.SetPositions(hosePoints);
        }
        if(automationActive && autoShoulder!=null && nozzle!=null)
        {
            Vector3 end=nozzle.TransformPoint(new Vector3(0,-.08f,-.45f));
            Vector3 elbow=(autoShoulder.position+end)*.5f+Vector3.up*.5f;
            Beam(upperArm,autoShoulder.position,elbow);Beam(lowerArm,elbow,end);
            if(elbowJoint!=null)elbowJoint.position=elbow;
        }
    }

    static void Beam(Transform beam,Vector3 a,Vector3 b)
    {
        if(beam==null)return;Vector3 delta=b-a;beam.position=(a+b)*.5f;
        if(delta.sqrMagnitude>.0001f)beam.rotation=Quaternion.FromToRotation(Vector3.up,delta.normalized);
        beam.localScale=new Vector3(.15f,Mathf.Max(.02f,delta.magnitude),.15f);
    }

    void UpdateMeter()
    {
        float p=FillProgress;
        if(fillBar!=null){var scale=fillBar.localScale;scale.x=barWidth*Mathf.Max(.01f,p);fillBar.localScale=scale;fillBar.localPosition=barHome-Vector3.right*barWidth*(1-p)*.5f;}
        if(statusLens==null)return;
        Color color=currentState==FuelState.Fueling?new Color(.1f,1,.45f):currentState==FuelState.Resting?new Color(.3f,.5f,1):currentState==FuelState.Idle?new Color(.12f,.2f,.2f):new Color(1,.62f,.1f);
        statusLens.GetPropertyBlock(block);block.SetColor("_BaseColor",color);block.SetColor("_EmissionColor",color*2);statusLens.SetPropertyBlock(block);
    }

    void OnDrawGizmosSelected(){Gizmos.color=Color.green;Gizmos.DrawWireCube(transform.position+Vector3.up*.7f,new Vector3(5.4f,1.4f,2.5f));}
}
