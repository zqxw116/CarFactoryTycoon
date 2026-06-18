using UnityEngine;

public enum AssemblyType { Frame, Paint, Interior, Exterior }
public class AssemblyStation : MonoBehaviour
{
    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Car"))
        {
            CarController car = other.GetComponent<CarController>();

            // [폐기] 확률 기반 체결 모델은 작업존 체류시간 기반(StationController)으로 대체됨.
            //        이 컴포넌트는 더 이상 사용하지 않음 (StationController 사용).
        }
    }
}