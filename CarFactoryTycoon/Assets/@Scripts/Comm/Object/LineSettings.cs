using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 라인(LINE_1~LINE_6)별 로봇팔 배치 설정을 보관하는 컴포넌트.
/// "로봇팔 배치기" 에디터 툴이 이 컴포넌트를 읽고 써서 설정값이 씬에 저장된다.
/// (툴을 닫았다 다시 열어도 값이 초기화되지 않음)
/// </summary>
public class LineSettings : MonoBehaviour
{
    public enum Direction { Left, Right }

    [Header("배치할 로봇팔 목록")]
    public List<Transform> arms = new List<Transform>();

    [Header("배치 설정")]
    public Vector2 startXZ = Vector2.zero;          // 시작 (X, Z)
    public Direction direction = Direction.Left;    // 진행 방향(왼쪽=-X / 오른쪽=+X)
    public float spacing = 5f;                      // 로봇팔 간 X 진행 간격
    public float zSpacing = 5f;                     // Z 지그재그 간격
    [Tooltip("박스 콜라이더 z기준 (center.z 값)")]
    public float laneWidth = 3f;                    // 박스 콜라이더 z기준 (center.z용)
}
