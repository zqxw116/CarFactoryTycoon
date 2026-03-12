using System.Collections.Generic;
using UnityEngine;

public class CarController : MonoBehaviour
{
    // 각 파츠들을 그룹별로 묶어둘 리스트 (Dictionary 활용)
    private Dictionary<PartGroup, List<AssemblyPart>> partsDictionary = new Dictionary<PartGroup, List<AssemblyPart>>();

    private void Awake()
    {
        InitializeCarParts();
    }

    // 1. 하위 오브젝트를 다 뒤져서 그룹별로 분류하는 초기화 함수
    private void InitializeCarParts()
    {
        // Enum에 있는 모든 그룹을 Dictionary에 빈 리스트로 생성
        foreach (PartGroup group in System.Enum.GetValues(typeof(PartGroup)))
        {
            partsDictionary[group] = new List<AssemblyPart>();
        }

        // 자식들에 붙어있는 모든 AssemblyPart 찾기
        AssemblyPart[] allParts = GetComponentsInChildren<AssemblyPart>(true);

        // 찾은 파츠들을 자신의 그룹에 맞게 리스트에 쏙쏙 넣기
        foreach (AssemblyPart part in allParts)
        {
            partsDictionary[part.myGroup].Add(part);
        }

        Debug.Log($"[{gameObject.name}] 차량 파츠 초기화 완료. 총 {allParts.Length}개의 파츠를 그룹화했습니다.");

        // (테스트용) 시작할 때 모든 파츠를 분리 상태(1)로 만듦
        SetAllPartsProgress(1f);
    }

    // 2. 특정 그룹의 모든 파츠를 체결(0) 상태로 만드는 함수 (로봇팔이 호출할 예정)
    public void AssembleGroup(PartGroup targetGroup)
    {
        if (partsDictionary.ContainsKey(targetGroup))
        {
            foreach (AssemblyPart part in partsDictionary[targetGroup])
            {
                // TODO: 나중에 이 부분을 DOTween이나 Coroutine으로 부드럽게 바꿀 예정
                part.assemblyProgress = 0f;

                // OnValidate는 에디터용이므로, 실제 게임에선 직접 위치 적용
                part.transform.localPosition = part.assembledPos;
                part.transform.localRotation = Quaternion.Euler(part.assembledRot);
            }
            Debug.Log($"[{targetGroup}] 그룹 체결 완료!");
        }
    }

    // 3. 모든 파츠의 진행도를 일괄 강제 설정 (초기화용)
    public void SetAllPartsProgress(float progress)
    {
        foreach (var groupList in partsDictionary.Values)
        {
            foreach (AssemblyPart part in groupList)
            {
                part.assemblyProgress = progress;
                part.transform.localPosition = Vector3.Lerp(part.assembledPos, part.detachedPos, progress);
                part.transform.localRotation = Quaternion.Euler(Vector3.Lerp(part.assembledRot, part.detachedRot, progress));
            }
        }
    }


    // CarController.cs 내부에 추가

    // 로봇팔이 호출할 함수: 해당 그룹의 파츠들의 progress를 일정량 감소시킴
    public void WorkOnGroup(PartGroup targetGroup, float workAmount)
    {
        if (partsDictionary.ContainsKey(targetGroup))
        {
            foreach (AssemblyPart part in partsDictionary[targetGroup])
            {
                // 현재 progress에서 workAmount만큼 뺌
                part.UpdateProgress(part.assemblyProgress - workAmount);
            }
        }
    }
}