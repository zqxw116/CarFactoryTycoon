using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 라인(LINE_1~LINE_6)별로 로봇팔을 지그재그로 자동 배치하는 에디터 툴.
///
/// 설정값은 각 LINE_X에 붙는 <see cref="LineSettings"/> 컴포넌트에 저장되므로
/// 툴을 닫았다 다시 열어도 초기화되지 않는다.
///
/// 사용법:
///   Tools ▸ CarFactory ▸ 로봇팔 배치기 → 창 오픈 시 GameObjectGroup의 자식
///   LINE_1~LINE_6을 자동 탐색해 각 라인 슬롯에 할당(없으면 LineSettings 자동 추가).
///   각 라인에 로봇팔 목록을 채우고 시작좌표(X,Z)·방향·간격을 지정한 뒤 [배치] 클릭.
///
/// 배치 규칙(예: 시작 X300,Z100 / 왼쪽 / X간격5 / Z간격5 / 4개):
///   진행축(왼쪽=-X)으로 (i+1)*X간격 이동, Z는 짝수 -Z간격 / 홀수 +Z간격 지그재그.
///   1번 → (295, 95), 2번 → (290, 105), 3번 → (285, 95), 4번 → (280, 105)
///   회전·작업존 콜라이더는 로봇팔이 아니라 연결된 StationController에 적용된다.
/// </summary>
public class RobotArmPlacerWindow : EditorWindow
{
    private const int LINE_COUNT = 6;
    private const string GROUP_NAME = "GameObjectGroup";
    private const string PREF_BOX_SIZE = "RobotArmPlacer.BoxSize"; // 공용 박스 크기 저장 키
    private const string PREF_PLACEMENT_SO = "RobotArmPlacer.PlacementSO"; // 배치 데이터 SO GUID 저장 키
    private const string PREF_APPLY_SO = "RobotArmPlacer.ApplyPlacementSO"; // SO 배치값 자동 적용 여부 저장 키

    // 데이터는 씬의 LineSettings 컴포넌트가 보관. 폴드아웃은 창 UI 상태(비영속).
    private readonly LineSettings[] lines = new LineSettings[LINE_COUNT];
    private readonly bool[] foldout = new bool[LINE_COUNT];
    private readonly bool[] armsFoldout = new bool[LINE_COUNT];
    private Vector2 scroll;

    // 모든 라인 공용 작업존 박스 크기 (라인별이 아님). EditorPrefs에 영속 저장.
    private Vector3 boxSize = new Vector3(3f, 0.5f, 3f);

    // 배치 시 각 StationController에 바인딩하고 배치 데이터를 굽는 SO. GUID로 EditorPrefs 영속.
    private StationPlacementDataSO placementData;

    // 배치 시 SO의 부품별 씬 독립 값(Pile/End/작업존 size·center.y)을 함께 적용할지. EditorPrefs 영속.
    // 좌/우(robotLineSide)는 SO가 아니라 창에서 고른 armSides가 최종이다.
    private bool applyPlacementFromSO = true;

    [MenuItem("Tools/CarFactory/로봇팔 배치기")]
    private static void Open() => GetWindow<RobotArmPlacerWindow>("로봇팔 배치기");

    private void OnEnable()
    {
        for (int i = 0; i < LINE_COUNT; i++) { foldout[i] = true; armsFoldout[i] = true; }
        LoadBoxSize();
        LoadPlacementSO();
        applyPlacementFromSO = EditorPrefs.GetBool(PREF_APPLY_SO, true);
        AutoAssignLines();
        SceneView.duringSceneGui += OnSceneGUI;
    }

    private void LoadBoxSize()
    {
        boxSize = new Vector3(
            EditorPrefs.GetFloat(PREF_BOX_SIZE + ".x", 3f),
            EditorPrefs.GetFloat(PREF_BOX_SIZE + ".y", 0.5f),
            EditorPrefs.GetFloat(PREF_BOX_SIZE + ".z", 3f));
    }

    private void SaveBoxSize()
    {
        EditorPrefs.SetFloat(PREF_BOX_SIZE + ".x", boxSize.x);
        EditorPrefs.SetFloat(PREF_BOX_SIZE + ".y", boxSize.y);
        EditorPrefs.SetFloat(PREF_BOX_SIZE + ".z", boxSize.z);
    }

    private void LoadPlacementSO()
    {
        string guid = EditorPrefs.GetString(PREF_PLACEMENT_SO, "");
        if (string.IsNullOrEmpty(guid)) return;
        string path = AssetDatabase.GUIDToAssetPath(guid);
        if (!string.IsNullOrEmpty(path))
            placementData = AssetDatabase.LoadAssetAtPath<StationPlacementDataSO>(path);
    }

    private void SavePlacementSO()
    {
        string guid = "";
        if (placementData != null)
        {
            string path = AssetDatabase.GetAssetPath(placementData);
            guid = AssetDatabase.AssetPathToGUID(path);
        }
        EditorPrefs.SetString(PREF_PLACEMENT_SO, guid);
    }

    private void OnDisable()
    {
        SceneView.duringSceneGui -= OnSceneGUI;
    }

    // 각 라인의 시작좌표를 씬 뷰에 구로 표시하고, 위치 핸들로 직접 드래그 편집.
    private void OnSceneGUI(SceneView sv)
    {
        for (int i = 0; i < LINE_COUNT; i++)
        {
            LineSettings line = lines[i];
            if (line == null) continue;

            float gizmoY = line.transform.position.y;
            Vector3 start = new Vector3(line.startXZ.x, gizmoY, line.startXZ.y);

            Handles.color = Color.cyan;
            Handles.SphereHandleCap(0, start, Quaternion.identity, 0.4f, EventType.Repaint);
            Handles.Label(start + Vector3.up * 0.6f, $"{line.name} 시작");

            EditorGUI.BeginChangeCheck();
            Vector3 moved = Handles.PositionHandle(start, Quaternion.identity);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(line, "Move Line Start");
                line.startXZ = new Vector2(moved.x, moved.z); // X/Z 평면만 편집
                EditorUtility.SetDirty(line);
                Repaint(); // 창 수치도 즉시 갱신
            }
        }
    }

    private void OnGUI()
    {
        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField($"\"{GROUP_NAME}\" 하위 LINE_1~LINE_6 자동 할당", EditorStyles.boldLabel);
            if (GUILayout.Button("라인 다시 찾기", GUILayout.Width(110)))
                AutoAssignLines();
        }

        EditorGUI.BeginChangeCheck();
        boxSize = EditorGUILayout.Vector3Field("작업존 박스 크기 (전체 공용)", boxSize);
        if (EditorGUI.EndChangeCheck()) SaveBoxSize();

        EditorGUI.BeginChangeCheck();
        placementData = (StationPlacementDataSO)EditorGUILayout.ObjectField(
            "배치 데이터 SO (배치 시 참조만 바인딩)", placementData, typeof(StationPlacementDataSO), false);
        if (EditorGUI.EndChangeCheck()) SavePlacementSO();

        using (new EditorGUI.DisabledScope(placementData == null))
        {
            EditorGUI.BeginChangeCheck();
            applyPlacementFromSO = EditorGUILayout.ToggleLeft(
                "SO 배치값 자동 적용 (부품별 Pile/End/작업존 크기·오프셋 — 좌/우는 아래 목록이 최종)", applyPlacementFromSO);
            if (EditorGUI.EndChangeCheck()) EditorPrefs.SetBool(PREF_APPLY_SO, applyPlacementFromSO);
        }

        EditorGUILayout.Space();

        scroll = EditorGUILayout.BeginScrollView(scroll);
        for (int i = 0; i < LINE_COUNT; i++)
            DrawLine(i, lines[i]);
        EditorGUILayout.EndScrollView();

        EditorGUILayout.Space();
        if (GUILayout.Button("전체 라인 일괄 배치", GUILayout.Height(28)))
        {
            for (int i = 0; i < LINE_COUNT; i++) PlaceLine(lines[i]);
        }
    }

    private void DrawLine(int index, LineSettings line)
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            string title = line != null ? line.name : $"LINE_{index + 1} (미할당)";
            foldout[index] = EditorGUILayout.Foldout(foldout[index], $"■ {title}", true);
            if (!foldout[index]) return;

            if (line == null)
            {
                EditorGUILayout.HelpBox($"LINE_{index + 1}을(를) 찾지 못했습니다. [라인 다시 찾기]를 눌러주세요.", MessageType.Warning);
                return;
            }

            EditorGUI.BeginChangeCheck();

            Vector2 startXZ  = EditorGUILayout.Vector2Field("시작 좌표 (X, Z)", line.startXZ);
            var direction    = (LineSettings.Direction)EditorGUILayout.EnumPopup("진행 방향", line.direction);
            float spacing    = EditorGUILayout.FloatField("로봇팔 간격 (X)", line.spacing);
            float zSpacing   = EditorGUILayout.FloatField("Z 지그재그 간격", line.zSpacing);
            float laneWidth  = EditorGUILayout.FloatField("박스 콜라이더 z기준", line.laneWidth);

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(line, "Edit Line Settings");
                line.startXZ = startXZ;
                line.direction = direction;
                line.spacing = spacing;
                line.zSpacing = zSpacing;
                line.laneWidth = laneWidth;
                EditorUtility.SetDirty(line);
            }

            DrawArms(index, line);

            EditorGUILayout.Space(2);
            GUI.backgroundColor = new Color(0.6f, 0.9f, 1f);
            if (GUILayout.Button("이 라인 배치", GUILayout.Height(22))) PlaceLine(line);
            GUI.backgroundColor = Color.white;
        }
        EditorGUILayout.Space(2);
    }

    private void DrawArms(int index, LineSettings line)
    {
        EditorGUILayout.Space(2);
        armsFoldout[index] = EditorGUILayout.Foldout(armsFoldout[index], $"로봇팔 목록 ({line.arms.Count})", true);

        // 선택/드래그한 여러 오브젝트를 한 번에 추가 (목록 접힘 여부와 무관하게 항상 노출)
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button($"선택 항목 추가 ({Selection.transforms.Length})"))
                AddArms(line, Selection.transforms);
            if (GUILayout.Button("선택 항목으로 교체"))
            {
                Undo.RecordObject(line, "Set Line Arms");
                line.arms.Clear();
                line.armSides.Clear();
                AddArms(line, Selection.transforms);
            }
        }
        DrawDropArea(line);

        if (!armsFoldout[index]) return;

        SyncSides(line); // armSides를 arms 개수에 맞춰 보정(부족분은 짝/홀 기본값)

        for (int a = 0; a < line.arms.Count; a++)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                var t = (Transform)EditorGUILayout.ObjectField($"{a + 1}", line.arms[a], typeof(Transform), true);
                // 로봇팔별 라인 좌/우 방향 선택
                var side = (RobotLineSideType)EditorGUILayout.EnumPopup(line.armSides[a], GUILayout.Width(70));
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(line, "Edit Line Arm");
                    line.arms[a] = t;
                    line.armSides[a] = side;
                    EditorUtility.SetDirty(line);
                }
                if (GUILayout.Button("－", GUILayout.Width(24)))
                {
                    Undo.RecordObject(line, "Remove Line Arm");
                    line.arms.RemoveAt(a);
                    if (a < line.armSides.Count) line.armSides.RemoveAt(a);
                    EditorUtility.SetDirty(line);
                    GUIUtility.ExitGUI();
                }
            }
        }
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("＋ 슬롯 추가"))
            {
                Undo.RecordObject(line, "Add Line Arm");
                line.arms.Add(null);
                EditorUtility.SetDirty(line);
            }
            if (GUILayout.Button("목록 비우기"))
            {
                Undo.RecordObject(line, "Clear Line Arms");
                line.arms.Clear();
                line.armSides.Clear();
                EditorUtility.SetDirty(line);
            }
        }
    }

    /// <summary>
    /// armSides 리스트를 arms 개수에 맞춘다. 부족분은 인덱스 짝/홀 기본값으로 채우고(짝=Left/아래, 홀=Right/위),
    /// 초과분은 잘라낸다. (배치 규칙: 위라인 +Z=Right, 아래라인 -Z=Left)
    /// </summary>
    private static void SyncSides(LineSettings line)
    {
        if (line.armSides.Count == line.arms.Count) return;

        while (line.armSides.Count < line.arms.Count)
        {
            int i = line.armSides.Count;
            line.armSides.Add((i % 2 == 1) ? RobotLineSideType.Right : RobotLineSideType.Left);
        }
        if (line.armSides.Count > line.arms.Count)
            line.armSides.RemoveRange(line.arms.Count, line.armSides.Count - line.arms.Count);
    }

    /// <summary>여러 Transform을 라인 목록에 중복 없이 추가. 계층 순서(형제 인덱스)대로 정렬.</summary>
    private static void AddArms(LineSettings line, Transform[] toAdd)
    {
        if (toAdd == null || toAdd.Length == 0) return;

        var sorted = new List<Transform>(toAdd);
        sorted.Sort((x, y) => x.GetSiblingIndex().CompareTo(y.GetSiblingIndex()));

        Undo.RecordObject(line, "Add Line Arms");
        foreach (Transform t in sorted)
            if (t != null && !line.arms.Contains(t)) line.arms.Add(t);
        EditorUtility.SetDirty(line);
    }

    /// <summary>씬/하이러키에서 여러 오브젝트를 드래그&드롭으로 한꺼번에 추가하는 영역.</summary>
    private void DrawDropArea(LineSettings line)
    {
        Rect rect = GUILayoutUtility.GetRect(0, 30, GUILayout.ExpandWidth(true));
        GUI.Box(rect, "여기로 로봇팔 드래그&드롭 (여러 개 가능)", EditorStyles.helpBox);

        Event evt = Event.current;
        if (!rect.Contains(evt.mousePosition)) return;

        if (evt.type == EventType.DragUpdated)
        {
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            evt.Use();
        }
        else if (evt.type == EventType.DragPerform)
        {
            DragAndDrop.AcceptDrag();
            var transforms = new List<Transform>();
            foreach (Object obj in DragAndDrop.objectReferences)
            {
                if (obj is GameObject go) transforms.Add(go.transform);
                else if (obj is Transform tr) transforms.Add(tr);
            }
            AddArms(line, transforms.ToArray());
            evt.Use();
            Repaint();
        }
    }

    /// <summary>씬에서 GameObjectGroup을 찾아 LINE_1~LINE_6 자식을 슬롯에 할당. LineSettings 없으면 추가.</summary>
    private void AutoAssignLines()
    {
        GameObject group = GameObject.Find(GROUP_NAME);
        if (group == null)
        {
            Debug.LogWarning($"[로봇팔 배치기] 씬에서 \"{GROUP_NAME}\"을(를) 찾지 못했습니다.");
            return;
        }

        for (int i = 0; i < LINE_COUNT; i++)
        {
            Transform found = group.transform.Find($"LINE_{i + 1}");
            if (found == null)
            {
                lines[i] = null;
                Debug.LogWarning($"[로봇팔 배치기] \"{GROUP_NAME}\" 하위에서 LINE_{i + 1}을(를) 찾지 못했습니다.");
                continue;
            }

            if (!found.TryGetComponent(out LineSettings settings))
                settings = Undo.AddComponent<LineSettings>(found.gameObject);
            lines[i] = settings;
        }
        Repaint();
    }

    /// <summary>한 라인의 로봇팔들을 지그재그로 배치하고, StationController 회전·작업존을 설정한다.</summary>
    private void PlaceLine(LineSettings line)
    {
        if (line == null || line.arms.Count == 0) return;

        SyncSides(line); // 로봇팔별 방향이 arms와 1:1 대응되도록 보정

        // 왼쪽=X 감소, 오른쪽=X 증가
        float mainSign = (line.direction == LineSettings.Direction.Left) ? -1f : 1f;

        int placed = 0;
        for (int i = 0; i < line.arms.Count; i++)
        {
            Transform arm = line.arms[i];
            if (arm == null) continue;

            // 로봇팔별 선택된 방향. 라인 좌/우는 진행 방향 기준이므로 z·회전·박스 모두 dirSign으로 보정한다.
            // (방향 Left=+1 / Right=-1) → Right 방향 라인은 물리적 lane과 회전이 전부 반대가 된다.
            // StationController.ApplyLineSide와 동일한 규칙.
            bool isRight = (line.armSides[i] == RobotLineSideType.Right);
            float dirSign = (line.direction == LineSettings.Direction.Left) ? 1f : -1f;

            // 위치: X는 진행방향으로 (i+1)*간격, Z는 ±zSpacing×dirSign, Y는 기존 높이 유지
            float x = line.startXZ.x + mainSign * (line.spacing * (i + 1));
            float z = line.startXZ.y + (isRight ? line.zSpacing : -line.zSpacing) * dirSign;
            Vector3 pos = new Vector3(x, arm.position.y, z);

            // 회전 Y: (Right? +90 : -90) × dirSign — 차량을 향하도록 방향에 따라 뒤집힌다. StationController에만 적용.
            float yRot = (isRight ? 90f : -90f) * dirSign;

            // 배치되는 로봇팔은 항상 해당 라인의 자식으로 편입
            if (arm.parent != line.transform)
                Undo.SetTransformParent(arm, line.transform, "Parent Robot Arm");

            // 로봇팔 위치만 배치 (로봇팔 자체 회전은 건드리지 않음)
            Undo.RecordObject(arm, "Place Robot Arm");
            arm.position = pos;
            EditorUtility.SetDirty(arm);

            // 회전·작업존 콜라이더는 로봇팔이 아니라 StationController에 적용
            StationController station = ResolveStation(arm);
            if (station != null)
            {
                // SO의 부품별 씬 독립 값(Pile/End/작업존 size·boxCenterOffset)을 함께 적용할지.
                bool hasSOPlacement = applyPlacementFromSO && placementData != null && placementData.Has(station.targetPartType);
                StationPlacement soPlacement = hasSOPlacement
                    ? placementData.GetPlacement(station.targetPartType)
                    : default;
                // 작업존 center 오프셋: SO 우선, 없으면 스테이션이 들고 있는 현재값 유지.
                Vector3 centerOffset = hasSOPlacement ? soPlacement.boxCenterOffset : station.boxCenterOffset;

                Undo.RecordObject(station.transform, "Rotate Station");
                station.transform.rotation = Quaternion.Euler(0f, yRot, 0f);
                EditorUtility.SetDirty(station.transform);
                ApplyWorkZone(station.transform, line, isRight, boxSize, centerOffset);

                // ApplyWorkZone 뒤 → SO의 박스 크기가 공용 boxSize를 덮어쓰고(center는 위에서 오프셋 포함 계산),
                // SetRobotLineSide 앞 → 좌/우는 창에서 고른 armSides가 최종이 되게 한다.
                if (hasSOPlacement)
                {
                    var undoTargets = new List<Object> { station };
                    if (station.stationPilePos != null) undoTargets.Add(station.stationPilePos);
                    if (station.endPos != null) undoTargets.Add(station.endPos);
                    if (station.TryGetComponent<BoxCollider>(out var zoneBox)) undoTargets.Add(zoneBox);
                    Undo.RecordObjects(undoTargets.ToArray(), "Apply Station Placement");

                    station.ApplyPlacement(soPlacement);
                }

                // 선택된 방향을 StationController에 기록 → 인스펙터 robotLineSide와 동기화.
                Undo.RecordObject(station, "Set Robot Line Side");
                station.SetRobotLineSide(line.armSides[i]);

                // 배치 데이터 SO 참조만 바인딩한다. (SO에 좌표를 저장하지 않음 —
                //  다른 씬에서 배치하면 그 씬 좌표로 SO가 덮어써지는 것을 막기 위함)
                if (placementData != null)
                    station.placementData = placementData;
                EditorUtility.SetDirty(station);
            }
            else
            {
                Debug.LogWarning($"[로봇팔 배치기] '{arm.name}'에 연결된 StationController를 찾지 못해 회전/작업존을 건너뜁니다.");
            }
            placed++;
        }

        Debug.Log($"[로봇팔 배치기] {line.name} - {placed}개 배치 완료." +
            (placementData != null
                ? $" (배치 데이터 SO '{placementData.name}' 참조 바인딩{(applyPlacementFromSO ? " + 배치값 적용" : "")}, 저장 안 함)"
                : ""));
    }

    /// <summary>
    /// 로봇팔에 연결된 StationController를 찾는다.
    /// 부모 → 자기 자신 → 자식 순으로 탐색하고, 없으면 씬 전체에서 robotArm이
    /// 이 로봇팔(또는 그 부모/자식)을 가리키는 StationController를 찾는다.
    /// </summary>
    private static StationController ResolveStation(Transform arm)
    {
        StationController sc = arm.GetComponentInParent<StationController>();
        if (sc != null) return sc;
        sc = arm.GetComponentInChildren<StationController>();
        if (sc != null) return sc;

        foreach (StationController s in Object.FindObjectsOfType<StationController>())
        {
            if (s.robotArm == null) continue;
            Transform armT = s.robotArm.transform;
            if (armT == arm || armT.IsChildOf(arm) || arm.IsChildOf(armT)) return s;
        }
        return null;
    }

    /// <summary>StationController의 작업존 BoxCollider(전체 공용 size)와 center를 라인 폭/방향에 맞게 설정.</summary>
    private static void ApplyWorkZone(Transform station, LineSettings line, bool isRight, Vector3 size, Vector3 centerOffset)
    {
        if (!station.TryGetComponent<BoxCollider>(out var box))
            box = Undo.AddComponent<BoxCollider>(station.gameObject);

        Undo.RecordObject(box, "Set Work Zone Collider");
        box.isTrigger = true;
        box.size = size;

        // center = 라인 파생 기본값(zSpacing, 0.5, ±laneWidth) + 부품별 오프셋.
        // StationController.ApplyLineSide와 공용 공식(GetLineBoxCenter) — 두 경로가 항상 같은 결과를 낸다.
        box.center = StationController.GetLineBoxCenter(line, isRight, centerOffset);
        EditorUtility.SetDirty(box);
    }
}
