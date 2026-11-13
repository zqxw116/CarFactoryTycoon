# 움직일 수 있는 리깅 작업자·기본 양팔 자세·실제 체결 재생
- ID: process-authoring/05
- Status: ready-for-agent
- State: closed
- Resolution: completed
- 출처:2026-11-08 후속 사용자 피드백.
- 원인: 고정 복사본의 위치를 매 tick 자동 덮어씀. 대기 위치에서 향하는 방향이 양손 축과 달라 대표 왼팔 목표.355m/오른팔.506m로 비대칭. 기존 손목 회전은 모델의 실제 손가락 축을 교정하지 않는 고정 축 계산.
- 구현: 양손 축에 맞춘 기본 서기, 휴머노이드 손가락 본 기준 손목 자동 정렬과 기본자세 버튼(접촉점 보존). 리깅 root 드래그를 공정별 편집용 pose에 저장하며 도달범위 밖 Idle/안 집기. Idle 버튼에서는 기준 집기 자세를 보여준다. Retry/실행에서는 두번째 모델도 실제 작업자의 본·부품 동작을 재생하되 재고/Worker는 복제하지 않는다.
- 보존: 사용자 전체 미저장 초안을 로컬 pose-edit-backup-2026-11-08.json 및 SessionState에 백업 후 Play 종료. 원래 접촉점은 유지, 손목/팔꿈치 기본자세 재설정은 별도 버튼/요청된 현재 초안에만 적용.
- 검증 계획: 양팔 거리/굽힘 대칭, 손가락 축·손목, root 수동이동 유지/저장/거리 Idle전환, Idle 강제집기, 실제 재시도 twin 본·부품 재생/재고1, 기존 pose/retry/core 회귀, 콘솔.

## 구현·검증
- 기존 대기 위치 편향 외에 Idle clip의 좌우 어깨가 비틀려 있었음. 실제 어깨 축과 양손 축의 회전·중점·수직 접근 방향을 맞춰 기본 자세 산출. default 검증 elbow왼23.61161°/오른24.6389°(기존93.39°/19.53°), 손가락 목표방향 dot>.99.
- 손목 자동정렬은 실제 Humanoid 중지/검지/새끼손가락 본으로 모델 hand basis를 계산, 부품 방향 frame과 결합. 추가euler0이 정상기준. opt-in이라 기존 저장 그립 전체를 자동 변경하지 않으며, 신규 초기화/기본자세 버튼에서 사용한다.
- 수동 root 이동/회전은 공정 actor의 별도 rigPreviewPose로 캡처. 부품은 고정해 두고 도달밖 Idle/도달안 집기. 반복tick 위치 덮어쓰기 없음, 프로필에 별도 값 기록 검사 PASS. Idle는 preview pose초기화 후 기본집기, 실제 worker일반대기/차량정차는 유지.
- 실제 체결 재생 PASS:620표본(보유316), 양팔6본 회전오차0°/손상대위치최대.000000608m/부품상대위치최대.000000477m, 실제체결완료/재고1. 모델만 재생하므로 두번째 Worker/재고소모 없음.
- 최종 기존9 pose검사+MovableRig PASS(Play Idle), core20배/반복/재고0/배속복원 및 robot/worker Retry선집기·보유·반복복원·완료 PASS. Console Error0. 코드·관련문서 diff --check PASS.
- 사용자 접촉점left/right와 다른 공정값은 보존. 요청된 현재공정 초안에서만 자동 손목+추가euler0/대칭팔꿈치·팔98% 적용. 원래 수동보정은 cwd의 pose-edit-backup-2026-11-08.json으로 보존. 소스 프로필/메인 저장·커밋·외부게시 안함.
- 시각 확인: Unity Scene Temp/AuthoringChecks/movable-rig-forward.png, movable-rig-arms.png. 독립 코드 검토의 중요2건을 수정하고 재검증. 추가 Important/Critical 없음. 전체공정·차종/자연스러움 최종승인으로 확대하지 않음.

- 독립검토2건: 손에서 놓은 뒤 Hits에서 부품이 슬롯으로 돌아가 보임 → Crew.PreviewPartFor 및 attempt 범위 cache로 망치질/완료후출구까지 실제부품 추적. 기본팔 강제가 다른 actor에 적용됨 → actor.id 범위로 제한, ForceReachScope PASS.
- 최종 late-phase replay PASS:618표본=보유312/해제후망치33/슬롯정리후출구271, 본회전차0°/손상대차최대.000000546m/부품상대차최대.000005960m, 완전체결/재고1. 이 결과가 이전 보유구간만 포함한620표본 검증을 보완한다.
- 종료: 기존처럼 Play 유지/Idle/문공정/잡기탭, 리깅 작업자 root 선택·Move도구·기본양팔뻗기. 사용자 설정은 초안, 명시 저장·메인 적용은 사용자 버튼으로 진행.
