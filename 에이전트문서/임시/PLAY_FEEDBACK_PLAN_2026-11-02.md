# Play Feedback Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans and dispatching-parallel-agents for independently owned files. Track tasks below.

**Goal:** 승인한 13개 플레이 피드백을 실제 씬에서 수정하고 런타임 검증한다.
**Architecture:** 카메라 orbit, 이동 기준축, 몸/행동 방향을 분리한다. 망치 접촉 경로 하나로 작업·레버·투입을 구동한다. 지지체 이동 보정과 차량 복합 충돌체, 공용 UI 좌표 배율을 추가한다.
**Tech Stack:** Unity 6000.3, Cinemachine, InputSystem, CharacterController, IMGUI, 기존 Humanoid Animator.
**Spec:** PLAY_FEEDBACK_DESIGN_2026-11-02.md (사용자 2026-11-02 진행 승인).

## Global Constraints
- 기존 수정·FBX·GUID 보존, 플레이 중 C# 수정 금지, 새 md는 에이전트문서/임시.
- 기존 체크아웃을 사용하고 작업 브랜치만 분리. 커밋/푸시 없음.
- Ruling: 사용자 진행 승인은 설계 실행을 승인한 것으로 받아들이고 계획에 대한 추가 승인 반복은 하지 않는다.
- Unity 플레이/컴파일 제어와 씬 저장은 root만 수행. 독립 에이전트는 자기 소유 파일만 편집한다.

## Review Focus
- 이동 중 뒤를 보는 상태에서 키 추가/해제와 운반 자세: Task 1.
- 재클릭 순간 망치 텔레포트가 타격을 만들거나 UI 클릭이 새 스윙을 만드는 문제: Task 2.
- 지지 차량 풀 반환·다른 차량으로 교체될 때 잘못 따라가는 문제: Task 3.
- 작은 화면의 표시/클릭 좌표 불일치, 정산 pause 중 타이머 및 중복 다음 날: Task 4.
- 노즐/주유구 사이 거리·벽·손 점유와 자동 주유 회귀: Task 5.

### Task 1: 카메라/운반/모션
Files: FactoryPlayerController.cs, PlayerAssemblyInteractor.cs, FactoryCharacterAnimationSetup.cs, FactoryPlayer.controller. Tests: Editor/FactoryFeedbackNov2Checks.cs.
- [x] 기존 상태에서 camera yaw 변경 시 root 회전, body/holdAnchor 분리, 망치 3초를 실패 근거로 기록한다.
- [x] RotateView(Vector2 delta), ResolveMoveDirection(Vector2 input), BodyForward/BodyRight/CarryAnchor를 정의. 모든 이동 키가 zero가 되면 다음 이동에 view yaw를 캡처한다. 몸/운반은 이동 방향을 따른다.
- [x] 기존 Kyle 걷기/달리기와 보유 점프 클립을 사용하도록 애니메이터를 수정. 발 위치/점프 정상 전환 점검.
- [x] 카메라 회전 중 W 방향 유지, S 운반 기준 일치, 이동 재개 시 새 기준, jump 상태를 검사한다.

### Task 2: 망치/레버/터널/질의 캐시
Files: WorkerHammerAnimation.cs, PlayerAssemblyInteractor.cs, PrototypeHammerContact.cs, PrototypeLineController.cs, PrototypeTunnelFeedback.cs.
- [x] 1초 및 재시작 요청, 원거리 입력 차단에 대한 실패 검증.
- [x] Duration=1, 새 클릭으로 진행률/접촉 latch/이전 머리 pose 초기화. 이동 방향과 별개인 몸 앞 도달 범위만 허용.
- [x] PrototypeLineController.TryHammerContact(Vector3 from, Vector3 to, float radius) 호출. 실제 범위의 터널/레버 한 번 처리, 기존 Update 원거리 입력과 지도 버튼 제거.
- [x] 터널 full 응답에도 불규칙 shake. 질의 노드 전용 그룹 및 null/파괴 cleanup, 중복 생성 방지.
- [x] 실제 접촉/허공/연타/UI 클릭/대기열 full 및 캐시 증가량 검증.

### Task 3: 차량 외관 충돌/지지체
Files: 새 Comm/Object/PrototypeVehicleSurface.cs, FactoryPlayerController.cs, CarController.cs (pool activation 경로).
- [x] 차량 지붕 지지체 이동 실패 재현.
- [x] 차량 렌더 bounds에 맞는 layer 11 복합 box 형상, 프레임 및 assembled 부품만 활성, 비다양체 mesh collider 사용 안함.
- [x] 지지 CarController 위치/회전 delta를 CharacterController 이동에 반영하고 점프·pool·Teleport에서 해제. belt 중복 적용 금지.
- [x] 보닛/지붕/트렁크 접근, 차량 이동/회전/점프/반환 테스트.

### Task 4: 화면 배율/정산 (독립 에이전트)
Files: 새 PrototypeGui.cs, PrototypeDayHUD.cs, PrototypeDayCycle.cs, FactoryHiringUI.cs, PrototypePlayerTestControls.cs 및 관련 OnGUI. 다른 agent 소유 파일은 제외.
- [x] 기존 화면 크기/정산 10초 실패 근거.
- [x] PrototypeGui.Scope(), Width, Height, PointerPosition, ToScreenRect(Rect) 공용 논리 좌표 계약을 제공. 별도 HUD 영역이 화면 밖으로 벗어나지 않도록 scaling/reflow.
- [x] unscaled 10초 자동 정산 진행과 수동 버튼 중복 방지.
- [x] 640×360/1280×720/1920×1080 좌표·범위 및 자동/수동 진행 검사.

### Task 5: 주유/보급 (독립 에이전트)
Files: FuelStation.cs, PrototypeServicesInteraction.cs, SupplyWorker.cs 및 보급 운반 표시 전용 코드.
Consumes: FactoryPlayerController.BodyForward/BodyRight, CarryAnchor, CanReachInteraction(Vector3,float).
- [x] 원거리 E 안내/노즐 순간연결 실패 근거.
- [x] 가까이에서 E 노즐 pickup, 손 운반, 가까운 주유구 연결 후 E 유지. 안내/실행 동일 조건. 이동 이탈/정산/부품 운반 차단.
- [x] 기존 고용/자동 주유 유지. 보급 박스의 목적·크기·손 고정 및 이동 방향 확인.
- [x] 실제 런타임 수동/고용/자동 주유 및 운반 자세 검사.

### Task 6: 통합/검토/기록
- [x] 컴파일 오류 0, 기존 FactoryCharacterChecks 및 새 피드백 검사 실행. 기능별 실제 씬 수치/조건 결과 기록.
- [x] fresh reviewer가 diff/spec 대비 검토, 중요한 문제 수정 후 재검증.
- [x] GAME_DESIGN 현재 확정 변경, HANDOFF와 DEV_HISTORY_LOG 갱신. 승인한 기술 검증과 사용자 손맛 판단 구분.
- [x] 테스트 종료 후 편집 모드, 결과와 남은 한계만 간결히 보고. 커밋 없음.


## 실행 판정 기록 (2026-11-03)
- Ruling: 새 PrototypeVehicleSurface 대신 기존 CarBodyCollision을 확장 — 중복 충돌체 및 이동 AI 경로 계약을 유지하기 위함.
- 카메라/운반/망치/레버/터널/차량 지지체/캐시/HUD 입력의 FactoryFeedbackNov2Checks 23개 검사 PASS(평행 이동·회전 포함).
- 기존 FactoryCharacterChecks contacts=true,npc=false PASS. 과거 실내 Controll 타격 fixture는 프레임에 가려지는 정상 차단을 확인하고 노출된 오른쪽 앞문으로 교체. 타격 작업량0→5, 허공 무효·그립·취소 PASS.
- 정산 초기 검사에서 프레임 이전의 deltaTime 반영 및 코루틴 overshoot 혼동 발견. 절대 realtime 마감시각으로 수정, 실제 자동 진행10.006초와 중복 진행 방지 PASS.
- 640×360/1280×720/1920×1080 레이아웃·좌표 검사 PASS, 실제596×274 GameView HUD/팝업 화면 검토.
- Ruling: 노즐 상태의 논리 유효성에서 OS 포커스 조건 제거 — 실제 입력 Update는 게임 포커스로 제한하되 마우스가 나갔다고 노즐을 강제 반납하지 않도록 함.
- Fresh reviewer 최종 Critical/Important 없음. HUD 클릭 중복 문제는 공용 blocker와 실제 입력 주입 검사로 수정/검증.
- 서비스 통합 검증 PASS: 수동/작업자/자동 주유 총3회, 실제 보급 이동/하역/예약/임금/다음날/음향 회귀. 최종 런타임 오류0, 편집 모드 복귀. 커밋/푸시 없음.
