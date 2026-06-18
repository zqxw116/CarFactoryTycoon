using UnityEditor;
using UnityEngine;

/// <summary>
/// 씬 뷰에서 AssemblyPart의 베지어 제어점(assembled / mid / mid2)을
/// 드래그 핸들로 직접 편집할 수 있게 하는 커스텀 에디터.
/// 좌표는 부모(차량) 로컬 기준이므로 부모 Transform으로 월드↔로컬 변환한다.
/// </summary>
[CustomEditor(typeof(AssemblyPart))]
public class AssemblyPartEditor : Editor
{
    private bool editRotation = false;

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var p = (AssemblyPart)target;

        // 체결 값 스크럽 슬라이더 (0 ~ requiredWork). 미리보기로 곡선 따라 이동 확인용.
        EditorGUILayout.Space();
        EditorGUI.BeginChangeCheck();
        float work = EditorGUILayout.Slider("체결 값 미리보기", p.currentWork, 0f, p.requiredWork);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(p, "Scrub AssemblyPart Work");
            p.SetWork(work);
            EditorUtility.SetDirty(p);
        }

        EditorGUILayout.Space();
        editRotation = EditorGUILayout.Toggle("회전 핸들 표시", editRotation);

        EditorGUILayout.HelpBox(
            "씬에서 초록(체결완료)·노랑(mid)·자홍(mid2) 핸들을 드래그해 경로를 편집하세요.\n" +
            "편집 후 아래 버튼으로 SO에 저장해야 영구 반영됩니다.", MessageType.Info);

        var part = (AssemblyPart)target;
        if (GUILayout.Button("★ 현재 값을 설계도(SO)에 저장"))
        {
            part.ApplyToSO();
        }
    }

    private void OnSceneGUI()
    {
        var part = (AssemblyPart)target;
        Transform parent = part.transform.parent;
        if (parent == null)
        {
            Handles.Label(part.transform.position, "부모(차량) Transform이 없어 편집 불가");
            return;
        }

        // 로컬 → 월드
        Vector3 wAssembled = parent.TransformPoint(part.assembledPos);
        Vector3 wMid       = parent.TransformPoint(part.midPos);
        Vector3 wMid2      = parent.TransformPoint(part.mid2Pos);

        // 베지어 곡선 미리보기 (assembled → mid → mid2). pile은 런타임 결정이라 제외.
        Handles.color = Color.cyan;
        Handles.DrawBezier(wAssembled, wMid2, wMid, wMid2, Color.cyan, null, 3f);

        // 제어점 연결선
        Handles.color = new Color(1f, 1f, 1f, 0.4f);
        Handles.DrawDottedLine(wAssembled, wMid, 4f);
        Handles.DrawDottedLine(wMid, wMid2, 4f);

        // 라벨
        Handles.color = Color.white;
        Handles.Label(wAssembled, "체결완료 (0.0)");
        Handles.Label(wMid, "mid");
        Handles.Label(wMid2, "mid2");

        EditorGUI.BeginChangeCheck();

        // 위치 핸들
        Vector3 nAssembled = Handles.PositionHandle(wAssembled, Quaternion.identity);
        Vector3 nMid       = Handles.PositionHandle(wMid, Quaternion.identity);
        Vector3 nMid2      = Handles.PositionHandle(wMid2, Quaternion.identity);

        // 회전 핸들 (옵션)
        Quaternion rAssembled = Quaternion.Euler(part.assembledRot);
        Quaternion rMid       = Quaternion.Euler(part.midRot);
        Quaternion rMid2      = Quaternion.Euler(part.mid2Rot);
        if (editRotation)
        {
            Quaternion pr = parent.rotation;
            rAssembled = Quaternion.Inverse(pr) * Handles.RotationHandle(pr * rAssembled, wAssembled);
            rMid       = Quaternion.Inverse(pr) * Handles.RotationHandle(pr * rMid, wMid);
            rMid2      = Quaternion.Inverse(pr) * Handles.RotationHandle(pr * rMid2, wMid2);
        }

        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(part, "Edit AssemblyPart Path");

            // 월드 → 로컬 변환 후 저장
            part.assembledPos = parent.InverseTransformPoint(nAssembled);
            part.midPos       = parent.InverseTransformPoint(nMid);
            part.mid2Pos      = parent.InverseTransformPoint(nMid2);

            if (editRotation)
            {
                part.assembledRot = rAssembled.eulerAngles;
                part.midRot       = rMid.eulerAngles;
                part.mid2Rot      = rMid2.eulerAngles;
            }

            EditorUtility.SetDirty(part);
        }
    }
}
