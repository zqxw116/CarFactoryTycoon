# CarFactoryTycoon — 게임 설계 & 작업 진행 노트

> 최종 업데이트: 2026-07-20

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

### 이날 시점의 다음 할 일 (참고용 — 이후 진행상황은 섹션 8 참고)
- [x] 플레이로 풀 루프 검증(스폰→흐름→IK 체결→판매→재사용, 떨림 해소 확인)
- [ ] 떨림 잔존 시: IK 데드존 또는 파츠 회전 Euler→Quaternion Slerp
- [ ] 인스펙터 튜닝값(prewarm/초기자금/배율) SO 또는 InitManager 필드로 분리 검토
- [x] 행거(라인 사이 이송) — WheelStation·WaterTestStation으로 구현 완료
- [ ] 판매/업그레이드 UI 배선, AI NPC, 피버타임

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
- **코인 연출**(임시 기본 이미지=흰 박스+코인색): 증가 시 화면 하단→텍스트로 모임, 감소 시 텍스트→위로 빠져나가며 페이드. 실제 코인 스프라이트 생기면 `Image.sprite`만 교체. (2026-07-14 이후 코인 연출은 섹션 6-3의 CashPopup 방식으로 개편)
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

## 5. 바퀴 리프트 공정 + 라인 정체(트래픽) 설계 (2026-07-12)

> 바퀴 4개는 흐르면서 체결하지 않는다. 전용 리프트 공정에 차량이 **정지** →
> 리프트가 차를 들어올림 → 로봇팔 동시 작업(고정 시간창) → 내려서 재출발.
> 시간창 안에 못 끝낸 바퀴는 **안 붙은 채 출고**(부분 체결 = 부드러운 실패).

### 5-1. 라인 정체 방식 = 어큐뮬레이션 (결정)

현재 CarController는 차간 인지 없이 각자 `pathProgress`를 증가시키는 개별 이동.
정지 차량이 생기면 뒷차 처리가 필요 — 후보 3안 비교:

| 방식 | 동작 | 판정 |
|---|---|---|
| ① 블록 신호식 | 정지구간 점유 시 상류 차량 전부 그 자리 정지 (7m 간격 유지한 채) | ✗ 라인 버퍼 낭비, 띄엄띄엄 멈춰 어색 |
| ② **어큐뮬레이션 (채택)** | 뒷차는 앞차 꽁무니 **minGap까지 접근 후 정지**. 줄서기 발생 | ✓ 실제 공장 컨베이어와 동일, 줄서는 시각 압박 = 업그레이드 동기 |
| ③ 추종 모델(IDM류) | 앞차 속도 매칭 감속 | ✗ 과설계. 1D 단일 라인엔 불필요 |

- **minGap = 5m** (차량 길이 4.5 + 여유 0.5, 호길이 기준). 스폰 피치 7m(속도×스폰주기)는
  흐를 때의 간격일 뿐 불변량 아님 — 정지구간 앞에서 5m로 압축되는 게 의도된 그림.
- **감속 밴드 decelBand = 2m**: gap 7m→5m 구간에서 속도 100%→0 선형 감속
  (`speedFactor = clamp01((gap − minGap) / decelBand)`). 하드 클램프의 급정거 방지.
  체결 소프트 게이트와 동일 패턴이라 튜닝 감각 공유.
- 리프트에서 방출된 뒤 하류 간격은 속도×리프트사이클로 재배열됨 — 자연스러운 현상, 방치.

### 5-2. LineTrafficManager (신규, MonoSingleton)

- **활성 차량 리스트를 pathProgress 내림차순 유지.** 스폰은 항상 라인 시작(꼬리)이고
  추월이 없으므로 정렬은 스폰 시 꼬리 삽입만으로 유지 (재정렬 불필요).
- 등록/해제: `CarController.SetPath`에서 Register, 출고/`CarPool.Return`에서 Unregister.
- **이동 주도권을 매니저로 이관**: 매 프레임 선두→후미 순서로 `car.MoveStep(maxProgress)` 호출.
  `maxProgress = 앞차.pathProgress − minGap/splineLength` (선두는 1f).
  선두부터 갱신하므로 같은 프레임 값 기준 클램프 = 1프레임 지연/스크립트 실행순서 이슈 없음.
- CarController는 **매니저 없으면 기존 자율 이동 폴백** (TestPartsScene 등 기존 씬 무변경).
- **스폰 가드**: `CarSpawner.SpawnCar` 시 시작점 앞 minGap 이내에 차가 있으면 스폰 보류
  (타이머 리셋 안 함 → 자리 나면 즉시 스폰). 큐가 스포너까지 차면 자연 스로틀 = 정체의 최종 비용.

### 5-3. WheelStation (리프트 공정, 신규)

상태기계: `Idle → Entering → Raising → Working → Lowering → Releasing → Idle`

- **위치**: `liftProgress`(스플라인 진행도 0~1)로 정의. Idle일 때 차가 `pathProgress ≥ liftProgress`
  도달하면 캡처: liftProgress로 스냅 + `isMoving = false`. 트래픽 관점에선 그냥 "멈춘 선두"라
  뒷차 줄서기는 5-2가 자동 처리 — 리프트는 뒷차를 전혀 몰라도 됨.
- **승강 연출**: `isMoving=false`면 SnapToProgress가 안 돌아 transform이 자유 →
  리프트가 차량 Y를 직접 DOTween 애니메이션 (기계 메시도 함께 상승). 내릴 때 원위치 복원.
- **로봇팔 4개 직접 오케스트레이션**: 트리거 진입 의존 X. Working 진입 시 WheelStation이
  각 StationController에 체결 시작 명령(동시 시작 연출 보장), **시간창 T 종료 시 미완료
  스테이션 강제 ResetStation**. 차량이 정지 상태라 dist≈0 → 기존 소프트 게이트가 전속 체결
  (게이트/AddWork 코드 재사용, 수정 불필요).
- **부분 체결**: T 안에 끝난 바퀴만 IsAssembled. 미체결 바퀴는 빠진 채 출고 = 시각적 재미 +
  판매가 감액 (현재 all-or-nothing 출고판정 → 부품 수 비례 감액으로 확장 필요).
- **초기 가동 팔 2개(앞바퀴)** → 업그레이드로 4개 해금.

### 5-4. 업그레이드 레버 / 파급효과

- 레버: ①리프트 승강 속도(사이클 오버헤드) ②시간창 T ③동시 가동 팔 수 2→4
  ④assembleSpeed 배율 ⑤(후반) 듀얼 리프트 = 병목 근본 해소 고가 목표.
- 처리량 = min(스폰율, 1/리프트사이클). 라인속도 업그레이드할수록 리프트가 병목으로
  부상 → 병목 옮겨가기 순환 완성.
- ⚠️ **이머전트 효과**: 정체로 멈춘 차가 일반 스테이션 작업존 안에 서 있으면 그 부품은
  전속 체결됨(정지 = dist≈0). "정체 = 처리량↓ 품질↑" 트레이드오프 — 일단 의도된 재미로
  수용, 밸런스 문제 시 재검토.
- 구현 순서 제안: ⑴ LineTrafficManager + CarController.MoveStep 분리 (리프트 없이도
  정체 동작 단독 검증 가능 — 차 하나 isMoving=false로 수동 정지시켜 테스트)
  ⑵ WheelStation 상태기계+승강 ⑶ 부분 체결 출고판정 확장 ⑷ 업그레이드 배선.

### 5-5. 구현 완료 (2026-07-13) — ⑴⑵ 코드 완료, ⑶⑷ 미착수

- **신규 `LineTrafficManager.cs`** (Comm/Manager): 설계 5-2대로. `minGap=5`/`decelBand=2` +
  `minApproachFactor=0.15`(감속 바닥 속도 — 순수 선형 감속은 지수 수렴이라 영원히 도착 안 함,
  바닥 속도 + 클램프 정지로 유한 시간 도킹). 게이트 정지점은 게이트 −1cm(`GateStopOffset`) —
  정확히 게이트 위에 세우면 '통과' 판정돼 캡처 없이 지나가는 경계 버그 방지.
  InitManager가 씬에 없으면 자동 생성(씬에 직접 배치하면 튜닝값 오버라이드 가능).
- **`CarController.cs`**: `MoveAlongSpline` → `MoveStep(speedFactor, maxProgress)`로 일반화
  (자율 이동 = `MoveStep(1,1)` 폴백). `SplineLength`/`IsDriving`/`SetProgress` 공개.
  SetPath에서 트래픽 등록, OnDisable(풀 반환)에서 해제.
- **`CarSpawner.cs`**: `SpawnCar()` bool 반환 — 출발점 앞 minGap 안에 차 있으면 스폰 보류
  (타이머 유지 → 자리 나면 즉시 스폰).
- **신규 `WheelStation.cs`** (Comm/Object): 설계 5-3대로. 게이트 등록, `autoProgressFromPosition`
  (오브젝트 위치→최근접 스플라인 지점, CarSpawner와 동일 패턴), 승강은 수동 SmoothStep 타이머
  (DOTween 불필요), `SetPartTypeAndStart`로 팔 일괄 시작(manualMode라 트리거 무관),
  조기 완료 감지(Assembling 팔 없으면 즉시 하강), 시간창 종료 시 `ResetStation`으로 포기.
  바퀴 다 붙은 차는 리프트 없이 통과. 기즈모: 정지 지점 차량 박스(대기=청록/작업=주황)+승강선.
- **`StationController.ResetStation`** private→public (WheelStation의 포기 처리용).

**씬 셋업 (Car_Factory 3):**
1. 빈 오브젝트에 `WheelStation` 추가, 라인 3 옆 원하는 정지 지점에 배치 (autoProgress가 최근접
   스플라인 지점을 잡음 — 기즈모 청록 차량 박스로 확인).
2. 바퀴 4개 `StationController`를 `wheelStations` 배열에 연결 — **타입 지정/배치는 5-6의
   자동 배치가 처리** (배열 순서 = 앞우/뒤우/앞좌/뒤좌).
3. ⚠️ **바퀴 스테이션에는 작업존 BoxCollider(트리거)를 두지 말 것** — 시작 직후엔 manualMode가
   아니라서 지나가는/줄 선 차량의 트리거 진입이 조기 체결을 시작시킨다. 리프트가 직접
   오케스트레이션하므로 콜라이더 자체가 불필요. (5-6 자동 배치가 콜라이더를 자동으로 끔)
4. `workWindow`(기본 6초) vs 바퀴 requiredWork/assembleSpeed로 성공/누락 밸런스 조정.
   `activeArmCount`=2로 낮추면 뒷바퀴 누락 상태를 바로 재현 가능.

**미착수(당시):** ⑶ 부분 체결 출고판정 — 현재 IsNotSuccessParts는 all-or-nothing(불량품이면
판매금 0). 미체결 부품 수 비례 감액으로 확장 필요. ⑷ 업그레이드 배선(팔 수/승강속도/시간창).
(→ 2026-07-20 기준 아직 미착수, 섹션 8 참고)

### 5-6. 테스트 편의 보강 + 바퀴 스테이션 자동 배치 (2026-07-13 ~ 07-14)

**7/13 — WheelStation 테스트 지원:**
- **캡처존 기즈모**: 게이트 직전 `CaptureEpsilon`(0.05→0.1로 상향) 구간을 마젠타 와이어박스로
  표시 — 트래픽 정지점(게이트 −1cm)이 이 안에 들어와야 캡처된다.
- **플레이 중 게이트 이동**: Idle 상태에서 transform 이동 감지 시 liftProgress 재계산
  (사이클 중엔 고정) — 정지 지점을 플레이 중에 옮겨가며 테스트 가능.
- **통과 사유 경고로그**: "작업할 바퀴 없음 → 통과"에 wheelStations 개수 포함 — 배열 미연결 /
  트리거 조기 체결 / 캡처 실패를 콘솔에서 구분.
- **차량 프리팹 교체**: CarModel_Origin.prefab을 Resources/Prefabs/로 이동,
  CarPool이 이것을 로드하도록 교체(구 CarModel 대체).

**7/14 — 바퀴 스테이션 4개 자동 배치 (`WheelStation.PlaceWheelStations`):**
- `wheelStations` 인스펙터 바인딩(또는 `stationAxialOffset` 변경) 시 OnValidate가 자동 실행.
  컨텍스트 메뉴 "바퀴 스테이션 자동 배치"로 수동 실행도 가능. (Undo 미지원 — 재배치로 복구)
- 배열 순서대로 `targetPartType` = 앞우41/뒤우42/앞좌43/뒤좌44 자동 지정 (robotArm 포함).
- **좌/우(lane)**: 배치 SO(placementData)의 robotLineSide 우선(ApplyPlacement 전체 적용),
  SO에 없으면 바퀴 이름의 좌/우 폴백. ⚠️ SO의 42=Left/43=Right 어긋남은 교정(재저장) 필요.
- **X(라인 방향)**: 리프트 중심 기준 앞바퀴 +`stationAxialOffset`(기본 1.5m)/뒷바퀴 − 대칭.
  z·회전·작업존 center는 ApplyLineSide가 현재 LINE 기준으로 계산.
- **계층**: WheelStation이 LINE 자식이면 스테이션들을 WheelStation 자식으로 편입 —
  SetActive 토글에 팔 4개가 함께 켜지고 꺼진다. 작업존 BoxCollider는 자동 비활성화.

**7/14 — TestPartsScene 바퀴 리프트 테스트 (드롭다운 전환):**
- `AssemblyTestManager.wheelStation` 필드 추가: 바퀴 4종 선택 시 일반 station 비활성화 +
  WheelStation 활성화(그 외 부품은 반대). 전환 전 `CancelCycle()`로 잡고 있던 차량 정리
  (차량 리셋 후 정리하면 캡처 시점 위치로 역텔레포트되는 버그 방지).
- `TestCarLooper.useManagedDrive`: 바퀴 테스트 시 차량 이동을 SetPath→LineTrafficManager에
  이관 — 게이트 감속 정지/캡처가 실제 라인과 동일하게 동작. 진행도 0.97에서 루프 재시작
  (1.0의 출고/풀 반환 로직 진입 전 가로챔). 일반 부품은 기존 직접 구동 유지.
- `WheelStation` Start→OnEnable 재구성: SetActive 재활성화 시 게이트 재등록
  (기존엔 Start 1회 등록이라 껐다 켜면 차량이 그냥 통과). OnDisable = CancelCycle + 등록 해제.
- 파츠 상태는 기존 시맨틱 유지: **41(앞우) 선택 = 바퀴 4개 전부 테스트**,
  44(뒤좌) 선택 = 그 바퀴 하나만 (이전 바퀴는 체결 완료 상태).

## 6. 도색 공정(PaintBooth) + 재화 연출 + 출고 주행(DepartureStation) (2026-07-14 ~ 07-15)

> 차체(Body)는 로봇팔 체결에서 분리해 전용 도색 공정으로 전환. 동시에 재화 획득 연출을
> 발생 위치(로봇팔·도색·출고)에서 재화 UI로 날아가는 방식으로 통일.

### 6-1. PaintBooth — 차체의 '체결' 공정 (신규)

- 라인 위에 세우는 트리거 박스 = "네모난 거울". 차량은 검정 언더코트 상태로 스폰되고
  (`CarPaintController`), 부스를 통과하는 동안 **지나간 부위만 픽셀 단위로 원본색이 드러난다**
  (월드 평면 컷오프, `CarPaintScan.shader`).
- `OnTriggerEnter` → 부스 중심을 지나는 스캔 평면 활성(법선 = 차량 진행 방향의 반대,
  아직 안 지난 뒤쪽이 검정으로 남도록). `OnTriggerExit` → 전체 원본색 확정 + 최초 1회만
  `StationConfig.partReward` 지급(재통과/중복 지급 방지 = `CarPaintController.Revealed` 플래그).
- 물리 레이어 실수 방지: Awake에서 Car 레이어와 충돌 안 하는 레이어에 배치돼 있으면
  Station 레이어로 자동 보정 + 경고 로그.
- `CarPaintController`: 대상 파츠(`PartGroup.Body`) 렌더러의 머티리얼을 차량별 인스턴스로
  `CarPaintScan` 셰이더로 교체(`_BaseMap`/`_BaseColor` 등 URP Lit과 동일 프로퍼티라 텍스처 승계됨).
  `_PaintMode`: 2=전체 언더코트 / 1=스캔 중(경계 글로우) / 0=원본색 확정. 풀 재사용 시
  OnEnable에서 언더코트로 리셋.

### 6-2. DepartureStation — 출고 주행 연출 (신규)

- 라인을 완주한 차량을 즉시 소멸시키는 대신: **부릉부릉(제자리 레브 진동, 사인파 피치/롤)
  → 가속 주행(도착 지점으로 회전+가속) → 도착 반경 진입 시 CarPool 반환**.
- `TryDepart(car)`: 씬에 이 공정이 없으면 false → 호출자(CarController)가 기존대로 즉시 반환.
  넘겨받는 즉시 `LeaveLine()`으로 트래픽 해제 — 출고 중인 차가 라인 끝(progress 1.0)에
  걸려 뒷차를 막는 것 방지. 판매가/불량 판정은 CarController가 라인 끝에서 이미 처리 완료,
  DepartureStation은 이동 연출만 담당.

### 6-3. 재화 연출 통일 — CashPopup (신규) + 코인 풀링

- `CashPopup` (MonoSingleton): "+$금액" 3D TextMeshPro 플로팅 텍스트. 펑 커지며 등장 →
  위로 떠오름(DOTween) → 후반 45% 구간 페이드아웃. 텍스트 오브젝트 풀링으로 GC 없음.
  `scale` 인자로 임팩트 차등(차량 출고 등 큰 이벤트=크게).
- 체결 보상(로봇팔)·도색 완료(PaintBooth)·출고(DepartureStation) 등 재화 발생 지점 전부
  `EconomyManager.Earn()` + `CashPopup.Show()` 페어로 통일. `MoneyUI` 쪽 코인 연출도
  발생 위치 → 재화 UI로 날아가는 방식으로 개편(코인 오브젝트 풀링).
- Car_Factory 3 씬에 DepartureStation 배치 + 배치 SO/차량 프리팹(CarModel_Origin) 튜닝,
  FactoryObject 프리팹 추가.

## 7. 방수 테스트 공정(WaterTestStation) (2026-07-20)

> 5라인 첫 공정. 집게형 행거로 차량을 들어올려 수영장에 침수시킨 뒤 체결 누락(방수 실패) 여부를
> 판정한다. WheelStation과 마찬가지로 `ILineGate` 구현 + `LineTrafficManager` 게이트 등록.

- **상태 머신 12단계**: `Idle → HangerDescending → Grabbing → Lifting → TravelToPool →
  Submerging → Testing → Emerging → TravelToLine → Lowering → Releasing → Cooldown`.
  행거 이동은 전 구간 `MoveHangerGrabTo()`(hangerRoot를 MoveTowards, grabPoint가 목표
  0.05m 이내 도달 시 완료) 하나로 통일.
- **판정**: `Testing` 진입 시 `CountUnassembledParts()`로 미체결 수 확인 →
  0=Pass / `majorDefectThreshold` 미만=MinorDefect(반액 보너스) / 이상=MajorDefect(보너스 없음).
- **결과 분기**:
  - Pass → `releasePoint`(게이트 이후 라인 위)에 내려놓고 `SetProgress(releaseProgress)` +
    `isMoving=true`로 라인 정상 복귀. `releaseProgress`는 `autoProgressFromPosition` 켜져
    있으면 releasePoint 위치에서 자동 계산.
  - Defect → `rejectPoint`(폐기 구멍)로 이송 후 `CarPool.Return()`으로 소멸. `TravelToLine`
    시작 시 즉시 `LeaveLine()` 호출해 뒷차가 게이트 클램프에서 풀리도록 하고, 동시에
    `rejectDoor`(슬라이딩 문)를 `TravelToLine`~`Lowering` 구간에서 미리 열어 도착 전에
    완전히 열리도록 함. `Cooldown`에서 문 원위치 복귀.
- **이펙트**: 입수/출수 스플래시, 검사 중 일반 기포(+불량 시 defect 기포 추가), 불량 감지
  스파크(failVFX, 물속에서), 통과 시 passVFX. 인스펙터 우클릭 "임시 VFX 생성" 컨텍스트
  메뉴로 파티클 5종 + URP 재질(`Assets/@Resource/Materials/VFX/VFX_*.mat`) 자동 생성
  (알파블렌드=물/기포, 가산합성=Pass/Fail).
- **씬 셋업**: `HangerRoot > HangerGrabPoint`, `PoolEntryPoint`, `PoolBottomPoint`,
  `ReleasePoint`(게이트 이후 스플라인 위), `RejectZone > RejectPoint + RejectDoor`.

**미착수(다음):** 압력/고속 테스트(5라인 나머지 공정), 방수 테스트 이펙트/사운드 다듬기,
저장/로드.

## 8. 전체 다음 할 일 (2026-07-20 기준)

- [x] ~~씬에서 루프 동작 확인~~ / ~~행거 이동(라인 사이 이송)~~ — WheelStation·WaterTestStation으로 구현 완료
- [ ] 5라인 나머지 공정: 압력 테스트, 고속주행 테스트, 판매 대기
- [ ] 부분 체결 출고판정 확장 (현재 all-or-nothing → 미체결 부품 수 비례 감액, 5-5부터 이월된 항목)
- [ ] 업그레이드 UI 배선 (팔 수/승강속도/시간창/라인속도 등 — UpgradeManager 로직은 있음, UI 미연결)
- [ ] AI NPC(누락 자동 보완), 피버타임, 라인 고장, 보스 차량
- [ ] 사운드, 저장/로드

### ★ 기획 방향 전환 (2026-07-21, 미착수 — 설계 결정만 확정)

**결정**: 기존 기획(전 라인 로봇팔 체결, 1~2장 참고)에서 방향 전환.
초기 공정은 **사람 작업자**가 체결하고, 로봇팔은 사장(유저)이 인력 관리 번거로움을
회피하기 위해 도입하는 **자동화 업그레이드**로 재배치한다. 최종적으로 전 라인이
로봇팔로 전환되면 게임의 의도된 종착점(엔딩)이다.

**왜 (판단 배경)**:
- 로봇팔 단독 체결은 결정론적이라 단조로움 → 유저 이탈(스팀 2시간 환불 기준) 우려.
- 타이쿤 장르의 재미 축은 "변수 있는 인력 관리"에 있음 (Game Dev Tycoon, Two Point
  Hospital류 참고). 재미 축을 "체결 애니메이션"에서 "관리 의사결정"으로 이동.
- 사용자 실무(현대/기아 협력사, 공장 디지털트윈)가 실제로는 로봇팔보다 사람 중심
  체결+노무관리 → 이 구조가 이색 포트폴리오 논리를 강화함 (로봇팔 애니메이션은
  누구나 만들 수 있지만, 노무관리+자동화 전환 도메인 지식은 실무 기반 이색 포인트).
- 기술 리스크 낮음: 기존 `ILineGate` 인터페이스(`WheelStation`, `WaterTestStation`이
  구현)에 사람 작업자용 `WorkerStation` 구현체를 추가하는 방식으로 확장 가능 —
  새 시스템이 아니라 기존 구조에 구현체 하나 추가하는 수준. 벨지어 곡선 경로 지정도
  로봇팔에서 이미 검증됐으므로 사람 캐릭터 이동 경로 지정에 재사용 가능.

**핵심 메커니즘 설계 방향**:
- **사람 작업자**: 초기 공정 담당. 휴식시간에는 체결 진행 안 됨 → 차량이 앞으로
  이동 못함. 작업 재개 시 체결 진행.
- **작업자 불량/변수**: 사람 특성상 확률적 불량 발생 가능 (로봇팔과 대비되는 지점).
- **노조 파업 이벤트**: 전체 이벤트로 발생, 현재 수익의 일정 %를 지불해야 파업 해지.
  단발성 버튼이 아니라 지속시간에 따라 %가 늘어나거나 방치 시 라인 전체 정지 등
  긴장감을 주는 방식으로 밸런싱 필요 (아직 미확정, 구현 시 설계 필요).
- **자동화 전환**: 사장(유저)이 인력 관리 번거로움 때문에 로봇팔 업그레이드를
  선택하는 방향으로 유도.

**리스크 / 주의할 점**:
1. **전환 페이싱이 핵심** — 사람→로봇 전환이 너무 빠르면 사람 파트가 튜토리얼처럼
   스쳐가고 결국 로봇팔 단조로움이 후반부에 재현됨. 너무 느리면 관리 번거로움이
   짜증으로만 남음.
2. **"완전자동화 = 게임 끝" 서사의 이중적 의미** — 포트폴리오/2시간 환불 관점에선
   타당하지만, 상업적 장기 출시까지 염두에 둔다면 완전자동화 이후 후속 확장 여지
   (멀티 팩토리, 수출 계약 등)를 구조적으로 열어둘 필요.
3. **사람 캐릭터 애니메이션 비용** — 걷기→작업 전환→작업 자세 블렌딩은 로봇팔보다
   리깅/애니메이션 비용이 더 큼. 풀 휴머노이드보다 단순화된 표현(예: 상반신 위주
   애니메이션, 로우폴리 스타일) 고려 권장.
4. **파업 이벤트 밸런싱** — 단발성이면 긴장감 없이 세금 알림처럼 소비될 위험.

**아키텍처 메모**: 기존 `ILineGate` 인터페이스(`WheelStation`, `WaterTestStation`이
구현)에 `WorkerStation` 구현체를 추가하는 방식. 사람 작업자 스테이션 작업 시작 전
1~7장의 최신 구현 상태를 코드에서 다시 확인할 것 (구현 현황이 로봇팔 기준으로
쓰여 있어 사람 작업자 반영 전임).

구현 착수 시 이 섹션을 9번으로 승격해 상세 기록할 것.

---

## 9. 인력·물류 관리 시스템 설계 (2026-08-18, 미착수 — 설계 논의 확정분)

> 8장 방향 전환(사람 작업자 → 로봇팔 자동화)의 구체화. 재미의 축을 "체결 애니메이션 관찰"에서
> **"인력·물류 관리 의사결정"** 으로 이동시킨다. 자동차 공장처럼 보이지만 실제로는 사람 관리 게임.

### 9-0. 선행 결정 (8장 방향 전환의 수정·보강)

- **작업자는 공정에 고정 배치한다.** 초기 구상이던 "작업자 1명이 차량을 따라 1공정→2공정→…→완성"은
  **기각**. 이유: ①컨베이어 라인·트래픽·게이트 공정이 전부 무의미해짐(셀 생산이 됨) ②작업자 추가가
  단순 병렬 증가라 선형 성장 = 의사결정 부재 → "로봇팔 배치일 뿐"이라던 기존 문제가 축만 바꿔 재현됨.
  고정 배치하면 공정별 `requiredWork` 차이에서 **병목**이 발생하고, "어느 공정에 인력을 더 붙일지 /
  라인 속도를 낮출지 / 그 공정만 먼저 로봇팔로 바꿀지"가 핵심 의사결정이 된다. (= 1장 원래 기획 유지)
- **"사람이 움직이는 볼거리"는 물류로 확보한다** — 아래 9-1. 작업자가 부품 파일↔차량을 왕복하고,
  보급 작업자가 창고↔공정을 왕복한다. 베지어 경로는 로봇팔에서 검증됐으므로 재사용.
- **체결 여부와 품질을 분리한다.** 현재 `currentWork ≥ requiredWork`(all-or-nothing)에 **품질값**을 추가.
  사람이 서둘러 붙이면 체결 100%지만 품질 60% → 방수/압력 테스트에서 확률적 실패. 로봇팔은 품질 고정 100%.
  → "사람 시대엔 검사 공정이 안전망으로 필수, 자동화할수록 통과율이 올라 검사를 줄일 수 있음"이라는
  자동화 보상 체감이 성립. 기존 우려("100% 체결되면 테스트 공정이 무의미해지나?")는 이 분리로 해소되며,
  이월 TODO였던 **부분 체결 감액 출고판정**도 같은 품질 축에 얹힌다.
- **성장 축은 3개 이상으로 분리** — 처리량(라인속도/작업자수/배치) · 품질(교육/로봇전환/검사) ·
  제품(차종 추가: 경차→세단→SUV→스포츠카, 단가·부품수·난이도↑). 여기에 9장의 **인력·물류**가 4번째 축.
  차종 추가는 파츠 조합·requiredWork만 바꾸면 되므로 리텐션 대비 비용이 가장 싸다.

### 9-1. 공정 부품 재고 + 물류 (★ 핵심 신규 재미 축)

- **각 공정에 부품이 실제로 쌓여 있다.** 파일(pile)에 물리적으로 적재된 부품이 체결마다 **눈에 보이게 줄어든다.**
  (기존 `stationPilePos` + `stationPileMesh`를 스택 표현으로 확장)
- **재고 0 = 해당 공정 체결 불가 → 라인 정지.** 라인 정지가 곧 손실이므로 유저에게 즉각적 압박이 된다.
- **보충 수단 3단계 (자동화 테마의 축소판):**
  1. **유저 직접 클릭** — 응급조치. 반드시 **비효율적이어야 한다**(1회 소량 + 쿨타임, 또는 창고→공정
     왕복 필요). 클릭이 너무 효율적이면 보급 작업자를 아무도 고용하지 않고, 너무 무력하면 짜증만 남는다.
  2. **공정 작업자가 직접 창고에 다녀온다** — 그 동안 해당 공정 체결 중단(기회비용). 초반 기본 동작.
  3. **전담 보급 작업자 고용** — 창고↔공정 순회 보충. 공정 작업자는 체결에만 집중.
- **보급 작업자 수는 상한이 있다**(무한 증원 불가) → 인원을 늘리는 게 아니라 **업그레이드**로 해결하게 유도.
  - 업그레이드 경로: 맨손 운반 → 손수레 → **지게차**(더 빠르고 1회 적재량↑).
  - ⚠️ 지게차는 통로/경로 개념이 필요해짐(NavMesh 또는 통로 웨이포인트) → **라인 레이아웃 설계가
    또 하나의 의사결정 레이어**가 되는 이득이 있으나 구현 비용도 커짐. 후순위 단계로 배치.
- 설계 효과: 물류는 체결 속도와 **직교하는 두 번째 병목**이다. 라인 속도를 올리면 체결 누락뿐 아니라
  부품 소모 속도도 올라가 물류가 먼저 터진다 → 단일 축 성장의 답답함이 구조적으로 해소된다.

### 9-2. 작업자 상태 세분화 + 머리 위 상태 표시

- 상태를 세분화하고 작업자 옆/위에 아이콘으로 표시한다. 방치형·관리형에서 **"한눈에 병목 파악"** 이 핵심 UX.
- 상태 예: `출근중 / 이동중 / 작업중 / 부품가져오는중 / 부품없음(대기) / 휴식중 / 피로 / 불량발생`.
- **표시 원칙: 정상 상태는 아이콘을 띄우지 않는다.** 문제 상황(부품없음·휴식·피로·불량)만 뜨게 해서
  화면 노이즈를 줄이고, 아이콘이 보이는 곳 = 유저가 개입할 곳이 되도록. 종류는 5~7개로 제한.

### 9-3. 경제: 고정비(급여) 도입

- **돈이 벌리기만 하지 않고 빠져나간다** — 작업자 급여 = 고정비. 순증 경제는 긴장이 없다.
  고정비가 있으면 **"고용 = 리스크"** 가 되고 손익분기 판단이 생겨 비로소 타이쿤이 된다.
- **급여일에 한 번에 빠지게 한다**(주급 등) → 급여일 전 처리량을 밀어붙이는 **긴장의 리듬**이 생긴다.
  매 프레임 조금씩 빠지면 체감이 없다.
- **★ 이 축이 로봇팔의 존재 이유를 완성한다**: 로봇팔 = 초기 비용 크지만 **급여 0**(소액 정비비만).
  그동안 애매했던 "로봇팔이 왜 좋은가"에 경제적 답이 생기고, 엔딩(전 라인 로봇팔화)이 곧 **"인건비 0"**
  이라는 서사적 의미까지 갖는다.
- **파산은 게임오버로 만들지 않는다** — 방치형에서 하드 실패는 짜증이 크다. 대출/평판 하락/납기 지연
  같은 소프트 페널티로 처리.

### 9-4. 시간 흐름 + 근무·피로·휴식

- **하루 시간 표시 + 출근 연출**: 07:00 작업 시작이면 06:30경 작업자들이 입구에서 각자 자리로 이동.
  저비용 고효과. 하루의 시작 신호가 되어 게임에 리듬을 만든다.
- 실시간이면 지루하므로 **시간 압축 배율** 필요(예: 1일 = 실시간 10~20분). 배속/스킵은 무료 기본 기능.
- **8시간 근무 중 1시간 휴식(최소 10분 ~ 최대 1시간)** — 휴식 중엔 해당 공정 체결 중단 → 라인 압박.
  - ⚠️ **완전 랜덤은 지양.** 대응 수단이 없는 랜덤은 불공평·짜증으로 읽힌다. **피로도가 임계치에 도달하면
    휴식**(예측 가능한 인과)으로 두고 랜덤 폭은 작게. 그러면 유저가 휴게실 업그레이드·교대 배치·예비
    인력으로 **대응할 수 있게** 되어 의사결정이 된다.
  - 휴식으로 멈춘 공정은 유저 직접 개입(위기 개입) 또는 유휴 작업자 재배치로 메꿀 수 있게 한다.
  - 공정 1개 정지가 전 라인 정지로 이어지면 페널티가 과도하므로, 공정 간 **버퍼 재고**로 완화 설계 필요.

### 9-5. 야간 정비 시간 (프레임 수정: 처벌 → 보상)

- **원안**: 07:00~24:00 2교대, 24:00~07:00은 유저가 부품 보충·더러운 작업대 청소. 청소 안 하면 작업효율
  −20% + 피로 20% 가속 디버프. 앱 결제로 시간 점프 및 자동 청소.
- **⚠️ 이 부분만 방향을 뒤집을 것을 권고 (미확정, 재논의 대상):**
  1. **"안 하면 디버프"는 처벌 기반 반복 노동(chore)** 이다. 매일 반복되는 청소는 게임이 아니라 숙제가 되고,
     그걸 결제로 지우는 구조는 **pay-to-remove-annoyance** — 리뷰 폭격의 대표 원인.
  2. **플랫폼 불일치**: 스팀 유저는 소액결제로 불편을 제거하는 설계에 매우 적대적이다(모바일 F2P 문법).
     스팀 목표라면 buy-to-play / DLC가 맞다. 포트폴리오 관점에서도 IAP 구현은 채용 가점이 거의 없고
     오히려 게임 이해도를 의심받을 수 있다.
- **수정안**: 야간 정비 시간이라는 아이디어 자체는 살리고 프레임만 뒤집는다.
  - 청소 **안 함 = 기본 상태(디버프 없음)** / 청소 **함 = 버프**(작업효율 +10%, 다음날 피로 축적 감소).
    같은 인센티브가 처벌이 아니라 보상으로 작동한다.
  - 자동화는 **결제가 아니라 게임 내 성장으로** 풀게 한다: **사장이 직접 청소 → 청소 작업자 고용 →
    자동 청소 로봇 구매.** 이 3단계는 게임 전체 테마(사람→자동화)의 축소판이라 주제와 완벽히 일치한다.
  - 시간 점프/배속은 무료 기본 기능으로 제공.
- **청소 작업자**는 이 설계에서 정식 직군으로 편입(공정 작업자 / 보급 작업자 / 청소 작업자).

### 9-6. 구현 우선순위 (스코프 관리)

9장 전체를 한 번에 넣으면 스코프가 터진다. 아래 순서로 단계별 검증 권장.

1. **핵심 루프** — `ILineGate` 구현체 `WorkerStation`(공정 고정 배치) + 공정 부품 재고/파일 감소 +
   재고 0 시 라인 정지 + 유저 클릭 응급 보충 + 상태 표시 아이콘(최소 `부품없음`). **품질 필드만 미리 심어둠.**
2. **물류 1단계** — 공정 작업자의 창고 왕복 → 전담 보급 작업자 고용 + 인원 상한.
3. **경제** — 급여 고정비 + 급여일 리듬 + 손익분기 UI.
4. **인적 변수** — 피로도/휴식(임계치 기반) + 상태 세분화 완성 + 확률적 불량 → 품질 축 활성화(검사 공정 연동).
5. **시간** — 하루 시간 흐름 + 출근 연출 + 시간 압축 배율.
6. **야간 정비** — 보상형 청소 + 청소 작업자 → 자동 청소 로봇.
7. **자동화 전환 경제** — 로봇팔 = 고초기비용/무급여로 재배치, 공정별 전환, 전 라인 전환 = 엔딩.
8. **물류 2단계(후순위)** — 손수레 → 지게차 + 통로/경로탐색 + 라인 레이아웃 의사결정.
9. **파업 이벤트**(8장 항목) — 급여·피로·청소 상태와 연동시켜 인력 관리의 최종 리스크로 배치.

> **작업 우선순위 메모**: 사운드/이펙트 등 폴리시는 유저 판단으로 **후순위**. 시스템 구축이 우선.
> (단 8장 진단에서 지적한 "피드백 빈약" 문제는 시스템 완성 후 반드시 회수할 것 — 체감 개선폭이 크다.)

### 9-7. 세부 확정 (2026-08-18 후속 논의)

**⑴ 유저 클릭 보충 (확정)**
- 클릭 1회 = 재고 **3개** 적립. 이후 **쿨타임** 동안 다른 공정도 추가 보충 불가(전역 쿨타임).
- **쿨타임 즉시 초기화 수단**: 카메라를 **창고 위치로 이동**해서 창고를 클릭. → 시간이 아니라
  **주의력·화면 기회비용**을 대가로 지불하는 구조(창고를 보는 동안 라인을 못 본다). 단순 쿨타임보다 좋음.
- **보급(재고관리) 작업자는 기본 1명 지급**으로 시작(당초 "0명 시작" 수정). 유저 클릭은 처음부터
  **응급 수단**으로 자리 잡고, 성장은 "보급 인원 상한 내 증원 → 손수레 → 지게차" 업그레이드로 진행.

**⑵ 로봇팔의 우위 = 고정비 0 + 가동률 100% (확정)**
- 급여 0(정비비만)이라는 **비용 우위**에 더해, **휴식이 없어 가동률이 100%** 라는 **처리량 우위**가 함께 있음.
  사람은 컨디션 소모로 필연적 휴식 → 로봇팔 전환의 동기가 비용·처리량 두 축에서 성립.

**⑶ 자금 마이너스 페널티 (⚠️ 중첩 주의 — 확정 필요)**
- 후보: ①작업자 이동속도 반감 ②판매액 20% 감소 ③업그레이드 등 재화 사용 잠금 + 벌리는 재화로 점차 상환.
- **권고: ③을 기본으로 하고 ①②는 넣지 않거나 매우 약하게.** 이유 = ①②는 **처리량 자체를 깎아서
  상환을 불가능하게 만드는 죽음의 소용돌이(death spiral)** 를 유발함. 마이너스에 빠진 유저가 더 못 벌게
  되면 회복 경로가 사라져 방치형에서 가장 나쁜 체감이 된다.
- ③만으로도 **성장 정지**라는 충분히 아픈 페널티이며, 처리량은 유지되므로 **회복 가능**하다.
  긴장을 더 주고 싶으면 ①②를 넣되 **마이너스 지속 일수에 비례해 단계적으로**(1일차 없음 → 3일차 10% →
  5일차 20%) 적용해 즉사 방지.

**⑷ 작업자 상태 표시 (확정·간소화)**
- 상태 = **부품없음 / 휴식중 / 작업중** + **컨디션 게이지**(아래 ⑸). **불량 아이콘은 제거**.
  `피로` 아이콘도 컨디션 게이지와 중복이므로 **게이지 색 변화로 대체**.
- **`작업중` 상태에서 유저가 클릭하면 체결량(work)을 추가 적립** — 상시 참여형 개입.
  - 밸런싱: 클릭당 고정량으로 두면, 공정 수가 늘어날수록 **물리적으로 다 못 누르게 되어 자연스러운 상한**이
    생긴다(별도 제한 장치 불필요). 규모가 커질수록 클릭의 상대적 영향력이 줄어 **자동화 유도**로 이어짐.
- **품질 축은 내부 값으로 유지**(아이콘만 제거): 컨디션이 낮을 때 체결하면 품질 하락 → 검사 공정 연동.

**⑸ 컨디션 게이지 (확정 — 랜덤 휴식 대체)**
- 작업자마다 **컨디션 100% 시작**. **차량 1대 담당 완료마다 −10%** → 게이지 + 색 변화로 표시.
  0% 도달 시 **강제 휴식**. → 9-4의 "8시간 중 랜덤 1시간 휴식"은 **폐기**, 완전 예측 가능한 인과로 대체.
- **선제 관리**: 체결을 마치고 **다음 차량을 기다리는 유휴 시간**에 유저가 클릭하면 미리 휴식 → 라인 정지 예방.
- **핵심 밸런싱 레버 = 선제 휴식과 강제 휴식의 비대칭**: 선제 휴식은 짧게/회복 효율 높게, 강제 휴식은
  길게 두어야 "미리 관리하는 플레이"에 보상이 생긴다. (동일하면 관리할 이유가 없어짐)
- 유저는 "10대 = 1사이클"로 카운트 가능 → 리듬이 읽히고 계획이 가능해짐.

**⑹ 야간 정비 (확정 — 스코프 축소)**
- **페널티 없음**(9-5 권고 수용). **작업대 더러워짐/청소 메커니즘은 이번 범위에서 구현하지 않음**
  (시간 대비 중요도 낮음). 청소 작업자·자동 청소 로봇도 함께 보류.
- **구현 범위는 "다음날로 스킵" 버튼만.** 스킵 시 **다음날 06:30**(출근 시각)으로 이동.
- 청소/버프 재도입 여부는 시스템이 자리 잡은 뒤 재논의.

### 9-8. 최종 확정 (2026-08-18) — 9-7 ⑶⑸ 결론

**⑶ 자금 마이너스 페널티 = 업그레이드 잠금만 (확정)**
- 마이너스 상태에서는 **재화 사용(업그레이드/고용/구매) 전면 잠금** + 벌리는 재화로 **점차 자동 상환**.
- **이동속도 반감·판매액 감소는 채택하지 않음** — 처리량을 깎으면 상환 능력까지 깎여 죽음의 소용돌이가 됨.
  성장 정지만으로 충분히 아프고, 처리량이 유지되므로 회복 경로가 남는다.

**⑸ 휴식 비대칭 (확정)**
- **선제 휴식 3초 / 강제 휴식 5초.** 회복량은 **양쪽 모두 컨디션 100% 전량 회복**(차이는 시간·타이밍만).
- **선제 휴식 조건**: 컨디션이 100% 미만이면 **유휴 상태에서 언제든** 클릭 가능(90%든 20%든).
  유저가 안전 마진을 스스로 정하는 것이 관리 재미의 핵심이므로 하한 제한을 두지 않는다.
- **비대칭의 실질은 길이가 아니라 타이밍**: 선제는 유휴 시간(다음 차량 대기)에 소화 → **기회비용 0**,
  강제는 작업 중 발동 → **라인 정지**. 2초 차이 자체가 아니라 "멈추는가"가 인센티브다.
- **파생 긴장(의도된 것)**: 라인 속도를 올리면 유휴 시간이 사라져 선제 휴식 창이 소멸 → 강제 휴식 리스크 상승.
  작업자가 늘면 클릭 대상이 늘어 주의력이 부족해짐 → 자동화 압박으로 연결.
- **⚠️ 튜닝 메모**: 차량 10대 작업이 ~60초라면 강제 5초는 다운타임 8%로 체감이 미미할 수 있음.
  긴장이 부족하면 **강제 쪽을 늘릴 것**(선제 3초 : 강제 10~15초). 절대값보다 **비율**이 레버.
- 구현 기준: 휴식 시간은 게임 시간(`Time.timeScale` 영향) 기준으로 두어 배속·스킵과 일관되게 처리.

### 9-9. 1단계 착수 전 선행 작업 (2026-08-18 시점 다음 할 일)

**① 현재 코드 실측 확인 (선행 필수)** — 기획서 3·5장과 메모리가 모두 **로봇팔 기준**이라 사람 작업자를
얹기 전 실제 인터페이스 확인 필요:
`ILineGate` 실제 계약(7/20 신설) / `StationController` 재사용 범위와 사람 공정 분리 지점 /
`LineTrafficManager` 게이트 등록·클램프 방식 / `AssemblyPart` 체결 모델(품질 필드 자리) /
`stationPilePos`·`stationPileMesh`의 재고 스택 시각화 확장 가능성.

**② 1단계 상세 설계 — 결정 사항 5개** (A가 나머지를 좌우함)
- **A. 사람 공정은 "차량 정지형(게이트)"인가 "흐름형"인가** ← 최우선. 로봇팔은 흐르는 차량에 체결하지만,
  **재고 0 → 라인 정지**를 구현하려면 게이트가 자연스럽다. 이 결정이 트래픽·컨디션·클릭 전부를 좌우.
- B. 재고 데이터 구조 + 스택 시각화(부품이 눈에 보이게 감소)
- C. 작업자 상태머신(출근/이동/작업/보충/휴식 전이)
- D. 클릭 입력 경로(카메라·레이캐스트·⚠️ 레이어 규칙 Car6/Station7/Robot8 — 트리거 감지는 7 또는 8만 가능)
- E. 품질 필드 심는 위치(추후 검사 공정 연동을 공짜로 만들기)

**③ 구현 → TestPartsScene 검증**
