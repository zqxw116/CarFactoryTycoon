using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Runtime handheld display. Screen coordinates and taps share the device's world-space canvas.</summary>
public sealed class FactoryManagementTablet : MonoBehaviour
{
    const float Width = 680f, Height = 380f, MeterPerUnit = .00054f;
    const int Rows = 4;
    static readonly Color Ink = new Color(.06f, .12f, .15f);
    static readonly Color Panel = new Color(.10f, .18f, .21f);
    static readonly Color Cyan = new Color(.40f, .91f, .86f);
    static readonly Color Muted = new Color(.64f, .74f, .76f);
    static readonly Rect CloseButton = new Rect(568, 8, 104, 65);
    static readonly Rect PreviousButton = new Rect(345, 308, 105, 64);
    static readonly Rect NextButton = new Rect(565, 308, 105, 64);
    public Canvas ScreenCanvas { get; private set; }
    public int Page { get; private set; }
    public FactoryTabletRoute Route { get; private set; }
    FirstProductionLoop loop;
    FactoryPlayerController player;
    RectTransform screen;
    TextMeshProUGUI money, feed, pageNumber;
    readonly TextMeshProUGUI[] stations = new TextMeshProUGUI[Rows];
    Material shell;
    Vector3 startPosition;
    Quaternion startRotation;
    float startFov, openedAt, refreshAt;

    public void Initialize(FactoryPlayerController owner, FirstProductionLoop production)
    {
        player = owner;
        loop = production;
        transform.localPosition = new Vector3(0, 1.28f, .40f);
        transform.localRotation = Quaternion.Euler(44f, 0f, 0f);
        var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
        body.name = "태블릿 케이스";
        body.layer = 2;
        body.GetComponent<Collider>().enabled = false;
        body.transform.SetParent(transform, false);
        body.transform.localScale = new Vector3(.392f, .230f, .018f);
        shell = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = new Color(.16f, .18f, .20f) };
        body.GetComponent<Renderer>().sharedMaterial = shell;

        var display = new GameObject("태블릿 화면", typeof(RectTransform), typeof(Canvas));
        screen = display.GetComponent<RectTransform>();
        screen.SetParent(transform, false);
        screen.localPosition = new Vector3(0, 0, -.0095f);
        screen.sizeDelta = new Vector2(Width, Height);
        screen.localScale = Vector3.one * MeterPerUnit;
        ScreenCanvas = display.GetComponent<Canvas>();
        ScreenCanvas.renderMode = RenderMode.WorldSpace;
        ScreenCanvas.worldCamera = player.viewCamera;
        Block("화면 바탕", new Rect(0, 0, Width, Height), Ink);
        money = Label("보유 자금", new Rect(14, 5, 285, 66), 32, Color.white);
        feed = Label("투입 상태", new Rect(300, 5, 265, 66), 26, Cyan);
        ButtonFace(CloseButton, "닫기");
        Block("약도 바탕", new Rect(10, 80, 316, 290), Panel);
        Label("약도 제목", new Rect(22, 85, 300, 39), 26, Color.white).text = "가동 구간 약도";
        var map = Place("컨베이어 경로", new Rect(24, 130, 288, 171));
        Route = map.gameObject.AddComponent<FactoryTabletRoute>();
        Route.raycastTarget = false;
        Label("범례", new Rect(19, 313, 302, 46), 21, Muted).text = "<color=#66e8db>◆ 세척</color>   <color=#ffffff>◆ 공정</color>   <color=#ffbf59>◆ 인수</color>";
        for (int i = 0; i < Rows; i++)
            stations[i] = Label("공정 " + (i + 1), new Rect(343, 80 + i * 56, 329, 55), 25, Color.white);
        ButtonFace(PreviousButton, "이전");
        ButtonFace(NextButton, "다음");
        pageNumber = Label("페이지", new Rect(449, 308, 118, 64), 23, Muted);
        pageNumber.alignment = TextAlignmentOptions.Center;
    }

    RectTransform Place(string label, Rect rect)
    {
        var node = new GameObject(label, typeof(RectTransform));
        var rt = node.GetComponent<RectTransform>();
        rt.SetParent(screen, false);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 1);
        rt.anchoredPosition = new Vector2(rect.x, -rect.y);
        rt.sizeDelta = rect.size;
        return rt;
    }

    void Block(string label, Rect rect, Color color)
    {
        var image = Place(label, rect).gameObject.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
    }

    TextMeshProUGUI Label(string name, Rect rect, int size, Color color)
    {
        var text = Place(name, rect).gameObject.AddComponent<TextMeshProUGUI>();
        text.font = TMP_Settings.defaultFontAsset;
        text.fontSize = size;
        text.color = color;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.enableAutoSizing = true;
        text.fontSizeMin = size * .8f;
        text.fontSizeMax = size;
        text.raycastTarget = false;
        return text;
    }

    void ButtonFace(Rect rect, string caption)
    {
        Block(caption, rect, Panel);
        var label = Label(caption, rect, 26, Cyan);
        label.text = caption;
        label.alignment = TextAlignmentOptions.Center;
    }

    public void Open()
    {
        gameObject.SetActive(true);
        startPosition = player.viewCamera.transform.position;
        startRotation = player.viewCamera.transform.rotation;
        startFov = player.viewCamera.fieldOfView;
        openedAt = Time.unscaledTime;
        Refresh();
    }

    public void FrameCamera()
    {
        var camera = player.viewCamera;
        const float distance = .30f;
        float halfHeight = Mathf.Max(.230f / .83f, .392f / (.9f * Mathf.Max(.1f, camera.aspect))) * .5f;
        float targetFov = 2f * Mathf.Atan(halfHeight / distance) * Mathf.Rad2Deg;
        float t = Mathf.SmoothStep(0, 1, (Time.unscaledTime - openedAt) / .25f);
        camera.fieldOfView = Mathf.Lerp(startFov, targetFov, t);
        camera.transform.SetPositionAndRotation(
            Vector3.Lerp(startPosition, screen.position - transform.forward * distance, t),
            Quaternion.Slerp(startRotation, transform.rotation, t));
        if (Time.unscaledTime >= refreshAt) Refresh();
    }

    void Refresh()
    {
        refreshAt = Time.unscaledTime + .25f;
        money.text = $"잔액  {EconomyManager.Instance.currentMoney:N0}원";
        feed.text = loop.AutomaticInputPurchased ? "차량 자동 투입" : "차량 수동 투입";
        var processes = loop.progression.processes;
        int pages = Mathf.Max(1, (processes.Length + Rows - 1) / Rows);
        Page = Mathf.Clamp(Page, 0, pages - 1);
        pageNumber.text = $"{Page + 1} / {pages}";
        for (int i = 0; i < Rows; i++)
        {
            int index = Page * Rows + i;
            if (index >= processes.Length) { stations[i].text = ""; continue; }
            var entry = processes[index];
            bool active = index < loop.OpenedProcesses;
            var owner = !active ? "잠김" : entry.automated ? "로봇팔" : entry.hired ? "작업자" : "직접";
            string name = index == 0 ? "대시보드" : entry.displayName;
            stations[i].text = $"{index + 1:D2}  {name} / {owner}";
            stations[i].color = active ? Color.white : Muted;
        }
        Route.UpdateLayout(loop);
    }

    public void NextPage(int direction) { Page += direction; Refresh(); }

    public void Tap(Vector2 screenPoint)
    {
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(screen, screenPoint, player.viewCamera, out var local)) return;
        var point = new Vector2(local.x + Width * .5f, Height * .5f - local.y);
        if (CloseButton.Contains(point)) player.SetMapMode(false);
        else if (PreviousButton.Contains(point)) NextPage(-1);
        else if (NextButton.Contains(point)) NextPage(1);
    }

    public void PoseHands(Animator actor)
    {
        PoseHand(actor, AvatarIKGoal.LeftHand, HumanBodyBones.LeftHand, HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftIndexProximal, -1f);
        PoseHand(actor, AvatarIKGoal.RightHand, HumanBodyBones.RightHand, HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightIndexProximal, 1f);
    }

    void PoseHand(Animator actor, AvatarIKGoal goal, HumanBodyBones handBone, HumanBodyBones middleBone, HumanBodyBones indexBone, float side)
    {
        var wrist = actor.GetBoneTransform(handBone);
        if (wrist == null) return;
        var grip = transform.TransformPoint(new Vector3(side * .195f, -.035f, .012f));
        var middle = actor.GetBoneTransform(middleBone);
        var index = actor.GetBoneTransform(indexBone);
        if (middle != null && index != null)
        {
            Vector3 fingers = middle.position - wrist.position;
            Vector3 thumb = index.position - middle.position;
            if (Vector3.Cross(fingers, thumb).sqrMagnitude > 1e-10f)
            {
                Quaternion boneFrame = Quaternion.Inverse(wrist.rotation) * Quaternion.LookRotation(fingers, thumb);
                Quaternion gripFrame = Quaternion.LookRotation(transform.up - transform.right * (.32f * side), -transform.forward);
                actor.SetIKRotationWeight(goal, 1f);
                actor.SetIKRotation(goal, gripFrame * Quaternion.Inverse(boneFrame));
                grip -= gripFrame * Vector3.forward * (fingers.magnitude * .65f);
            }
        }
        actor.SetIKPositionWeight(goal, 1f);
        actor.SetIKPosition(goal, grip);
    }

    void OnDestroy() { if (shell != null) Destroy(shell); }
}
