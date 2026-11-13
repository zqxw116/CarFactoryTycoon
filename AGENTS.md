# CarFactoryTycoon — 작업 규칙

> Claude Code와 Codex가 함께 사용하는 공통 작업 규칙이다. Codex는 이 파일을 직접 읽고, Claude Code는 루트 `CLAUDE.md`에서 이 파일을 불러온다. **다른 PC에서도 저장소를 동기화하면 동일하게 적용된다.**

규칙 변경은 이 파일에서만 한다. `CLAUDE.md`에는 공통 규칙을 복제하지 않는다.

## 프로젝트

자동차 공장 작업·자동화 게임 (Unity 6000.3 + URP 17.3).
**한 줄 정의 (2026-10-27 방향 전환, 프로토타입 검증 전)** — 멈추지 않는 자동차 조립 라인에서 혼자 시작한 작업자(1인칭/근접 3인칭)가 뛰어다니며 부품을 붙이고 망치질하다가, 사람과 로봇을 들이며 하루 처리량을 끝없이 끌어올리는 우당탕탕 B급 공장 게임. 상세는 `에이전트문서/GAME_DESIGN.md`.
(이전 정의 — '사람 관리 게임, 전 라인 로봇화가 엔딩' — 는 폐기. 기존 시스템 위에 새 방향의 M1~M4 프로토타입을 구현·기술 확인했으며 최신 상태는 HANDOFF.md를 따른다.)

### ★ 설계 결정 2단계 원칙 (2026-10-27 사용자 확정)

- **기존 코드·씬의 배치는 구 방향에서 가져온 시작점이다.** 라인 길이(210.9m)·23공정·공정 간격·속도 등 기존 씬 수치는 **새 방향의 전제가 아니라 참고값**이다. 프로토타입 플레이 후 레이아웃·수치를 검토한다.
- **1단계(프로토타입 전)**: 그릴미로 **프로토타입 착수에 필요한 최소 결정만** 확정한다 — 구조 결정(작업 구간, 레버 속도, 체결 시간, 수리 기한, 물리 매트릭스, 시점 구조, 프로토타입 공정·판정)과 프로토타입용 임시 수치.
- **2단계(프로토타입 후)**: 튜닝 수치(업그레이드 비용표, 로봇 가격·유지비, 미션 보너스·증가율, 이동·던지기 손맛, UI 세부 등)는 **프로토타입을 직접 플레이한 뒤 두 번째 그릴미 세션**에서 정한다. 이 항목들은 `GAME_DESIGN.md` 8장에 "프로토타입 후 그릴미 검토"로 표시한다.
- 1단계에서 2단계 항목을 미리 확정하지 않는다(근거 없는 추측 → 재작업). 구현 중 임시값이 필요하면 SO에 임시값으로 두고 문서에 임시임을 표기한다.

- 작업 씬: `Car_Factory 3` / 체결 테스트 씬: `TestPartsScene`
- **에이전트 문서는 모두 `에이전트문서/`에 있다.** 작업 시작 시 `에이전트문서/HANDOFF.md` → `에이전트문서/GAME_DESIGN.md` 순으로 읽는다.

## 기록 위치 (역할이 다르므로 중복 기록하지 말 것)

| 위치 | 역할 |
| --- | --- |
| `에이전트문서/GAME_DESIGN.md` | **현재 확정 게임 설계** (단일 원본). 방향·규칙이 바뀌면 여기를 고친다 |
| `에이전트문서/HANDOFF.md` | **인수인계서** — 현재 상태·다음 할 일. Claude와 Codex가 다음 세션에서 이어갈 공통 인계 정보. 항상 최신 1개 상태만 유지 |
| `에이전트문서/DEV_HISTORY_LOG.md` | **개발 히스토리 일지** — 날짜별 설계 결정·구현 노트 누적 기록(구 `GAME_DESIGN_PROGRESS.md`). 새 기록은 맨 아래에 추가 |
| `에이전트문서/IDEA_MEMO.md` | **아이디어 메모** — 확정 아닌 아이디어·레퍼런스를 날짜별로 누적. 2단계 그릴미·아트 세션에서 꺼내 검토하고, 채택되면 `GAME_DESIGN.md`로 옮긴다. 구현 근거로 쓰지 않는다 |
| `에이전트문서/ART_DIRECTION.md` | **아트 방향 단일 원본** — 팔레트, 형태·비율, 셰이더 결정, AI 에셋 파이프라인 |
| `에이전트문서/ASSET_GENERATION_PROMPTS.md` | **AI 에셋 생성 요청문** — Tripo/Meshy/이미지 생성용 프롬프트와 생성 순서 |
| `에이전트문서/과거논의사항/` | 확정 이전 검토·분석·검증 문서 보관(읽기 전용 참고) |
| Notion `개발진행` DB | 날짜별 작업 이력 **요약** (커밋 메시지 스타일 1행) + 페이지 본문에 결정 배경 |

### ★ 문서(md) 작성 규칙

- **새 md 파일은 처음에 반드시 `에이전트문서/임시/`에 만든다.** 분석, 검토, 비교, 프롬프트, 메모 등 종류와 관계없이 모두 해당한다. 루트에는 `AGENTS.md`, `CLAUDE.md`, `README.md`만 둔다. Unity 프로젝트 폴더(`CarFactoryTycoon/`) 안에는 md를 만들지 않는다.
- `에이전트문서/` 바로 아래에는 **상시 문서**만 둔다: `GAME_DESIGN.md`, `HANDOFF.md`, `DEV_HISTORY_LOG.md`, `ART_DIRECTION.md`, `ASSET_GENERATION_PROMPTS.md`, `IDEA_MEMO.md`. 이 여섯 파일은 파일명을 고정하고 내용만 갱신한다. 임시 파일을 따로 만들지 않고 직접 고친다.
- 임시 문서를 앞으로도 계속 참조해야 하면 **사용자 확인 후** `에이전트문서/`로 올린다. 역할이 끝난 문서는 `에이전트문서/과거논의사항/`으로 옮긴다. 필요 없는 문서는 사용자 확인 후 삭제한다.
- 일회성 문서는 파일명 끝에 작성일을 붙인다 (예: `XXX_REVIEW_2026-10-26.md`).
- 코드 주석의 `GAME_DESIGN_PROGRESS.md N-N` 참조는 `DEV_HISTORY_LOG.md`의 같은 장을 뜻한다.

도구별 로컬 메모리는 보조 용도로만 사용한다. 공통 규칙·결정·진행 상태를 로컬 메모리에만 남기지 않는다. 작업 시작 시 `HANDOFF.md`에서 현재 상태와 다음 할 일을 확인한다.

### Notion 링크

- 프로젝트 페이지 (개요/컨셉/설계 원칙):
  `https://app.notion.com/p/2d5d1aad58e280a0b7cefac070f35722`
- `개발진행` DB (날짜별 작업 이력):
  `https://app.notion.com/p/341d1aad58e280d085c1e565d83fa183?v=341d1aad58e280b88530000c7915f460`
  - data source: `collection://341d1aad-58e2-80ab-aa24-000bf62cf013`
  - 속성 2개뿐: **`날짜`(title, `YYYY-MM-DD`)** / **`텍스트`(작업 요약)**. 상세는 페이지 본문에 작성.
  - 접근하려면 사용하는 도구의 Notion 연결/MCP 인증이 필요하다. Claude Code와 Codex 각각에서 연결 상태를 확인한다.

## ★ 세션 전체정리 규칙

**일반 작업의 세션 전체 정리는 Notion 기록 여부를 확인한다. 단, M1·M2 등 프로토타입 마일스톤은 사용자가 완료 시 기록·커밋·푸시·Notion 정리를 상시 승인했다(2026-10-27). 구현과 필요한 검증을 마치고 차단 문제가 없으면 별도 제목 승인·Notion 재확인 없이 해당 단계 수정사항을 기록하고 커밋·푸시·Notion 정리까지 진행한다. 손맛·재미 피드백은 사용자 플레이로 이어가며, 기술 검증과 구분해서 미확인 상태를 기록한다. 해결되지 않은 차단 문제가 있으면 완료로 표시하지 않고 상태와 다음 조치를 보고한다.**

이유: Notion은 다른 PC에서도 확인할 수 있는 공용 진행 현황판이다.
저장소의 공통 문서는 Git으로 동기화하며, 도구별 로컬 메모리는 자동으로 공유되지 않는다.

정리 시 절차:
1. `에이전트문서/DEV_HISTORY_LOG.md` 맨 아래에 날짜별 상세 기록 (항상)
2. `에이전트문서/HANDOFF.md`의 현재 상태·다음 할 일을 갱신 (항상). 설계가 바뀌었으면 `GAME_DESIGN.md`도 갱신
3. 일반 작업은 **Notion에도 입력할지 사용자에게 확인** → 승인 시 `개발진행` DB에 날짜 행 추가. 프로토타입 마일스톤 완료 정리는 상시 승인에 따라 바로 기록
   (요약은 `텍스트` 속성, 결정 배경·검증 항목은 페이지 본문)

## 코드 작업 시 주의사항 (실측으로 확인된 함정)

- **`AssemblyPart.SetWork(0)`은 내부에서 `SetActive(false)`를 호출한다.**
  → 순서 필수: `BeginWorldAssembly` → `SetWork(0)` → `SetActive(true)`
- **`CarController.GetUnassembledPart`는 호출마다 `Debug.Log`를 남긴다.**
  → 매 프레임 호출 금지. 캡처 시점에 1회만 조회해 캐시할 것 (로그 폭주 + GC 발생)
- **물리 레이어**: 6=Car / 7=Station / 8=Robot / 9=Parts / 10=Player / 11=CarBody.
  기존 Car↔Station, Car↔Robot과 Default↔Default/Robot을 유지한다. M2에서 Parts↔Default/Parts/Player/CarBody와 Player↔Default/CarBody를 추가했다. CarBody↔Default/CarBody는 차단. 차량 트리거와 물리 바디는 별개이며, 운반·스택·체결 부품의 콜라이더는 끄고 던진 부품만 켠다(설계 2-6 E-2).
  → 트리거로 차량을 감지하는 새 오브젝트는 반드시 레이어 7 또는 8.
  단 **`Physics.Raycast`는 충돌 매트릭스와 무관**하므로 클릭 대상 레이어는 자유.
- **`StationController`(로봇팔)는 로봇팔에 강결합**(`Update` 첫 줄 `if (robotArm == null) return;`).
  사람 공정은 이걸 건드리지 않고 `AssemblyPart` 공개 API를 직접 구동한다 → 사람·로봇 공정이 같은 라인에 병존.
- **월드 공간 UI에 Quad 프리미티브를 쓰지 말 것** — 콜라이더가 딸려와 클릭 레이캐스트를 가로챈다.
  작업자 컨디션 게이지는 씬의 공용 월드 스페이스 캔버스 1개(`WorkerGaugeCanvas`)에 풀링한 uGUI Image(Filled)로 그린다
  (작업자별 캔버스 금지, 게이지는 캔버스 로컬 z=0 평면에 투영해 배칭 유지, Raycast Target 끔).
- **커밋은 사용자가 명시적으로 요청할 때만** 실행한다. M1·M2 등 프로토타입 단계 완료 시 커밋·푸시는 위 상시 승인에 포함된다.

## 에셋·AI 생성·차량 부품 작업 규칙 (2026-11-01 추가)

- **폴더·이름 규칙**: Unity에 맞는 용도별 폴더를 쓴다(`@Resource/Models/Characters/Worker/`, `Models/Vehicles/`, `Prefabs/Characters/`, `Prefabs/Vehicles/`, `Materials/…`, `Textures/…`). `AIGen` 같은 생성 도구 이름 폴더에 최종 에셋을 두지 않는다(`AIGen/_Reference/`는 컨셉·입력 이미지 보관용). 파일명은 **분류가 앞**: `Worker_Helmet`, `Worker_Base`, `Car_Sedan`, `Car_Classic` (O) / `Classic Car` (X).
- **AI 에셋은 알베도만** 쓴다: URP Lit, Metallic 0, Smoothness 0, 노멀맵 없음. 빨강은 미체결 표시 전용(테일램프 빨강은 사용자 승인 예외).
- **Tripo 크레딧·다운로드는 사용자 확인 후** 쓴다. 생성·리토폴·파트 보완 등 크레딧이 드는 버튼은 화면에서 비용을 확인해 보고하고 승인을 받는다. 결제·로그인·API 키 입력은 사용자가 한다. API 키는 저장소에 넣지 않는다(`Tools/Tripo/tripo_gen.py`는 환경변수 `TRIPO_API_KEY`만 읽는다. 현재 계정은 웹 Pro 플랜이라 API는 쓰지 않는다).
- **기존 게임 씬·게임플레이 프리팹·스크립트를 에셋 실험에 쓰지 않는다.** 비교·검증은 별도 씬이나 프리뷰 씬에서 한다. `Car_Factory 3` 교체 작업은 사용자 확인 후 진행한다.
- **Unity 에디터가 플레이 모드일 때 `.cs`를 추가·수정하지 않는다.** 이 프로젝트는 "플레이 중 재컴파일 후 계속" 설정이라 실행 중인 세션이 리로드된다. 먼저 플레이를 종료한다.
- **Unity MCP**: Claude Code는 `claude mcp add --transport http unityMCP http://127.0.0.1:8080/mcp`로 등록(PC마다 1회), Codex는 `~/.codex/config.toml`의 `[mcp_servers.unityMCP]`. 도메인 리로드 중 "session not ready/disconnected"는 재시도하면 된다. 스크립트 수정 직후 `execute_code`로 실행하면 리로드 전 옛 어셈블리로 돌 수 있으므로 컴파일 완료를 확인한 뒤 실행한다.
- **에셋 삭제는 사용자 확인 후** 한다. `Assets/Screenshots/`(검증 캡처)와 `Assets/Scenes/CarCompareTemp.unity`(비교용 임시 씬)는 로컬 작업 산출물이라 커밋하지 않는다.
- **차량 부품 분해 원칙** (상세: HANDOFF "차량 부품 트랙", `에이전트문서/임시/VEHICLE_PART_SPLIT_GUIDE_2026-10-29.md`)
  - 터널에서 들어오는 `Frame_1`은 **문·후드·램프 등이 모두 빠진 빈 차체 껍데기**다(차대/바닥 구조물이 아님). 작업자가 여기에 부품을 붙여 나간다.
  - **프레임에서 떼어낸 것은 반드시 별도 부품으로 존재해야 한다**(아니면 완성 차에 구멍이 남는다). 프레임 속은 비어 있어야 하며 속 채움 판(cavity)은 쓰지 않는다.
  - 분할 결과 메쉬를 손으로 고치지 않는다. `@Scripts/Editor/Data/SedanSplitDefinition.json`을 고치고 메뉴 `Tools ▸ CarFactory ▸ Vehicles ▸ Split Sedan Parts`로 다시 생성한다. 좌우 부품은 반전 복사하지 않고 각각 자른다.
  - 기존 `PartType` 27종은 무료 에셋 분류를 따른 것일 뿐 고정이 아니다. 새 차량의 부품 종류·개수는 늘거나 줄 수 있다.

## graphify

This project has a knowledge graph at graphify-out/ with god nodes, community structure, and cross-file relationships.

When the user types `/graphify`, use the installed graphify skill or instructions before doing anything else.

Rules:
- When graphify-out/graph.json exists, run `graphify query "<question>"` before your first grep, search or source-file read for a codebase question, and put this rule in every subagent prompt that explores code. Read raw files only for lines the graph or an earlier read already located. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts. These return a scoped subgraph, usually much smaller than GRAPH_REPORT.md or raw grep output.
- Dirty graphify-out/ files are expected after hooks or incremental updates; dirty graph files are not a reason to skip graphify. Only skip graphify if the task is about stale or incorrect graph output, or the user explicitly says not to use it.
- If graphify-out/wiki/index.md exists, use it for broad navigation instead of raw source browsing.
- Read graphify-out/GRAPH_REPORT.md only for broad architecture review or when query/path/explain do not surface enough context.
- After modifying code, run `graphify update .` to keep the graph current (AST-only, no API cost).
- A committed graphify-out/graph.json is rewritten whole on every build, so a large diff to it in a pull request is expected. Do not flag it as churn or suggest skipping the graph rebuild or its hooks.
