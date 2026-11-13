# 재고 편집 무입력 좌표 드리프트
- ID: process-authoring/07
- Category: bug
- Status: ready-for-agent
- State: closed
- Resolution: completed
- 출처:2026-11-09 사용자 ‘시작·끝점 소수점이 계속 증가’.
- 원인: DrawStockPacking의 월드 Vector3Field 결과를 매 OnGUI에서 PackingLocal로 역변환해 원본에 대입. 입력이 없어도 부동소수점 왕복오차가 쌓이고 Scene 양방향 감지까지 전달됨. offset/row/layer/control/rotation도 같은 패턴.
- RED: 현재 실제frame/저장값 왕복1000회에서 startX+.000953674m/startZ-.000476837m, endX+.000715256m/endZ+.000238419m 재현.
- 수정: 각 필드 내부 Begin/EndChangeCheck로 실제 입력에만 역변환, 무입력은 기존 Vector3 그대로 반환. 위치/제어점/offset/회전 모두 적용. Scene 드래그는 기존 실제change조건 유지.
- 보존: 현재 전체초안 stock-drift-backup-2026-11-09.json+SessionState 백업 후 Play종료·cs수정. 이미 사용자가 편집한 값은 반올림/과거배치복원하지 않는다.
- 검증 계획: 실제 Edit/Play200회 update/repaint 전후 stock직렬화 완전동일, 기존packinggeometry/pool/ready/독립변환, Console0.

- 최종 PASS: 실제 편집창을 열고 Edit200회/Play200회 update+Repaint 전후 JsonUtility stock직렬화 완전동일. 부분근사 비교가 아니라 시작끝/제어점/offset/회전 포함 설정 전체 동일. Console Error0, 대상코드 diff --check PASS.
- 종료는 기존 Play/Idle/재고배치탭/사용자초안 유지. 새 commit/push/외부게시 없음.
