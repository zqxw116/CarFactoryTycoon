using UnityEngine;
using System.Collections;
// 불량 테스트 공정 (5 라인)
public class TestStation : MonoBehaviour
{
    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Car"))
        {
            CarController car = other.GetComponent<CarController>();

            //if (car.defectCount > 0)
            //{
            //    StartCoroutine(DefectRoutine(car.gameObject));
            //}
            //else
            //{
            //    GameManager.Instance.SellCar(100);
            //    PoolManager.Instance.ReturnCar(car.gameObject); // 정상 판매 후 풀 반환
            //}
        }
    }

    private IEnumerator DefectRoutine(GameObject car)
    {
        // TODO: 차량 이동 멈춤 (CarController 스크립트 비활성화 등)
        // TODO: 프레스 압축, 물방울, 튕겨나가는 Fake 연출 재생
        yield return new WaitForSeconds(1f);
        //PoolManager.Instance.ReturnCar(car); // 폐기 처리 (풀 반환)
    }
}