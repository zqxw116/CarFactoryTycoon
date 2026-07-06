using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

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
        PresetListScroll(index);
    }

    /// <summary>
    /// 드롭다운 목록은 열 때마다 Template을 복제해 만들어지므로,
    /// Template의 Content 위치·Scrollbar 값을 선택 항목 위치로 미리 세팅해 두면
    /// 다음에 목록이 열릴 때 그 스크롤 상태로 시작한다.
    /// </summary>
    private void PresetListScroll(int index)
    {
        if (dropdown.template == null) return;

        ScrollRect scrollRect = dropdown.template.GetComponent<ScrollRect>();
        int count = dropdown.options.Count;
        if (scrollRect == null || scrollRect.content == null || count <= 1) return;

        // 항목 높이 = Template Content 안의 Item 높이
        RectTransform item = scrollRect.content.childCount > 0
            ? scrollRect.content.GetChild(0) as RectTransform : null;
        if (item == null || item.rect.height <= 0f) return;

        float itemHeight = item.rect.height;
        float viewportHeight = scrollRect.viewport != null
            ? scrollRect.viewport.rect.height : dropdown.template.rect.height;

        // 선택 항목이 목록 맨 위에 오도록 Content를 올린다 (스크롤 가능 범위로 클램프)
        float maxOffset = Mathf.Max(0f, itemHeight * count - viewportHeight);
        float offset = Mathf.Clamp(itemHeight * index, 0f, maxOffset);

        Vector2 pos = scrollRect.content.anchoredPosition;
        pos.y = offset;
        scrollRect.content.anchoredPosition = pos;

        if (scrollRect.verticalScrollbar != null)
            scrollRect.verticalScrollbar.value = 1f - (float)index / (count - 1);
    }

    private void OnDestroy()
    {
        if (dropdown != null)
            dropdown.onValueChanged.RemoveListener(OnDropdownChanged);
    }
}
