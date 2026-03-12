using UnityEngine;


// [Must-Have] 유저 터치/클릭 처리기
public class InputManager : MonoBehaviour
{
    void Update()
    {
        if (Input.GetMouseButtonDown(0)) // 모바일 터치도 동일하게 작동
        {
            Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
            if (Physics.Raycast(ray, out RaycastHit hit))
            {
                if (hit.collider.CompareTag("Car"))
                {
                    //hit.collider.GetComponent<CarController>().FixDefect();
                }
            }
        }
    }
}