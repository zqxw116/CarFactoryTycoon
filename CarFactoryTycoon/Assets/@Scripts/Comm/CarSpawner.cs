using UnityEngine;
using UnityEngine.Splines; // [추가]

public class CarSpawner : MonoBehaviour
{
    [Header("생성 설정")]
    public GameObject carPrefab;
    public float spawnInterval = 5f;

    [Header("공정 라인(경로) 연결")]
    [Tooltip("자동차가 따라갈 스플라인 오브젝트를 여기에 넣으세요")]
    public SplineContainer mainLineSpline;
    public float globalLineSpeed = 1.5f;

    private float timer = 0f;

    private void Update()
    {
        timer += Time.deltaTime;

        if (timer >= spawnInterval)
        {
            SpawnCar();
            timer = 0f;
        }
    }

    public void SpawnCar()
    {
        if (carPrefab == null || mainLineSpline == null) return;

        // 차량은 어차피 스플라인의 0.0 위치(시작점)로 순간이동하므로 위치는 Vector3.zero로 생성
        GameObject newCar = Instantiate(carPrefab, Vector3.zero, Quaternion.identity);

        // 생성된 자동차에 스플라인 경로와 속도를 주입
        if (newCar.TryGetComponent<CarController>(out var controller))
        {
            controller.SetPath(mainLineSpline, globalLineSpeed);
        }
    }
}