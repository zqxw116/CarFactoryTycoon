using System;
using UnityEngine;

[Serializable]
public sealed class ProcessStockPacking
{
    public bool enabled,curve,centerSpacing;
    public int columns=6,layers=2;
    public Vector3 gap=new Vector3(.08f,.08f,.015f);
    public Vector3 end,control1,control2,offset,rowOffset,layerOffset;
    public int Signature()
    {
        unchecked
        {
            int h=columns*397^layers;
            h=h*397^gap.GetHashCode();h=h*397^end.GetHashCode();h=h*397^control1.GetHashCode();h=h*397^control2.GetHashCode();
            h=h*397^offset.GetHashCode();h=h*397^rowOffset.GetHashCode();h=h*397^layerOffset.GetHashCode();
            return h*397^(curve?1:0)^(centerSpacing?2:0);
        }
    }
}
public static class StockPackingGeometry
{
    public static Vector3 Curve(Vector3 a,Vector3 b,Vector3 c,Vector3 d,float t)
    {float u=1-t;return u*u*u*a+3*u*u*t*b+3*u*t*t*c+t*t*t*d;}
    public static Vector3 Along(Vector3 start,Vector3 end,Vector3 c1,Vector3 c2,bool curve,float distance)
    {
        var direction=end-start;if(direction.sqrMagnitude<.000001f)direction=Vector3.right;
        if(!curve)return start+direction.normalized*distance;
        Vector3 previous=start;
        for(int i=1;i<=96;i++)
        {
            var next=Curve(start,c1,c2,end,i/96f);float length=Vector3.Distance(previous,next);
            if(distance<=length)return Vector3.Lerp(previous,next,length>.000001f?distance/length:0);
            distance-=length;previous=next;
        }
        var tangent=end-c2;if(tangent.sqrMagnitude<.000001f)tangent=direction;
        return end+tangent.normalized*distance;
    }
}
