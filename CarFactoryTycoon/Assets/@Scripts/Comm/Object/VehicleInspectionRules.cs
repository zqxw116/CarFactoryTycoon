using System;
using System.Collections.Generic;

public enum VehicleInspectionDomain { None, Assembly, Sealing }

/// <summary>Each installed part has one inspection owner. Fatigue/legacy quality never affects results.</summary>
public static class VehicleInspectionRules
{
    static readonly PartType[] Types = (PartType[])Enum.GetValues(typeof(PartType));

    public static VehicleInspectionDomain DomainOf(PartType type)
    {
        switch (type)
        {
            case PartType.None:
            case PartType.Frame_1: return VehicleInspectionDomain.None;
            case PartType.BehindWindow_Left_31:
            case PartType.BehindWindow_Right_32:
            case PartType.FrontWindow_33:
            case PartType.FrontDoor_Left_34:
            case PartType.FrontDoor_Right_35: return VehicleInspectionDomain.Sealing;
            default: return VehicleInspectionDomain.Assembly;
        }
    }

    public static int CountMissing(CarController car, VehicleInspectionDomain domain, List<PartType> missing = null)
    {
        return CountMissing(car != null ? car.GetComponentsInChildren<AssemblyPart>(true) : null, domain, missing);
    }

    public static int CountMissing(AssemblyPart[] parts, VehicleInspectionDomain domain, List<PartType> missing = null)
    {
        if (missing != null) missing.Clear();
        int count = 0;
        foreach (var type in Types)
        {
            if (DomainOf(type) != domain || domain == VehicleInspectionDomain.None) continue;
            AssemblyPart found = null;
            if (parts != null) foreach (var part in parts)
                if (part != null && part.myType == type) { found = part; break; }
            if (found != null && found.IsAssembled) continue;
            count++;
            if (missing != null) missing.Add(type);
        }
        return count;
    }
}
