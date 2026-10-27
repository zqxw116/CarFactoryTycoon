# 그릴미 세션 프롬프트: 세부 설정 확정 (2026-10-27)

> 용도: `/grill-me` 세션을 시작할 때 아래 "프롬프트 본문"을 그대로 붙여 넣는다. Claude와 Codex 공용.
> 다 쓰면(세부 설정 확정 후) 이 파일은 `과거논의사항/`으로 옮긴다.

---

## 프롬프트 본문

```
/grill-me

CarFactoryTycoon의 확정 방향(1인칭/근접 3인칭 작업자로 혼자 시작 → 고용·자동화로 하루 처리량 성장)의
세부 설정을 그릴미 방식으로 하나씩 확정하자. 이번 세션은 결정과 문서화만 한다. 코드·씬·에셋·설정은 바꾸지 말고, 커밋도 하지 마.

## 1. 먼저 읽을 문서 (순서대로)
1. AGENTS.md — 작업 규칙, 물리 레이어 함정, 문서 작성 규칙
2. 에이전트문서/HANDOFF.md — 현재 상태·다음 할 일
3. 에이전트문서/GAME_DESIGN.md — 확정 규칙(2장), 결정 이력(6장), 프로토타입 사양(7장), 미결(8장), 위험(9장)
4. 에이전트문서/DEV_HISTORY_LOG.md — 필요한 장만: 9장(인력·물류 설계 수치), 10장(보급·연출 구현), 맨 아래 2026-10-27 기록
   (1~11장은 이전 방향의 설계이므로 "구현 사실과 수치"로만 참고한다)
5. 에이전트문서/과거논의사항/CLAUDE_GAME_DESIGN_REVIEW_2026-10-26.md 3장 — 시장·경쟁작 참고(필요할 때만)

## 2. 질문 전에 코드와 씬에서 직접 확인할 것 (물어보지 말고 측정·조회)
- 라인 길이와 공정 간 거리: CarFactoryTycoon/Assets/Car_Factory/DemoScene/Car_Factory 3.unity의 스플라인,
  LineTrafficManager.cs(minGap 등), SplineConveyorDeformer.cs(구간)
- 현재 라인 속도와 스폰: CarSpawner.cs(spawnInterval, globalLineSpeed), UpgradeManager.cs(호출하지 않는 라인 속도 업그레이드)
- 공정과 부품: WorkerStation.cs, Worker.cs(이동 속도, 체결 속도, 컨디션 수치), AssemblyPart.cs(requiredWork, 궤적),
  PartStack.cs(용량), CarModel_Origin.prefab(부품 수와 requiredWork), AssemblyPartDataSO.asset
- 로봇: StationController.cs(도달 게이트, assembleSpeed), RoboticArmIK.cs
- 특수 공정: WheelStation.cs, ShakeTestStation.cs, WaterTestStation.cs, VehicleInspectionRules.cs, FuelStation.cs, DepartureStation.cs
- 보급: SupplyManager.cs, SupplyWorker.cs(속도, 운반량 6), NavMover.cs, LineCrossing.cs
- 경제와 진행: EconomyManager.cs, ProductionProgression.cs(experienced 개념, 가격식), ProductionContract.cs
- 물리: ProjectSettings/TagManager.asset(레이어), ProjectSettings/DynamicsManager.asset(충돌 매트릭스),
  차량 프리팹의 콜라이더가 트리거인지 여부
- 카메라와 입력: CameraController.cs, InputManager.cs, Packages/manifest.json(inputsystem, cinemachine, animation.rigging)
- 위 측정값으로 "혼자 첫 차 1대 처리 시간"과 "저속에서 공정당 작업 가능 시간"을 계산해서 질문의 근거로 제시해.

## 3. 결정 트리 (이 순서로, 앞 결정에 의존하는 것은 뒤로)
A. 핵심 루프 수치
   - 작업 구간 길이, 라인 속도 단계(정지/저속/고속)의 실제 m/s, 업그레이드별 최고 속도 곡선
   - 부착 시간, 망치 간격, 망치 횟수 업그레이드 비용·단계
   - 혼자 첫 차 처리 시간 목표(튜토리얼 길이)와 초반 미션 페이싱(혼자서는 하루 10대가 불가능한 문제)
   - 수리 기한(검사 전까지 / 다음 공정까지)
B. 차량 투입
   - 터널 클릭 1회 = 1대인지, 연속 투입, 대기열 상한, 자동 투입 업그레이드 여부
C. 경제
   - 대당 수입, 일당(작업자·보급자), 특근 규칙, 로봇 가격·유지비, 업그레이드 비용표, 부품 손실액
   - 미션 보너스와 월별 증가율, 마이너스 자금 처리
D. 인력과 자동화
   - 작업자 피로와 망치 간격의 관계, 휴식, 출퇴근(08~20 정규, 20~22 특근) 연출
   - 고용 방식(공정 현장 클릭), 해고·교체 여부, 보급자 수와 행동, 로봇 도입 조건(experienced 규칙 유지 여부)
E. 플레이어
   - 시점 기본값(1인칭/근접 3인칭), 이동·달리기·점프 수치, 벨트 타기 규칙
   - 들기 개수, 던지기 힘·사거리, 줍기 범위, 바닥 부품 수명(5분)·깜빡임(30초)
   - 물리: 충돌 매트릭스 변경안, 차량 부딪힘 전용 콜라이더 구성
F. 피드백과 UI
   - 우측 상단 미체결 카운터 형식, 붉은 표시 방식(머티리얼/셰이더), 부착·망치 절차적 모션 파라미터
   - 지도(탑다운) 모드, 현장 업그레이드 팝업 구성, 하루 정산 화면 항목
G. 시간
   - 하루 5분 매핑, 주와 월의 경계, 저장 시점(하루 끝?)
H. 프로토타입 범위와 판정
   - GAME_DESIGN.md 7장 사양의 공정 4개를 어느 공정으로 할지, 통과 기준 수치

## 4. 진행 규칙
- 한 번에 질문 1~2개만 묻고, 매 질문에 내 추천 답과 근거(측정값 포함)를 함께 제시해.
- 코드나 문서로 답할 수 있는 것은 묻지 말고 확인해서 보고해.
- 앞선 결정이나 GAME_DESIGN.md의 확정 사항과 모순되면 짚고 다시 물어봐. 무조건 동의하지 마.
- 한 갈래(A~H)가 끝날 때마다 "지금까지 확정" 요약을 보여주고,
  GAME_DESIGN.md의 해당 장과 6장 결정 이력 표, 8장 미결 목록을 바로 갱신해.
- 세션이 끝나면:
  1. DEV_HISTORY_LOG.md 맨 아래에 날짜별 기록 추가
  2. HANDOFF.md 갱신(남은 미결, 다음 할 일 = 프로토타입 구현 착수 조건)
  3. Notion 개발진행 DB 입력 여부를 나에게 확인(AGENTS.md 세션 정리 규칙)
- 새 md 파일이 필요하면 에이전트문서/임시/ 안에 만든다(AGENTS.md 문서 작성 규칙). 상시 문서 3개(GAME_DESIGN, HANDOFF, DEV_HISTORY_LOG)는 직접 갱신한다.
```

---

## 참고: 갈래별 예상 질문 수

| 갈래 | 예상 질문 수 | 비고 |
|---|---|---|
| A 핵심 루프 | 6~8 | 가장 중요. 다른 수치가 모두 여기에 의존한다 |
| B 투입 | 2~3 | |
| C 경제 | 5~7 | A가 정해진 뒤에 진행 |
| D 인력 | 4~6 | |
| E 플레이어 | 5~7 | 물리 매트릭스는 측정 후 제안 |
| F 피드백·UI | 4~5 | |
| G 시간 | 2~3 | |
| H 프로토타입 | 2~3 | 마지막에 범위 고정 |

질문이 30~40개쯤 될 수 있습니다. 한 세션에 다 끝내기 어려우면 A~C까지만 하고 HANDOFF.md에 진행 위치를 남긴 뒤 다음 세션에서 이어갑니다.
