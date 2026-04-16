using System;
using System.Linq;
using UnityEngine;

public static class Constants
{
    public static WaitForSeconds waitOneSeconds = new WaitForSeconds(1f);


    public static readonly PartType[] allPartsType =
    ((PartType[])Enum.GetValues(typeof(PartType)))
    .Where(p => p != PartType.None)
    .ToArray();

    public static int GetPartTypeIndex(PartType _type) => Array.IndexOf(allPartsType, _type);
}
