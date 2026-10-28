using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>Predictive yielding plus complete local visibility-graph routes around vehicle footprints.
/// A route includes the far side of a blocker, instead of repeatedly picking its nearest corner.</summary>
[DisallowMultipleComponent]
public class PrototypeVehicleAvoidance : MonoBehaviour
{
    public float predictionTime=1.2f,safetyMargin=.2f,yieldBeforeDetour=.6f;
    const int Capacity=128;
    readonly Collider[] nearby=new Collider[Capacity];
    readonly Bounds[] bounds=new Bounds[Capacity];
    readonly Vector3[] velocities=new Vector3[Capacity];
    readonly Vector3[] nodes=new Vector3[Capacity],path=new Vector3[Capacity],staticCorners=new Vector3[Capacity];
    readonly float[] costs=new float[Capacity];
    readonly int[] parent=new int[Capacity];
    readonly bool[] visited=new bool[Capacity];
    NavMeshPath staticPath;
    static Vector3[] sceneCorners;
    readonly Vector3[] localSceneNodes=new Vector3[64];
    readonly float[] localSceneScores=new float[64];
    struct Observation{public Vector3 position;public float time;}
    readonly Dictionary<int,Observation> observations=new Dictionary<int,Observation>();
    int count,nodeCount,pathCount,pathIndex;
    float blockedFor,nextPlan;
    Vector3 plannedGoal;
    public bool IsYielding{get;private set;}
    public string DiagnosticState=>$"cars={count}, yielding={IsYielding}, route={pathIndex}/{pathCount}, blocked={blockedFor:F2}s, waypoint={(pathIndex<pathCount?path[pathIndex]:plannedGoal)}";
    void Awake()=>staticPath=new NavMeshPath();
    public void ClearDetour(){pathCount=0;pathIndex=0;blockedFor=0;nextPlan=0;IsYielding=false;}
    public Vector3 SelectStep(NavMeshAgent agent,Vector3 pathGoal,Vector3 finalGoal,float speed,float radius,float dt)
    {
        if(dt<=0||speed<=0)return Vector3.zero;
        var p=agent.nextPosition;count=0;
        float lineSpeed=PrototypeLineController.Current!=null?PrototypeLineController.Current.CurrentSpeed:1f;
        float sensingRadius=Mathf.Max(6f,(speed+lineSpeed)*predictionTime+3f);
        int found=Physics.OverlapSphereNonAlloc(p,sensingRadius,nearby,1<<11,QueryTriggerInteraction.Ignore);
        if(found==nearby.Length){IsYielding=true;return Vector3.zero;}
        for(int i=0;i<found;i++)
        {
            var c=nearby[i];var b=c.bounds;
            if(b.max.y<p.y-.1f||b.min.y>p.y+agent.height)continue;
            var car=c.GetComponentInParent<CarController>();
            var v=car!=null&&car.IsDriving?car.transform.forward*Mathf.Max(0,car.moveSpeed):Vector3.zero;
            int id=c.GetInstanceID();
            if(observations.TryGetValue(id,out var observation))
            {
                float elapsed=Time.time-observation.time;
                if(elapsed>0&&elapsed<.25f)v=(b.center-observation.position)/elapsed;
            }
            observations[id]=new Observation{position=b.center,time=Time.time};
            v.y=0;b.Expand(new Vector3((radius+safetyMargin)*2,0,(radius+safetyMargin)*2));
            bounds[count]=b;velocities[count++]=v;
        }
        var direct=Flat(pathGoal-p);
        bool directSafe=Safe(p,direct.normalized*speed,Mathf.Max(dt,Mathf.Min(predictionTime,direct.magnitude/speed)),out _);
        // NavMover supplies a connected native path segment. Boundary visibility rays can
        // report a hit at its shared corner; the native Move still clamps to walkable space.
        bool directVisible=SegmentClear(p,pathGoal);
        if(!directSafe||!directVisible)blockedFor+=dt;else blockedFor=0;
        Vector3 goal=pathGoal;
        if(directVisible&&directSafe){pathCount=0;pathIndex=0;}
        else
        {
            if(Time.time>=nextPlan||Flat(plannedGoal-pathGoal).sqrMagnitude>.25f)
            {
                plannedGoal=pathGoal;nextPlan=Time.time+.7f;
                BuildRoute(agent,p,InsideAny(pathGoal)?finalGoal:pathGoal);
            }
            while(pathIndex<pathCount&&Flat(path[pathIndex]-p).magnitude<.18f)
            {
                if(pathIndex+1<pathCount && (!SegmentClear(p,path[pathIndex+1]) || NavMesh.Raycast(p,path[pathIndex+1],out _,agent.areaMask)) && Flat(path[pathIndex]-p).magnitude>.025f)break;
                pathIndex++;
            }
            if(pathIndex<pathCount)goal=path[pathIndex];
        }
        bool threatened=!Safe(p,Vector3.zero,Mathf.Max(dt,predictionTime),out _);
        bool haveRoute=directVisible||pathIndex<pathCount;
        // Without a complete side route, wait and replan. Arbitrary sideways/backward wandering
        // is reserved for imminent collision, so it cannot oscillate forever around a stopped car.
        if(!threatened&&(!haveRoute||(!directSafe&&blockedFor<yieldBeforeDetour)))
        {IsYielding=true;return Vector3.zero;}
        var desired=Flat(goal-p).normalized*speed;
        if(threatened&&desired.sqrMagnitude<.0001f)desired=transform.forward*speed;
        Vector3 best=Vector3.zero;float bestScore=float.NegativeInfinity;
        float distance=threatened?speed*dt:Mathf.Min(speed*dt,Flat(goal-p).magnitude);
        for(int i=0;i<21;i++)
        {
            float angle=i==0?0:((i+1)/2)*22.5f*(i%2==0?-1:1);
            // A complete planned route takes priority. Don't replace it with a looping retreat
            // when a passing car temporarily blocks its first segment.
            if(!threatened&&i>0)break;
            var velocity=i<17?Quaternion.AngleAxis(angle,Vector3.up)*desired:
                (i==17?Vector3.forward:i==18?Vector3.back:i==19?Vector3.right:Vector3.left)*speed;
            var end=p+velocity.normalized*distance;
            // The selected native/visibility-graph segment is already validated. Agent.Move
            // performs the final NavMesh clamp, including crossing shared polygon corners.
            // Prediction stops at the waypoint; extrapolating through the turn falsely blocks
            // valid corner routes and was another source of endless corner oscillation.
            float horizon=threatened?predictionTime:Mathf.Max(dt,Mathf.Min(predictionTime,Flat(goal-p).magnitude/speed));
            if(!Safe(p,velocity,horizon,out _))continue;
            float score=Vector3.Dot(velocity.normalized,desired.normalized)-.08f*Mathf.Abs(angle)/180;
            if(score>bestScore){bestScore=score;best=velocity.normalized*distance;}
        }
        IsYielding=best.sqrMagnitude<.000001f;
        return best;
    }
    void BuildRoute(NavMeshAgent agent,Vector3 start,Vector3 goal)
    {
        pathCount=0;pathIndex=0;nodeCount=2;nodes[0]=start;nodes[1]=goal;
        // Every side of every sensed body participates in the same route search.
        for(int i=0;i<count&&nodeCount<Capacity-4;i++)
        {
            var b=bounds[i];const float pad=.35f;
            // A narrow inter-car gap needs an axis-aligned exit before it can turn.
            // Corners alone require a diagonal that clips the adjacent car.
            AddReachableNode(agent,start,new Vector3(start.x,start.y,b.min.z-pad));
            AddReachableNode(agent,start,new Vector3(start.x,start.y,b.max.z+pad));
            AddReachableNode(agent,start,new Vector3(b.min.x-pad,start.y,start.z));
            AddReachableNode(agent,start,new Vector3(b.max.x+pad,start.y,start.z));
            for(int x=-1;x<=1;x+=2)for(int z=-1;z<=1;z+=2)
            {
                var candidate=new Vector3(x<0?b.min.x-pad:b.max.x+pad,start.y,z<0?b.min.z-pad:b.max.z+pad);
                if(!NavMesh.SamplePosition(candidate,out var hit,.7f,agent.areaMask)||InsideAny(hit.position))continue;
                AddNode(hit.position);
                // Preserve paths around curved/static geometry rather than demanding direct access
                // from the start to every vehicle corner.
                if(NavMesh.Raycast(start,hit.position,out _,agent.areaMask)&&
                    NavMesh.CalculatePath(start,hit.position,agent.areaMask,staticPath)&&staticPath.status==NavMeshPathStatus.PathComplete)
                {
                    int n=staticPath.GetCornersNonAlloc(staticCorners);
                    for(int k=1;k<n-1;k++)if(!InsideAny(staticCorners[k]))AddNode(staticCorners[k]);
                }
            }
        }
        AddSceneCorners(start,goal);
        for(int i=0;i<nodeCount;i++){costs[i]=float.PositiveInfinity;parent[i]=-1;visited[i]=false;}
        costs[0]=0;
        for(int iteration=0;iteration<nodeCount;iteration++)
        {
            int current=-1;float best=float.PositiveInfinity;
            for(int i=0;i<nodeCount;i++)if(!visited[i]&&costs[i]<best){best=costs[i];current=i;}
            if(current<0)break;if(current==1)break;visited[current]=true;
            for(int next=0;next<nodeCount;next++)
            {
                if(visited[next]||next==current)continue;
                float cost=best+Flat(nodes[next]-nodes[current]).magnitude;
                if(cost>=costs[next]||!SegmentClear(nodes[current],nodes[next]))continue;
                if(NavMesh.Raycast(nodes[current],nodes[next],out _,agent.areaMask))continue;
                costs[next]=cost;parent[next]=current;
            }
        }
        int destination=1;
        if(parent[1]<0)
        {
            // A complete route can genuinely be blocked by stopped cars and equipment.
            // First leave a narrow traffic gap for a reachable safer pocket, then wait/replan.
            float initial=Clearance(start),bestScore=float.NegativeInfinity;destination=-1;
            for(int i=2;i<nodeCount;i++)
            {
                if(float.IsInfinity(costs[i])||Flat(nodes[i]-start).magnitude<.5f)continue;
                float clearance=Clearance(nodes[i]);if(clearance<initial+.12f)continue;
                float score=Mathf.Min(1.5f,clearance)*2f-costs[i]*.12f-Flat(nodes[i]-goal).magnitude*.02f;
                if(score>bestScore){bestScore=score;destination=i;}
            }
            if(destination<0)return;
        }
        int index=destination;
        while(index!=0&&index>=0&&pathCount<Capacity){path[pathCount++]=nodes[index];index=parent[index];}
        System.Array.Reverse(path,0,pathCount);
    }
    void AddSceneCorners(Vector3 start,Vector3 goal)
    {
        // Vehicle corners alone cannot route around a nearby stock pile or wall. Use the
        // already-eroded NavMesh polygon corners too, shared by every worker in this scene.
        if(sceneCorners==null)
        {
            var mesh=NavMesh.CalculateTriangulation();sceneCorners=new Vector3[mesh.indices.Length];
            for(int i=0;i<mesh.indices.Length;i+=3)
            {
                Vector3 a=mesh.vertices[mesh.indices[i]],b=mesh.vertices[mesh.indices[i+1]],c=mesh.vertices[mesh.indices[i+2]];
                Vector3 center=(a+b+c)/3;
                sceneCorners[i]=a+(center-a).normalized*.035f;
                sceneCorners[i+1]=b+(center-b).normalized*.035f;
                sceneCorners[i+2]=c+(center-c).normalized*.035f;
            }
        }
        int used=0,limit=Mathf.Min(localSceneNodes.Length,Capacity-nodeCount);
        if(limit<=0)return;
        float radius=Mathf.Max(10f,Flat(goal-start).magnitude+7f);
        foreach(var point in sceneCorners)
        {
            if(Mathf.Abs(point.y-start.y)>1f||Flat(point-start).sqrMagnitude>radius*radius||InsideAny(point))continue;
            float score=Flat(point-start).magnitude+Flat(point-goal).magnitude;
            if(used==limit&&score>=localSceneScores[used-1])continue;
            bool duplicate=false;for(int j=0;j<used;j++)if(Flat(localSceneNodes[j]-point).sqrMagnitude<.01f){duplicate=true;break;}
            if(duplicate)continue;
            int at=Mathf.Min(used,limit-1);
            while(at>0&&localSceneScores[at-1]>score){if(at<limit){localSceneScores[at]=localSceneScores[at-1];localSceneNodes[at]=localSceneNodes[at-1];}at--;}
            localSceneScores[at]=score;localSceneNodes[at]=point;if(used<limit)used++;
        }
        for(int i=0;i<used;i++)AddNode(localSceneNodes[i]);
    }
    void AddReachableNode(NavMeshAgent agent,Vector3 start,Vector3 candidate)
    {
        if(!NavMesh.SamplePosition(candidate,out var hit,.7f,agent.areaMask)||InsideAny(hit.position))return;
        AddNode(hit.position);
        if(NavMesh.Raycast(start,hit.position,out _,agent.areaMask)&&NavMesh.CalculatePath(start,hit.position,agent.areaMask,staticPath)&&staticPath.status==NavMeshPathStatus.PathComplete)
        {
            int n=staticPath.GetCornersNonAlloc(staticCorners);
            for(int k=1;k<n-1;k++)if(!InsideAny(staticCorners[k]))AddNode(staticCorners[k]);
        }
    }
    void AddNode(Vector3 p)
    {
        if(nodeCount>=Capacity)return;
        for(int i=0;i<nodeCount;i++)if(Flat(nodes[i]-p).sqrMagnitude<.01f)return;
        nodes[nodeCount++]=p;
    }
    float Clearance(Vector3 p)
    {
        float best=float.PositiveInfinity;
        for(int i=0;i<count;i++)
        {
            var b=bounds[i];float dx=Mathf.Max(Mathf.Max(b.min.x-p.x,0f),p.x-b.max.x),dz=Mathf.Max(Mathf.Max(b.min.z-p.z,0f),p.z-b.max.z);
            best=Mathf.Min(best,Mathf.Sqrt(dx*dx+dz*dz));
        }
        return best;
    }
    bool InsideAny(Vector3 p)
    {
        for(int i=0;i<count;i++){var b=bounds[i];if(p.x>b.min.x&&p.x<b.max.x&&p.z>b.min.z&&p.z<b.max.z)return true;}
        return false;
    }
    bool SegmentClear(Vector3 a,Vector3 b)
    {
        var d=Flat(b-a);
        for(int i=0;i<count;i++)
        {
            var box=bounds[i];
            // An already-overlapped start can leave a footprint but cannot terminate inside it.
            bool inside=a.x>box.min.x&&a.x<box.max.x&&a.z>box.min.z&&a.z<box.max.z;
            if(inside)
            {if(b.x>box.min.x&&b.x<box.max.x&&b.z>box.min.z&&b.z<box.max.z)return false;continue;}
            float enter=0,exit=1;
            if(Slab(a.x,d.x,box.min.x,box.max.x,ref enter,ref exit)&&Slab(a.z,d.z,box.min.z,box.max.z,ref enter,ref exit))return false;
        }
        return true;
    }
    bool Safe(Vector3 p,Vector3 velocity,float horizon,out int blocker)
    {
        blocker=-1;
        for(int i=0;i<count;i++)
        {
            var b=bounds[i];var relative=velocity-velocities[i];
            bool inside=p.x>b.min.x&&p.x<b.max.x&&p.z>b.min.z&&p.z<b.max.z;
            if(inside)
            {
                // A common sideways exit between two bodies may not be either body's nearest edge.
                float escape=float.PositiveInfinity;
                if(relative.x<-.05f)escape=Mathf.Min(escape,(p.x-b.min.x)/-relative.x);
                if(relative.x>.05f)escape=Mathf.Min(escape,(b.max.x-p.x)/relative.x);
                if(relative.z<-.05f)escape=Mathf.Min(escape,(p.z-b.min.z)/-relative.z);
                if(relative.z>.05f)escape=Mathf.Min(escape,(b.max.z-p.z)/relative.z);
                if(escape<=Mathf.Max(horizon,predictionTime))continue;
                blocker=i;return false;
            }
            float enter=0,exit=horizon;
            if(Slab(p.x,relative.x,b.min.x,b.max.x,ref enter,ref exit)&&Slab(p.z,relative.z,b.min.z,b.max.z,ref enter,ref exit)){blocker=i;return false;}
        }
        return true;
    }
    static bool Slab(float p,float v,float min,float max,ref float enter,ref float exit)
    {
        if(Mathf.Abs(v)<.0001f)return p>=min&&p<=max;
        float a=(min-p)/v,b=(max-p)/v;if(a>b){float t=a;a=b;b=t;}
        enter=Mathf.Max(enter,a);exit=Mathf.Min(exit,b);return enter<=exit;
    }
    static Vector3 Flat(Vector3 v){v.y=0;return v;}
}
