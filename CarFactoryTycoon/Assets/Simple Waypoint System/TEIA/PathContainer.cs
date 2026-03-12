using System.Collections.Generic;
using UnityEngine;
using System;
using SWS;

public class PathContainer : MonoSingleton<PathContainer>
{
    public enum Direction
    {
        None = 0,
        Forward,
        Reverse,
    };

    public struct PathPointInfo
    {
        public Vector3 v3;
        public int globalIndex;
        public int localIndex; 
        public string lineCd;
    };

    // 모든 패스 포인트 리스트
    private List<PathPointInfo> allPathPointList = new List<PathPointInfo>();
    Dictionary<string, PathManager> pathContainer = new Dictionary<string, PathManager>();
    public Dictionary<int, List<PathPointInfo>> pathPointDict = new Dictionary<int, List<PathPointInfo>>();

    public void Init()
    {
        BezierPathManager[] bezierPathArr = GameObject.FindGameObjectWithTag("WayPoint").GetComponentsInChildren<BezierPathManager>();
        for (int i = 0; i < bezierPathArr.Length; i++)
        {
            bezierPathArr[i].Initiazlie();
        }
        pathContainer = WaypointManager.Paths;
        ConvertPathPoint();
    }
    private void ConvertPathPoint()
    {
        int pathIndex = 0;

        foreach (KeyValuePair<string, PathManager> pm in pathContainer) 
        {
            string pathName = pm.Value.name;
            Vector3[] v3Array = pm.Value.GetPathPoints(); 

            for (int i = 0; i < v3Array.Length; i++)
            {
                PathPointInfo pi = new PathPointInfo(); 
                pi.globalIndex = pathIndex++;   
                pi.localIndex = i;              
                pi.lineCd = pathName;           
                pi.v3 = v3Array[i];

                List<PathPointInfo> pathPointList = null;
                int intX = (int)Math.Truncate(pi.v3.x);

                if (pathPointDict.ContainsKey(intX)){
                    pathPointList = pathPointDict[intX];
                }
                else
                {
                    pathPointList = new List<PathPointInfo>();
                    pathPointDict.Add(intX, pathPointList);
                }

                pathPointList.Add(pi);
                allPathPointList.Add(pi);
            }
        }
    }
    public PathManager GetpathContainer(string lineName)
    {
        if (pathContainer.ContainsKey(lineName))
        {
            return pathContainer[lineName];
        }
        return null;
    }

    /// <summary>
    /// 넘겨받은 좌표 기준 가장 가까운 세그먼트의 위치
    /// </summary>
    public (string lineCd, int globalIndex, int localindex) GetClosePathPointInfo(Vector3 v3)
    {
        //PathPointInfo? pointInfo = null;
        PathPointInfo pointInfo = new PathPointInfo();

        int intX = (int)Math.Truncate(v3.x);
        float minDistance = 99999f;

        Compare(v3, intX - 1, ref pointInfo, ref minDistance);
        Compare(v3, intX, ref pointInfo, ref minDistance);
        Compare(v3, intX + 1, ref pointInfo, ref minDistance);

        return (pointInfo.lineCd, pointInfo.globalIndex, pointInfo.localIndex);
    }


    public int GetGlobalIndex(Vector3 v3)
    {
        //PathPointInfo? pointInfo = null;
        PathPointInfo pointInfo = new PathPointInfo();

        int intX = (int)Math.Truncate(v3.x);
        float minDistance = 99999f;

        Compare(v3, intX - 1, ref pointInfo, ref minDistance);
        Compare(v3, intX, ref pointInfo, ref minDistance);
        Compare(v3, intX + 1, ref pointInfo, ref minDistance);

        return pointInfo.globalIndex;
    }

    public (string lineCd, int globalIndex, int localIndex) GetPointInfo(Vector3 v3)
    {
        //PathPointInfo? pointInfo = null;
        PathPointInfo pointInfo = new PathPointInfo();

        int intX = (int)Math.Truncate(v3.x);
        float minDistance = 99999f;

        Compare(v3, intX - 1, ref pointInfo, ref minDistance);
        Compare(v3, intX, ref pointInfo, ref minDistance);
        Compare(v3, intX + 1, ref pointInfo, ref minDistance);

        return (pointInfo.lineCd, pointInfo.globalIndex, pointInfo.localIndex);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="v3"></param>
    /// <param name="index"></param>
    /// <param name="resultPoint"></param>
    /// <param name="minDistance"></param>
    //private void Compare(Vector3 v3, int index, ref PathPointInfo? resultPoint, ref float minDistance)
    private void Compare(Vector3 v3, int index, ref PathPointInfo resultPoint, ref float minDistance)
    {
        if (pathPointDict.ContainsKey(index)){

            foreach (PathPointInfo point in pathPointDict[index]){
                float distance = (point.v3 - v3).sqrMagnitude;
                if (distance < 9f){
                    if (minDistance > distance){
                        resultPoint = point;
                        minDistance = distance;
                    }
                }
            }
        }
    }

    /// <summary>
    /// 기준점 기준 넘겨받은 거리만큼 떨어진 위치에 있는 가장 가까운 세그먼트를 찾는다. (곡선, 직선 상관없음)
    /// </summary>
    public void GetClosestPoint(int standardPoint, Direction type, float distance, ref PathPointInfo? result)
    {
        if (allPathPointList.Count <= standardPoint){
            Debug.Log("You need to check the Index Number");
            return;
        }

        float total = 0.0f;
        Vector3 originCod = allPathPointList[standardPoint].v3;

        if (type.Equals(Direction.Forward)){
            for (int i = standardPoint + 1; i < allPathPointList.Count; i++)
            {
                total += Mathf.Sqrt((allPathPointList[i].v3 - originCod).sqrMagnitude);
                originCod = allPathPointList[i].v3;

                if (distance <= total)
                {
                    //Vector3 compareTarget = allPathPointList[i - 1].v3;

                    float nextSeg   = Mathf.Sqrt((allPathPointList[standardPoint].v3 - allPathPointList[i + 1].v3).sqrMagnitude);
                    float TargetSeg = Mathf.Sqrt((allPathPointList[standardPoint].v3 - allPathPointList[i].v3).sqrMagnitude);

                    //                     5.8 - ? = ?
                    float next = Mathf.Abs(distance - nextSeg);
                    //                     5.8 - ? = ?
                    float index = Mathf.Abs(distance - TargetSeg);

                    if (next < index){
                        result = allPathPointList[i + 1];
                    }
                    else{
                        result = allPathPointList[i];
                    }

                    break;
                }
            }
        }
        else if (type.Equals(Direction.Reverse))
        {
            //                  100 >= 0
            for (int i = standardPoint - 1; i >= 0; i--){

                total += Mathf.Sqrt((allPathPointList[i].v3 - originCod).sqrMagnitude);
                originCod = allPathPointList[i].v3;

                if (distance <= total){

                    //Debug.Log("GetClosestPoint: " + total);
                    float beforeSeg = Mathf.Sqrt((allPathPointList[standardPoint].v3 - allPathPointList[i - 1].v3).sqrMagnitude);
                    float TargetSeg = Mathf.Sqrt((allPathPointList[standardPoint].v3 - allPathPointList[i].v3).sqrMagnitude);

                    float before = Mathf.Abs(distance - beforeSeg);
                    float index = Mathf.Abs(distance - TargetSeg);

                    /*오차 범위가 더 작은걸 찾아서 반환*/
                    //if (a.Equals(b)) {
                    //    result = allPathPointList[i + 1];
                    //}
                    //else 
                    //   6    ,   5 
                    //   10, 10.5, 11
                    //   0.5
                    // next < target
                    //  1    <    2
                    if (before < index){
                        result = allPathPointList[i - 1];
                    }
                    else{
                        // next > target
                        //   2      1
                        // next == target
                        result = allPathPointList[i];
                    }

                    break;
                }
            }

        }
    }

    public void GetDistanceBetweenPoint(int origin, int target, ref float result)
    {
        if (allPathPointList.Count <= origin  || allPathPointList.Count <= target)
        {
            Debug.Log("You need to check the Index Number");
            return;
        }

        if (target < origin){
            int temp = target;
            target = origin;
            origin = temp;
        }

        float total = 0.0f;
        Vector3 originCod = allPathPointList[origin].v3;

        for (int i = origin + 1; i <= target; i++){
            total += Mathf.Sqrt((allPathPointList[i].v3 - originCod).sqrMagnitude);
            originCod = allPathPointList[i].v3;
        }

        result = total;
    }

    /// <summary>
    /// 차량에 부착된 태그위치 기반 좌표 조정
    /// </summary>
    /// <returns></returns>
    //public Vector3 Calibration(Vector3 sourceV3, float value = 1.0f)
    public Vector3 Calibration(Vector3 sourceV3, Vector3 correctionValue)
    {
        Vector3 result = Vector3.zero;
        (string lineCd, int globalIndex, int localindex) close1;

        //PathPointInfo? close2;
        //인접한 좌표 검색
        close1 = GetClosePathPointInfo(sourceV3);

        //if (close1 == null)
        if (close1 != default)
        {
            //int globlaIndex = close1.Value.globalIndex;
            //close2 = allPathPointList[close1.globalIndex + 1];
            Vector3 unitV3 = Vector3.Normalize(allPathPointList[close1.globalIndex + 1].v3 - allPathPointList[close1.globalIndex].v3);
            //result = sourceV3 + (unitV3 * value);
            result = sourceV3 + (unitV3 + correctionValue);
        }

        return result;
    }

    public bool GetPathRangeX(string lineCd, out float minX, out float maxX)
    {
        minX = float.MaxValue;
        maxX = float.MinValue;

        // 해당 라인 코드를 가진 PathManager가 있는지 확인
        if (pathContainer.ContainsKey(lineCd))
        {
            PathManager pm = pathContainer[lineCd];
            Vector3[] points = pm.GetPathPoints();

            if (points.Length > 0)
            {
                for (int i = 0; i < points.Length; i++)
                {
                    if (points[i].x < minX) minX = points[i].x;
                    if (points[i].x > maxX) maxX = points[i].x;
                }
                return true; // 찾음
            }
        }

        return false; // 못 찾음 (Commander2는 공정 크기 사용하게 됨)
    }
}
