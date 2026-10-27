#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using UnityEngine;

/// <summary>A bounded speed sample, not a proof of an unlimited physical speed ceiling.</summary>
public static class FactoryLineM4SpacingChecks
{
    public static string Result = "idle";
    public static void Start()
    {
        Result = "running";
        var player = Object.FindFirstObjectByType<FactoryPlayerController>();
        player.SetMapMode(true); player.interactor.inputEnabled = false;
        PrototypeDayCycle.Current.enabled = false;
        player.StartCoroutine(Run());
    }
    static IEnumerator Run()
    {
        var line = PrototypeLineController.Current;
        var traffic = LineTrafficManager.Instance;
        var p = ProductionProgression.Current;
        float smallestGap = float.MaxValue, deepestOverlap = 0;
        foreach (int level in new[] { 0, 8, 16 })
        {
            foreach (var old in traffic.ActiveCars.ToArray()) { old.LeaveLine(); CarPool.Instance.Return(old); }
            line.SetHighSpeedLevel(level); line.SetLever(PrototypeLineController.LeverPosition.High);
            var cars = new CarController[4];
            for (int i = 0; i < cars.Length; i++)
            {
                cars[i] = CarPool.Instance.Get();
                cars[i].SetPath(p.mainSpline, line.CurrentSpeed, .1f + (traffic.minGap + .1f) * i / p.mainSpline.CalculateLength());
                p.Stamp(cars[i]);
            }
            var boxes = cars.Select(c => c.GetComponentsInChildren<BoxCollider>(true).Where(b => b.gameObject.layer == 11).ToArray()).ToArray();
            float end = Time.time + 2f;
            while (Time.time < end)
            {
                for (int i = 1; i < cars.Length; i++)
                {
                    smallestGap = Mathf.Min(smallestGap, (cars[i].pathProgress - cars[i-1].pathProgress) * cars[i].SplineLength);
                    foreach (var a in boxes[i]) foreach (var b in boxes[i-1])
                        if (Physics.ComputePenetration(a,a.transform.position,a.transform.rotation,b,b.transform.position,b.transform.rotation,out var direction,out float distance))
                            deepestOverlap = Mathf.Max(deepestOverlap,distance);
                }
                yield return null;
            }
        }
        foreach (var car in traffic.ActiveCars.ToArray()) { car.LeaveLine(); CarPool.Instance.Return(car); }
        line.SetHighSpeedLevel(0); line.SetLever(PrototypeLineController.LeverPosition.Low);
        PrototypeDayCycle.Current.enabled = true;
        Result = (smallestGap >= traffic.minGap - .02f && deepestOverlap < .01f ? "PASS" : "FAIL") +
            ": four cars, levels 0/8/16 (1 / 5.96 / 35.53 m/s), 2 game seconds each; min path gap " + smallestGap.ToString("F3") +
            " m, max adjacent CarBody penetration " + deepestOverlap.ToString("F4") + " m. Higher-speed limits and thrown-part tunnelling remain unmeasured.";
    }
}
#endif
