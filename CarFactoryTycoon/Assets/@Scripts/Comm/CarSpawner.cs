using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;

public class CarSpawner : MonoBehaviour
{
    [Header("생성 설정")]
    public float spawnInterval = 15f;

    [Header("공정 라인(경로) 연결")]
    [Tooltip("비워두면 씬에서 'MainSpline' 이름의 오브젝트를 자동으로 찾습니다.")]
    public SplineContainer mainLineSpline;
    public float globalLineSpeed = 1.5f;

    [Header("기즈모 (차량 생성 위치 표시)")]
    [Tooltip("대략적인 차량 크기 (X=폭, Y=높이, Z=길이)")]
    public Vector3 carSize = new Vector3(2f, 1.5f, 4.5f);

    private const string MainSplineName = "MainSpline";
    private float timer = 0f;

    private float SpeedMultiplier =>
        UpgradeManager.Instance != null ? UpgradeManager.Instance.lineSpeedMultiplier : 1f;

    private void Awake()
    {
        ResolveSpline();
    }

    /// <summary>인스펙터에 연결돼 있지 않으면 씬에서 'MainSpline'을 찾는다.</summary>
    private void ResolveSpline()
    {
        if (mainLineSpline != null) return;

        GameObject go = GameObject.Find(MainSplineName);
        if (go != null) go.TryGetComponent(out mainLineSpline);

        if (mainLineSpline == null)
            Debug.LogWarning($"[CarSpawner] '{MainSplineName}' 스플라인을 찾지 못했습니다.");
    }

    private void Update()
    {
        // 라인 속도 배율이 높을수록 생성도 빨라짐
        timer += Time.deltaTime * SpeedMultiplier;

        if (timer >= spawnInterval)
        {
            SpawnCar();
            timer = 0f;
        }
    }

    public void SpawnCar()
    {
        if (mainLineSpline == null || CarPool.Instance == null) return;

        CarController car = CarPool.Instance.Get();
        if (car == null) return;

        // 스포너 위치에서 가장 가까운 스플라인 지점(진행도)을 출발점으로 사용
        float startProgress = GetNearestProgress();
        car.SetPath(mainLineSpline, globalLineSpeed * SpeedMultiplier, startProgress);
    }

    /// <summary>스포너 위치에 가장 가까운 스플라인 위의 진행도(0~1)를 반환.</summary>
    private float GetNearestProgress()
    {
        // GetNearestPoint는 스플라인 로컬 좌표 기준이므로 월드 위치를 로컬로 변환
        float3 localPoint = mainLineSpline.transform.InverseTransformPoint(transform.position);
        SplineUtility.GetNearestPoint(mainLineSpline.Spline, localPoint, out _, out float t);
        return Mathf.Clamp01(t);
    }

    // 실제 출발 지점(스포너에서 가장 가까운 스플라인 점)에 차량 크기 박스로 표시.
    private void OnDrawGizmos()
    {
        SplineContainer spline = mainLineSpline;
        if (spline == null)
        {
            GameObject go = GameObject.Find(MainSplineName);
            if (go != null) go.TryGetComponent(out spline);
        }

        Vector3 pos = transform.position;
        Quaternion rot = transform.rotation;

        if (spline != null)
        {
            float3 localPoint = spline.transform.InverseTransformPoint(transform.position);
            SplineUtility.GetNearestPoint(spline.Spline, localPoint, out _, out float t);
            spline.Evaluate(Mathf.Clamp01(t), out float3 wp, out float3 wt, out float3 wu);
            pos = (Vector3)wp;
            if (((Vector3)wt).sqrMagnitude > 0.0001f)
                rot = Quaternion.LookRotation((Vector3)wt, (Vector3)wu);

            // 스포너 → 실제 출발 지점 연결선
            Gizmos.color = Color.yellow;
            Gizmos.DrawLine(transform.position, pos);
        }

        Matrix4x4 old = Gizmos.matrix;
        Gizmos.matrix = Matrix4x4.TRS(pos + rot * new Vector3(0f, carSize.y * 0.5f, 0f), rot, Vector3.one);
        Gizmos.color = new Color(0f, 1f, 1f, 0.9f);
        Gizmos.DrawWireCube(Vector3.zero, carSize);
        Gizmos.matrix = old;
    }
}
