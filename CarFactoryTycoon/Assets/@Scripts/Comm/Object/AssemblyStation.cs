using UnityEngine;

public enum AssemblyType { Frame, Paint, Interior, Exterior }
public class AssemblyStation : MonoBehaviour
{
    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Car"))
        {
            CarController car = other.GetComponent<CarController>();

            // 로봇팔 체결 확률 계산
            if (Random.value > GameManager.Instance.assemblySuccessRate)
            {
                //car.AddDefect(); // 앗! 체결 누락 (불량 발생)
            }
            else
            {
                // 정상 체결. TODO: 뚝딱거리는 이펙트 및 해당 부품 mesh 활성화
            }
        }
    }
}