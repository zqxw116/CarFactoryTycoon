#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Splines;
#endif

using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Splines;

/// <summary>
/// 직선 컨베이어 모듈 메시(예: Kenney Factory Kit)를 스플라인을 따라 이어 붙이고,
/// 정점마다 스플라인 진행도에 맞춰 휘게 만드는 시각 전용 컴포넌트.
/// - 스플라인 데이터는 읽기만 한다 (차량 이동용 스플라인을 건드리지 않음)
/// - 생성 메시는 씬에 저장하지 않고(DontSave) 활성화 때마다 다시 만든다
/// - 콜라이더 없음
/// - 폭은 구간별로 늘림: 벨트 면(가운데)만 목표 폭으로 늘리고, 옆 브래킷은 원래 두께로 바깥에 평행이동
/// - 벨트 윗면은 두 번째 서브메시로 분리해 줄무늬 머티리얼을 진행 방향으로 흘림 (MaterialPropertyBlock)
/// - 제외 구간(거리 범위)에는 벨트를 만들지 않고 양쪽을 끝단 모듈로 마감 (예: 라인 위 방수 테스트 수조)
/// - 구간식 흐름(어큐뮬레이션 컨베이어): 윗면을 구간(zone)별 서브메시로 나누고, 플레이 중 각 구간은
///   그 위 차량의 실제 이동 속도 중 최소값(차 없으면 라인 속도)으로 흐름 → 정체 차량 아래 벨트만 멈춤
/// </summary>
[ExecuteAlways]
[DisallowMultipleComponent]
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class SplineConveyorDeformer : MonoBehaviour
{
    public enum LengthAxis { X, Z }

    [Header("★ 대상 스플라인 ★")]
    [Tooltip("벨트를 따라 놓을 스플라인 (컨테이너 안의 모든 스플라인이 대상)")]
    [SerializeField] SplineContainer m_Spline;

    [Header("★ 모듈 모델 ★")]
    [Tooltip("반복해서 이어 붙일 가운데 모듈 (FBX 모델 또는 프리팹)")]
    [SerializeField] GameObject m_MiddleModule;
    [Tooltip("스플라인 양 끝에 붙일 끝단 모듈 (비우면 가운데 모듈만 사용, 닫힌 스플라인은 무시)")]
    [SerializeField] GameObject m_EndModule;
    [Tooltip("끝단 모듈의 마감(롤러) 쪽이 길이축 + 방향이면 체크 (Kenney FBX는 임포트 시 X가 뒤집혀 끔이 맞음)")]
    [SerializeField] bool m_EndCapAtPositive = false;
    [Tooltip("모듈 모델의 길이 방향 축")]
    [SerializeField] LengthAxis m_LengthAxis = LengthAxis.X;
    [Tooltip("비우면 모듈 모델의 머티리얼을 그대로 사용")]
    [SerializeField] Material m_Material;

    [Header("★ 크기·높이 ★")]
    [Tooltip("벨트 면(윗면) 폭 (m). 0 이하이면 모델 원래 폭 사용. 옆 브래킷은 이 폭 바깥에 붙음")]
    [SerializeField] float m_Width = 2f;
    [Tooltip("스플라인 번호별 벨트 면 폭 (m). 비었거나 0 이하이면 위 기본 폭 사용")]
    [SerializeField] List<float> m_SplineWidths = new List<float>();
    [Tooltip("옆 브래킷 두께 배율 (1 = 모델 원래 두께). 벨트 면 폭과 무관하게 유지")]
    [SerializeField] float m_BracketScale = 1.5f;
    [Tooltip("높이 배율 (모델 두께)")]
    [SerializeField] float m_HeightScale = 1f;
    [Tooltip("스플라인 위로 띄우는 높이 (m). 음수면 아래로")]
    [SerializeField] float m_HeightOffset = 0f;
    [Tooltip("모듈 하나의 목표 길이 (m). 0 이하이면 모델 원래 길이. 스플라인 길이에 맞게 정수 개로 나눈 뒤 살짝 늘림")]
    [SerializeField] float m_ModuleLength = 0f;

    [Tooltip("모듈을 길이 방향으로 잘게 나누는 간격 (모델 원래 크기 기준 m). 곡선이 매끄러워지는 대신 정점 증가. 0이면 자르지 않음")]
    [SerializeField] float m_SliceStep = 0.25f;

    [Header("★ 비교용 ★")]
    [Tooltip("끄면 모듈을 휘지 않고 중심 위치에만 놓음 (SplineInstantiate 방식과 동일한 결과 비교용)")]
    [SerializeField] bool m_Deform = true;

    [Header("★ 벨트 면 줄무늬 ★")]
    [Tooltip("벨트 윗면 전용 머티리얼 (비우면 윗면도 모듈 머티리얼로 그림, 흐름 없음)")]
    [SerializeField] Material m_SurfaceMaterial;
    [Tooltip("줄무늬 간격 (m) = 이웃한 홈 사이 거리. 텍스처 한 장에 홈 8줄 → 한 장이 덮는 길이 = 간격 × 8.\n" +
             "스플라인 누적 거리 기준이라 곡선에서도 밀도 균일. 메시 재생성 없이 즉시 반영, 흐름 속도도 자동 환산")]
    [Range(0.05f, 2f)]
    [SerializeField] float m_StripeSpacing = 0.125f;
    [Tooltip("벨트 윗면만 길이 방향으로 더 잘게 자르는 간격 (모델 원래 크기 기준 m). 곡선에서 줄무늬가 꺾여 보이는 것을 줄임. 0이면 자르지 않음")]
    [SerializeField] float m_SurfaceSliceStep = 0.0625f;
    [Tooltip("켜면 줄무늬가 차량 진행 방향으로 흐름. 끄면 그 자리에 정지\n(기본 켬 — 차량 정체 중에도 벨트는 항상 흐름)")]
    [SerializeField] bool m_FlowStripes = true;
    [Tooltip("켜면 CarSpawner 라인 속도 × 업그레이드 배율로 흐름 (차량 진행 속도와 일치)")]
    [SerializeField] bool m_SyncLineSpeed = true;
    [Tooltip("라인 속도 출처 (비우면 씬에서 자동 검색)")]
    [SerializeField] CarSpawner m_LineSource;
    [Tooltip("라인 속도 연동을 끄거나 출처가 없을 때의 흐름 속도 (m/s)")]
    [SerializeField] float m_ScrollSpeed = 1f;
    [Tooltip("편집 모드(비플레이)에서도 흐름 미리보기 (씬 뷰가 계속 다시 그려짐)")]
    [SerializeField] bool m_PreviewInEditor = false;

    [Header("★ 구간식 흐름 (어큐뮬레이션) ★")]
    [Tooltip("켜면 벨트를 구간으로 나눠 각 구간이 그 위 차량 속도로 흐름 (정체 차량 아래는 정지, 빈 구간은 라인 속도).\n" +
             "끄면 전체가 한 속도로 흐름. 플레이 중에만 적용 (편집 모드 미리보기는 전체 동일)")]
    [SerializeField] bool m_ZonedFlow = true;
    [Tooltip("구간 하나의 목표 길이 (m). 모듈 경계에 맞춰 정수 개 모듈로 묶음. 차간 최소 간격(6.3m) 근처면 구간당 차 0~1대")]
    [SerializeField] float m_ZoneLength = 6f;
    [Tooltip("구간 속도가 0 ↔ 라인 속도로 바뀌는 데 걸리는 시간 (초). 덜컥거림 방지")]
    [SerializeField] float m_ZoneAccelTime = 0.4f;
    [Tooltip("차체 반길이 (m). 차량 중심 ± 반길이가 겹치는 구간을 그 차량 아래로 판정 (콜라이더 길이 5.12 기준)")]
    [SerializeField] float m_CarHalfLength = 2.6f;

    [Header("★ 벨트 제외 구간 ★")]
    [Tooltip("벨트를 만들지 않을 구간 목록 (스플라인 번호 + 시작·끝 거리 m). 구간 양쪽은 끝단 모듈로 마감.\n" +
             "예: 방수 테스트 수조처럼 라인 위에 설비가 걸쳐 있는 곳. 비우면 기존과 동일")]
    [SerializeField] List<ExcludeRange> m_ExcludeRanges = new List<ExcludeRange>();

    [Header("★ 기존 벨트 ★")]
    [Tooltip("이 벨트가 켜져 있는 동안 숨길 기존 벨트 렌더러 (MainSpline 로프트 메시)")]
    [SerializeField] Renderer m_OriginalBeltRenderer;
    [Tooltip("켜면 이 컴포넌트 활성 시 기존 벨트 렌더러를 끄고, 비활성 시 다시 켬")]
    [SerializeField] bool m_HideOriginalBelt = true;

    /// <summary>벨트를 생성하지 않을 스플라인 구간 (거리 m 기준, start &lt; end).</summary>
    [System.Serializable]
    public struct ExcludeRange
    {
        [Tooltip("대상 스플라인 번호 (컨테이너 안 순서)")]
        public int splineIndex;
        [Tooltip("제외 시작 거리 (m, 스플라인 시작점 기준)")]
        public float start;
        [Tooltip("제외 끝 거리 (m)")]
        public float end;
    }

    // 모듈 하나를 길이축 +Z, 폭 X, 높이 Y 공간으로 정리해 둔 캐시
    class ModuleData
    {
        public Vector3[] positions;
        public Vector3[] normals;
        public Vector2[] uvs;
        public int[] indices;      // 벨트 윗면을 뺀 나머지
        public int[] topIndices;   // 벨트 윗면 (위를 향한 가운데 면)
        public bool[] isTop;       // 정점이 벨트 윗면 전용인지 (UV를 새로 계산)
        public float minZ, length, halfWidth;
        public Material material;
    }

    static readonly int s_BaseMapST = Shader.PropertyToID("_BaseMap_ST");
    static readonly int s_MainTexST = Shader.PropertyToID("_MainTex_ST");

    // 줄무늬 텍스처(KenneyBeltStripe) 한 장에 들어 있는 홈 개수
    const int StripesPerTexture = 8;

    Mesh m_Mesh;
    bool m_RebuildRequested;
    int m_BuiltMeshHash;   // 마지막으로 메시를 만든 설정값 해시 (줄무늬 간격·흐름만 바뀌면 재생성 생략)
    MaterialPropertyBlock m_Block;
    float m_ScrollOffset;
#if UNITY_EDITOR
    double m_LastEditorTime;
#endif

    readonly List<Vector3> m_Positions = new List<Vector3>();
    readonly List<Vector3> m_Normals = new List<Vector3>();
    readonly List<Vector2> m_Uvs = new List<Vector2>();
    readonly List<int> m_Indices = new List<int>();
    readonly List<int> m_TopIndices = new List<int>();

    // 구간식 흐름: 구간별 윗면 삼각형(서브메시 1+k) 과 거리 범위. 0번 스플라인 구간이 앞쪽에 오름차순으로 쌓인다
    bool m_BuildZoned;                 // 이번 Rebuild에서 윗면을 구간으로 나누는지
    int m_ZoneCount;                   // 전체 구간 수 (= 윗면 서브메시 수)
    int m_Spline0ZoneCount;            // 0번 스플라인(차량 이동 스플라인) 구간 수
    float m_Spline0Length;             // 0번 스플라인 길이 (m, 로컬) — 차량 진행도 → 거리 환산
    readonly List<List<int>> m_ZoneTris = new List<List<int>>();
    readonly List<float> m_ZoneStart = new List<float>();
    readonly List<float> m_ZoneEnd = new List<float>();
    float[] m_ZoneOffset = new float[0];
    float[] m_ZoneSpeed = new float[0];
    float[] m_ZoneTarget = new float[0];
    int m_SurfaceSubmeshCount;         // 줄무늬 서브메시 수 (구간식이면 구간 수, 아니면 1, 줄무늬 없으면 0)
    MeshRenderer m_Renderer;

    // 차량 실제 속도 측정 (프레임 간 진행도 변화). 매 프레임 재사용해 GC 0
    LineTrafficManager m_Traffic;
    int m_NextTrafficSearchFrame;
    readonly Dictionary<CarController, Vector2> m_PrevProgress = new Dictionary<CarController, Vector2>(); // (진행도, 프레임)
    readonly List<float> m_CarCenter = new List<float>();
    readonly List<float> m_CarSpeed = new List<float>();

    public bool enableWalkableSurface;
    public Vector3 GetSurfaceVelocity(Vector3 worldPoint)
    {
        if (m_Spline == null) return Vector3.zero;
        float3 local = m_Spline.transform.InverseTransformPoint(worldPoint);
        float best = float.MaxValue, nearestT = 0f; int nearest = 0;
        for (int i = 0; i < m_Spline.Splines.Count; i++)
        {
            SplineUtility.GetNearestPoint(m_Spline.Splines[i], local, out float3 point, out float t);
            float distance = math.lengthsq(point - local);
            if (distance < best) { best = distance; nearestT = t; nearest = i; }
        }
        float speed = CurrentLineSpeed();
        if (nearest == 0 && m_ZoneSpeed.Length > 0)
        {
            float distance = nearestT * m_Spline0Length;
            for (int i = 0; i < m_Spline0ZoneCount; i++)
                if (distance >= m_ZoneStart[i] && distance <= m_ZoneEnd[i]) { speed = m_ZoneSpeed[i]; break; }
        }
        var tangent = (Vector3)SplineUtility.EvaluateTangent(m_Spline.Splines[nearest], nearestT);
        return Vector3.ProjectOnPlane(m_Spline.transform.TransformDirection(tangent), Vector3.up).normalized * speed;
    }
    public SplineContainer Spline { get => m_Spline; set { m_Spline = value; RequestRebuild(); } }

    void OnEnable()
    {
        Rebuild();
        if (m_LineSource == null) m_LineSource = FindAnyObjectByType<CarSpawner>();
        UnityEngine.Splines.Spline.Changed += OnSplineChanged;
#if UNITY_EDITOR
        EditorSplineUtility.AfterSplineWasModified += OnAfterSplineWasModified;
        Undo.undoRedoPerformed += RequestRebuild;
#endif
    }

    void OnDisable()
    {
        UnityEngine.Splines.Spline.Changed -= OnSplineChanged;
#if UNITY_EDITOR
        EditorSplineUtility.AfterSplineWasModified -= OnAfterSplineWasModified;
        Undo.undoRedoPerformed -= RequestRebuild;
#endif
        // 이 벨트가 꺼지면 기존 벨트를 다시 보이게
        if (m_OriginalBeltRenderer != null) m_OriginalBeltRenderer.enabled = true;
        GetComponent<MeshFilter>().sharedMesh = null;
        if (m_Mesh != null)
        {
            if (Application.isPlaying) Destroy(m_Mesh); else DestroyImmediate(m_Mesh);
            m_Mesh = null;
        }
    }

    void OnValidate()
    {
        // 줄무늬 간격·흐름은 MPB 타일링만 바꾸면 되므로 즉시 반영, 메시 관련 값이 바뀐 경우에만 재생성
        ApplySurfaceST();
        if (MeshSettingsHash() != m_BuiltMeshHash) RequestRebuild();
#if UNITY_EDITOR
        else if (!Application.isPlaying) EditorApplication.QueuePlayerLoopUpdate();
#endif
    }

    void Update()
    {
        if (m_RebuildRequested)
        {
            m_RebuildRequested = false;
            Rebuild();
        }
        if (!Application.isPlaying) UpdateScroll();
    }

    // 플레이 중에는 LateUpdate에서 흘림 — 트래픽 매니저가 Update에서 차량을 옮긴 뒤라 이번 프레임 이동량을 바로 읽음
    void LateUpdate()
    {
        if (Application.isPlaying) UpdateScroll();
    }

    /// <summary>줄무늬 텍스처 한 장이 덮는 길이 (m) = 줄무늬 간격 × 홈 개수.</summary>
    float StripeTileLength => Mathf.Max(0.01f, m_StripeSpacing) * StripesPerTexture;

    /// <summary>
    /// 벨트 윗면 줄무늬를 차량 진행 방향(스플라인 거리 증가 방향)으로 흘린다.
    /// UV V = 스플라인 거리(m)이고 타일링 = 1 / 타일 길이, 오프셋은 텍스처 단위라
    /// 속도(m/s) / 타일 길이 로 환산하면 간격을 바꿔도 줄무늬가 차량 속도와 일치한다.
    /// 무늬가 +V로 가려면 오프셋을 줄여야 한다. 흐름을 끄면 오프셋 고정(정지).
    /// </summary>
    void UpdateScroll()
    {
        if (m_SurfaceMaterial == null || !m_FlowStripes) return;

        float dt;
        if (Application.isPlaying) dt = Time.deltaTime;
        else
        {
#if UNITY_EDITOR
            if (!m_PreviewInEditor) return;
            double now = EditorApplication.timeSinceStartup;
            dt = m_LastEditorTime > 0d ? Mathf.Min((float)(now - m_LastEditorTime), 0.1f) : 0f;
            m_LastEditorTime = now;
            EditorApplication.QueuePlayerLoopUpdate(); // 다음 프레임도 Update가 돌도록
#else
            return;
#endif
        }

        float lineSpeed = CurrentLineSpeed();
        m_ScrollOffset = Mathf.Repeat(m_ScrollOffset - lineSpeed * dt / StripeTileLength, 1f);
        if (Application.isPlaying && m_BuildZoned && m_ZoneCount > 0 && dt > 0f) UpdateZones(lineSpeed, dt);
        ApplySurfaceST();
    }

    /// <summary>
    /// 구간별 목표 속도(그 위 차량 실제 속도 최소값, 차 없으면 라인 속도)를 구하고,
    /// 가감속 시간에 맞춰 선형으로 따라간 뒤 구간 오프셋을 각자 누적한다.
    /// </summary>
    void UpdateZones(float lineSpeed, float dt)
    {
        CollectCars(lineSpeed, dt);
        ComputeZoneTargets(m_ZoneStart, m_ZoneEnd, m_Spline0ZoneCount, m_CarCenter, m_CarSpeed,
            m_CarHalfLength, lineSpeed, m_ZoneTarget);

        // 0 ↔ 라인 속도를 가감속 시간 안에 오가는 기울기 (라인 속도 0이어도 멈춰 있지 않게 바닥값)
        float step = m_ZoneAccelTime > 0f ? Mathf.Max(lineSpeed, 0.1f) / m_ZoneAccelTime * dt : float.MaxValue;
        float invTile = 1f / StripeTileLength;
        for (int k = 0; k < m_ZoneCount; k++)
        {
            m_ZoneSpeed[k] = Mathf.MoveTowards(m_ZoneSpeed[k], m_ZoneTarget[k], step);
            m_ZoneOffset[k] = Mathf.Repeat(m_ZoneOffset[k] - m_ZoneSpeed[k] * dt * invTile, 1f);
        }
    }

    /// <summary>
    /// 트래픽 매니저의 활성 차량(선두=0번, 진행도 내림차순)에서 이 벨트 스플라인 위 차량의
    /// 중심 거리(m)와 이번 프레임 실제 속도(m/s)를 뽑는다. 크레인에 들린 차(LeaveLine → targetSpline null)는 제외.
    /// </summary>
    void CollectCars(float lineSpeed, float dt)
    {
        m_CarCenter.Clear(); m_CarSpeed.Clear();
        // MonoSingleton.Instance는 없으면 새로 만들 수 있으므로 직접 찾아 캐시 (없으면 1초 간격으로 재시도)
        if (m_Traffic == null && Time.frameCount >= m_NextTrafficSearchFrame)
        {
            m_Traffic = FindFirstObjectByType<LineTrafficManager>();
            m_NextTrafficSearchFrame = Time.frameCount + 60;
        }
        if (m_Traffic == null) return;

        if (m_PrevProgress.Count > 512) m_PrevProgress.Clear(); // 파괴된 차량 키가 쌓이는 것 방지 (풀링이라 보통 도달 안 함)
        int frame = Time.frameCount;
        var cars = m_Traffic.ActiveCars;
        for (int i = 0; i < cars.Count; i++)
        {
            var car = cars[i];
            if (car == null || !car.isActiveAndEnabled || car.targetSpline != m_Spline) continue;

            float p = car.pathProgress;
            float maxSpeed = Mathf.Max(lineSpeed, car.moveSpeed);
            float speed;
            // 직전 프레임 기록이 있고 뒤로 가지 않았으면 실측 (풀 재사용으로 진행도가 리셋된 경우는 새 차로 취급)
            if (m_PrevProgress.TryGetValue(car, out var prev) && (int)prev.y == frame - 1 && p >= prev.x - 0.0001f)
                speed = Mathf.Clamp((p - prev.x) * car.SplineLength / dt, 0f, maxSpeed);
            else
                speed = car.IsDriving ? Mathf.Min(car.moveSpeed, maxSpeed) : 0f;
            m_PrevProgress[car] = new Vector2(p, frame);

            m_CarCenter.Add(p * m_Spline0Length);
            m_CarSpeed.Add(speed);
        }
    }

    /// <summary>
    /// 구간 목표 속도 계산 (순수 함수, 에디터에서 가짜 데이터로 검증 가능).
    /// zoneStart/zoneEnd: 0..zoneCount-1 은 0번 스플라인 구간(거리 오름차순), 그 뒤는 다른 스플라인 구간(차 없음 → 라인 속도).
    /// carCenter/carSpeed: 차량 중심 거리 내림차순(선두 먼저). 차량 [중심 ± 반길이]와 겹치는 구간은 그 차 속도 이하로.
    /// 차량이 뒤로 갈수록 상한(중심+반길이)도 줄어드므로 '앞쪽으로 완전히 벗어난 구간' 포인터는 되돌아가지 않음 → O(n+m).
    /// </summary>
    public static void ComputeZoneTargets(List<float> zoneStart, List<float> zoneEnd, int zoneCount,
        List<float> carCenter, List<float> carSpeed, float carHalfLength, float lineSpeed, float[] target)
    {
        for (int k = 0; k < target.Length; k++) target[k] = lineSpeed;
        int j = zoneCount - 1;
        for (int c = 0; c < carCenter.Count; c++)
        {
            float lo = carCenter[c] - carHalfLength, hi = carCenter[c] + carHalfLength;
            while (j >= 0 && zoneStart[j] >= hi) j--;
            for (int k = j; k >= 0 && zoneEnd[k] > lo; k--)
                if (carSpeed[c] < target[k]) target[k] = carSpeed[c];
        }
    }

    /// <summary>
    /// 윗면 슬롯(1번~)에만 MaterialPropertyBlock으로 _BaseMap_ST(타일링 = 1/타일 길이, 오프셋)를 넣는다.
    /// 머티리얼 인스턴스를 만들지 않으며, 메시 UV가 거리(m)라 간격 변경은 이 값만 바꾸면 된다.
    /// 구간식 흐름(플레이 중)이면 구간마다 자기 오프셋, 아니면 전 구간 공통 오프셋.
    /// </summary>
    void ApplySurfaceST()
    {
        if (m_SurfaceMaterial == null || m_SurfaceSubmeshCount == 0) return;
        if (m_Renderer == null) m_Renderer = GetComponent<MeshRenderer>();
        if (m_Renderer == null) return;
        if (m_Block == null) m_Block = new MaterialPropertyBlock();
        bool perZone = Application.isPlaying && m_BuildZoned && m_ZoneOffset.Length == m_SurfaceSubmeshCount;
        float tiling = 1f / StripeTileLength;
        for (int k = 0; k < m_SurfaceSubmeshCount; k++)
        {
            var st = new Vector4(1f, tiling, 0f, perZone ? m_ZoneOffset[k] : m_ScrollOffset);
            m_Block.SetVector(s_BaseMapST, st);
            m_Block.SetVector(s_MainTexST, st);
            m_Renderer.SetPropertyBlock(m_Block, 1 + k);
        }
    }

    /// <summary>메시 모양에 영향을 주는 설정값 해시. 줄무늬 간격·흐름·속도 관련 값은 넣지 않는다.</summary>
    int MeshSettingsHash()
    {
        unchecked
        {
            int h = 17;
            void Add(int v) => h = h * 31 + v;
            Add(m_Spline != null ? m_Spline.GetHashCode() : 0);
            Add(m_MiddleModule != null ? m_MiddleModule.GetHashCode() : 0);
            Add(m_EndModule != null ? m_EndModule.GetHashCode() : 0);
            Add(m_EndCapAtPositive ? 1 : 0);
            Add((int)m_LengthAxis);
            Add(m_Material != null ? m_Material.GetHashCode() : 0);
            Add(m_Width.GetHashCode());
            if (m_SplineWidths != null) foreach (var w in m_SplineWidths) Add(w.GetHashCode());
            Add(m_BracketScale.GetHashCode());
            Add(m_HeightScale.GetHashCode());
            Add(m_HeightOffset.GetHashCode());
            Add(m_ModuleLength.GetHashCode());
            Add(m_SliceStep.GetHashCode());
            Add(m_Deform ? 1 : 0);
            Add(m_SurfaceMaterial != null ? m_SurfaceMaterial.GetHashCode() : 0);
            Add(m_SurfaceSliceStep.GetHashCode());
            Add(m_OriginalBeltRenderer != null ? m_OriginalBeltRenderer.GetHashCode() : 0);
            Add(m_HideOriginalBelt ? 1 : 0);
            Add(m_ZonedFlow ? 1 : 0);
            Add(m_ZoneLength.GetHashCode());
            if (m_ExcludeRanges != null)
                foreach (var r in m_ExcludeRanges) { Add(r.splineIndex); Add(r.start.GetHashCode()); Add(r.end.GetHashCode()); }
            return h;
        }
    }

    /// <summary>차량 라인 속도 (m/s). CarSpawner와 같은 계산: globalLineSpeed × 업그레이드 배율.</summary>
    float CurrentLineSpeed()
    {
        if (!m_SyncLineSpeed || m_LineSource == null) return m_ScrollSpeed;
        // UpgradeManager.Instance는 없으면 새로 만들므로 플레이 중에만 조회 (CarSpawner도 플레이 중 조회)
        float multiplier = Application.isPlaying ? UpgradeManager.Instance.lineSpeedMultiplier : 1f;
        return m_LineSource.globalLineSpeed * (m_LineSource.manualInput ? 1f : multiplier);
    }

    /// <summary>이 벨트가 켜져 있는 동안 기존 벨트(MainSpline 로프트 메시) 렌더러를 숨긴다. 끄면 OnDisable에서 복구.</summary>
    void ApplyOriginalBeltVisibility()
    {
        if (m_OriginalBeltRenderer == null) return;
        bool visible = !m_HideOriginalBelt;
        if (m_OriginalBeltRenderer.enabled != visible) m_OriginalBeltRenderer.enabled = visible;
    }

    void OnSplineChanged(UnityEngine.Splines.Spline spline, int knotIndex, SplineModification modification) => RequestRebuildIfMine(spline);
    void OnAfterSplineWasModified(UnityEngine.Splines.Spline spline) => RequestRebuildIfMine(spline);

    void RequestRebuildIfMine(UnityEngine.Splines.Spline spline)
    {
        if (m_Spline == null) return;
        foreach (var s in m_Spline.Splines)
            if (s == spline) { RequestRebuild(); return; }
    }

    void RequestRebuild()
    {
        m_RebuildRequested = true;
#if UNITY_EDITOR
        if (!Application.isPlaying) EditorApplication.QueuePlayerLoopUpdate();
#endif
    }

    [ContextMenu("★ 벨트 다시 생성")]
    public void Rebuild()
    {
        var filter = GetComponent<MeshFilter>();
        if (m_Mesh == null)
        {
            // 씬 파일에 메시 데이터가 저장되지 않도록 DontSave
            m_Mesh = new Mesh { name = "SplineConveyor (생성)", hideFlags = HideFlags.DontSave, indexFormat = IndexFormat.UInt32 };
        }
        m_Mesh.Clear();
        m_BuiltMeshHash = MeshSettingsHash();
        m_Positions.Clear(); m_Normals.Clear(); m_Uvs.Clear(); m_Indices.Clear(); m_TopIndices.Clear();
        m_ZoneCount = 0; m_Spline0ZoneCount = 0; m_Spline0Length = 0f;
        m_ZoneStart.Clear(); m_ZoneEnd.Clear();
        m_SurfaceSubmeshCount = 0;
        // 구간 분할은 줄무늬 머티리얼이 있고 구간식 흐름이 켜졌을 때만 (끄면 기존처럼 윗면 서브메시 1개)
        m_BuildZoned = m_SurfaceMaterial != null && m_ZonedFlow && m_ZoneLength > 0f;
        ApplyOriginalBeltVisibility();

        var middle = BuildModule(m_MiddleModule);
        if (m_Spline == null || middle == null) { filter.sharedMesh = m_Mesh; return; }
        var end = BuildModule(m_EndModule);

        var splines = m_Spline.Splines;
        for (int i = 0; i < splines.Count; i++)
        {
            float width = i < m_SplineWidths.Count && m_SplineWidths[i] > 0f ? m_SplineWidths[i] : m_Width;
            AppendSpline(i, splines[i], middle, end, width);
            if (i == 0)
            {
                m_Spline0ZoneCount = m_ZoneCount;
                m_Spline0Length = splines[0] != null ? splines[0].GetLength() : 0f;
            }
        }

        m_Mesh.SetVertices(m_Positions);
        m_Mesh.SetNormals(m_Normals);
        m_Mesh.SetUVs(0, m_Uvs);
        // 줄무늬 머티리얼이 있으면 윗면을 1번 서브메시로(구간식이면 1~N번 구간별), 없으면 0번에 합침
        bool split = m_SurfaceMaterial != null;
        bool zoned = split && m_BuildZoned && m_ZoneCount > 0;
        if (!zoned) m_BuildZoned = false;
        m_SurfaceSubmeshCount = !split ? 0 : zoned ? m_ZoneCount : 1;
        if (!split) m_Indices.AddRange(m_TopIndices);
        m_Mesh.subMeshCount = 1 + m_SurfaceSubmeshCount;
        m_Mesh.SetIndices(m_Indices, MeshTopology.Triangles, 0);
        if (zoned)
            for (int k = 0; k < m_ZoneCount; k++) m_Mesh.SetIndices(m_ZoneTris[k], MeshTopology.Triangles, 1 + k);
        else if (split) m_Mesh.SetIndices(m_TopIndices, MeshTopology.Triangles, 1);
        m_Mesh.RecalculateBounds();
        filter.sharedMesh = m_Mesh;
        if (enableWalkableSurface)
        {
            var collision = GetComponent<MeshCollider>();
            if (collision == null) collision = gameObject.AddComponent<MeshCollider>();
            collision.sharedMesh = null; collision.sharedMesh = m_Mesh;
        }

        // 구간 상태 초기화: 공통 오프셋에서 출발, 라인 속도로 흐르는 상태
        if (m_ZoneOffset.Length != m_ZoneCount)
        {
            m_ZoneOffset = new float[m_ZoneCount];
            m_ZoneSpeed = new float[m_ZoneCount];
            m_ZoneTarget = new float[m_ZoneCount];
        }
        float initSpeed = CurrentLineSpeed();
        for (int k = 0; k < m_ZoneCount; k++) { m_ZoneOffset[k] = m_ScrollOffset; m_ZoneSpeed[k] = initSpeed; }

        var mat = m_Material != null ? m_Material : middle.material;
        var rend = GetComponent<MeshRenderer>();
        m_Renderer = rend;
        var current = rend.sharedMaterials;
        bool same = current.Length == 1 + m_SurfaceSubmeshCount && current[0] == mat;
        for (int k = 1; same && k < current.Length; k++) same = current[k] == m_SurfaceMaterial;
        if (mat != null && !same)
        {
            var mats = new Material[1 + m_SurfaceSubmeshCount];
            mats[0] = mat;
            for (int k = 1; k < mats.Length; k++) mats[k] = m_SurfaceMaterial;
            rend.sharedMaterials = mats;
        }
        ApplySurfaceST(); // 흐름을 꺼도 줄무늬 간격(타일링)은 항상 반영
    }

    /// <summary>구간 하나를 추가하고 그 윗면 삼각형 목록을 돌려준다 (리스트는 재생성 간 재사용).</summary>
    List<int> AddZone(float start, float end)
    {
        if (m_ZoneCount >= m_ZoneTris.Count) m_ZoneTris.Add(new List<int>());
        var tris = m_ZoneTris[m_ZoneCount++];
        tris.Clear();
        m_ZoneStart.Add(start);
        m_ZoneEnd.Add(end);
        return tris;
    }

    /// <summary>모듈 GameObject의 모든 MeshFilter를 루트 기준으로 합치고, 길이축을 +Z로 돌려 정리한다.</summary>
    ModuleData BuildModule(GameObject module)
    {
        if (module == null) return null;
        var filters = module.GetComponentsInChildren<MeshFilter>(true);
        if (filters.Length == 0) return null;

        // 길이축 X → +Z 로 회전 (Y축 -90도)
        var axisRot = m_LengthAxis == LengthAxis.X ? Quaternion.Euler(0f, -90f, 0f) : Quaternion.identity;
        var rootInv = module.transform.worldToLocalMatrix;

        var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var uv = new List<Vector2>(); var idx = new List<int>();
        Material material = null;
        foreach (var f in filters)
        {
            var src = f.sharedMesh;
            if (src == null) continue;
            if (material == null)
            {
                var r = f.GetComponent<MeshRenderer>();
                if (r != null) material = r.sharedMaterial;
            }
            var m = Matrix4x4.Rotate(axisRot) * rootInv * f.transform.localToWorldMatrix;
            int baseIndex = pos.Count;
            var v = src.vertices; var n = src.normals; var u = src.uv;
            for (int i = 0; i < v.Length; i++)
            {
                pos.Add(m.MultiplyPoint3x4(v[i]));
                nrm.Add(n.Length == v.Length ? m.MultiplyVector(n[i]).normalized : Vector3.up);
                uv.Add(u.Length == v.Length ? u[i] : Vector2.zero);
            }
            for (int s = 0; s < src.subMeshCount; s++)
                foreach (var t in src.GetTriangles(s)) idx.Add(baseIndex + t);
        }
        if (pos.Count == 0) return null;

        // 폭 기준은 윗면(벨트 면)의 폭. 옆 브래킷이 더 넓어도 벨트 면을 목표 폭에 맞춘다
        float minZ = float.MaxValue, maxZ = float.MinValue, maxX = 0f, maxY = float.MinValue;
        foreach (var p in pos)
        {
            minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z);
            maxY = Mathf.Max(maxY, p.y);
        }
        foreach (var p in pos)
            if (p.y >= maxY - 0.01f) maxX = Mathf.Max(maxX, Mathf.Abs(p.x));

        // 길이 방향으로 긴 면은 휘어도 직선(현)으로 남으므로, 일정 간격 평면으로 잘라 정점을 늘린다
        if (m_SliceStep > 0f)
        {
            int slices = Mathf.CeilToInt((maxZ - minZ) / m_SliceStep);
            for (int k = 1; k < slices; k++)
                SliceAtZ(minZ + (maxZ - minZ) * k / slices, pos, nrm, uv, idx);
        }

        // 벨트 윗면 삼각형 분리: 세 꼭짓점이 모두 맨 위 높이·벨트 면 폭 안이고 위를 향함
        bool IsTopVertex(int i) => pos[i].y >= maxY - 0.01f && Mathf.Abs(pos[i].x) <= maxX + 1e-4f && nrm[i].y > 0.7f;
        var body = new List<int>(idx.Count); var top = new List<int>();
        for (int i = 0; i < idx.Count; i += 3)
        {
            var list = IsTopVertex(idx[i]) && IsTopVertex(idx[i + 1]) && IsTopVertex(idx[i + 2]) ? top : body;
            list.Add(idx[i]); list.Add(idx[i + 1]); list.Add(idx[i + 2]);
        }
        // 윗면과 다른 면이 함께 쓰는 정점은 복제 (윗면만 UV를 새로 계산하므로)
        var usedByBody = new bool[pos.Count];
        foreach (var i in body) usedByBody[i] = true;
        var dup = new Dictionary<int, int>();
        for (int k = 0; k < top.Count; k++)
        {
            int i = top[k];
            if (!usedByBody[i]) continue;
            if (!dup.TryGetValue(i, out int copy))
            {
                pos.Add(pos[i]); nrm.Add(nrm[i]); uv.Add(uv[i]);
                copy = pos.Count - 1; dup[i] = copy;
            }
            top[k] = copy;
        }
        // 곡선에서 사다리꼴 면의 선형 보간 때문에 줄무늬가 꺾여 보이므로 윗면만 더 잘게 자름
        if (m_SurfaceSliceStep > 0f)
        {
            int slices = Mathf.CeilToInt((maxZ - minZ) / m_SurfaceSliceStep);
            for (int k = 1; k < slices; k++)
                SliceAtZ(minZ + (maxZ - minZ) * k / slices, pos, nrm, uv, top);
        }
        var isTop = new bool[pos.Count];
        foreach (var i in top) isTop[i] = true;

        return new ModuleData
        {
            positions = pos.ToArray(), normals = nrm.ToArray(), uvs = uv.ToArray(), indices = body.ToArray(),
            topIndices = top.ToArray(), isTop = isTop,
            minZ = minZ, length = Mathf.Max(0.001f, maxZ - minZ), halfWidth = Mathf.Max(0.001f, maxX), material = material
        };
    }

    /// <summary>
    /// 평면 z = planeZ 를 가로지르는 삼각형을 잘라 나눈다.
    /// 같은 변의 교점은 캐시로 공유해 이웃 삼각형과 틈이 생기지 않게 한다.
    /// </summary>
    static void SliceAtZ(float planeZ, List<Vector3> pos, List<Vector3> nrm, List<Vector2> uv, List<int> idx)
    {
        const float eps = 1e-5f;
        var cut = new Dictionary<long, int>();
        var result = new List<int>(idx.Count + 64);

        int EdgePoint(int a, int b)
        {
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            if (cut.TryGetValue(key, out int found)) return found;
            float t = (planeZ - pos[a].z) / (pos[b].z - pos[a].z);
            pos.Add(Vector3.Lerp(pos[a], pos[b], t));
            nrm.Add(Vector3.Lerp(nrm[a], nrm[b], t).normalized);
            uv.Add(Vector2.Lerp(uv[a], uv[b], t));
            cut[key] = pos.Count - 1;
            return pos.Count - 1;
        }

        for (int i = 0; i < idx.Count; i += 3)
        {
            int[] v = { idx[i], idx[i + 1], idx[i + 2] };
            float[] d = { pos[v[0]].z - planeZ, pos[v[1]].z - planeZ, pos[v[2]].z - planeZ };
            int pos0 = 0, neg0 = 0;
            for (int k = 0; k < 3; k++) { if (d[k] > eps) pos0++; else if (d[k] < -eps) neg0++; }
            if (pos0 == 0 || neg0 == 0) { result.Add(v[0]); result.Add(v[1]); result.Add(v[2]); continue; }

            // 한쪽에 혼자 있는 꼭짓점(lone)을 기준으로 회전시켜 정리 (감김 순서 유지)
            int lone = -1;
            for (int k = 0; k < 3; k++)
            {
                bool sideK = d[k] > eps, sideK1 = d[(k + 1) % 3] > eps, sideK2 = d[(k + 2) % 3] > eps;
                bool onK1 = Mathf.Abs(d[(k + 1) % 3]) <= eps, onK2 = Mathf.Abs(d[(k + 2) % 3]) <= eps;
                if (Mathf.Abs(d[k]) <= eps) continue;
                if ((onK1 || sideK1 != sideK) && (onK2 || sideK2 != sideK)) { lone = k; break; }
            }
            if (lone < 0) { result.Add(v[0]); result.Add(v[1]); result.Add(v[2]); continue; }

            int a = v[lone], b = v[(lone + 1) % 3], c = v[(lone + 2) % 3];
            float db = d[(lone + 1) % 3], dc = d[(lone + 2) % 3];
            if (Mathf.Abs(db) <= eps)
            {
                // b 가 평면 위 → a-c 변만 자름
                int pc = EdgePoint(a, c);
                result.AddRange(new[] { a, b, pc, b, c, pc });
            }
            else if (Mathf.Abs(dc) <= eps)
            {
                int pb = EdgePoint(a, b);
                result.AddRange(new[] { a, pb, c, pb, b, c });
            }
            else
            {
                int pb = EdgePoint(a, b), pc = EdgePoint(a, c);
                result.AddRange(new[] { a, pb, pc, pb, b, c, pb, c, pc });
            }
        }
        idx.Clear();
        idx.AddRange(result);
    }

    // Runtime clipping preserves authored knots, excludes and robot placement.
    [SerializeField, HideInInspector] float productionEnd = 1f;
    public void SetProductionEnd(float progress)
    {
        float next = Mathf.Clamp01(progress);
        if (Mathf.Abs(next - productionEnd) < .00001f) return;
        productionEnd = next;
        Rebuild();
    }

    void AppendSpline(int splineIndex, UnityEngine.Splines.Spline spline, ModuleData middle, ModuleData end, float width)
    {
        if (spline == null || spline.Count < 2) return;
        float splineLength = spline.GetLength();
        if (splineLength < 0.01f) return;

        bool capEnds = end != null && !spline.Closed;

        // 제외 구간이 없으면 [0, 길이] 한 구간 (기존과 동일). 있으면 남는 구간마다 따로 배치하고
        // 제외 구간과 맞닿은 쪽은 끝단 모듈로 마감한다.
        var ranges = new List<Vector2>();
        if (m_ExcludeRanges != null)
            foreach (var r in m_ExcludeRanges)
            {
                if (r.splineIndex != splineIndex) continue;
                float a = Mathf.Clamp(Mathf.Min(r.start, r.end), 0f, splineLength);
                float b = Mathf.Clamp(Mathf.Max(r.start, r.end), 0f, splineLength);
                if (b - a > 0.001f) ranges.Add(new Vector2(a, b));
            }
        if (splineIndex == 0 && productionEnd < .99999f)
            ranges.Add(new Vector2(productionEnd * splineLength, splineLength));
        ranges.Sort((p, q) => p.x.CompareTo(q.x));

        float from = 0f;
        bool capFrom = capEnds;
        foreach (var r in ranges)
        {
            if (r.x > from + 0.01f)
                AppendSegment(spline, splineLength, from, r.x, capFrom, end != null, middle, end, width);
            from = Mathf.Max(from, r.y);
            capFrom = end != null;
        }
        if (splineLength > from + 0.01f)
            AppendSegment(spline, splineLength, from, splineLength, capFrom, capEnds, middle, end, width);
    }

    /// <summary>
    /// 스플라인의 [segFrom, segTo] 거리 구간에 모듈을 이어 붙인다.
    /// capStart/capEnd: 구간 시작·끝에 끝단 모듈을 둘지 (끝단 모듈이 없으면 무시).
    /// </summary>
    void AppendSegment(UnityEngine.Splines.Spline spline, float splineLength, float segFrom, float segTo,
        bool capStart, bool capEnd, ModuleData middle, ModuleData end, float width)
    {
        if (end == null) { capStart = false; capEnd = false; }
        float rangeLength = segTo - segFrom;
        float nominal = m_ModuleLength > 0f ? m_ModuleLength : middle.length;

        // 모듈 개수를 정수로 맞추고, 남는 길이는 전 모듈을 균등하게 늘려 흡수
        int minCount = (capStart && capEnd) ? 2 : 1;
        int count = Mathf.Max(minCount, Mathf.RoundToInt(rangeLength / nominal));
        float segLength = rangeLength / count;

        // 벨트 면(|x| ≤ halfWidth)만 widthScale로 늘리고, 바깥 브래킷은 m_BracketScale로 두께 유지 후 평행이동
        float widthScale = width > 0f ? (width * 0.5f) / middle.halfWidth : 1f;
        float bracketScale = Mathf.Max(0.01f, m_BracketScale);
        float surfaceHalf = middle.halfWidth * widthScale;
        bool surfaceUv = m_SurfaceMaterial != null;
        var toLocal = transform.worldToLocalMatrix * m_Spline.transform.localToWorldMatrix;

        // 구간 분할: 모듈 경계에 맞춰 모듈 mpz개씩 묶음. 구간 수는 반올림이라 마지막 구간은 남는 모듈을 흡수(빈 구간 없음).
        // 제외 구간·스플라인 끝에서 이 함수가 끊기므로 구간도 자연스럽게 그곳에서 나뉨
        int modulesPerZone = 1, zoneCount = 0, zoneBase = m_ZoneCount;
        if (m_BuildZoned)
        {
            modulesPerZone = Mathf.Max(1, Mathf.RoundToInt(m_ZoneLength / segLength));
            zoneCount = Mathf.Max(1, Mathf.RoundToInt(count / (float)modulesPerZone));
            for (int z = 0; z < zoneCount; z++)
            {
                int first = z * modulesPerZone;
                int last = z == zoneCount - 1 ? count : first + modulesPerZone;
                AddZone(segFrom + first * segLength, segFrom + last * segLength);
            }
        }

        for (int i = 0; i < count; i++)
        {
            var topList = m_BuildZoned ? m_ZoneTris[zoneBase + Mathf.Min(i / modulesPerZone, zoneCount - 1)] : m_TopIndices;
            bool isStart = capStart && i == 0;
            bool isEnd = capEnd && i == count - 1 && !isStart;
            var data = (isStart || isEnd) ? end : middle;

            // 끝단 모듈의 마감 쪽이 스플라인 바깥을 향하도록 길이·폭 방향을 함께 뒤집음(180도 회전)
            bool flip = (isEnd && !m_EndCapAtPositive) || (isStart && m_EndCapAtPositive);

            float centerDist = segFrom + (i + 0.5f) * segLength;
            EvaluateFrame(spline, centerDist, splineLength, toLocal, out var cPos, out var cRot);

            int baseIndex = m_Positions.Count;
            for (int v = 0; v < data.positions.Length; v++)
            {
                var p = data.positions[v];
                var n = data.normals[v];
                if (flip) { p = new Vector3(-p.x, p.y, data.minZ + data.length - (p.z - data.minZ)); n = new Vector3(-n.x, n.y, -n.z); }

                float u = (p.z - data.minZ) / data.length; // 모듈 안 진행도 0~1
                Vector3 framePos; Quaternion frameRot;
                if (m_Deform)
                {
                    EvaluateFrame(spline, segFrom + (i + u) * segLength, splineLength, toLocal, out framePos, out frameRot);
                }
                else
                {
                    // 비교용: 휘지 않고 모듈 중심 프레임에 원래 길이로 배치
                    frameRot = cRot;
                    framePos = cPos + cRot * new Vector3(0f, 0f, (u - 0.5f) * data.length * (nominal / middle.length));
                }

                float ax = Mathf.Abs(p.x);
                bool inBracket = ax > data.halfWidth + 1e-4f;
                float x = inBracket ? Mathf.Sign(p.x) * (surfaceHalf + (ax - data.halfWidth) * bracketScale) : p.x * widthScale;
                var offset = new Vector3(x, p.y * m_HeightScale + m_HeightOffset, 0f);
                m_Positions.Add(framePos + frameRot * offset);
                var ns = new Vector3(n.x / (inBracket ? bracketScale : widthScale), n.y / m_HeightScale, n.z * data.length / segLength);
                m_Normals.Add((frameRot * ns).normalized);

                // 벨트 윗면: U = 폭 0~1, V = 스플라인 누적 거리(m) (모듈 경계와 무관하게 이어짐)
                // 줄무늬 간격은 MPB 타일링(1 / 타일 길이)으로 곱하므로 간격을 바꿔도 메시는 그대로
                if (surfaceUv && data.isTop[v])
                    m_Uvs.Add(new Vector2(x / (surfaceHalf * 2f) + 0.5f, segFrom + (i + u) * segLength));
                else
                    m_Uvs.Add(data.uvs[v]);
            }
            foreach (var t in data.indices) m_Indices.Add(baseIndex + t);
            foreach (var t in data.topIndices) topList.Add(baseIndex + t);
        }
    }

    /// <summary>스플라인 위 거리(m)에서 위치·회전 프레임을 이 오브젝트 로컬 기준으로 구한다.</summary>
    static void EvaluateFrame(UnityEngine.Splines.Spline spline, float distance, float splineLength, Matrix4x4 toLocal,
        out Vector3 position, out Quaternion rotation)
    {
        float t = spline.ConvertIndexUnit(math.clamp(distance, 0f, splineLength), PathIndexUnit.Distance, PathIndexUnit.Normalized);
        SplineUtility.Evaluate(spline, t, out var pos, out var tan, out var up);
        if (math.lengthsq(tan) < 1e-8f)
        {
            float t2 = spline.ConvertIndexUnit(math.clamp(distance + 0.01f, 0f, splineLength), PathIndexUnit.Distance, PathIndexUnit.Normalized);
            tan = (float3)spline.EvaluatePosition(t2) - pos;
            if (math.lengthsq(tan) < 1e-8f) tan = new float3(0, 0, 1);
        }
        position = toLocal.MultiplyPoint3x4(pos);
        var fwd = toLocal.MultiplyVector(tan);
        var upv = toLocal.MultiplyVector(up);
        if (upv.sqrMagnitude < 1e-8f) upv = Vector3.up;
        rotation = Quaternion.LookRotation(fwd, upv);
    }
}
