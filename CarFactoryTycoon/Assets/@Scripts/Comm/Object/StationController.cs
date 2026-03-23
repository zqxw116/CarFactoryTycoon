using UnityEngine;
using System; // Action 사용을 위해 추가

public class StationController : MonoBehaviour
{
    // 공정의 현재 상태를 정의하는 Enum (가독성과 상태 관리를 위함)
    public enum WorkState
    {
        Idle,           // 대기 중 (차량 없음)
        FetchingPart,   // 거치대에서 부품을 집기 위해 이동 중
        MovingToCar,    // 부품을 집고 차량의 조립 위치로 이동 중
        Assembling      // 차량에 부품을 조립하는 중
    }

    [Header("공정 및 스테이션 설정")]
    public PartGroup targetGroup;
    public Transform partSpawnPoint;   // 스테이션에 부품이 놓여있는 거치대 위치

    [Header("로봇팔 능력치 (업그레이드 요소)")]
    public float workSpeed = 0.5f;     // 조립 속도 (초당 진행도 감소량)
    public float armMoveSpeed = 5f;    // 로봇팔이 목표로 이동/회전하는 속도

    [Header("연동된 로봇팔")]
    public RoboticArmIK myRobotArm;

    private CarController currentCar;
    private AssemblyPart currentPart;
    private WorkState currentState = WorkState.Idle;

    // IK 도달 여부를 판단하기 위한 거리 오차 허용 범위 (캐싱)
    private const float REACH_THRESHOLD = 0.1f;

    private void Update()
    {
        // 1. GC 할당을 막기 위해 Update 내에서는 상태에 따른 로직만 분기 처리합니다.
        if (currentState == WorkState.Idle) return;

        // 차량이 이동해버리거나 파괴되었는지 안전 검사 (타이쿤 최적화 기본)
        if (currentCar == null || currentPart == null)
        {
            ResetStation();
            return;
        }

        switch (currentState)
        {
            case WorkState.FetchingPart:
                ProcessFetchingPart();
                break;

            case WorkState.MovingToCar:
                ProcessMovingToCar();
                break;

            case WorkState.Assembling:
                ProcessAssembling();
                break;
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (currentState != WorkState.Idle) return;

        // 최적화 포인트: GetComponentInParent는 부하가 있으므로, 
        // 가능하면 차량의 Layer를 분리하고 물리 충돌 매트릭스를 설정하는 것을 권장합니다.
        CarController car = other.GetComponentInParent<CarController>();
        if (car != null)
        {
            currentCar = car;
            currentPart = car.GetUnassembledPart(targetGroup);

            if (currentPart != null && myRobotArm != null)
            {
                // 로봇팔의 타겟을 스테이션 거치대로 설정하고 상태 변경
                myRobotArm.target = partSpawnPoint;
                myRobotArm.isWorking = true;

                currentState = WorkState.FetchingPart;
                Debug.Log($"[{gameObject.name}] 차량 진입! 거치대로 부품 가지러 가는 중...");
            }
        }
    }

    private void OnTriggerExit(Collider other)
    {
        // 차량이 빠져나갔을 때의 처리
        if (currentCar != null)
        {
            CarController car = other.GetComponentInParent<CarController>();
            if (car != null && car == currentCar)
            {
                // 조립이 덜 끝났다면 불량 상태(Progress > 0)로 남게 됨
                Debug.Log($"[{gameObject.name}] 차량 이탈. 현재 조립 진행도 남은 량: {currentPart?.assemblyProgress}");
                ResetStation();
            }
        }
    }

    #region State Processing Methods


    private void ProcessMovingToCar()
    {
        // 로봇팔이 차량의 부품 조립 위치를 따라가며 도달했는지 확인
        float distance = Vector3.Distance(myRobotArm.GetEndEffectorPosition(), currentPart.transform.position);

        if (distance <= REACH_THRESHOLD)
        {
            currentState = WorkState.Assembling;
            Debug.Log($"[{gameObject.name}] 차량 도달! 조립 시작...");
        }
    }
    private void ProcessFetchingPart()
    {
        Vector3 offset = myRobotArm.GetEndEffectorPosition() - partSpawnPoint.position;

        // sqrMagnitude를 이용한 거리 오차 검사 (성능 최적화)
        if (offset.sqrMagnitude <= REACH_THRESHOLD * REACH_THRESHOLD)
        {
            // 1. 로봇팔 끝단의 부품 모델링 활성화!
            myRobotArm.ToggleAttachedPart(true);

            // (선택) 스테이션 거치대의 부품을 잠시 꺼서 집어간 것처럼 연출할 수도 있습니다.
            // stationPartView.SetActive(false); 

            // 2. 타겟을 차량으로 변경
            myRobotArm.target = currentPart.transform;
            currentState = WorkState.MovingToCar;
        }
    }

    private void ProcessAssembling()
    {
        currentPart.assemblyProgress -= workSpeed * Time.deltaTime;

        if (currentPart.assemblyProgress <= 0f)
        {
            currentPart.assemblyProgress = 0f;

            // 1. 차량 쪽 부품 활성화 (AssemblyPart 스크립트 내부에 함수가 있다고 가정)
            currentPart.CompleteAssembly();

            // 2. 로봇팔이 들고 있던 부품 비활성화
            myRobotArm.ToggleAttachedPart(false);

            // (선택) 스테이션 거치대 부품을 다시 활성화하여 무한 리필되는 것처럼 연출
            // stationPartView.SetActive(true);

            Debug.Log($"[{gameObject.name}] 조립 완료 및 뷰 전환 성공!");
            ResetStation();
        }
    }
    #endregion

    private void ResetStation()
    {
        currentState = WorkState.Idle;
        currentCar = null;
        currentPart = null;

        if (myRobotArm != null)
        {
            myRobotArm.isWorking = false;
            // 로봇팔을 기본 대기 위치(초기 위치)로 돌려보내는 로직을 추가하면 좋습니다.
            // myRobotArm.target = idleTransform; 
        }
    }
}