# CarFactoryTycoon — 작업 규칙

> 이 파일은 Claude Code가 세션 시작 시 자동으로 읽는다. **다른 PC에서 작업할 때도 동일하게 적용된다.**

## 프로젝트

자동차 공장 자동화 방치형/경영 타이쿤 (Unity 6000.3 + URP 17.3).
**한 줄 정의** — 자동차 공장처럼 보이지만 실제로는 사람 관리 게임. 초기 공정은 사람 작업자가 체결하고,
사장(유저)이 인력 관리의 번거로움을 피하려 로봇팔 자동화를 사들이며, 전 라인이 로봇팔로 바뀌는 순간이 엔딩.

- 작업 씬: `Car_Factory 3` / 체결 테스트 씬: `TestPartsScene`
- **설계·구현 상세는 `CarFactoryTycoon/GAME_DESIGN_PROGRESS.md`** 가 단일 원본(single source of truth).
  기획 방향 전환은 8장, 인력·물류 시스템 설계는 9장.

## 기록 위치 (3중 구조 — 역할이 다르므로 중복 기록하지 말 것)

| 위치 | 역할 |
| --- | --- |
| `CarFactoryTycoon/GAME_DESIGN_PROGRESS.md` | 설계 결정·구현 노트 **상세**. git으로 이력 관리 |
| Notion `개발진행` DB | 날짜별 작업 이력 **요약** (커밋 메시지 스타일 1행) + 페이지 본문에 결정 배경 |
| Claude 메모리 | 다음 세션에서 바로 이어가기 위한 **현재 상태**와 다음 할 일 |

### Notion 링크

- 프로젝트 페이지 (개요/컨셉/설계 원칙):
  `https://app.notion.com/p/2d5d1aad58e280a0b7cefac070f35722`
- `개발진행` DB (날짜별 작업 이력):
  `https://app.notion.com/p/341d1aad58e280d085c1e565d83fa183?v=341d1aad58e280b88530000c7915f460`
  - data source: `collection://341d1aad-58e2-80ab-aa24-000bf62cf013`
  - 속성 2개뿐: **`날짜`(title, `YYYY-MM-DD`)** / **`텍스트`(작업 요약)**. 상세는 페이지 본문에 작성.
  - 접근하려면 Notion MCP 인증 필요 → 터미널에서 `/mcp` → "claude.ai Notion" 선택.

## ★ 세션 전체정리 규칙

**작업 세션을 마무리하며 전체 정리를 할 때는, 정리 내용을 Notion에도 입력할지 사용자에게 물어볼 것.**

이유: 다른 PC에서 작업하거나 오랜 공백 후 복귀할 때 Notion이 유일한 공용 진행 현황판이 된다.
로컬 md와 Claude 메모리는 PC에 묶여 있어 옮겨가지 않는다.

정리 시 절차:
1. `GAME_DESIGN_PROGRESS.md`에 상세 기록 (항상)
2. Claude 메모리에 현재 상태 + 다음 할 일 기록 (항상)
3. **Notion에도 입력할지 사용자에게 확인** → 승인 시 `개발진행` DB에 날짜 행 추가
   (요약은 `텍스트` 속성, 결정 배경·검증 항목은 페이지 본문)

## 코드 작업 시 주의사항 (실측으로 확인된 함정)

- **`AssemblyPart.SetWork(0)`은 내부에서 `SetActive(false)`를 호출한다.**
  → 순서 필수: `BeginWorldAssembly` → `SetWork(0)` → `SetActive(true)`
- **`CarController.GetUnassembledPart`는 호출마다 `Debug.Log`를 남긴다.**
  → 매 프레임 호출 금지. 캡처 시점에 1회만 조회해 캐시할 것 (로그 폭주 + GC 발생)
- **물리 레이어**: 6=Car / 7=Station / 8=Robot / 9=Parts.
  충돌 매트릭스는 **Car↔Station, Car↔Robot 두 조합만** 허용(Default 포함 나머지 차단).
  → 트리거로 차량을 감지하는 새 오브젝트는 반드시 레이어 7 또는 8.
  단 **`Physics.Raycast`는 충돌 매트릭스와 무관**하므로 클릭 대상 레이어는 자유.
- **`StationController`(로봇팔)는 로봇팔에 강결합**(`Update` 첫 줄 `if (robotArm == null) return;`).
  사람 공정은 이걸 건드리지 않고 `AssemblyPart` 공개 API를 직접 구동한다 → 사람·로봇 공정이 같은 라인에 병존.
- **월드 공간 UI에 Quad 프리미티브를 쓰지 말 것** — 콜라이더가 딸려와 클릭 레이캐스트를 가로챈다.
  게이지는 3D TMP 문자 블록(`▮▯`)으로 그린다.
- **커밋은 사용자가 명시적으로 요청할 때만** 실행한다.
