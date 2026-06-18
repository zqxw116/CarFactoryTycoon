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

        // 콜라이더는 트랜스폼 로컬 공간(center/size) 기준 → 핸들도 같은 공간에서 그린다.
        Matrix4x4 matrix = Matrix4x4.TRS(
            station.transform.position, station.transform.rotation, station.transform.lossyScale);

        using (new Handles.DrawingScope(matrix))
        {
            // 1) 박스 면 핸들 → size 편집 (면을 끌면 center도 함께 보정됨)
            boxHandle.center = box.center;
            boxHandle.size = box.size;
            boxHandle.SetColor(station.workZoneColor);

            EditorGUI.BeginChangeCheck();
            boxHandle.DrawHandle();
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(box, "Edit Station Work Zone Size");
                box.center = boxHandle.center;
                box.size = boxHandle.size;
                EditorUtility.SetDirty(box);
            }

            // 2) 위치 핸들 → center 자유 이동
            EditorGUI.BeginChangeCheck();
            Vector3 newCenter = Handles.PositionHandle(box.center, Quaternion.identity);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(box, "Move Station Work Zone Center");
                box.center = newCenter;
                EditorUtility.SetDirty(box);
            }
        }
    }
}
