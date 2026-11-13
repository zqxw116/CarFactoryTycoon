# 전체 세척→첫 납품→수동 재투입→첫 투자
- ID: first-production-loop/01
- Category: enhancement
- Status: ready-for-agent
- State: closed
- Resolution: completed
- 완료: 2026-11-13. 현행 GAME_DESIGN 최상단 2026-11-12 설계를 기준으로 사용자 승인 목표를 구현·Unity 플레이 검증했다.
- 목표: 차량 전체 외장 세척 → 대시보드 집기·부착·실제 망치 체결 → 대차 인수금 → 현장 수동 재투입 → 정상 수입으로 첫 고용 또는 다음 공정 해금.
- 보존: 기존 메인 씬·공정 프로필·NavMesh 파일 SHA256 동일. 공정 편집 창 dirty=false를 유지했고 포즈·그립·재고 배치를 재튜닝하지 않았다. 모든 cs 수정은 Play 종료 확인 후 진행했다.
- [사용법](usage.md)

## 구현
- 메인 Car_Factory 3/prototype 모드에 FirstProductionLoop가 runtime으로 연결된다. 기존 CarSpawner/CarPool, PartStack 대기 슬롯, 플레이어 부착·망치, 계약·경제·대차를 재사용했다. 씬 파일을 덮어쓰지 않는다.
- VehicleWashSurface/VehicleWash.shader: 실제 활성 차체 메시의 외장 5방향 투영과 색 복구 마스크. 하부·내부 틈 면은 제외한다. 실제 바닥/컨베이어 지지 높이와 서기·점프 시점에서 분사 가능한 면을 찾고, 가로 12cm/높이 25cm 시점 변화에도 닿는 면을 택한다. 최종 차량 대상 5,444개를 모두 씻어야 완료다. 완료 비율 임계값을 낮추지 않았다.
- 첫 차만 생명주기별 표적 gate로 세척·대시보드 체결을 기다린다. 이후 차는 작업 완료를 기다리지 않는다. 현장 터널·속도 레버는 기존 실제 망치 접촉으로 조작한다.
- 투입 시 필수 부품/세척/가격/끝단을 계약에 동결한다. 인수 시작 때 판정하고 수리 창을 닫으며, 정상 전액 또는 미완료0원·이유를 표시한다. 중복 지급과 체결 잔돈을 막는다.
- 현장 표지 F에서 고용 또는 다음 공정 해금. 새 공정·재고와 벨트가 열리고, 옛 차량 인수가 끝난 뒤 납품 지점을 옮긴다. PendingProductionOutletGate는 새 계약 차량이 그 끝단을 지나치지 않게 대기시킨다.
- FirstProductionSettings 임시 기본값: 초기 납품100 / 첫 고용180 / 다음 공정200 / 추가 공정당 납품+60. 최종 밸런스 확정이 아니다. 기존 밸런스/authoring 자산을 변경하지 않았다.
- 1 분사건 / 2 조립, 현재 작업·납품 이유·다음 목표·현장 투자 UI를 화면 비율에 맞춘다. 기존 중복 자금 HUD와 구 프로토타입 구매·하루 흐름은 새 루프에서 제외했다.

## 검증 결과
- [x] 실제 입력 플레이: 새 세션0원 → 첫 차 전체 세척100% → 대기 슬롯 E 집기(12→11) → E 유지 부착 → 가까이서 실제 망치 접촉 체결 → 인수100원.
- [x] 실제 터널 타격으로 두번째 차 투입, 풀 재사용 세척0%·첫차 gate 해제 확인. 실제 레버 Stop/Low를 선택해 작업 시간을 확보했다. 두번째 전체 세척100%/재고11→10/부착·체결/인수100원.
- [x] 총 정상 수입200원으로 현장 F 팝업·숫자2 입력 → 엔진 공정 해금, opened2/outlet2/엔진 표시, 잔액0. 정상2/실패0인 새 세션으로 확인했다. 입력 경로를 단계별로 보정하며 검증했으며, 돈·체결·세척 완료 플래그는 주입하지 않았다.
- [x] 앞선 별도 플레이에서는 미완료 두번째 차가 실제로 통과하여 ‘전체 세척 미완료·0원’ 처리되고 첫100원이 유지됨을 확인했다.
- [x] 격리 도메인 검사: 계약 배열 복사·미완료 거절·잔액 부족 유지, 새 outlet 대상차/생명주기 gate, 인수 후 수리 차단·중복 capture 거절·정산 이벤트1회. 실제 입력 플레이와 별도로 수행했다.
- [x] 세척 안정 시점 6방향 검사 0/5,444 불안정. 워밍업 후 실제 Spray200회 hit200 / managed allocation0 bytes. 전체 프레임 GC나 GPU 비용0을 뜻하지 않는다.
- [x] 기존 FactoryAssemblyM2Checks passed: 26대기 슬롯 부품/77도메인 타격·중복완료·검사·기한/리셋. FactoryCharacterChecks(contacts=true,npc=false) PASS59. FactoryFeedbackNov2Checks PASS23. 캐릭터 audit: missingScripts0/brokenRefs0/humanoids29. 캐릭터 fixture의 마우스는 None으로 유지했다.
- [x] 반복 Play/Stop: 생성 루트1/재질누락0 → 종료 루트0/세척 질의 루트0, 재실행 후 다시 종료0. 새 컴파일 오류0, 최종 종료 후 Console Error/Warning0, 대상 diff --check PASS. 기존 컴파일 obsolete/CS0114 경고는 수정 범위 밖이다.
- [x] 최종 메인 Car_Factory 3 Edit/dirtyfalse, 공정 편집 창 dirtyfalse, CursorNone, LegacyChecks=false. 메인 씬·프로필·NavMesh 해시 PASS.
- [x] HANDOFF/DEV_HISTORY_LOG/사용법/인덱스 갱신. 새 커밋·푸시·외부 게시 없음.

## 진단과 수정 근거
- 초기98% 부근 정체: 투영이 내부 면을 포함했고, 메시 최저점을 바닥으로 오인했다. 실제 바닥/컨베이어 높이와 분사 가림·면 방향을 함께 검증하도록 바꿨다.
- 마지막1개 모서리는 약2cm 위치 정밀도를 요구했다. 단순히 완료로 처리하지 않고 모든 후보 시점의 수평/수직 여유를 검증하여 안정적인 외장 목표로 정리했다. 이후 실제 입력으로 두 차 모두100% 완료했다.
- 검증 이동은 큰 더미/고용 표지를 가로지르거나 부착 가능 거리에서 멀리 망치를 휘둘렀다. 차량 외곽·남쪽29.5m 통로·슬롯 전면·가까운 실제 타격 거리로 경로를 보정했다.
- 해금 후 분홍 화면: DontSave 루트가 Play 종료 뒤 invalid-scene 상태로 남고 재질만 해제되어 이전 납품벽이 중복 렌더링됐다. 해당 테스트 생성 루트5개만 정리하자 즉시 정상 복구. 새 루트는 일반 Play 씬 수명을 쓰며 종료 시 tool 재질/임시 설정/traffic gate를 정리한다. 반복 종료 잔여0으로 재검증했다.

## 증거와 한계
- cwd first-production-2026-11-13/verification.txt, protected-sha256.json, preservation-check.json 및 PNG들. Unity SessionState FirstProductionFinalFresh/FirstProductionLegacyM2/FirstProductionLegacyCharacter/FirstProductionLegacyFeedback 등에 결과를 저장했다.
- 실제 구매 선택은 다음 공정 해금이다. 고용 가격·공통 소비 경로는 구현했지만 이번 새 루프에서 고용 후 NPC 작업을 끝까지 플레이한 것은 아니다.
- 진행 중 해금은 계약 스냅샷·대기 gate 격리 검사 범위이며 다차량 혼잡 실플레이는 후속이다. 영구 저장, 자동 투입 업그레이드, 후속 공정 전체 밸런스, 최종 수밀/주행/스캔, 태블릿, Blender 신규 아트 통합은 이번 완료 범위가 아니다.
- 검증 대상은 Unity Editor Play다. 독립 빌드/셰이더 포함·스트리핑/새 차종·최종 아트의 성능은 별도 확인이 필요하다. 독립 리뷰 에이전트는 사용량 한도로 결과를 내지 못해 직접 코드 점검과 Unity 회귀로 마감했다.
