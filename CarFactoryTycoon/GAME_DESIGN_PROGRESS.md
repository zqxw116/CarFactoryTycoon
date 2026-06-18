# CarFactoryTycoon — 게임 설계 & 작업 진행 노트

> 최종 업데이트: 2026-06-18

---

## ★ 2026-06-18 작업 요약 (오늘)

### A. 매니저 구조 전면 개편 (GameManager 해체 → 역할별 분리)
- 옛 **GameManager.cs 삭제**. 역할을 셋으로 분리:
  - **EconomyManager** (`MonoSingleton<T>`) — `currentMoney`, `SellCar(price)`, `TrySpend(cost)`, `OnMoneyChanged(amount, delta)`
  - **UpgradeManager** (`MonoSingleton<T>`) — 라인/로봇/차량 업그레이드 배율 (`lineSpeedMultiplier`, `robotAssembleMultiplier`, `carSellPriceMultiplier`) + 구매는 EconomyManager.TrySpend 차감
  - **GameFlowManager** (`MonoSingleton<T>`) — 게임 상태(Ready/Running/Paused)
- **StationConfig** (`MonoSingleton<T>`) — 전 로봇팔 공유 체결 전역값(`assembleSpeed`, `assembleReachThreshold`, `reachReleaseMargin`). 한 곳만 바꾸면 일괄 적용.
- **InitManager** (씬 1개 배치) — Awake에서 각 매니저 `.Instance.Init()` 호출로 생성/초기화 순서 일원화. CarPool은 `GameObjectGroup` 자식으로 생성.
- **CarPool** (`GameObjectSingleton<T>`) — Resources 런타임 로드, 활성 차량은 `ActiveCars` 컨테이너로 정리(`GameObjectGroup > CarPool > ActiveCars > 차량`).
- 싱글톤 패턴 정리: 코드생성형(자동 생성) = MonoSingleton, 씬 배치형(자식 필요) = GameObjectSingleton. 개별 Awake null-가드 제거.

### B. 돈 UI / 연출 (MoneyUI.cs, DOTween)
- `MonoSingleton<MoneyUI>` — Init에서 Canvas+TMP 코드 생성, `EconomyManager.OnMoneyChanged` 구독.
- 카운트업/다운(DOVirtual.Float) + 텍스트 펀치. 코인 연출(임시 흰박스): 증가=하단→텍스트, 감소=텍스트→위 페이드. (실 스프라이트 생기면 Image.sprite 교체)

### C. 차량 스폰 / 라인
- **CarSpawner** — 스플라인 `MainSpline` 자동 탐색, 풀링 사용, 스포너 위치에서 **가장 가까운 스플라인 지점**을 출발점으로(`GetNearestPoint`). 출발 지점 박스 기즈모 + 연결선.
- **CarController.SetPath(spline, speed, startProgress)** — 출발 진행도 인자 추가.

### D. 체결 판정 모델 회귀 (체류시간 → IK 도달 게이트)
- "부품이 팔에 닿지 않아도 알아서 체결" 문제 → **Rig_End(endEffector) ↔ 파츠 ArmLookTarget 거리 ≤ threshold일 때만 AddWork**.
- **버그 수정**: `AssemblyPart.Awake`가 `armLookTarget`을 자기 자신으로 덮어쓰던 것 → 인스펙터 존중 + 자식 "ArmLookTarget" 탐색.
- **떨림(덜덜거림) 수정**: 게이트 경계 ON/OFF 토글이 원인 → **히스테리시스**(가까우면 engage ≤threshold, 멀어질 때만 release >threshold+margin). `reachEngaged` 상태로 관리.

### E. StationController 동작/툴 개선
- 작업존 점유 차량 추적(`carsInZone`): 진입 등록 → 비면 `TryBeginAssembly`로 대상 선정, 이탈 시 진행중이면 포기·복귀 후 남은 차량 재시도. (지나간 차량 계속 바라보는 문제 해결)
- 작업존(트리거 BoxCollider) 기즈모 표시 + **커스텀 에디터**(`StationControllerEditor`)로 씬에서 마우스 핸들로 size/center 직접 편집.
- TrackingTarget을 스테이션 자식으로 배치(하이러키 정리).

### 폐기 파일
- GameManager.cs, FactoryLineBuilder.cs(라인 생성툴 — S자 라인은 CreateSplineMesh로 직접 배치, 이름 `MainSpline`).
- AssemblyStation.cs(옛 확률 체결) 로직 비움.

### 씬 배치(직접 필요) — 나머지는 자동 생성
- **InitManager** ×1, **CarSpawner**, **GameObjectGroup**(빈 오브젝트), **MainSpline**(S자 라인), 로봇팔별 **StationController**(+RoboticArmIK, 트리거 BoxCollider).
- 로봇팔: `RoboticArmIK.endEffector`=Rig_End, 파츠에 "ArmLookTarget" 자식 필요.

### 다음 할 일
- [ ] 플레이로 풀 루프 검증(스폰→흐름→IK 체결→판매→재사용, 떨림 해소 확인)
- [ ] 떨림 잔존 시: IK 데드존 또는 파츠 회전 Euler→Quaternion Slerp
- [ ] 인스펙터 튜닝값(prewarm/초기자금/배율) SO 또는 InitManager 필드로 분리 검토
- [ ] 행거(라인 사이 이송), 판매/업그레이드 UI 배선, AI NPC, 피버타임

---


## 1. 게임 개요

자동차 공장 자동화 **방치형/클리커/공장 운영 게임**.

- **시점**: ISO(아이소메트릭)
- **라인 구조**: 화면 상단 1라인 → 하단 5라인으로 이어지는 **S자 컨베이어벨트**
- **핵심 딜레마**: 라인 속도↑ → 생산량↑ 이지만 로봇팔 체결이 못 따라가 **체결 누락** 발생 → 테스트 구간에서 불량 처리. 유저 클릭/AI NPC/자동화로 보완하며 *속도·체결안정성·수익·자동화*의 균형을 맞추는 게임.

### 라인 구성
1. **1라인 프레임 조립** (스캔/용접)
2. **2라인 도장** (프라이머/색상/코팅, 부스 진입 시 재질·색 변경)
3. **3라인 내부 조립** (시트/문/창문)
4. **4라인 외부 조립** (엔진/보닛/바퀴, 리프트 연출)
5. **5라인 테스트** (방수→압력→고속주행→판매대기, 누락 시 불량 연출)

### 차량 기준 치수
- 차량 길이 큐브 기준 **scale X=2, Z=4.5**
- 로봇팔은 라인별 파츠 개수만큼 **4.5m(차량 길이) 간격**으로 배치

## 2. 체결 시스템 설계 (확정)

### 판정 = IK 도달 게이트 (2026-06-18 재변경)
- 스테이션 **트리거 콜라이더**는 "후보 진입 감지"용(작업 대상 선정). 콜라이더는 라인 앞쪽에 둬서 차량이 일찍 닿으면 로봇팔이 미리 체결 준비.
- **실제 체결 진행 조건**: 로봇팔 끝 `Rig_End`(= `RoboticArmIK.endEffector`)와 파츠 `ArmLookTarget`(= `AssemblyPart.armLookTarget`)의 거리 ≤ `assembleReachThreshold`(기본 0.5m)일 때만 `AddWork` 누적.
  - 팔이 못 닿으면 대기(준비), 닿으면 체결값 증가, 차량이 멀어져 거리 벌어지면 증가 멈춤.
- 콜라이더 점유 차량 추적: `OnTriggerEnter` 등록 → 비면 `TryBeginAssembly`로 대상 선정, `OnTriggerExit` 시 진행중이면 포기·제자리 복귀 후 남은 차량 재시도.
- 주의(고친 버그): `AssemblyPart.Awake`가 `armLookTarget`을 `GetComponentInChildren<Transform>()`(자기 자신)으로 덮어쓰던 것 → 인스펙터 지정 존중 + 없으면 자식 "ArmLookTarget" 탐색으로 수정.
- 완성시간 = `requiredWork / assembleSpeed` (닿아 있는 동안 기준).
- 이전(체류시간 타워디펜스 모델)은 폐기: "부품이 팔에 닿지 않아도 알아서 체결되는" 문제 때문에 IK 도달 게이트로 회귀.

### 파츠별 난이도 `requiredWork` (10~100 / 평균 50)
- 옆면(문/바퀴 등): 크게(예 100) — 체류시간 길어 자연히 채워짐
- 앞면(범퍼 등): 작게(예 10) — 선두라 사거리 체류 짧아 빨리 끝나야 함
- 앞쪽 파츠 차체 관통 문제는 **로봇/콜라이더 배치**로 해결 (앞 부품=앞쪽 로봇)

### 체결 값 `currentWork` (방향: 0 → requiredWork, 증가)
- 기존 `assemblyProgress`(1→0) **폐기**.
- `0` = 분리(pile, 숨김), `requiredWork` = 체결완료.
- 위치 보간: **큐빅 베지어** `pile → mid2 → mid → assembled` (`Fill = currentWork/requiredWork`).
  - mid/mid2는 통과점이 아니라 **곡선 제어 핸들**.

## 3. 주요 스크립트 현황

### 체결/파츠
- **AssemblyPart.cs** — `requiredWork`, `currentWork`, `Fill`, `IsAssembled`, `IsDetached`
  - API: `AddWork(amount)`, `SetWork(work)`, `SetAssembled()`, `SetDetached()`, `Reset()`
  - `ApplyLocalPose(fill)` = 큐빅 베지어. `CubicBezier()` 헬퍼.
- **AssemblyPartDataSO.cs** — `PartConfig`에 `requiredWork` 포함.
- **StationController.cs** — 트리거 진입 시 `StartAssembly`, Assembling 중 `AddWork(assembleSpeed * dt)`, `IsAssembled` 시 완료.
  - **`assembleSpeed = 6`** (work/초, 안정적 확정값).
- **AssemblyPartEditor.cs** (Editor) — 씬 핸들로 베지어 제어점 드래그 편집, 회전 핸들 토글, `currentWork` 미리보기 슬라이더, "SO에 저장" 버튼.

### 차량 흐름 / 풀링
- **CarController.cs** — `SetPath(spline, speed)`로 경로 주입(부품 리셋 + 시작점 스냅). `pathProgress` 0→1 이동. 끝 도달 시 출고 판정(`IsNotSuccessParts`) → 성공 시 `EconomyManager.SellCar` (판매가 = sellPrice × `UpgradeManager.carSellPriceMultiplier`) → **CarPool.Return**(파괴 안 함).
- **CarPool.cs** — `Get()`/`Return()` 차량 재사용 풀. `prewarmCount` 미리 생성. **프리팹은 인스펙터 연결 없이 `Resources.Load("Prefabs/CarModel")` 런타임 로드** (경로 필드 `prefabResourcePath`).
- **CarSpawner.cs** — 풀(CarPool)에서 꺼내 스폰. 스플라인은 비워두면 씬에서 **`MainSpline` 이름 오브젝트 자동 탐색**. `UpgradeManager.lineSpeedMultiplier`로 속도·생성주기 연동. **OnDrawGizmos**: 스플라인 시작점에 차량 크기(기본 2×1.5×4.5) 와이어 박스로 생성 위치 표시.

### 매니저 (GameManager 분리 — 2026-06-18)
> Economy/Upgrade/GameFlow = `MonoSingleton<T>` 상속 → `.Instance` 접근 시 오브젝트 자동 생성. 각 `Init()` 보유.
> **생성주기 일원화**: 씬에 **InitManager.cs** 1개만 배치. Awake에서 `EconomyManager.Instance.Init()` / `UpgradeManager...` / `GameFlowManager...` / `MoneyUI.Instance.Init()` 호출로 초기화 순서 통제.
> - CarPool은 `GameObjectSingleton<CarPool>`(GameObjectGroup 자식 배치 필요). InitManager가 `GameObjectGroup`(유저가 씬 생성) 자식으로 생성 → 차량 계층 `GameObjectGroup > CarPool > 차량들`. prefab경로/prewarm은 const.
> - 인스펙터 튜닝값(prewarm/초기자금/배율)은 코드 생성이라 노출 안 됨 → 추후 InitManager 필드/SO로 분리 예정.

### 돈 UI / 연출 (MoneyUI.cs — DOTween)
- `MonoSingleton<MoneyUI>`. `Init()`에서 **Canvas+TMP 텍스트 코드 생성**(씬 배치 불필요), `EconomyManager.OnMoneyChanged(newAmount, delta)` 구독.
- 숫자 **카운트업/다운**(DOVirtual.Float, 주르륵) + 텍스트 펀치 스케일.
- **코인 연출**(임시 기본 이미지=흰 박스+코인색): 증가 시 화면 하단→텍스트로 모임, 감소 시 텍스트→위로 빠져나가며 페이드. 실제 코인 스프라이트 생기면 `Image.sprite`만 교체.
- DOTween은 `Assets/Simple Waypoint System/Extensions/DOTween/`의 DLL 사용(asmdef 없어 전역 사용 가능). 모듈 의존 피하려 `DOTween.To`/`DOVirtual` 위주 사용.
- **EconomyManager.cs** — 자금. `currentMoney`, `SellCar(price)`, `TrySpend(cost)`, `OnMoneyChanged`.
- **UpgradeManager.cs** — 라인/로봇/차량 업그레이드 현재값(배율). `lineSpeedMultiplier`, `robotAssembleMultiplier`, `carSellPriceMultiplier` + `UpgradeXxx(add, cost)`(EconomyManager.TrySpend로 차감).
- **GameFlowManager.cs** — 게임 상태/흐름. `GameState{Ready,Running,Paused}`, `SetState`, `IsRunning`, `OnStateChanged`.
- **StationConfig.cs** — `MonoSingleton<StationConfig>`. 모든 StationController가 공유하는 전역 체결값(`assembleSpeed`, `assembleReachThreshold`). 로봇팔 수십 개여도 한 곳만 바꾸면 일괄 적용. StationController는 Start에서 `config = StationConfig.Instance` 1회 캐싱 후 Update에서 `config.xxx` 참조(getter 호출 회피). 플레이 중 하이러키에서 StationConfig 오브젝트 선택해 값 변경 시 전 스테이션 즉시 반영.
- (폐기) **GameManager.cs** 삭제. 옛 `assemblySuccessRate`(확률 체결)도 제거 — 체결은 작업존 체류시간 기반. `AssemblyStation.cs`(옛 확률 모델)는 로직 비움, StationController로 대체.

### 라인 / 연출
- **CreateSplineMesh.cs** — Unity Splines 로프트 메시 생성기. **그대로 사용 OK**.
- **ConveyorBeltMover.cs** — 머티리얼 텍스처 오프셋 스크롤 = **바닥 이동 연출**.
- **FactoryLineBuilder.cs** (Editor) — `Tools ▸ CarFactory ▸ 라인 생성기`. S자 5라인 스플라인 + 로프트 메시 + 바닥 스크롤 자동 생성.

### 폐기/대체
- **PoolManager.cs** — 깨진 옛 스텁. CarPool로 대체(미사용).
- **FactoryLineBuilder.cs** — 라인 생성툴 삭제됨. S자 라인은 `CreateSplineMesh`로 직접 배치(스플라인 오브젝트 이름 = `MainSpline`).

## 4. 씬 셋업 순서 (Car_Factory 3)

1. `Tools ▸ CarFactory ▸ 라인 생성기`로 S자 라인 생성 (벨트 머티리얼은 세로 흐름 텍스처).
2. `CarPool` 배치 → `carPrefab`에 `CarModel.prefab` 연결. (차량에 **Collider** + CarController 필수)
3. `CarSpawner` 배치 → `mainLineSpline`에 FactoryLine의 SplineContainer 연결.
4. `GameManager` 1개 배치.
5. 라인별 파츠 수만큼 로봇팔(`StationController`+`RoboticArmIK`) 4.5m 간격 배치. 트리거 콜라이더 = 사거리.

## 5. 다음 할 일

- [ ] 씬에서 루프 동작 확인 (스폰→흐름→재사용, 라인속도↑ 시 누락 발생)
- [ ] 행거 이동 (라인 사이 곡선 구간 개별 이송 연출)
- [ ] 판매 / 업그레이드 UI 배선
- [ ] AI NPC (누락 자동 보완), 피버타임, 라인 고장, 보스 차량
