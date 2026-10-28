using UnityEngine;
using UnityEngine.AI;

/// <summary>Explicit NavMesh corner following with vehicle-aware local routes.
/// No automatic-agent steering is mixed with manual Move, and no warp/straight fallback is used.</summary>
[DisallowMultipleComponent]
public class NavMover : MonoBehaviour
{
    public bool useNavMesh=true, requireCompletePath=true;
    public float sampleRadius=1.5f;
    public float agentRadius=.35f,agentHeight=1.85f,baseOffset=.085f;
    public float acceleration=12f,angularSpeed=720f;
    [Range(0,99)] public int avoidancePriority=50;
    public float speed=2f,arriveRadius=.25f,turnSpeed=540f;
    public bool verboseLog;
    NavMeshAgent agent;
    PrototypeVehicleAvoidance avoidance;
    NavMeshPath route;
    readonly Vector3[] corners=new Vector3[128];
    int cornerCount,moveFrame=-1;
    Vector3 destination,sampledDestination;
    bool hasDestination;
    float nextRetry,currentSpeed;
    public float Speed {get=>speed;set=>speed=Mathf.Max(0,value);}
    public float CurrentSpeed=>moveFrame==Time.frameCount?currentSpeed:0;
    public bool IsWaitingToCross=>avoidance!=null&&avoidance.IsYielding;
    public bool UsingNavMesh=>agent!=null&&agent.enabled&&agent.isOnNavMesh;
    public string DiagnosticState=>$"nav={UsingNavMesh}, corners={cornerCount}, destination={sampledDestination}, speed={currentSpeed:F2}";
    void Awake()
    {
        route=new NavMeshPath();agent=GetComponent<NavMeshAgent>();
        avoidance=GetComponent<PrototypeVehicleAvoidance>();
        if(avoidance==null)avoidance=gameObject.AddComponent<PrototypeVehicleAvoidance>();
    }
    void Start()=>Initialize();
    bool Initialize()
    {
        if(!useNavMesh)return false;
        if(agent==null)agent=gameObject.AddComponent<NavMeshAgent>();
        agent.radius=agentRadius;agent.height=agentHeight;agent.baseOffset=baseOffset;
        agent.acceleration=acceleration;agent.angularSpeed=angularSpeed;
        agent.updateRotation=false;agent.autoTraverseOffMeshLink=false;
        agent.avoidancePriority=avoidancePriority;agent.enabled=true;
        if(!agent.isOnNavMesh)return false;
        agent.isStopped=true;agent.velocity=Vector3.zero;agent.ResetPath();
        return true;
    }
    public bool MoveTo(Vector3 target)
    {
        if(moveFrame==Time.frameCount)return false;
        moveFrame=Time.frameCount;currentSpeed=0;
        if(!UsingNavMesh)
        {
            if(Time.time>=nextRetry){nextRetry=Time.time+1;Initialize();}
            if(!UsingNavMesh)return false;
        }
        agent.isStopped=true;agent.velocity=Vector3.zero;
        if(!hasDestination||Flat(target-destination).sqrMagnitude>.0025f)
        {
            destination=target;hasDestination=true;nextRetry=0;cornerCount=0;
            agent.ResetPath();avoidance.ClearDetour();
        }
        // Refresh from the actual position, including deviations made around vehicles.
        // steeringTarget is intentionally unused: a stopped agent need not advance its path corridor.
        if(Time.time>=nextRetry)
        {
            nextRetry=Time.time+.6f;cornerCount=0;
            if(!NavMesh.SamplePosition(destination,out var hit,sampleRadius,agent.areaMask))return false;
            sampledDestination=hit.position;
            if(!NavMesh.CalculatePath(agent.nextPosition,sampledDestination,agent.areaMask,route)||route.status!=NavMeshPathStatus.PathComplete)return false;
            // Keep the native polygon corridor current even though movement/turn selection is manual.
            // Without a path, Move can clamp at a concave polygon corner and never traverse it.
            agent.SetPath(route);
            cornerCount=route.GetCornersNonAlloc(corners);
            if(cornerCount==corners.Length){cornerCount=0;return false;}
        }
        if(cornerCount==0)return false;
        var before=agent.nextPosition;
        if(Flat(before-sampledDestination).magnitude<=arriveRadius){Stop();return true;}
        // Consecutive native path corners are connected even at a concave boundary.
        // Skipping a corner 10cm early and requiring a new visibility ray can deadlock
        // a worker only 2cm from that corner. Finish each short segment instead.
        int corner=1;
        while(corner<cornerCount-1&&Flat(corners[corner]-before).magnitude<.02f)corner++;
        if(corner>=cornerCount){nextRetry=0;return false;}
        Vector3 goal=corners[corner];
        var delta=avoidance.SelectStep(agent,goal,sampledDestination,speed,agentRadius,Time.deltaTime);
        if(delta.sqrMagnitude>.000001f)agent.Move(delta);
        var moved=Flat(agent.nextPosition-before);
        currentSpeed=Time.deltaTime>0?moved.magnitude/Time.deltaTime:0;
        if(moved.sqrMagnitude>.000001f)FaceTowards(before+moved);
        return false;
    }
    void LateUpdate()
    {
        // Loading, unloading and idle workers must also step aside for an approaching vehicle.
        if(moveFrame==Time.frameCount||!UsingNavMesh||avoidance==null)return;
        var before=agent.nextPosition;
        var delta=avoidance.SelectStep(agent,before,before,Mathf.Max(.5f,speed),agentRadius,Time.deltaTime);
        if(delta.sqrMagnitude>.000001f){agent.Move(delta);FaceTowards(before+delta);}
    }
    public void Stop()
    {
        hasDestination=false;cornerCount=0;currentSpeed=0;
        if(UsingNavMesh){agent.isStopped=true;agent.velocity=Vector3.zero;agent.ResetPath();}
        if(avoidance!=null)avoidance.ClearDetour();
    }
    void OnDisable()=>Stop();
    public void FaceTowards(Vector3 point)
    {
        var d=Flat(point-transform.position);
        if(turnSpeed>0&&d.sqrMagnitude>.0001f)transform.rotation=Quaternion.RotateTowards(transform.rotation,Quaternion.LookRotation(d),turnSpeed*Time.deltaTime);
    }
    static Vector3 Flat(Vector3 v){v.y=0;return v;}
}
