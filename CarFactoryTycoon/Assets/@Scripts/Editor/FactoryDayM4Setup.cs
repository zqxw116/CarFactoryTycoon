#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class FactoryDayM4Setup
{
    public static string Apply()
    {
        if (Application.isPlaying) throw new InvalidOperationException("Edit mode required.");
        var p = UnityEngine.Object.FindFirstObjectByType<ProductionProgression>();
        if (p == null || !p.prototypeMode) throw new InvalidOperationException("Prototype main scene required.");
        Undo.RegisterFullObjectHierarchyUndo(p.gameObject, "M4 day cycle");
        var player = UnityEngine.Object.FindFirstObjectByType<FactoryPlayerController>();
        var cycle = p.GetComponent<PrototypeDayCycle>(); if (cycle == null) cycle = p.gameObject.AddComponent<PrototypeDayCycle>();
        cycle.progression = p; cycle.settings = p.prototypeSettings; cycle.player = player;
        var defects = p.GetComponent<PrototypeDefectDisplay>(); if (defects == null) defects = p.gameObject.AddComponent<PrototypeDefectDisplay>();
        defects.progression = p;
        var hud = p.GetComponent<PrototypeDayHUD>(); if (hud == null) hud = p.gameObject.AddComponent<PrototypeDayHUD>();
        hud.cycle = cycle; hud.defects = defects; hud.player = player;
        var upgrades = p.GetComponent<PrototypeUpgradePurchases>(); if (upgrades == null) upgrades = p.gameObject.AddComponent<PrototypeUpgradePurchases>();
        upgrades.settings = p.prototypeSettings; upgrades.line = p.GetComponent<PrototypeLineController>(); upgrades.interactor = player.interactor;
        foreach (var item in new UnityEngine.Object[] { cycle, defects, hud, upgrades, p.prototypeSettings }) EditorUtility.SetDirty(item);
        var scene = p.gameObject.scene;
        EditorSceneManager.MarkSceneDirty(scene); AssetDatabase.SaveAssets(); EditorSceneManager.SaveScene(scene);
        return "M4 day, ledger, defect display and prototype upgrades saved.";
    }
}
#endif
