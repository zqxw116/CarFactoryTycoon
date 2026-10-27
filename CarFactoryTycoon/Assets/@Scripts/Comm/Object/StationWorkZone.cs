using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;

/// <summary>Station windows follow the same path-distance convention as line traffic.</summary>
public static class StationWorkZone
{
    public static float ProgressAt(SplineContainer spline, Vector3 position)
    {
        SplineUtility.GetNearestPoint(spline.Spline,
            (float3)spline.transform.InverseTransformPoint(position), out _, out float progress);
        return Mathf.Clamp01(progress);
    }

    public static bool Contains(CarController car, SplineContainer spline, float start, float meters)
    {
        if (car == null || !car.gameObject.activeInHierarchy || !car.CanRepair ||
            car.targetSpline != spline || car.SplineLength <= 0f) return false;
        float distance = (car.pathProgress - start) * car.SplineLength;
        return distance >= 0f && distance < Mathf.Max(0f, meters);
    }

    public static CarController Find(SplineContainer spline, float start, float meters)
    {
        if (spline == null || LineTrafficManager.Instance == null) return null;
        foreach (var car in LineTrafficManager.Instance.ActiveCars)
            if (Contains(car, spline, start, meters)) return car;
        return null;
    }
}
