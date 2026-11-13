# 공정 편집 조작 피드백 수정

Category: bug
Status: ready-for-agent
State: closed
Resolution: completed

사용자 요청: Game 관찰 카메라 이동, 커서 비잠금/Hierarchy 즉시조작, 읽기 쉬운 편집 메뉴, 숫자와 Scene Transform 드래그의 양방향 배치 편집.

기존 승인된 공정 편집 흐름의 누락·결함을 수정한다. Scene 뷰는 배치, Game 뷰는 Play 관찰, 편집 창은 동일 프로필의 수치·저장·실행 제어로 명시한다. 편집/Play 일시정지 중 수치와 실제 Transform/기준점 드래그를 동기화한다. 실행 중에는 배치 편집을 잠그고 일시정지를 안내하며, 관찰 카메라는 우클릭+WASD/QE/Shift와 휠로 이동한다. 카메라 입력은 Game 뷰 안에서만 받고 커서를 가두지 않으며 일시정지/배속과 독립한다. 기본 Scene 탐색은 Unity 원래 조작을 사용한다.

원인 확인: ApplyLayout이 Play면 무조건 return하여 일시정지 중 수치/핸들 변경도 미반영. Transform→draft 가져오기 없음. 모든 위치/회전 핸들이 한꺼번에 겹침. 좁은 창의 긴 단일 스크롤이 선택/저장/실행을 가림. Kyle의 비활성 StarterAssetsInputs도 OnApplicationFocus가 cursorLocked=true로 잠금 설정. 관찰카메라 제어 컴포넌트 없음.

수정: NPC용 StarterAssets 입력 제거(원본/플레이어 보존), 편집씬 전용 카메라, 선택한 대상만 핸들 표시, 배치/동선·IK/차종/공통 탭과 고정 선택·실행·저장 영역, 읽기 쉬운 라벨·좌표기준 안내. 실제 재고/주체 Transform과 대기/집기/정차 기준점을 같은 저작값에 연결. 자동 움직임을 설정으로 캡처하지 않는다. Undo/미저장/프로필 왕복/대표체결 회귀를 검사한다. 외부 게시·커밋·원본 에셋 변경 없음.

## 검증 원장

- RED: disabled StarterAssetsInputs의 OnApplicationFocus 호출 후 Cursor=Locked. NPC에서 해당 입력 컴포넌트를 제거하고 편집 카메라가 None/visible을 유지한다.
- RED: Play 일시정지 중 inventory X+1 후 ApplyLayout의 실제 이동0m. 수정 후 edit/paused 양쪽 실제1m 이동 PASS.
- FactoryProcessEditorUXChecks.Run(): 수치→실제재고, Scene Transform→초안, 집기기준점→실제ReadySlot, Undo,100회idle polling무변경, 일시정지에서도카메라이동/포커스게이트/커서None PASS.
- 독립검토 Important2: 다른 대상 선택 후 작업자 numeric Undo, 일시정지 초안의 공정 왕복. 둘 다 추가검사 RED로 재현 후 선택과 무관한 authored-root 변경 반영/초안 재적용·pause 보존으로 GREEN. Minor로 분류된 오래된 활성 기준점도 이번 클릭무반영 문제에 직접 관련돼 함께 비활성화했다. review 재실행 대신 동일회귀로 확인한다.
- 실제 CUA: Game 클릭 후 SceneHierarchyWindow로 클릭만 이동, ProcessAuthoring 선택/커서None 확인(Esc 미사용). Scene 기본 Move 핸들 드래그로 재고 약0.74m 이동/편집창 Z값갱신/dirty, CtrlZ 후 pose오차0 확인. 진단용 이동은 저장하지 않고 복원했다.
- UI 전후 캡처 확인: 긴 단일스크롤·겹친 전체핸들에서 선택/실행/저장 고정 +4탭 +선택대상 핸들로 변경. 기존 Unity native IMGUI 스타일 유지. 동작·배치 화면 구분과 좌표 기준 표시.

- 실제 domain reload 검증: 미저장 inventory X+0.125m 초안을 만든 뒤 EditorUtility.RequestScriptReload 실행. 복구 초안 오차0/실제Scene pose오차0 확인, 진단 변경 복원. idle polling 외에 passive Scene handle repaint가 좌표를 매번 재기록하지 않게 실제 핸들 변경시에만 값을 쓴다.
- ReadyDisplay 직접선택이 재고더미를 선택하던 RED를 재현. 수정 후 집기슬롯 기준점을 선택하는 검사 GREEN. 배치 선택이 동선의 주체/슬롯 선택에도 연결되고 복합 공정 슬롯 선택을 유지한다.
- FactoryProcessPreviewChecks.Start(): 곡선·리깅 작업자20배/냉간로봇20배/재시도/재고복원/자동반복/재고0/.1배/종료시계 PASS·error0.
- FactoryProcessPreviewChecks.StartMatrix(): 엔진·문·바퀴·주유×작업자/로봇8조합20배/재고정확 PASS·error0. 일시정지 초안전환 수정은 별도 UX 검사로 검증한다.
- 최종 FactoryProcessEditorUXChecks edit/paused: 수치/Transform/ReadyDisplay/슬롯/Undo/다른대상선택후WorkerUndo/paused공정왕복/idle100회/카메라unscaled·포커스·freecursor PASS. 마지막 선택context/복합슬롯 UI 연결 후 edit 재실행 PASS. Unity native IMGUI 대상이라 web detector([])보다 실제창 전후캡처/기능동작을 검증 근거로 사용했다.
- 편집씬에 전용 카메라와 NPC 입력제거 저장. 메인도 새로 additive로 열어24공정 작업자·주유원의 demo 입력만 제거하고 저장/닫기(원본모델/플레이어 입력 보존). 메인 Audit missingScripts0/brokenRefs0/Humanoid29.

- 메인 FactoryCharacterChecks.Start(true,true) 입력제거 저장 후 새Play PASS: 누락0/Humanoid29,16방향/점프1.165m, 물리망치접촉/허공0/그립고정, NPC3타·완료1. 콘솔0.
- 마지막 Scene owner 갱신 보강 후 main→편집Scene 전환 상태에서 Owner일치, edit/paused UX 전체 재실행 PASS·콘솔0. 씬을 다시 열어도 이전 파괴된 Transform 연결을 사용하지 않는다.
- 미검증/범위밖: 장시간 모든공정/모든차종, 기존 엔진 그립·자세 튜닝, 사용자 주관적 자연스러움. 이번에는 게임플레이 체결 규칙·원본아트를 바꾸지 않았다. 새 커밋·푸시·Notion 게시 없음.
