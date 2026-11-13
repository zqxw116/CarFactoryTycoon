using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;

/// <summary>Moving end-of-line tests. The traffic manager retains ownership of the car's progress.</summary>
public sealed class FinalInspectionLane : MonoBehaviour
{
    public float EntryProgress { get; private set; }
    public float RollerProgress { get; private set; }
    public float ScanProgress { get; private set; }

    SplineContainer line;
    float sprayEnd, rollerEnd, scanEnd;
    FinalInspectionEquipment equipment;
    readonly Dictionary<CarController, Inspection> inspections = new Dictionary<CarController, Inspection>();

    public void Initialize(SplineContainer spline, float lastAssemblyEnd)
    {
        if (equipment != null || spline == null || lastAssemblyEnd >= 1f) return;
        line = spline;
        float length = Mathf.Max(.01f, line.CalculateLength());
        float span = 1f - Mathf.Clamp01(lastAssemblyEnd);
        EntryProgress = lastAssemblyEnd + Mathf.Min(span * .12f, 5f / length);
        sprayEnd = EntryProgress + Mathf.Min(span * .18f, 5.5f / length);
        float rollerCenter = Mathf.Lerp(lastAssemblyEnd, 1f, .52f);
        float halfRoller = Mathf.Min(span * .13f, 3.5f / length);
        RollerProgress = rollerCenter - halfRoller;
        rollerEnd = rollerCenter + halfRoller;
        ScanProgress = Mathf.Lerp(lastAssemblyEnd, 1f, .83f);
        scanEnd = ScanProgress + Mathf.Min(span * .10f, 2.5f / length);
        equipment = gameObject.AddComponent<FinalInspectionEquipment>();
        equipment.Build(line, EntryProgress, sprayEnd, rollerCenter, halfRoller * length * 2f, ScanProgress);
    }

    public void Advance(CarController car, ProductionContract contract)
    {
        if (line == null || car == null || contract == null || !contract.isCompleteVehicle ||
            !contract.requireWholeWash || car.targetSpline != line || car.pathProgress < EntryProgress) return;

        if (!inspections.TryGetValue(car, out var test) || test.version != car.LifeVersion)
        {
            car.CloseRepairWindow();
            test = new Inspection(car, contract);
            inspections[car] = test;
        }

        float progress = car.pathProgress;
        test.spraying = progress <= sprayEnd;
        test.rollerLift = 0f;
        test.scanning = progress >= ScanProgress && progress < scanEnd;
        if (progress >= RollerProgress)
        {
            float roller = Mathf.InverseLerp(RollerProgress, rollerEnd, progress);
            if (!test.ejected && roller >= .32f)
            {
                foreach (var loose in test.looseParts) PrototypeCarDebris.DropSideways(loose, car);
                test.ejected = true;
            }
            if (progress < rollerEnd)
            {
                test.rollerLift = Mathf.SmoothStep(0f, 1f, Mathf.Min(roller, 1f - roller) * 4f);
                // Advance is called immediately after the conveyor reapplies the spline pose.
                var up = car.transform.up;
                car.transform.position += up * ((.16f + .022f * Mathf.Sin(Time.time * 46f)) * test.rollerLift);
                car.transform.rotation *= Quaternion.Euler(0f, 0f, Mathf.Sin(Time.time * 39f) * .7f * test.rollerLift);
                test.spinAngle = Mathf.Repeat(test.spinAngle + 940f * Time.deltaTime, 360f);
                foreach (var wheel in test.wheels) wheel.Spin(car, test.spinAngle);
                test.spinning = true;
            }
            else if (test.spinning) test.RestoreWheels();
        }
        if (progress >= scanEnd && !test.finished)
        {
            test.finished = true;
            equipment.ShowResult(string.IsNullOrEmpty(test.reason));
        }
    }

    public bool RequiresFuelStop(CarController car)
    {
        if (car == null || car.targetSpline != line || !car.TryGetComponent<ProductionContract>(out var contract) ||
            !contract.isCompleteVehicle || !contract.requireWholeWash || contract.settled) return false;
        // Reserve the stop before the inspection result arrives, even on a long traffic frame.
        return !inspections.TryGetValue(car, out var test) || test.version != car.LifeVersion ||
               !test.finished || string.IsNullOrEmpty(test.reason);
    }

    public bool Passed(CarController car, out string reason)
    {
        if (car != null && inspections.TryGetValue(car, out var test) && test.version == car.LifeVersion && test.finished)
        {
            reason = test.reason;
            return string.IsNullOrEmpty(reason);
        }
        reason = "최종 스캔 미완료";
        return false;
    }

    public void Forget(CarController car)
    {
        if (car == null || !inspections.TryGetValue(car, out var test)) return;
        if (test.version == car.LifeVersion && test.spinning) test.RestoreWheels();
        inspections.Remove(car);
    }

    void LateUpdate()
    {
        if (equipment == null) return;
        bool spraying = false, scanning = false;
        float lift = 0f;
        foreach (var entry in inspections)
        {
            if (entry.Key == null || !entry.Key.isActiveAndEnabled || entry.Value.version != entry.Key.LifeVersion) continue;
            spraying |= entry.Value.spraying;
            scanning |= entry.Value.scanning;
            lift = Mathf.Max(lift, entry.Value.rollerLift);
        }
        equipment.Animate(spraying, lift, scanning);
    }

    sealed class Inspection
    {
        public readonly int version;
        public readonly List<AssemblyPart> looseParts = new List<AssemblyPart>();
        public readonly List<Wheel> wheels = new List<Wheel>();
        public readonly string reason;
        public bool spraying, ejected, spinning, scanning, finished;
        public float rollerLift, spinAngle;

        public Inspection(CarController car, ProductionContract contract)
        {
            version = car.LifeVersion;
            var wash = car.GetComponent<VehicleWashSurface>();
            if (wash == null || !wash.IsClean) reason = "차량 전체 세척 미완료";
            var parts = car.GetComponentsInChildren<AssemblyPart>(true);
            if (contract.requiredParts == null || contract.requiredParts.Length == 0)
            {
                reason = "필수 부품 목록 누락";
                return;
            }
            foreach (var type in contract.requiredParts)
            {
                var domain = VehicleInspectionRules.DomainOf(type);
                if (domain == VehicleInspectionDomain.None) continue;
                bool found = false;
                foreach (var part in parts)
                {
                    if (part.myType != type) continue;
                    found = true;
                    if (part.IsAssembled)
                    {
                        if (part.myGroup == PartGroup.Wheel) wheels.Add(new Wheel(car, part));
                        continue;
                    }
                    bool attached = !part.IsPlayerAttaching && (part.IsPlayerAttached || part.currentWork > 0f);
                    if (attached && !looseParts.Contains(part)) looseParts.Add(part);
                    if (reason == null)
                        reason = Defect(type, domain, attached ? "체결 미완료" : "미부착");
                }
                if (!found && reason == null) reason = Defect(type, domain, "누락");
            }
        }

        static string Defect(PartType type, VehicleInspectionDomain domain, string defect)
        {
            string stage = domain == VehicleInspectionDomain.Sealing ? "수밀 불량" : "체결 불량";
            return stage + " · " + PlayerAssemblyInteractor.PartLabel(type) + " " + defect;
        }

        public void RestoreWheels()
        {
            foreach (var wheel in wheels) wheel.Restore();
            spinning = false;
        }
    }

    sealed class Wheel
    {
        readonly Transform wheel;
        readonly Quaternion rotation;
        readonly Vector3 position, center;
        public Wheel(CarController car, AssemblyPart part)
        {
            wheel = part.transform;
            position = wheel.localPosition;
            rotation = wheel.localRotation;
            // Imported wheel meshes may share the body origin. Spin around the tire center.
            var renderers = car.GetPartRenderers(part);
            var bounds = new Bounds(wheel.position, Vector3.zero);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (i == 0) bounds = renderers[i].bounds;
                else bounds.Encapsulate(renderers[i].bounds);
            }
            center = wheel.parent.InverseTransformPoint(bounds.center);
        }
        public void Spin(CarController car, float angle)
        {
            var axle = wheel.parent.InverseTransformDirection(car.transform.right);
            var spin = Quaternion.AngleAxis(angle, axle);
            wheel.SetLocalPositionAndRotation(center + spin * (position - center), spin * rotation);
        }
        public void Restore() => wheel.SetLocalPositionAndRotation(position, rotation);
    }
}
