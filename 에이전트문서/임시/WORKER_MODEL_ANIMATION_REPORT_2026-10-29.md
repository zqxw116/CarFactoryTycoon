# 새 작업자 모델·애니메이션 및 플레이 피드백 — 2026-10-29

상태: 구현·기술 검증 완료. 자연 플레이 손맛·미세 포즈는 사용자 확인 대상이다. 검증 전 게임 방향 표기는 유지한다.

## 범위

- Car_Factory 3 플레이어: Worker_Base. 공정 작업자 26명·보급자 1명·주유 작업자 1명: Worker_Helmet. 게임 로직 루트와 씬 참조 유지, 외형 자식만 교체. 장식용 Geometry/KyleRobot은 제외.
- 플레이어 레이어10, NPC8 유지. WorkerSetupTool·FactoryPlayerSetup 재실행. WorkerModelSetupTool.cs 및 새 모델 임포트 설정은 변경하지 않는다.
- 사용자 승인: Mixamo XBot 원본 동작은 자체 소스 Avatar로 읽고 새 작업자 Avatar에 Humanoid 리타겟한다. 캐릭터 Animator는 Worker_Base/Worker_Helmet Avatar 유지. 다른 비율의 소스에 목표 Avatar를 강제 복사하던 설정에서 발이 뜨는 문제를 확인했다.

## Mixamo 파일

저장: `CarFactoryTycoon/Assets/@Resource/Animations/Characters/Worker/`.
전부 FBX for Unity / Without Skin / 30fps. 이동 모션은 In Place. 반복 대기·이동·공중은 Loop Time, 도약·착지·망치는 비반복.

| 파일 | Mixamo 원본 |
|---|---|
| Worker_Idle | Idle — Standing Idle |
| Worker_Walk | Unarmed Walk Forward |
| Worker_Walk_Backward | Walking Backwards — Male Walk Backwards |
| Worker_Strafe_Left | Left Strafe Walking |
| Worker_Strafe_Right | Right Strafe Walking |
| Worker_Run | Unarmed Run Forward |
| Worker_Run_Backward | Running Backward — Backwards Running |
| Worker_Run_Left | Standing Run Left |
| Worker_Run_Right | Standing Run Left, Mixamo Mirror |
| Worker_Jump_Takeoff | Jumping Up — Action Idle Game Blend |
| Worker_Jump_Air | Falling Idle |
| Worker_Jump_Land | Jumping Down — Falling Idle Game Blend |
| Worker_Hammer | Standing Melee Attack Downward — Downward Attack With Axe |

망치는 별도 이름의 Hammer 검색 결과가 없어 한손 내려치기를 망치 도구에 적용했다. 원본 도구 외형은 받지 않는다.

## 주요 수치

| 항목 | 이전 → 변경 |
|---|---|
| 플레이어 캡슐 높이 / 중심Y | 1.8 / .9 → 1.75 / .875m (반경 .28m 유지) |
| 일반 NPC 박스 | (.85,1.94,.34) → (.57,1.77,.43)m |
| NPC 운반 손 높이 / 전방 | 1.094 / .266 → .999 / .230m |
| 망치 동작 | 절차적 .34초 → 실제 상체 클립 .5초 |
| 망치 그립 | 손가락 방향 → 검지–새끼손가락 가로축, 폭 약 .08m에서 자루 굵기 .028m 계산 |
| 망치 머리 거리 | 플레이어 .22 / NPC .29 → 공통 .26m |
| 망치 IK | 강한 직접 위치 보정 → 플레이어 위치 보정 한도 .10m·가중치 .35, NPC .35m·.75. 팔 길이94%로 제한하며 플레이어 실제 접촉 판정 유지 |
| 상체 레이어 | 항상1 → 평소0·타격1·종료/취소0 |
| 터널 연기 방출 | .8 → 4.5초 |
| 연기 수명 / 속도 | 2.2초 / .22 → 5.5~7.5초 / 1.8~2.8m/s |
| 연기 입자 상한 | 36 → 144 |
| 타격/완료 스파크 개수 | 8/18 → 22/36 (상한40→96) |
| 파손 부품 | 약 .8m/s 작은 측방 낙하 → 3.2~5.4m/s 방사 방향, 토크 .55→2.4 |
| 잔해 상한 / 수명 | 40 / 4.25초 유지 |
| 벨트 투척 부품 | 정지 → 지지 접점·저마찰·수평 가속도10m/s²로 벨트 속도 추종 |

## 추가 동작

- 관리자 패널: C 시점 전환, 1 마지막2개 미체결, 2 전체완성, +1,000 재화. 테스트 재화 지급은 출고 미션을 늘리지 않는다.
- Esc 커서 해제. 관리자 패널·일반 uGUI 위 클릭은 커서를 다시 잠그거나 망치질로 소비하지 않는다.
- NPC는 기존 차량 박스3개를 캡슐 질의로 피해 이동. 새 동적 MeshCollider는 추가하지 않는다.
- 불합격 부품 분산 후 프레임·대차 동반 하강. 근접/차량 내부에서도 미체결 배지 표시.

## 확인된 검증

- 연기 상승 약12.6~13.9m, 최대80/144개, 5초 이상 잔존. 레버는 연기/흔들림을 발생시키지 않는다.
- 스파크20개 이상·수명·상한·재사용 통과.
- 실제 투척 박스 부품의 벨트 이동·실제 이동거리·정지·줍기 통과.
- NPC 차량 우회·캡슐 접촉·차량 내부 배지 통과. 검사 도구의 EditorApplication.update 시점 문제를 실제 게임 프레임 구동으로 수정했으며 회피 알고리즘은 그대로다.
- 진동 중 부품 탈락, 불합격 부품 분산, 프레임/대차 함께 -3m 아래 하강, 잔해40개 상한/수명 후0 통과.
- 29개 캐릭터 본 매핑, 누락 스크립트0·깨진 씬 참조0. 13개 모션 Humanoid/30fps 확인.
- 방향별 걷기/달리기 16조합·점프 3단계 기술 검사 통과. 리타겟 수정 후 플레이어 실제 타격/허공 차단, 고정 그립 오차0m/0도, 타격 종료·취소 때 레이어0, NPC 실제 Mixamo 클립 재생 및 기존 자동3타/완료1부품 통과. NPC의 자동 작업 시간·횟수 규칙은 유지하며, 신규 메시 접촉 조건으로 자동 작업을 막지 않는다.

## 성능 측정 한계

첫 측정은 Unity Editor Play, Game뷰572×357, 창 비활성, 차량/NPC0 조건이다. 평균 약30FPS, GPU4.6~4.9ms, 스크립트 Update .7ms, PlayerLoop 약12.9ms, 배치248·SetPass65·약34만 삼각형. 편집기 대기/작업이 포함되어 배칭 병목으로 단정하지 않는다. 출시 빌드/실제 해상도/많은 차량과 인원 조건은 별도 측정이 필요하다.

## 검증 잔여

- 안정 착지 후 발뼈 바닥 높이 .155/.156m, 실제 스킨 메시 발바닥은 바닥 위 .0248m(콜라이더 여유). 1m 이상 뜨던 잘못된 Avatar 복사 문제 해소.
- Esc는 게임 Input System 이벤트로 None/visible=true 확인. 관리자 +1,000 버튼 실제 클릭 후 재화1000·출고0·커서None 유지. 새 모델 1인칭 몸 숨김/3인칭 복원 확인.
- 실행 중 오류·경고0, 컴파일 오류0. 캡처는 메모리에서 채팅에만 표시했다.
- 미확인: 모든 부품/모든 NPC 포즈의 육안 완성도, 자연 보행 완주·손맛, 목표 해상도 출시 빌드 및 다수 차량/인원 부하 성능.
- 직접 플레이 전체 완주와 애니메이션 손맛은 사용자 피드백 대상.

포커스 있는 Editor Play 추가 측정(572×357, 차량/NPC0): 평균30.3FPS, CPU Main32.99ms·PlayerLoop17.77ms·스크립트 .58ms·물리 .21ms·GPU3.16ms·배치204·SetPass64·삼각형 약30.8만. 렌더 스레드 카운터는 사용할 수 없었다. 배칭을 단독 병목으로 확정하지 않으며, 40FPS를 출시 목표 달성으로 판정하지 않는다.

## 수정 파일 및 에셋

- `CarFactoryTycoon/Assets/@Scripts/Comm/Manager/PrototypeDefectDisplay.cs`
- `CarFactoryTycoon/Assets/@Scripts/Comm/Navigation/NavMover.cs`
- `CarFactoryTycoon/Assets/@Scripts/Comm/Object/CarBodyCollision.cs`
- `CarFactoryTycoon/Assets/@Scripts/Comm/Object/PortableAssemblyPart.cs`
- `CarFactoryTycoon/Assets/@Scripts/Comm/Object/PrototypeAssemblyEffects.cs`
- `CarFactoryTycoon/Assets/@Scripts/Comm/Object/PrototypeCarDebris.cs`
- `CarFactoryTycoon/Assets/@Scripts/Comm/Object/PrototypeCrewHandIK.cs`
- `CarFactoryTycoon/Assets/@Scripts/Comm/Object/PrototypeProcessCrew.cs`
- `CarFactoryTycoon/Assets/@Scripts/Comm/Object/PrototypeTunnelFeedback.cs`
- `CarFactoryTycoon/Assets/@Scripts/Comm/Object/ShakeTestStation.cs`
- `CarFactoryTycoon/Assets/@Scripts/Comm/Object/Worker.cs`
- `CarFactoryTycoon/Assets/@Scripts/Comm/Player/FactoryPlayerController.cs`
- `CarFactoryTycoon/Assets/@Scripts/Comm/Player/PlayerAssemblyHandIK.cs`
- `CarFactoryTycoon/Assets/@Scripts/Comm/Player/PlayerAssemblyInteractor.cs`
- `CarFactoryTycoon/Assets/@Scripts/Comm/Player/PrototypePlayerTestControls.cs`
- `CarFactoryTycoon/Assets/@Scripts/Editor/FactoryPlayerSetup.cs`
- `CarFactoryTycoon/Assets/@Scripts/Editor/WorkerSetupTool.cs`
- 신규 도구: FactoryCharacterMigration / FactoryCharacterAnimationSetup / WorkerAnimationImport / FactoryCharacterChecks / FactoryWorkerFeedbackChecks. 신규 공통 WorkerHammerAnimation.
- FactoryPlayer.controller / WorkerAnimator.controller, Car_Factory 3 씬, 위13 FBX와 메타데이터. 새 작업자 프리팹·모델·재질·텍스처는 씬의 필수 의존 에셋으로 함께 기록하며 모델 임포트 설정과 WorkerModelSetupTool 소스는 편집하지 않았다.
- 기존 아트/차량 분할/ProPixelizer/AGENTS/솔루션 작업은 이번 변경에서 분리해 보존한다.

분담: Astra Low 초기 모델·IK 설계/이전 도구, Sol Medium 연출·물리·후속 연결, Luna High 관리자 UI, 주 에이전트 최종 리타겟·검증·통합. 사용량 제한으로 중단된 담당자의 나머지는 주 에이전트가 처리했다.
