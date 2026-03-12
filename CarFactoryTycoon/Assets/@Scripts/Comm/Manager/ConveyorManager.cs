using UnityEngine;

public class ConveyorManager : MonoBehaviour
{
    public static ConveyorManager Instance;

    [Header("WP_0 부터 순서대로 드래그해서 넣으세요")]
    public Transform[] waypoints;

    [Header("차량 기본 생성 주기 (초)")]
    public float baseSpawnInterval = 3.0f;

    private float spawnTimer = 0f;

    private void Awake() { Instance = this; }

    private void Update()
    {
        // GameManager의 라인 속도 배율을 가져와 타이머에 곱해줌 (업그레이드 시 생성 속도 빨라짐)
        // 당장 GameManager가 없다면 GameManager.Instance.lineSpeedMultiplier 대신 1f 를 곱하세요.
        float currentSpeedMultiplier = 1f; // GameManager 연동 시 교체할 부분

        spawnTimer += Time.deltaTime * currentSpeedMultiplier;

        // 설정한 주기에 도달하면 차량 생성 후 타이머 초기화
        if (spawnTimer >= baseSpawnInterval)
        {
            SpawnCar();
            spawnTimer = 0f;
        }
    }

    private void SpawnCar()
    {
        if (waypoints.Length == 0) return;

        //GameObject car = PoolManager.Instance.GetCar(waypoints[0].position);
        //car.GetComponent<CarController>().Init();
    }
}