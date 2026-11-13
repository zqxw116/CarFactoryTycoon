# 부품/작업자 독립 회전·월드 크기 재고 배치
- ID: process-authoring/06
- Status: ready-for-agent
- State: closed
- Resolution: completed
- 출처:2026-11-09 사용자 요청. 실행중 미저장초안 백업 stock-edit-backup-2026-11-09.json 후 Play종료.
- 확정: 시작점에서 간격/개수 기준 배치. 끝점은 방향 기준이며 가까우면 초과/멀어도 간격늘리지않음. 대기부품은 처음 재고방향을 공유하고 독립회전가능.
- 원인: 자동기본서기 매tick 재계산/표시용 자식Transform 덮어쓰기. 재고는 고정local간격·rotation만변경하던 구조.
- 구현 계획: 작업자pose고정/자식선택루팅/대기부품Transform 저장/반대면잡기·운반각도설명 → 공정별 월드packing/실제회전메시크기/행열높이·gap·offset → 시작끝/control handle·선택적Bezier → pooled count/ready/consume/GC·기존회귀 → 문서.
- 제약: 재고API/수량/ready슬롯 의미 유지. 배치변경은 기존인스턴스Transform만 수정, 런타임재고소모 재생성없음. 원본prefab/다른공정배치자동변경없음.
- 독립 회전 검사 PASS: ready65°회전 반영/worker위치각도유지, worker180°회전보존/ready영향없음.

## 사용자 결정
- 간격 직접지정. 끝점은 자유로이 옮기며 시작점에서 고정간격 배치만 한다. 가까우면 넘어가고 멀면 간격대로만 끝난다.
- 대기 슬롯은 처음 재고와 같은 방향, 필요하면 슬롯만 별도 회전.

## 구현·검증
- 새 재고 배치 탭: 고정풀수량/행열높이/월드회전/실제메시크기+gap 또는 중심간격/전체·행·층 offset/시작끝control/선택Bezier·거리기준샘플·끝초과접선연장.
- 씬 대표부품 회전→전체재고자세, 이동→전체offset. start는첫부품 바닥기준. 기존저장공정은 opt-in으로만 전환.
- Scene rig부품변환→실제ready변환, worker기본pose를한번만고정, 자식선택→정확한root. 반대면은접촉표면/양손/방향/서기 반전. ‘몸 기준 운반 회전’을 ‘들고 걸을 때 부품 방향’으로 설명.
- Edit/Play PASS: 독립회전/worker180°유지,12개6x2/끝길이무관/offset/월드두께/stackroot기울기/동일poolID·수량/ready소모1·1초refill. 실제문flat월드크기(1.30,.35,1.44)m, 층간.3663m(빈간격.02포함).
- Play100회소모·보충 관리힙0bytes/정확count PASS. 현재검증scope에서는 배치마다 재생성없음. 최초/형상변경시 mesh배열·위치cache 갱신할당은 steady재고경로와 구분.
- 독립검토 Important/Critical없음. source재준비시sample인스턴스와length확인으로배치cache무효화 보완.
- 사용자현재문 초안에 눕힌 재고 예시 적용. ready는회전공유하며 기존손접촉중점의 월드높이를 유지하도록 slot루트위치 보정. 사용자다른공정·saved프로필보존. 현재 저작 workPose=(0,0,0)/bodySupport=false는 사용자의현재값을 덮지않음.

## 최종 검증·인계
- core20배·package rig·로봇체결/재시도·배속·반복·재고0 PASS. 반대면 root180°/접근방향/반대접촉경계·아바타/부품자식 선택루팅 PASS. Console Error0, 관련 diff --check PASS.
- current 문공정의 hasWorkPose=true/zero위치 때문에 새 배치 검증을 방해하므로 임시 프로필에서 자동작업위치로 먼저 검증. **눕힌 ready 실제집기→차량체결→1회완료·재고1 PASS**. 성공한 workPose 약(-1.390,-.405,.389)를 현재초안의 잘못된zero에만 반영. bodySupport=false는 보존. 이전 사용자편집은 백업 JSON.
- 시작·끝점/row·layer/gap·offset/Bezier, 실제회전bounds, 동일12인스턴스/수량·슬롯재충전, 100회GC0가 이번 기능검증. 전체23공정·새차종·복잡곡선밀집/자연스러운손자세 전부승인으로 확대하지 않음. 비평면표면은 bounds기준반대면초기값 후 접촉점으로 미세튜닝.
- 종료: ProcessAuthoringScene 단독 **Play/Idle/오른쪽문/재고배치탭**, count12(ready1+rear11),6묶음2층 예시·미저장초안. Source/메인프로필저장·커밋·푸시·Notion게시 없음.
- Unity Scene 캡처 Temp/AuthoringChecks/stock-flat-grid-detail-2026-11-09.png. 회전/배치/행열/높이 수치는 월드로 노출하며 프로필은 frame좌표로 보존해 메인라인에도 적용.
