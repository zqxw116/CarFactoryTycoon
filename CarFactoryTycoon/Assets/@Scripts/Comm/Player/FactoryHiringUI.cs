using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>Small on-foot hiring panel opened at a nearby process placard.</summary>
public sealed class FactoryHiringUI : MonoBehaviour
{
    public ProductionProgression progression;
    public FactoryPlayerController player;
    public Transform postsRoot;
    public float interactionRange = 3.5f;

    readonly RaycastHit[] hits = new RaycastHit[24];
    int focusedProcess = -1;
    int openProcess = -1;
    PrototypeProcessCrew activeCrew;
    bool previousCursorVisible;
    CursorLockMode previousCursorLock;

    public bool PopupIsOpen => openProcess >= 0;

    public void Configure(ProductionProgression owner, FactoryPlayerController actor, Transform placards)
    {
        progression = owner;
        player = actor;
        postsRoot = placards;
    }

    void Update()
    {
        if (!progression || !player || postsRoot == null) return;
        var keyboard = Keyboard.current;

        if (PopupIsOpen)
        {
            if (player.MapMode || Vector3.Distance(player.transform.position, GetPostPosition(openProcess)) > interactionRange + .25f)
            {
                ClosePopup(!player.MapMode);
                return;
            }
            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
            {
                ClosePopup();
                return;
            }
            if (keyboard != null && keyboard.tabKey.wasPressedThisFrame)
            {
                ClosePopup(false);
                player.SetMapMode(true);
            }
            return;
        }

        if (player.MapMode || !player.ControlsActive) { focusedProcess = -1; return; }
        focusedProcess = FindFocusedProcess();
        if (focusedProcess >= 0 && keyboard != null && keyboard.fKey.wasPressedThisFrame)
            OpenProcess(focusedProcess);
    }

    /// <summary>Opens only when called on foot, with controls captured and within placard range.</summary>
    public void OpenProcess(int index)
    {
        if (progression == null || player == null || player.MapMode || !player.ControlsActive ||
            index < 0 || index >= progression.processes.Length || !IsNearPost(index)) return;
        openProcess = index;
        activeCrew = PrototypeProcessCrew.Ensure(progression, index);
        previousCursorLock = Cursor.lockState;
        previousCursorVisible = Cursor.visible;
        player.InteractionUIOpen = true;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        if (player.interactor != null) player.interactor.inputEnabled = false;
    }

    void ClosePopup(bool restoreCursor = true)
    {
        openProcess = -1;
        activeCrew = null;
        if (player == null) return;
        player.InteractionUIOpen = false;
        if (player.MapMode)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        else if (restoreCursor)
        {
            Cursor.lockState = previousCursorLock;
            Cursor.visible = previousCursorVisible;
        }
        if (player.interactor != null) player.interactor.inputEnabled = player.ControlsActive;
    }

    int FindFocusedProcess()
    {
        if (player.viewCamera == null) return -1;
        var ray = new Ray(player.viewCamera.transform.position, player.viewCamera.transform.forward);
        Vector3 reachOrigin = player.interactor != null ? player.transform.position + Vector3.up * 1.1f : player.transform.position;
        float rayLength = Vector3.Distance(player.viewCamera.transform.position, reachOrigin) + interactionRange + 1f;
        int count = Physics.RaycastNonAlloc(ray, hits, rayLength, ~0, QueryTriggerInteraction.Ignore);
        int nearest = -1;
        float distance = float.PositiveInfinity;
        for (int i = 0; i < count; i++)
        {
            var hit = hits[i];
            if (hit.collider == null || hit.transform == player.transform || hit.transform.IsChildOf(player.transform)) continue;
            if (hit.distance < distance) { nearest = i; distance = hit.distance; }
        }
        if (nearest < 0) return -1;

        Transform post = hits[nearest].transform;
        while (post != null && post.parent != postsRoot) post = post.parent;
        if (post == null || post.parent != postsRoot) return -1;
        int index = post.GetSiblingIndex();
        return IsNearPost(index) ? index : -1;
    }

    Vector3 GetPostPosition(int index)
    {
        if (postsRoot == null || index < 0 || index >= postsRoot.childCount) return Vector3.positiveInfinity;
        return postsRoot.GetChild(index).position;
    }

    bool IsNearPost(int index) => Vector3.Distance(player.transform.position, GetPostPosition(index)) <= interactionRange;

    void OnGUI()
    {
        if (PopupIsOpen)
        {
            DrawPopup();
            return;
        }
        if (focusedProcess < 0 || progression == null || player == null || !player.ControlsActive) return;
        var process = progression.processes[focusedProcess];
        string state = progression.IsProcessAutomated(focusedProcess) ? "로봇 운영 중" :
            progression.IsProcessHired(focusedProcess) ? "작업자 근무 중" : "미고용";
        GUI.Label(new Rect(Screen.width * .5f - 170f, Screen.height * .72f, 340f, 34f),
            $"F  {process.displayName}  ·  {state}");
    }

    void DrawPopup()
    {
        int index = openProcess;
        var process = progression.processes[index];
        float width = 370f, height = 302f;
        var rect = new Rect((Screen.width - width) * .5f, (Screen.height - height) * .5f, width, height);
        GUI.Box(rect, GUIContent.none);
        GUI.Label(new Rect(rect.x + 18f, rect.y + 12f, width - 36f, 28f), process.displayName);
        string status = activeCrew != null ? activeCrew.StatusText :
            progression.IsProcessAutomated(index) ? "로봇 자동 체결" :
            progression.IsProcessHired(index) ? "작업자 배치됨" : "미고용";
        int money = EconomyManager.Instance != null ? EconomyManager.Instance.currentMoney : 0;
        GUI.Label(new Rect(rect.x + 18f, rect.y + 44f, width - 36f, 24f), $"상태: {status}");
        GUI.Label(new Rect(rect.x + 18f, rect.y + 66f, width - 36f, 20f), $"보유 금액: {money}");

        bool canHire = !progression.IsProcessHired(index) && (activeCrew == null || !activeCrew.IsBusy);
        int hireCost = progression.prototypeSettings != null ? progression.prototypeSettings.hirePrice : 0;
        GUI.enabled = canHire && money >= hireCost;
        if (GUI.Button(new Rect(rect.x + 18f, rect.y + 90f, width - 36f, 34f), $"작업자 고용  {hireCost}"))
        {
            if (progression.TryHireProcess(index)) ClosePopup();
        }

        bool canRobot = !progression.IsProcessAutomated(index) && (activeCrew == null || !activeCrew.IsBusy);
        int robotCost = progression.prototypeSettings != null ? progression.prototypeSettings.robotPrice : 1000;
        GUI.enabled = canRobot && money >= robotCost;
        if (GUI.Button(new Rect(rect.x + 18f, rect.y + 130f, width - 36f, 34f), $"로봇 구매  {robotCost}"))
        {
            if (progression.TryPurchaseRobot(index)) ClosePopup();
        }
        var upgrades = progression.GetComponent<PrototypeUpgradePurchases>();
        if (upgrades != null)
        {
            GUI.enabled = upgrades.HammerAvailable && money >= upgrades.HammerCost;
            if (GUI.Button(new Rect(rect.x + 18f, rect.y + 170f, width - 36f, 32f), upgrades.HammerAvailable ? "망치 강화 3 → 2타  " + upgrades.HammerCost : "망치 강화 완료 · 2타")) upgrades.TryUpgradeHammer();
            GUI.enabled = money >= upgrades.NextLineCost;
            if (GUI.Button(new Rect(rect.x + 18f, rect.y + 208f, width - 36f, 32f), "라인 최대 속도 ×1.25  " + upgrades.NextLineCost)) upgrades.TryUpgradeLine();
            GUI.Label(new Rect(rect.x + 18f, rect.y + 246f, width - 36f, 20f), "업그레이드 가격은 프로토타입 임시값입니다.");
        }
        GUI.enabled = true;
        if (GUI.Button(new Rect(rect.x + width - 100f, rect.y + height - 28f, 82f, 22f), "닫기")) ClosePopup();
    }

    void OnDisable()
    {
        if (PopupIsOpen) ClosePopup();
    }
}
