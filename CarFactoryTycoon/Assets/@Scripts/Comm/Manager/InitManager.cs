using UnityEngine;

/// <summary>
/// 게임 시작 시 관리용 매니저들의 생성/초기화를 한 곳에서 담당하는 부트스트랩.
/// 씬에는 이 오브젝트 하나만 두면 되고, 위치가 의미 없는 매니저(Economy/Upgrade/GameFlow/CarPool)는
/// 여기서 코드로 생성한다. (위치 배치가 필요한 CarSpawner 등은 씬에 직접 둔다.)
/// </summary>
public class InitManager : MonoBehaviour
{
    [Header("풀링 오브젝트(CarPool)를 자식으로 둘 그룹 오브젝트 이름")]
    public string groupObjectName = "GameObjectGroup";

    private void Awake()
    {
        // 관리용 매니저는 MonoSingleton이라 .Instance 접근 시 자동 생성된다.
        // 여기서는 생성주기(초기화 순서)를 한 곳에서 통제하기 위해 Init만 호출한다.
        EconomyManager.Instance.Init();
        UpgradeManager.Instance.Init();
        GameFlowManager.Instance.Init();
        StationConfig.Instance.Init();

        // 돈 UI (자금 변동을 화면에 표시 + 트윈 연출). EconomyManager 이후에 초기화.
        MoneyUI.Instance.Init();

        // 라인 트래픽(차간 간격/정체/게이트). 씬에 직접 배치돼 있으면(튜닝값 오버라이드) 그것을 쓰고,
        // 없으면 기본값으로 생성. 이 매니저가 없는 씬(TestPartsScene 등)은 차량이 자율 이동한다.
        if (FindFirstObjectByType<LineTrafficManager>() == null)
            new GameObject("LineTrafficManager").AddComponent<LineTrafficManager>();

        // CarPool은 생성 차량 계층 정리를 위해 GameObjectGroup 아래 별도 오브젝트로 생성.
        CreateCarPool();
    }

    private void CreateCarPool()
    {
        GameObject group = GameObject.Find(groupObjectName);
        if (group == null)
            Debug.LogWarning($"[InitManager] '{groupObjectName}' 오브젝트를 찾지 못해 루트에 CarPool을 생성합니다.");

        GameObject poolGo = new GameObject("CarPool");
        if (group != null) poolGo.transform.SetParent(group.transform, false);
        poolGo.AddComponent<CarPool>();
    }
}
