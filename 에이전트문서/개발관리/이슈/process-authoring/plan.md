# 공정 편집 도구 Implementation Plan

> For agentic workers: Use superpowers:executing-plans for continuous execution in the current Unity checkout. 사용자 개발 시작 지시로 계획 작성 후 바로 실행한다. 추가 계획 승인/실행방식 질문은 반복하지 않는다.

**Goal:** 카메라/로봇 작업자를 복구하고 메인과 동일 설정·실행 경로로 공정/차종/주체를 편집·테스트하는 도구를 만든다.
**Architecture:** 설정 SO → 라인 좌표 해석/적용 → 기존 공정 실행기를 공유한다. 에디터 창/핸들과 프리뷰 세션은 저작 값과 실행 상태를 분리한다.
**Tech Stack:** Unity6000.3, Splines2.8.4, Cinemachine2.10.5, Animation Rigging1.4.1, 기존 Editor 정적 검사 패턴.
**Spec:** [승인 명세](spec.md)

## Global Constraints

- Play 중 .cs 추가/수정 금지. 코드 수정 전 MCP/화면에서 편집 모드 확인.
- 기존 아트/dirty 변경 보존. 현재 실제 Unity 체크아웃 재사용, 새 프로젝트 복제 없음.
- 반복 집기/슬롯 재충전에 Instantiate/Destroy 없음, Count와 예약/환불 유지.
- 배속 0.1~1은0.1단위, 1~20은 정수. 종료 시 시간 복원.
- 작업 가능한 작업자 공정만 정차; 취소 시 해제. 로봇팔은 흐름형 유지.
- 원본 FBX 수정/유료도구/신규 외부 게시/커밋·푸시는 이번 착수 단계에 자동 포함하지 않음.

## Review Focus

1. 진입 중 수락→정차점/차량 풀 재사용으로 취소가 누락되지 않아야 함 (Task3).
2. 공통 횡거리 변경 시 반대 진행 라인의 좌우와 수동 진행좌표 보존 (Task2).
3. 복합 부품/주체 설정이 동일 PartType 키로 덮어써지지 않아야 함 (Task2/4).
4. Play 종료/리로드 후 배속·예약·이벤트 구독과 메인 저장 상태 누수 없음 (Task4/6).
5. 미지원 차종·도달불가 경로를 정상 체결로 취급하지 않음 (Task2/5).

## Task1 — 카메라 기준 이동·공정 작업자 RobotKyle

Files: Comm/Player/FactoryPlayerController.cs, Editor/FactoryFeedbackNov2Checks.cs, Editor/FactoryProcessAuthoringChecks.cs, Editor/FactoryWorkerRobotSetup.cs (아래 소스 경로는 모두 CarFactoryTycoon/Assets/@Scripts/ 기준).
Interfaces: ResolveMoveDirection(Vector2) 현재 yaw 기준; RotateView(Vector2) 시선 갱신; SimulateMovement 기존 호출 유지. FactoryWorkerRobotSetup.Apply() 공정 Worker 루트 외형만 전환.
- [ ] 기존 ResolveMoveDirection으로 W 유지 중 yaw180 회전 시 반대 진행을 기대하는 검사 RED.
- [x] 같은 실제 호출 경로로 카메라 이동 기준과 우클릭 빠른 회전 반영, 이전 자유시점 검사 최신 요구로 교체.
- [ ] 메인 Worker의 RobotKyle Avatar/손 앵커/IK/레이어 검사를 RED로 확인 후 루트·고용 참조를 보존해 전환.
- [x] 컴파일과 정적 검사 GREEN, 실제 고용/집기/운반/귀환·카메라 검사. 원본 아트 변경 없음 확인.

## Task2 — 공정 저작 데이터·해석·메인 적용

Files: Data/ProcessAuthoringProfile.cs(new), Comm/Authoring/ProcessAuthoringLayout.cs(new), Comm/Manager/ProductionProgression.cs, Editor/FactoryProcessAuthoringChecks.cs.
Interfaces: profile 공정ID/차종ID/주체ID/부품ID; ProcessAuthoringLayout.Apply(ProductionProgression, ProcessAuthoringProfile) 저장값 적용; 미설정 기존값 fallback. Source record poses line-local, destinations car-local.
- [ ] 좌우/±X·공통+개별 보정·동일PartType 복합 슬롯·차종 미지원·기존 Origin fallback RED.
- [x] 공정 안정ID와 프로필 저장, 기존 배치 가져오기, 저작값 있는 슬롯 자동계산 방지.
- [x] 저장/로드/재적용 pose와 실제 메인 배치 일치 GREEN.

## Task3 — 공유 실행기의 정차·작업자 대기·중단

Files: Comm/Object/PrototypeProcessCrew.cs, Comm/Object/Worker.cs, Comm/Manager/LineTrafficManager.cs(필요시), Editor/FactoryProcessAuthoringChecks.cs.
Interfaces: PrototypeProcessCrew implements ILineGate; GateProgress/GateEnabled; CancelCycle() 프리뷰/중단 경로. 기존 교통 등록/정지와 LifeVersion 사용.
- [ ] 준비 가능 정차/재고0 통과/퇴근·취소 해제/뒤차/풀 재사용 RED.
- [x] 작업 수락·예약과 게이트 소유를 한곳에서 관리. 일반 대기/체결 대기를 분리하고 정차 후 부착·3타 체결, 완료 방출.
- [x] 기존 재고 환불/슬롯 재충전·로봇 흐름형·검사 회귀 GREEN.

## Task4 — 새 편집 씬·에디터 창·프리뷰 실행

Files: Editor/ProcessAuthoringWindow.cs, Editor/ProcessAuthoringSceneSetup.cs, Comm/Authoring/ProcessAuthoringPreview.cs, @Scene/ProcessAuthoringScene.unity(new).
Interfaces: 창은 프로필 저작값을 편집/저장, 프리뷰는 공정 목록/실행기 재사용. Play() / Retry() / Stop() / SetSpeed(float) 외부 제어.
- [ ] 첫 공정/차종/다중 주체 로드, 저장 왕복, 재시도 스냅샷·일반 반복 소모, 배율 경계·시간 복원 RED.
- [x] 메인과 같은 실폭 직선 레일·Camera/Light, 그룹 하이어라키, 창과 핸들/Undo/미저장 전환 구현.
- [x] 메인 경제/PlayerPrefs와 격리. 기존 씬을 묵시적 additive로 열지 않으며 미저장 변경은 보존.
- [x] 1회/반복/중간재시도/0.1·1·20배 실제 실행 GREEN.

## Task5 — 주체별 경로·리깅·주유

Files: Comm/Authoring/ProcessAuthoringPath.cs(new), PrototypeProcessCrew/Worker/WorkerHandIK/AssemblyPart 기존 소스 필요한 경계, FuelStation와 주유 프리뷰 어댑터, 편집 창 핸들.
Interfaces: 작업자 root/부품/로봇 목표 경로 분리; 차종 로컬 그립/부착 pose; 도달/관통 유효성 결과.
- [ ] 다른 주체 경로 독립 저장·엔진 접근/문/바퀴/주유 노즐/불가능목표 취소 RED.
- [x] 베지어 경로 및 손 목표 연동, 기존 애니메이션에 제한 상체/발 보정. 같은 뼈 중복 IK 방지.
- [ ] 엔진·문·주유 실제 캡처/오차·관통/발 미끄러짐 기록, 사용자 시각평가는 별도 미확인.

## Task6 — 통합 회귀·인계

Files: 관련 Editor 검사 + 명세/계획/작업기록, HANDOFF, DEV_HISTORY_LOG.
- [x] 새 씬과 메인의 설정 일치, 기존 M2/Character/Services 및 수정된 피드백 검사 실행.
- [ ] 핵심 슬롯 경로 GC/풀 재사용, 취소/정산/20배/씬종료 검증.
- [x] fresh reviewer로 이번 변경 범위 검토, 중요 지적 수정 후 관련 회귀 재실행.
- [ ] 사용법·검증 근거·한계 기록. 완료한 항목만 완료 표시.

## Execution ledger

- 2026-11-04: 사용자 설계 승인 및 개발 착수. 별도 반복 승인 없이 현 체크아웃에서 직접 실행한다.
- Ruling: 기존 dirty Unity 체크아웃 재사용 — 활성 에디터/사용자 아트 작업과 같은 참조를 유지하기 위함 — 변경 범위를 정확히 구분해 검토해야 한다.
- Unity MCP stale session을 UI Disconnect/Connect로 복구. 실제 edit mode, 콘솔 error0 확인.

- Task1: held-W yaw 기준 RED(dot1)→GREEN, 24 process Worker RobotKyle/scale/hand/IK PASS. 실제 hire actor visible; idle right-drag 180deg/8frames PASS. Feedback23 PASS. Character real NPC3hits/one part + locomotion/jump PASS, final console error0.
- Task1 수정: Kyle prefab demo ThirdPersonController conflicted with NPC; disabling did not block AnimationEvents, so scene instances remove that controller and retain WorkerFootstepReceiver. Source prefab/FBX untouched.
- Task2: 23 records/50 actor transforms imported; max position roundtrip error0.000001907m; actual conveyor width2m. Speed boundaries/stable IDs/unknown vehicle checks PASS. Main links profile; authored stocks bypass automatic layout. Complete UI/vehicle override coverage remains.
- Task3: targeted gate added to shared crew and traffic. Actual hire→stop within6cm→assembly→release, consume1, cancellation, reused car, stock0 pass, off-duty pass all PASS; console error0.
- Task4 초기 기록: ProcessAuthoringScene created, only1 scene loaded,23 groups/first visible/worker+car present. Basic Play initializes. Window and handles implemented; end-to-end preview/saving still under test. Conveyor source scene refs were detected and explicitly cleared.
- 위 초기 단계 이후의 최종 상태는 아래 검증 원장을 따른다.

### 통합 검증·최종 검토 (2026-11-04)

- FactoryProcessPreviewChecks.StartMatrix(): 엔진1/문8/바퀴13/주유23 × 작업자·로봇 8조합 모두 완료 PASS. 조립6조합 각각 정확히1·1·4개 재고 소모. 저장 후 재시도 배치 재적용, 일시정지 상태 도구 종료의 시간복원, 공유 FuelStation 차종별 주유구, 도달불가 작업위치 미완료 판정 PASS. 콘솔 error0.
- FactoryProcessPreviewChecks.Start(): 별도 프로필 fixture에서 실제 베지어 경로+Animation Rigging 작업자20배/냉간 로봇20배, 각3타/소모1, 중간재시도/실행시작재고복원/재집기, pause/.1배/정지 시간복원, 자동반복 소모, 재고0 통과·미완료 PASS. 콘솔 error0.
- FactoryProcessEditorChecks.Run(): 부착점/그립 보정 해제→프리팹 원값, 동일ID 차종 프리팹 교체/Undo, 메인의 기존 dirty 상태 보존, 전체 공정 작업자 저장 pose 일치 PASS. 실행 중 코드 수정 없이 편집 모드에서 검사.
- 메인 FactoryProcessGateChecks.Start(): 실제 고용→정차6cm이내→체결→출발/소모1, 풀 재사용/취소해제/재고0통과/퇴근통과 PASS. 콘솔 error0.
- FactoryAssemblyM2Checks.Start(): 초기 Bumper_51 집기 RED. 원인: 프로필 참조 외 활성 legacy ` PartStack_Bumper`가 씬에 남아 M2/보급/클릭 후보에 섞임. 원본 인스턴스를 삭제하지 않고 비활성 RetiredProcessInventory로 보관. 재실행 실제26슬롯/77도메인타격/검사·부분수리·초기화 PASS. authored 분기에서도 legacy robot pile 숨김을 유지하도록 보강.
- FactoryCharacterChecks.Start(true,true): 참조누락0/스크립트누락0/Humanoid29, 실제16방향·점프1.175m·앞문망치접촉/허공0·그립오차0, NPC실제망치3타·완료1 PASS. 발바닥 높이 약.205/.213m는 관측값이며 자연스러움 합격판정 아님. 콘솔 error0.
- 독립 최종검토: Critical0, Important6. 원래 동작을 확인한 재시도배치 RED, 주유구 오차1.5676m RED, 먼 작업위치도 완료 RED, 창닫기 후 timeScale0 RED → 모두 수정 후 행렬 GREEN. 차종교체/Undo·dirty보존은 에디터 통합검사 GREEN. Undo 첫 검사에서 파괴된 참조 RED가 추가로 드러나 CompleteObjectUndo로 수정 후 GREEN. 별도 재검토 대신 동일 검사로 검증.
- 추가 실제행렬이 발견한 결함: 바퀴 복제 시 부모 scale 누락(.33→1), 체결대기/작업위치 혼용, 엔진 측면 도달불가, 바퀴 앞2개 후 조기방출. 원인을 수정하고8조합/정확재고 검사 GREEN. 엔진 기본 접근은 차체 바깥 코너를 돌아 앞 범퍼에서 작업하며 명시적인 저작 경로/위치는 우선한다.
- Final: Ruling: 사용자 수정 중인 같은 Unity 체크아웃 유지 — 기존 씬/아트 참조를 보존하고 이번 코드·씬만 식별 — 잘못 분리하면 다른 작업 변경까지 반영될 비용이 있으므로 전체 stage 금지.
- Final: Ruling: 전신 IK로 확대하지 않고 기존 팔 리깅+제한 상체/발 보정 유지 — 사용자 승인 범위이며 시각적 품질은 별도 평가 — 잘못 판단하면 더 복잡한 자세에서 추가 전신솔버 작업이 필요.
- Final: minor (deferred): 없음. 제외된 아트/설정/실험 디렉터리는 검토 범위 밖이며 변경 완료라고 주장하지 않는다. 시각적 자연스러움·장시간 부하·전체 차종은 현재 별도 미확인 범위다.

- FactoryPrototypeServicesChecks.Start(): 독립 새Play에서 전체 PASS(보급 실제 이동/하역·퇴근보존/예약/임금/부채/수동·고용RobotKyle·자동주유/작업음). 연속 검사로 생긴80원 때문에 초기조건을 위반한 실행은 채택하지 않았고 새Play로 재실행했다. 보관재고까지 대상27로 수집한 RED는 participatesInSupply=false로 명시 제외하여 실재 활성26대상 GREEN. 마지막 음향 fixture는 기존 speed0 진입점 고정에서 새 정차점으로1.2m/s 실제 진입하도록 수정 후 GREEN. 콘솔 error0.
- FactoryPickupSlotChecks.RunCoreGc(): 워밍업 후100회 집기/재충전/Add 관리힙0bytes PASS.
- FactoryFeedbackNov2Checks.Start():23항목 PASS(카메라현재yaw/S운반/망치1초·연타초기화/UI차단/접촉조작/full터널/단일외피/차량지지이동·회전·점프/쿼리그룹·재사용). 콘솔 error0.
- 시각 검증 중: 엔진 기존 리깅 useAnimationRigging/bodySupport를 fixture에서 켜고1배속 캡처. 부착 중 측정 WristError약0.941m, 운반 시 약0.395m. 자동 체결 PASS와 별개로 기본 양손 목표/상체 자세가 자연스럽다고 승인할 수 없다. 이 옵션은 기본 비활성이며 전신IK 채택 근거로 과장하지 않는다. 캡처 Temp/ProcessAuthoringCaptures/EngineWorkerAttachRig.png(원본984×548).

- 시각 검사로 추가 재현: PrototypeCarryPoint가 기존 Worker의 그립 중심 보정을 누락. 수동 실측 중심오차0.5809273m RED → 기존 그립중심·스케일·운반회전을 반영하도록 수정. CheckCarryGrip 회귀 추가. 수정 후 StartMatrix8조합/재고/저장Retry/종료시계/주유구/먼위치미완료 전체 PASS·콘솔0. 최종 체감 오차는 후속 캡처값을 따른다.
- 문 package rig 부착 표본 WristError0.0813m, 캡처 DoorWorkerAttachRig.png. 엔진 앞면 그립/전용운반곡선 실험은 별도 런타임 프로필 복사본에서만 수행했으며 원본 프로필에 미채택했다. 해당 실험은 솔버 성능만이 아니라 그립/운반점 선택이 품질에 영향을 준다는 진단이다.

- 최종 carry 수정 뒤 FactoryProcessPreviewChecks.Start()/StartMatrix() 두 검사를 다시 실행해 모두 PASS·콘솔 error0. CheckCarryGrip: 그립 중심1mm이내 정렬 GREEN. 워밍업 이후 핵심 슬롯100회0bytes는 앞 절의 실측값을 유지한다(배치/운반 수정으로 슬롯 소비 구현을 바꾸지 않음).
- 체크박스 주의: RED를 별도로 보지 않은 포괄 항목/모든 조건을 아직 실측하지 않은 시각 항목은 비워 두었다. 이를 미구현 목록과 동일시하지 말고 위 실행 결과·명세의 실제 확인 범위를 함께 읽는다. 전체23공정·새차종·장시간부하·발미끄러짐 정량 측정은 대표공정 회귀로 대체하지 않는다.

- 최종 후속 실행: FactoryProcessEditorChecks.Run() 재열기 선택포함 전체 PASS. FactoryCharacterChecks.Start(true,true) carry수정 후 새메인Play 재실행 PASS(실제NPC3타/완료1·플레이어접촉·16방향·점프·참조정합), 콘솔0. 발본높이 이번표본L.248/R.217m(기존.205/.213은 다른표본), 발미끄러짐 정량합격으로 해석하지 않는다.
- 최종 캡처: 엔진 기본그립+package rig+bodySupport 부착 표본 WristError.8919m(EngineAfterCarryFix.png), 고용주유RobotKyle/native IK 주유 표본.1087m(FuelRobotKyle1x.png), 문 package rig 표본.0813m(DoorWorkerAttachRig.png). 표본값·주체/옵션이 달라 solver우열/전체모션 통계로 비교하지 않는다. 창에 실제 손목오차·15cm초과 경고를 추가했다.
- Final: Ruling: 검토에서 제외한 사용자 아트/기존 실험/프로젝트 설정은 이번 변경의 검증·완료 주장에 포함하지 않음 — 원본 작업을 보존 — 합칠 경우 별도검토가 필요하다.
- Final: Ruling: 대표공정/선택프레임만으로 전체 자연스러움·전체차종·장시간 다중차량·에디터재시작후Undo를 보증하지 않음 — 도구는 사용 가능하되 시각 튜닝은 ready-for-human/open 유지 — 이후 확장·장시간 사용에서 추가보정 비용이 남는다. 실제 창 재열기/교체Undo/도메인리로드 이후 기존 프로필로드는 확인했다.
- 종료 상태: 편집 모드·시간1·ProcessAuthoringScene 단독/공정0·공정 편집창 열림. 테스트 fixture의 임시 프로필·선택·카메라는 저장하지 않았다. 신규 커밋/푸시/외부게시 없음.
