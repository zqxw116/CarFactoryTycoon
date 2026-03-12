using System.Collections.Generic;
using UnityEngine;

public class PoolManager : MonoSingleton<PoolManager>
{

    [Header("차량 프리팹을 여기에 넣으세요")]
    public GameObject carPrefab;

    private Queue<GameObject> pool = new Queue<GameObject>();

    public override void Init()
    {
        var car = Resources.Load<GameObject>("Prefabs/ObjCar.prefab");
        var objCar = GameObject.Instantiate(car, this.transform);
    }

}