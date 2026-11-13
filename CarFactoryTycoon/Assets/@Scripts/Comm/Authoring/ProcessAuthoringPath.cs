using UnityEngine;

public static class ProcessAuthoringPath
{
    public static Vector3 Evaluate(AuthoringCurve curve, Quaternion frame, Vector3 from, Vector3 to, float t)
    {
        t=Mathf.Clamp01(t);float u=1-t;
        return u*u*u*from+3*u*u*t*(from+frame*curve.control1)+3*u*t*t*(to+frame*curve.control2)+t*t*t*to;
    }
    public static float Length(AuthoringCurve curve,Quaternion frame,Vector3 from,Vector3 to)
    {
        float length=0;var previous=from;
        for(int i=1;i<=16;i++){var point=Evaluate(curve,frame,from,to,i/16f);length+=Vector3.Distance(previous,point);previous=point;}
        return Mathf.Max(.01f,length);
    }
    public static bool Walkable(AuthoringCurve curve,Quaternion frame,Vector3 from,Vector3 to,out Vector3 blocked)
    {
        var previous=from;blocked=Vector3.zero;
        for(int i=1;i<=20;i++)
        {
            var point=Evaluate(curve,frame,from,to,i/20f);point.y=from.y;
            var delta=point-previous;
            if(delta.sqrMagnitude>.000001f && Physics.SphereCast(previous+Vector3.up*.85f,.23f,delta.normalized,out var hit,delta.magnitude,(1<<0)|(1<<11),QueryTriggerInteraction.Ignore))
            {blocked=hit.point;return false;}
            previous=point;
        }
        return true;
    }
}
