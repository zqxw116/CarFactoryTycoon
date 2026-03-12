using UnityEngine;

public abstract class StationController : MonoBehaviour
{
    // 차량이 Trigger 영역(공정)에 들어왔을 때 실행
    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Car"))
        {
            CarController car = other.GetComponent<CarController>();
            if (car != null)
            {
                ProcessCar(car);
            }
        }
    }

    // 자식 클래스(조립장, 테스트장)에서 각각의 입맛에 맞게 구현할 핵심 메서드
    protected abstract void ProcessCar(CarController car);
}