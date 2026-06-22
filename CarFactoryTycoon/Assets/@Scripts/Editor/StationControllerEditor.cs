using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

/// <summary>
/// StationController의 작업존(트리거 BoxCollider)을 씬 뷰에서 마우스 핸들로
/// 직접 크기 조정/편집할 수 있게 해주는 커스텀 에디터.
/// (오브젝트 자체의 배치는 기본 이동 툴(W)로, 작업존 크기/중심은 박스 핸들로 편집)
/// </summary>
[CustomEditor(typeof(StationController))]
public class StationControllerEditor : Editor
{
    private readonly BoxBoundsHandle boxHandle = new BoxBoundsHandle();

    private void OnSceneGUI()
    {
        var station = (StationController)target;
        if (!station.drawWorkZoneGizmo) return;
        if (!station.TryGetComponent<BoxCollider>(out var box)) return;

        // 핸들 행렬에 스케일을 넣으면 박스 핸들이 오브젝트 스케일만큼 커진다.
        // → 회전만 반영(스케일 제외)하고, center/size는 lossyScale로 직접 보정해
        //   실제 콜라이더 월드 크기는 유지하되 핸들 크기는 스케일과 무관하게 만든다.
        Vector3 scale = station.transform.lossyScale;
        Matrix4x4 matrix = Matrix4x4.TRS(
            station.transform.position, station.transform.rotation, Vector3.one);

        using (new Handles.DrawingScope(matrix))
        {
            // 1) 박스 면 핸들 → size 편집 (면을 끌면 center도 함께 보정됨)
            boxHandle.center = Vector3.Scale(box.center, scale);
            boxHandle.size = Vector3.Scale(box.size, scale);
            boxHandle.SetColor(station.workZoneColor);

            EditorGUI.BeginChangeCheck();
            boxHandle.DrawHandle();
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(box, "Edit Station Work Zone Size");
                box.center = InvScale(boxHandle.center, scale);
                box.size = InvScale(boxHandle.size, scale);
                EditorUtility.SetDirty(box);
            }

            // 2) 위치 핸들 → center 자유 이동
            EditorGUI.BeginChangeCheck();
            Vector3 newCenter = Handles.PositionHandle(Vector3.Scale(box.center, scale), Quaternion.identity);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(box, "Move Station Work Zone Center");
                box.center = InvScale(newCenter, scale);
                EditorUtility.SetDirty(box);
            }
        }

        // PilePos(파츠 대기)·EndPos(로봇팔 대기) 위치를 월드 공간에서 드래그 편집
        // (로봇팔 최대 도달범위를 벗어나지 못하도록 클램프)
        DrawRestHandle(station.stationPilePos, "Pile Pos\n(파츠 대기)", station.pilePosColor, station.robotArm);
        DrawRestHandle(station.endPos, "End Pos\n(로봇팔 대기)", station.endPosColor, station.robotArm);
    }

    /// <summary>요소별 나눗셈(0 방지). 월드 좌표를 다시 로컬(스케일 제거) 값으로 변환.</summary>
    private static Vector3 InvScale(Vector3 v, Vector3 s) => new Vector3(
        Mathf.Approximately(s.x, 0f) ? v.x : v.x / s.x,
        Mathf.Approximately(s.y, 0f) ? v.y : v.y / s.y,
        Mathf.Approximately(s.z, 0f) ? v.z : v.z / s.z);

    /// <summary>대기 위치 Transform을 씬 뷰 위치 핸들로 드래그 이동 + 라벨 표시. 로봇팔 도달범위로 클램프.</summary>
    private void DrawRestHandle(Transform t, string label, Color color, RoboticArmIK arm)
    {
        if (t == null) return;

        // 로봇팔 도달범위를 벗어나 있으면 먼저 범위 안으로 끌어들인다.
        Vector3 clamped = ClampToReach(t.position, arm);
        if (clamped != t.position)
        {
            Undo.RecordObject(t, "Clamp Station Rest Position");
            t.position = clamped;
            EditorUtility.SetDirty(t);
        }

        Handles.color = color;
        Handles.Label(t.position + Vector3.up * 0.25f, label);

        EditorGUI.BeginChangeCheck();
        Vector3 newPos = Handles.PositionHandle(t.position, t.rotation);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(t, "Move Station Rest Position");
            t.position = ClampToReach(newPos, arm); // 최대범위를 넘으면 경계까지만
            EditorUtility.SetDirty(t);
        }
    }

    /// <summary>pos가 로봇팔 도달 반경을 벗어나면 뿌리 기준 경계 위로 끌어당긴 좌표를 반환.</summary>
    private static Vector3 ClampToReach(Vector3 pos, RoboticArmIK arm)
    {
        if (arm == null || !arm.TryGetReach(out Vector3 basePos, out float reach) || reach <= 0f)
            return pos;

        Vector3 offset = pos - basePos;
        if (offset.magnitude <= reach) return pos;
        return basePos + offset.normalized * reach;
    }
}
