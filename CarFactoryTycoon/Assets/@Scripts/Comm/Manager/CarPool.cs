using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 차량 오브젝트 풀. 차량을 파괴(Destroy)하지 않고 비활성화 후 재사용한다.
/// 라인 끝 도착/제거 시 Return으로 반환되고, 스폰 시 Get으로 다시 꺼낸다.
/// </summary>
public class CarPool : GameObjectSingleton<CarPool>
{
    // 인스펙터 노출 없이 InitManager가 코드로 생성하므로 기본값 사용.
    private const string prefabResourcePath = "Prefabs/CarModel_Origin";
    private const int prewarmCount = 5;

    private GameObject carPrefab;
    private readonly Queue<CarController> pool = new Queue<CarController>();

    // 라인에서 동작 중인(활성) 차량들을 담는 컨테이너. 하이러키 정리용.
    // 계층: GameObjectGroup > CarPool > ActiveCars > (동작 중 차량들)
    private Transform activeRoot;

    private void Awake()
    {
        activeRoot = new GameObject("ActiveCars").transform;
        activeRoot.SetParent(transform, false);

        // 차량 프리팹은 인스펙터 연결 없이 실행 시 Resources에서 로드한다.
        carPrefab = Resources.Load<GameObject>(prefabResourcePath);
        if (carPrefab == null)
        {
            Debug.LogError($"[CarPool] 차량 프리팹 로드 실패: Resources/{prefabResourcePath}");
            return;
        }

        for (int i = 0; i < prewarmCount; i++)
            pool.Enqueue(CreateNew());
    }

    private CarController CreateNew()
    {
        GameObject go = Instantiate(carPrefab, transform);
        if (!go.TryGetComponent<CarController>(out var car))
            car = go.AddComponent<CarController>();
        // 차체 도색(언더코트→부스 통과 시 원본색) 담당 — 스폰 즉시 검정 차체로 보이게
        if (!go.TryGetComponent<CarPaintController>(out _))
            go.AddComponent<CarPaintController>();
        go.SetActive(false);
        return car;
    }

    /// <summary>풀에서 차량을 꺼낸다 (비어 있으면 새로 생성).</summary>
    public CarController Get()
    {
        CarController car = pool.Count > 0 ? pool.Dequeue() : CreateNew();
        car.transform.SetParent(activeRoot, true); // 활성 차량 컨테이너로 이동
        car.gameObject.SetActive(true);
        return car;
    }

    /// <summary>차량을 풀로 반환한다 (파괴하지 않음).</summary>
    public void Return(CarController car)
    {
        if (car == null) return;
        car.gameObject.SetActive(false);
        car.transform.SetParent(transform);
        pool.Enqueue(car);
    }
}
