# 차량 외피·집기 슬롯·공정 숫자 수정 계획

- 사용자 요청: 차량 범퍼 경사를 오를 수 있으면서 엔진룸 끼임 방지, 작업자/로봇의 고정 집기 슬롯 및 손 이탈0.5m/최대1초 재충전, 공정 숫자 깊이 검사. 끝나면 기록·커밋·푸시·Notion 기록 승인.
- 사용자 확정: **플레이어도 슬롯에서 집기**. 작업자·로봇·플레이어 모두 같은 고정 슬롯을 사용한다.
- 차량: 원본 Body/Origin에 열린 경계·비다양체가 있어 직접 삼각메시 사용 대신 폐쇄된32정점/60삼각형 convex외피. 엔진/캐빈 내부 채움, 범퍼 경사40.4도 이하. 기존 NPC 밀림 복구도 convex에 대응. 외부 패키지 도입 없음.
- 슬롯 회계: Count는 슬롯을 포함한 전체 남은 수량. 대기1개는 기존 표시풀 최상단 인스턴스를 이동해 사용. 실제집기 때1차감·슬롯숨김, 손이0.5m 이동하거나1초면 남은더미 최상단을 슬롯으로 이동. 전송 과정 Instantiate/Destroy 없음. 재고0은 비우고 Add 공급 시 재개.
- PartStack.TryConsume/TryTakePortable/Add 및 Count/rank 표시 계약 유지. 새 예약/TakeReady API로 실제 집기 시점에만 소모. 소모전 취소는 예약해제, 소모후 조립실패의 기존환불 계약 유지. GC0 핫패스.
- 대상: PrototypeProcessCrew, Worker/WorkerStation, WheelStation, SupplyManager/SupplyWorker, PlayerAssemblyInteractor, InputManager, FactoryAssemblyM2Checks/FactoryCharacterChecks 및 관련회귀.
- 텍스트: 23개 Index TextMesh의 GUI/Text Shader를 전용 ZTest LEqual URP텍스트 재질로 변경. 폰트 원본전역재질 수정하지 않고 font별캐시.
- 검증: 차량Geometry·앞뒤경사/엔진상부/이동·회전·점프/침투복구, 숫자공유재질/실제가림, 슬롯수량/예약중복/거리·시간/0재고/Add/손실/중첩/작업자·로봇·플레이어 경로, 기존회귀.
- 완료 후 GAME_DESIGN/HANDOFF/DEV_HISTORY_LOG 갱신. 이전 이 채팅13개 gameplay변경과 이번변경을 범위확인 후커밋하되 외부진행중 WorkerFBX/프리팹/참고이미지/복구폴더/환경설정은 섞지 않는다. origin 현재브랜치푸시, Notion개발진행 실제스키마로 기록.
