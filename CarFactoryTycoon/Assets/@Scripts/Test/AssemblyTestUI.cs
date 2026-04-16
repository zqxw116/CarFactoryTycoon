using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// 체결 테스트 씬 전용 UI.
/// 씬에 배치된 TMP_Dropdown으로 파츠를 선택하면 AssemblyTestManager.RunTest를 호출한다.
/// </summary>
public class AssemblyTestUI : MonoBehaviour
{
    [Header("참조")]
    public AssemblyTestManager testManager;
    public TMP_Dropdown dropdown;

    private void Start()
    {
        if (testManager == null)
            testManager = GetComponent<AssemblyTestManager>();

        if (testManager == null)
        {
            Debug.LogError("[AssemblyTestUI] AssemblyTestManager를 찾을 수 없습니다.");
            return;
        }

        if (dropdown == null)
        {
            Debug.LogError("[AssemblyTestUI] Dropdown이 연결되지 않았습니다.");
            return;
        }

        InitDropdown();
    }

    private void InitDropdown()
    {
        dropdown.ClearOptions();

        var options = new List<TMP_Dropdown.OptionData>();
        foreach (PartType type in Constants.allPartsType)
            options.Add(new TMP_Dropdown.OptionData(type.ToString()));

        dropdown.AddOptions(options);
        dropdown.value = 0;
        dropdown.RefreshShownValue();

        dropdown.onValueChanged.AddListener(OnDropdownChanged);
    }

    private void OnDropdownChanged(int index)
    {
        testManager.RunTest(Constants.allPartsType[index]);
    }

    private void OnDestroy()
    {
        if (dropdown != null)
            dropdown.onValueChanged.RemoveListener(OnDropdownChanged);
    }
}
