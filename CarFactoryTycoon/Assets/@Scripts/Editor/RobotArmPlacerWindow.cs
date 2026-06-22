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

    // 데이터는 씬의 LineSettings 컴포넌트가 보관. 폴드아웃은 창 UI 상태(비영속).
    private readonly LineSettings[] lines = new LineSettings[LINE_COUNT];
    private readonly bool[] foldout = new bool[LINE_COUNT];
    private readonly bool[] armsFoldout = new bool[LINE_COUNT];
    private Vector2 scroll;

    // 모든 라인 공용 작업존 박스 크기 (라인별이 아님). EditorPrefs에 영속 저장.
    private Vector3 boxSize = new Vector3(3f, 0.5f, 3f);

    [MenuItem("Tools/CarFactory/로봇팔 배치기")]
    private static void Open() => GetWindow<RobotArmPlacerWindow>("로봇팔 배치기");

    private void OnEnable()
    {
        for (int i = 0; i < LINE_COUNT; i++) { foldout[i] = true; armsFoldout[i] = true; }
        LoadBoxSize();
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
                AddArms(line, Selection.transforms);
            }
        }
        DrawDropArea(line);

        if (!armsFoldout[index]) return;

        for (int a = 0; a < line.arms.Count; a++)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                var t = (Transform)EditorGUILayout.ObjectField($"{a + 1}", line.arms[a], typeof(Transform), true);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(line, "Edit Line Arm");
                    line.arms[a] = t;
                    EditorUtility.SetDirty(line);
                }
                if (GUILayout.Button("－", GUILayout.Width(24)))
                {
                    Undo.RecordObject(line, "Remove Line Arm");
                    line.arms.RemoveAt(a);
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
                EditorUtility.SetDirty(line);
            }
        }
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

        // 왼쪽=X 감소, 오른쪽=X 증가
        float mainSign = (line.direction == LineSettings.Direction.Left) ? -1f : 1f;

        int placed = 0;
        for (int i = 0; i < line.arms.Count; i++)
        {
            Transform arm = line.arms[i];
            if (arm == null) continue;

            // 짝수 인덱스 = 라인 아래(-Z), 홀수 인덱스 = 라인 위(+Z)
            bool isAbove = (i % 2 == 1);

            // 위치: X는 진행방향으로 (i+1)*간격, Z는 ±zSpacing 지그재그, Y는 기존 높이 유지
            float x = line.startXZ.x + mainSign * (line.spacing * (i + 1));
            float z = line.startXZ.y + (isAbove ? line.zSpacing : -line.zSpacing);
            Vector3 pos = new Vector3(x, arm.position.y, z);

            // 회전: 아래라인 -90°, 위라인 +90° (Y축) — StationController에만 적용
            float yRot = isAbove ? 90f : -90f;

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
                Undo.RecordObject(station.transform, "Rotate Station");
                station.transform.rotation = Quaternion.Euler(0f, yRot, 0f);
                EditorUtility.SetDirty(station.transform);
                ApplyWorkZone(station.transform, line, isAbove, boxSize);
            }
            else
            {
                Debug.LogWarning($"[로봇팔 배치기] '{arm.name}'에 연결된 StationController를 찾지 못해 회전/작업존을 건너뜁니다.");
            }
            placed++;
        }

        Debug.Log($"[로봇팔 배치기] {line.name} - {placed}개 배치 완료.");
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
    private static void ApplyWorkZone(Transform station, LineSettings line, bool isAbove, Vector3 size)
    {
        if (!station.TryGetComponent<BoxCollider>(out var box))
            box = Undo.AddComponent<BoxCollider>(station.gameObject);

        Undo.RecordObject(box, "Set Work Zone Collider");
        box.isTrigger = true;
        box.size = size;

        // center.x: Z 지그재그 간격으로 적용
        // center.z: 왼쪽이면 아래 -폭/위 +폭, 오른쪽이면 반대
        float dirSign = (line.direction == LineSettings.Direction.Left) ? 1f : -1f;
        float centerZ = (isAbove ? line.laneWidth : -line.laneWidth) * dirSign;
        box.center = new Vector3(line.zSpacing, 0.5f, centerZ);
        EditorUtility.SetDirty(box);
    }
}
