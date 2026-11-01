# AI 에셋 생성 요청문 (Tripo / Meshy)

> 작성: 2026-10-28 (Claude Opus 5.5) · 개정 1: 2026-10-28 프롬프트 재검토 반영 · 스타일 기준: `ART_DIRECTION.md`

---

## 0. 개정 1에서 바꾼 것 (왜 바꿨는가)

| 구분 | 이전 | 문제 | 변경 |
|---|---|---|---|
| 과함 | 3D 프롬프트에 긴 스타일 문장(약 70단어)을 붙였다 | 3D 생성기는 대상 명사와 형태 단어 위주로 반응한다. "storybook, evenly lit, 3/4 view, background" 같은 이미지 용어는 효과가 없고 대상 설명을 희석한다. 서비스별 글자 수 제한에도 걸린다 | **3D용(짧게)과 컨셉 이미지용(길게) 프리픽스를 분리** |
| 과함 | 프리픽스에 팔레트 색 6개를 나열했다 | 대상 문장의 색 지정과 충돌해서 색이 무작위로 섞인다 | 프리픽스에서 색 제거. **대상별 2~3색만** 지정 |
| 과함 | 네거티브 20여 개 | 3D와 무관한 항목이 섞여 있었다(anime, thick outlines) | 3D에 실제 영향 있는 것만 남김 |
| 부족 | 하드서피스 지시가 없었다 | AI 3D는 녹아내린 듯 뭉개진 형태가 기본값이다. 공장 기계에 치명적이다 | `hard surface, crisp flat planes, straight edges` 추가. "soft bevel"은 뭉개짐을 부추겨서 `small bevels`로 약화 |
| 부족 | 디테일을 형태로 만들라는 요구가 약했다 | AI는 볼트를 텍스처로 그려 넣는다 | `modeled geometric details, not painted` 명시 |
| 부족 | 얇은 구조물 대책이 없었다 | 호스, 사다리, 체인, 얇은 선반 기둥, 위에 올린 작은 공구는 녹아 붙거나 구멍이 난다 | 얇은 요소는 두껍게 지정하거나 대상 문장에서 뺌(랙, 카트, 탱크, 주유기, 조명 수정) |
| **부족(중요)** | 컨셉 이미지에 "illustration" 톤을 요구했다 | 이미지에 그려진 **선과 음영이 텍스처에 구워지거나 형상으로 오인된다.** 아웃라인은 셰이더가 그린다 | 컨셉 이미지는 **선 없음, 평탄한 조명, 음영 최소** |
| **부족(중요)** | 에셋마다 따로 생성해서 서로 비율과 톤이 어긋날 수 있었다 | 생성기마다 스타일이 표류한다 | **스타일 앵커**: 첫 통과작을 이후 모든 컨셉 이미지의 스타일 참조 이미지로 사용(§1-4) |
| **오류 수정** | 문, 후드 같은 차체 패널을 개별 생성 후 조립 | 따로 뽑은 패널은 차체 곡면과 맞지 않는다 | **차체 패널은 차체 1회 생성 → Blender 분리.** 개별 생성은 독립 부품(바퀴, 시트, 엔진, 램프)만 |
| 부족 | 부품 색 기준이 없었다 | 도장 후 체결되는 부품(문 등)은 차량 색을 따라가야 한다 | 도장 대상 부품은 **중성 연회색 단색**(엔진에서 틴트) |
| 부족 | 캐릭터 리그 조건 | 팔다리가 몸에 붙으면 자동 리그가 실패한다 | A-pose, 팔다리 분리, 손은 단순한 벙어리장갑형 |
| 부족 | 재질 영역 구분 | 생성 후 부위별 팔레트 머티리얼로 교체하기 어렵다 | `clear color blocking by material zones` 추가 |
| 부족 | 검수 | 뒷면과 바닥은 생성기가 지어낸다 | 체크리스트에 뒷면, 바닥, 피벗 확인 추가 |


> **개정 3 (2026-10-28 사용자 결정):** 스타일 앵커는 2종(`CHAR_Worker` + 단순 차량 `CAR_Body` 1대)으로 줄였다(로봇팔은 기존 에셋이 있어 제외, 용접 스테이션 기계도 제외하고 목록에서 뺐다). 차량 부품은 앵커 확정 후 별도 단계에서 파생하며(§3 차량 부품), 외장 패널은 차체에서 분리하고 내부·독립 부품은 확정 차량 컨셉 이미지를 스타일 참조로 넣어 새로 생성한다. Flat Kit은 할인 시 구매로 보류하고, 그 전 앵커 판단은 임시 확인 환경(§4)에서 한다.

---

## 1. 사용법

### 1-1. 기본 흐름 (권장)
1. 이미지 생성 AI에 **§2-2 컨셉 프리픽스 + 대상 문장(+ 스타일 앵커 이미지)**을 넣어 컨셉 이미지를 만든다.
2. 컨셉 이미지가 §5 컨셉 검수를 통과하면 Tripo 또는 Meshy **image-to-3D**에 넣는다.
3. 서비스 옵션: 리메시/폴리 제한 켜기, 텍스처는 base color 위주(PBR 끄기 또는 메탈릭·러프니스 맵 무시), 대칭 옵션이 있으면 대칭 오브젝트에만 켜기.
4. 결과를 §5 3D 검수 → 통과분만 FBX로 import.

### 1-2. 빠른 흐름 (text-to-3D)
- `§2-1 3D 프리픽스 + 대상 문장`. 네거티브 칸이 있을 때만 §2-3을 넣는다.
- 품질 편차가 크므로 **P0 비교 테스트용**으로만 쓰고, 양산은 1-1로 한다.

### 1-3. 글자 수
- 서비스마다 프롬프트 길이 제한이 있다. **3D 프롬프트는 프리픽스 + 대상 문장 합쳐 400자 안팎**을 목표로 한다.

### 1-4. 스타일 앵커 (일관성의 핵심)
1. **앵커 2종(화면 점유율이 가장 큰 것)을 먼저** 만든다(개정 2 사용자 지적 반영, 개정 3에서 2종으로 축소). 화면 분위기는 크고 오래 보이는 것이 정하고, 소품은 그것을 따라간다.
   - `CHAR_Worker`(P1-C1): 근접 3인칭에서 항상 화면에 있다. 팔레트(머스터드, 슬레이트 블루, 크림)의 주 운반자다.
   - `CAR_Body`(P1 차량): 화면 중앙의 주인공인 **단순 차량 1대**다. 앵커 확정 후 부품 파생의 기준이 된다.
   - 로봇팔은 기존 에셋이 있으므로 앵커에서 제외한다.
   - 바닥, 벽, 조명도 화면의 큰 부분을 차지하지만 AI 대상이 아니다(ProBuilder + 조명). 앵커를 Unity에 놓을 때 회색 블록아웃이라도 함께 둔다.
2. 두 컨셉 이미지가 검수를 통과하고 **서로 같은 게임처럼 보이면** `StyleAnchor_v1_*.png`로 저장한다(`에이전트문서/` 밖, 예: 개인 작업 폴더나 `Assets/@Resource/Models/AIGen/_Reference/`).
3. 이후 모든 컨셉 이미지 생성 때 이 이미지를 **스타일 참조 이미지**로 함께 넣는다. 레퍼런스 원본(도시 이미지)은 넣지 않는다.
4. 앵커를 바꾸면 v2로 버전을 올리고, 그 전에 만든 에셋을 다시 검수한다.

---

## 2. 공통 블록

### 2-1. 3D 프리픽스 (text-to-3D용, 짧게)
```
Stylized hard surface game prop, chunky simple proportions, crisp flat planes, straight edges with small bevels,
modeled geometric details not painted, matte flat colors, clear color blocking by material zones, solid closed mesh.
```

### 2-2. 컨셉 이미지 프리픽스 (이미지 생성 AI용)
```
3D rendered stylized game asset, single object, whole object fully visible, front-left three-quarter view from slightly above,
plain light warm grey background, flat even soft lighting, minimal shading, no cast shadow, no outlines, no line art,
matte flat colors, warm muted desaturated tones, chunky simple hard surface shapes, small bevels,
detail from small modeled parts like bolts and panel seams, clean surfaces, cozy and friendly feel.
```

### 2-3. 네거티브 (칸이 있을 때만)
```
photorealistic, glossy, chrome, reflections, rust, dirt, scratches, decals, text, logo,
multiple objects, ground plane, base platform, thin wires, melted shapes
```
※ 캐릭터 외 대상은 `ground plane, base platform`이 있어야 바닥판이 붙지 않는다. 기계 대상 중 원래 받침이 있는 것(로봇팔 원형 받침 등)은 대상 문장에 명시하므로 영향이 없다.

### 2-4. 앵커 컨셉 이미지 프롬프트 (개정 4, 2026-10-28)

> 배경: text-to-3D A/B 2회차 결과, 사용자는 **Meshy 작업자 2차**(애니메이션 영화풍 반실사, 둥근 형태, 풍부한 디테일)를 확정 톤으로 선택했다. 차량은 Tripo 1차는 장난감 같고 2차는 특색 없는 실사 세단이어서, **"현실적인 차 디자인 + 살짝 과장된 둥근 형태"**가 목표다. Meshy text-to-3D가 2026-10-31에 종료(에이전트로 이전)되므로, 이후 앵커는 **컨셉 이미지 → image-to-3D**로 만든다.
> 대상 도구: ChatGPT(GPT 이미지), Gemini, Midjourney 등. 이 프리픽스는 §2-2보다 우선한다(§2-2는 소품용).

**공통 원칙 (image-to-3D 입력용)**
- 대상 1개, 전체가 잘리지 않게, 단색 배경, 고른 조명, 그림자·선·글자 없음 → 형상 오인과 텍스처 구움을 막는다.
- 캐릭터는 **정면**(리깅·대칭), 차량은 **앞 왼쪽 3/4**(형태 정보가 가장 많음). Tripo 멀티뷰용으로 정면·측면·후면 변형을 따로 둔다.
- 유리는 **불투명한 짙은 틴트**로 지정한다. 투명 유리는 실내가 비쳐 3D 변환 시 형상이 깨진다.
- 작업자 이미지가 확정되면, 차량 이미지 생성 때 그 이미지를 **스타일 참조**로 함께 넣는다(§1-4).

**작업자 (CHAR_Worker) — 정면 A-pose**
```
A 3D rendered character for a stylized video game, in the style of a modern animated feature film:
believable anatomy with soft, rounded, appealing shapes, not a toy and not photorealistic.
Full body of a friendly young factory worker, about 6.5 heads tall, standing in a relaxed A-pose facing the camera:
arms straight down and angled about 30 degrees away from the torso, hands open with fingers together and not touching the body,
feet shoulder-width apart, both feet flat on the ground.
Mustard yellow hard hat, cream work shirt with a chest pocket and sleeves rolled to the elbows,
slate blue denim overalls with visible stitched seams, side pockets, shoulder straps and brass buttons,
brown leather work gloves, a small leather tool pouch on the belt, sturdy brown lace-up work boots.
Warm friendly face, expressive eyes, light rosy cheeks, short brown hair under the hat, closed gentle smile.
Matte hand-painted textures, warm muted colors, no glossy highlights.
Straight front view, camera at chest height with little perspective, whole body fully visible with empty margin around it,
plain light warm grey background, soft even studio lighting, gentle shading, no cast shadow, no outlines, no text, single character only.
```

**차량 (CAR_Body) — 앞 왼쪽 3/4**
```
A 3D rendered car for a stylized video game, in the style of a modern animated feature film:
a believable real-world car design with gently exaggerated, soft rounded forms and appealing proportions,
not a toy and not photorealistic.
A retro-modern compact four-door sedan with a short separate trunk, inspired by small 1960s European family cars:
friendly face with round headlights and a small smiling grille, rounded roof, puffy rounded wheel arches,
short overhangs, wide stance, wheels slightly larger than real.
Clean smooth body panels with clearly visible panel gaps around the four doors, hood and trunk,
simple door handles, side mirrors, round tail lights, body-colored bumpers.
Desaturated soft mint green matte paint, matte cream trim instead of chrome, cream multi-spoke rims, dark warm grey tires,
opaque dark smoky blue-grey tinted windows with no interior visible.
Doors closed, wheels pointing straight. No logos, no badges, no license plate.
Front-left three-quarter view from slightly above, whole car fully visible with empty margin around it,
plain light warm grey background, soft even studio lighting, gentle shading, no cast shadow, no reflections,
no outlines, no text, single vehicle only.
```

**변형 (필요할 때만)**
- Tripo 멀티뷰: 마지막 시점 문장을 `Exact side view from the left, ...` / `Straight back view, ...`로 바꿔 같은 대화에서 이어서 생성한다("same character/car as the previous image"를 앞에 붙인다).
- 스타일 참조 첨부 시 첫 문장 앞에 추가: `Match the rendering style, proportions and color mood of the attached reference image.`
- Midjourney: 끝에 `--ar 1:1 --style raw --no outline, text, logo, shadow, reflection, background props`.
- 차체를 엔진 틴트용으로 뽑을 때: 페인트 문장을 `neutral light grey matte paint`로 바꾼다(형태 확정 후).

**검토에서 고친 점 (초안 → 최종)**
| 항목 | 초안 | 문제 | 최종 |
|---|---|---|---|
| 작업자 비율 | 5.5 heads | 사용자가 고른 Meshy 2차는 약 7등신. 이미지 AI는 수치를 그대로 따른다 | 6.5 heads |
| 손 | open hands | 손가락이 벌어지면 3D에서 녹아 붙는다 | fingers together, not touching the body |
| 음영 | minimal shading | 얼굴과 옷 주름이 납작해져 매력이 줄어든다 | gentle shading (선·그림자만 금지) |
| 차량 특색 | compact sedan | 2차처럼 평범한 세단이 나온다 | retro-modern, 1960s 유럽 소형차 영감, 둥근 헤드램프와 미소 그릴 |
| 유리 | tinted glass | 투명하면 실내가 비쳐 형상이 깨진다 | opaque, no interior visible |
| 크롬 | (없음) | 이미지 AI가 범퍼·몰딩을 크롬으로 칠한다 | matte cream trim instead of chrome |

### 2-5. 기본 작업자 — 헬멧 없는 버전 (2026-10-28)

> 배경: 커스터마이징 방향(옷차림별 통짜 캐릭터 + 선택적 머리 소품 + 색 변형)을 위해 **헬멧 없는 기본 머리**가 필요하다. AI 메쉬는 겉껍데기라 헬멧을 분할로 떼면 머리가 비므로, 레퍼런스 시트 단계에서 헬멧을 뺀 뒤 다시 생성한다.
> 사용법: ChatGPT에 `AIGen/_Reference/StyleAnchor_v1_Worker/건설 노동자 캐릭터 모델 시트.png`를 첨부하고 아래 문장을 그대로 붙인다. 결과는 같은 폴더에 `작업자 모델 시트_헬멧없음.png`로 저장 → 정면·측면·후면을 잘라 Tripo 멀티뷰(H3.1) → (분할) → 리토폴로지 → Mixamo 리그.

```
Edit the attached character model sheet. Make exactly one change: remove the yellow hard hat from the character everywhere it appears
(all five full-body views, the face close-up, the three face angles and the small pose thumbnail), and show the bare head with hair instead.

Hair: short dark brown hair matching the bangs already visible in the sheet, styled as soft, chunky, clearly separated clumps
like a modern animated feature film character. Neat and close to the head: short on the sides and back, slightly tousled with
low volume on top, a gentle side-swept fringe on the forehead, ears fully visible. No thin strands, no flyaway hairs, no spikes.
The skull must be naturally round and full, not flattened by a hat, and the hairline must be consistent across all views,
including the back of the head in the back and 3/4 back views.

Keep everything else exactly the same: the same face, eyes, expression and skin tone; the same head size and body proportions;
the same pose with arms down; the same clothes, gloves, boots, colors, stitching and details; the same camera angles, positions
and scale of each view; the same layout, panels, background color and lighting. Do not make the character taller or move the
feet; only the area above the forehead changes. Keep every text label exactly as it is.
Matte hand-painted look, no glossy highlights on the hair. Same image size and aspect ratio (1536 x 1024).
```

**검토에서 고친 점 (초안 → 최종)**
| 항목 | 초안 | 문제 | 최종 |
|---|---|---|---|
| 변경 범위 | remove the helmet | 이미지 AI가 시트 전체를 다시 그리며 얼굴·옷이 바뀐다 | "exactly one change" + 유지 항목을 하나씩 나열 |
| 머리 형태 | short brown hair | 헬멧에 눌린 납작한 머리나 큰 머리가 나와 나중에 모자가 안 맞는다 | 둥근 두개골, 옆·뒤 짧게, 윗부분 볼륨 낮게 |
| 머리 표현 | (없음) | 가는 가닥·잔머리는 3D에서 녹아 덩어리가 된다 | chunky clumps, no thin strands / flyaways |
| 후면 | (없음) | 정면만 고쳐지고 뒷머리가 어색해지기 쉽다 | 뒷머리·헤어라인을 모든 시점에서 일치 |
| 키 | (없음) | 헬멧이 빠진 만큼 몸을 늘려 화면을 채우려 한다 | 키·발 위치 고정, 이마 위만 변경 |
| 자세 | A-pose로 변경 | 한 번에 두 가지를 바꾸면 얼굴·옷이 더 많이 흔들린다. 팔 내린 자세로도 리그·걷기 미리보기가 정상이었다 | 자세 유지 (A-pose는 필요해지면 별도 편집) |
| 글자 | (없음) | 다시 그리면서 한글 라벨이 깨진다 | 라벨 유지 지시 (깨져도 시점만 잘라 쓰므로 무방) |

---

## 3. 에셋 목록

우선순위: **P0** 룩데브 샘플 / **P1** 핵심 플레이 / **P2** 분위기·확장
치수는 가로×세로×높이(m) 대략값이다. 프롬프트에 넣지 않고 Unity에서 스케일을 맞출 때 쓴다.
대상 문장은 §2-1 또는 §2-2 뒤에 붙인다.

### P0 — 룩데브 샘플 (10종, 위에서부터 순서대로)

| 순서 | ID | 이름 | 치수 | 대상 문장 |
|---|---|---|---|---|
| 1 | P0-05 | `PROP_ToolCart` | 0.9×0.5×1.0 | `a rolling workshop tool cart with two wide drawers and chunky handles, mustard yellow body, slate grey top, four small thick caster wheels, empty top surface` |
| 2 | P0-01 | `TOOL_Hammer` | 0.35 | `a chunky workshop hammer, oversized slate grey steel head, thick light wood handle with a mustard yellow grip band` |
| 3 | P0-02 | `PROP_PartRack` | 1.5×0.6×1.8 | `a sturdy industrial storage rack, thick sage green square posts, three solid cream shelves, bolted corner plates` |
| 4 | P0-03 | `PROP_PartCrate` | 0.8×0.6×0.5 | `a stackable industrial parts crate, slate blue-grey metal box with thick rim, blank cream label plate, two side handle cutouts` |
| 5 | P0-04 | `PROP_Pallet` | 1.2×1.0×0.15 | `a wooden shipping pallet with thick planks and three solid blocks, warm light wood` |
| 6 | P0-06 | `PROP_SafetyBollard` | 0.15×0.15×1.0 | `a thick round safety bollard post, mustard yellow with one terracotta orange band, rounded top` |
| 7 | P0-07 | `PROP_TrafficCone` | 0.35×0.35×0.5 | `a chunky traffic cone with a square base, terracotta orange with one cream stripe` |
| 8 | P0-08 | `MACH_StationSign` | 0.8×0.1×1.2 | `a standing sign board on a thick square post with a flat foot plate, blank cream panel with a slate blue frame` |
| 9 | P0-09 | `MACH_LineLever` | 0.4×0.4×1.1 | `a floor control box with one big thick lever, sage green boxy housing, mustard yellow lever knob, three raised position notches` |
| 10 | P0-10 | `PROP_HangingLamp` | 0.6×0.6×0.4 | `a large industrial dome pendant lamp shade, sage green outside, cream inside, short thick mounting stem, no chain` |

- 텍스트(공정 번호)는 생성하지 않는다. Unity TextMeshPro로 넣는다.
- 램프 체인과 케이블은 Unity에서 실린더나 라인으로 붙인다.

### P1 — 핵심 플레이

**캐릭터** (Humanoid 리그 필수)
| ID | 이름 | 대상 문장 |
|---|---|---|
| P1-C1 | `CHAR_Worker` | `a friendly factory worker character, about 5 heads tall, slightly stubby proportions, simple round face with dot eyes, mustard yellow hard hat, slate blue overalls, cream shirt, chunky boots, simple mitten-like hands, A-pose with arms and legs clearly apart from the body, full body` |
| P1-C2 | `CHAR_WorkerVariant` | P1-C1에서 색만 교체: `terracotta orange hard hat, sage green overalls` |

- 캐릭터는 3D 프리픽스 대신 `Stylized game character, simple clean shapes, matte flat colors, clear color blocking.`를 쓴다(하드서피스 단어 제외).
- 자동 리그(Tripo/Meshy) → Unity Humanoid 매핑 검증. 실패하면 Mixamo.

**스테이션·기계**
| ID | 이름 | 치수 | 대상 문장 |
|---|---|---|---|
| P1-M1 | `MACH_RobotArm` | 1.0×1.0×2.2 | `an industrial six-axis robot arm on a round floor base, chunky rounded segments, mustard yellow body, slate grey joints, simple thick two-finger gripper` |
| P1-M2 | `MACH_CarLift` | 4.5×2.5×0.4 | `a low car lift platform with two thick runway ramps, sage green, slate grey bolted base plates` |
| P1-M3 | `MACH_PaintBooth` | 6×4×3.5 | `a boxy car paint booth building, slate blue-grey wall panels with seams, cream framed small windows, large open doorway on both short ends, two roof vent boxes` |
| P1-M4 | `MACH_InputTunnel` | 4×4×3 | `a car entry tunnel gate, thick arched frame, mustard yellow and slate grey panels, large open passage` |
| P1-M5 | `MACH_WaterTestTank` | 6×3×1.5 | `an open rectangular water test pool tank, thick sage green steel walls with rivets, cream top rim, empty inside` |
| P1-M6 | `MACH_ShakeTester` | 4.5×2.5×0.6 | `a vehicle test platform with four thick wheel roller pairs, slate grey base, mustard yellow safety edges` |
| P1-M7 | `MACH_FuelStation` | 1.0×0.6×1.8 | `a compact fuel dispenser box, cream body, sage green front panel, one thick nozzle holstered on the side` |
| P1-M8 | `MACH_DepartureGate` | 5×1×4 | `a thick steel gate frame for cars to drive through, slate blue posts, mustard yellow top beam, one round status lamp on top` |
| P1-M9 | `MACH_Hanger` | 4.5×2×2 | `an overhead car carrier hanger, thick C-shaped hook frame with a flat bottom cradle, slate grey steel, mustard yellow accents` |
| P1-M10 | `MACH_WeldingStation` | 1.5×1.5×2 | `a boxy welding machine cabinet with a thick swing arm, sage green, small control panel with big round buttons` |

- 대형 구조물(M3, M4, M8)은 내부가 비어 있어야 하는데, AI가 막힌 덩어리로 만들 수 있다. 통과 못 하면 ProBuilder로 만들고 AI 결과는 외장 디테일 참고용으로만 쓴다.

**차량 부품**
- 차체(`CAR_Body`)는 **앵커로 한 번만 생성**한다. 생성된 차체는 닫힌 껍데기라 내부(시트·엔진)가 없어 AI가 부품을 '추출'할 수 없다. 외장 패널(Frame / Door×4 / Hood / Trunk / Bumper)은 Blender에서 차체를 분리한다(개정 3). 이름은 기존 `CarController` 부품 이름과 1:1로 맞춘다.
  `a compact four-door sedan car body, slightly short and tall friendly proportions, large wheel arches, clearly visible panel gaps between doors hood and trunk, no wheels, uniform neutral light grey`
- 내부·독립 부품(Seat, Engine, Wheel, 램프, 미러)은 **확정된 차량 컨셉 이미지를 스타일 참조로 넣어** 이미지 생성 AI로 부품 컨셉을 만든 뒤 image-to-3D로 생성하고, 크기는 Blender/Unity에서 차체에 맞춘다(개정 3). 일부 서비스의 파트 분할 기능은 시도해볼 수 있으나 품질 미검증이라 기본 경로가 아니다. 도장 대상이 아닌 부품만 색을 지정한다.

| ID | 이름 | 대상 문장 |
|---|---|---|
| P1-P1 | `PART_Wheel` | `a car wheel with chunky rounded tire in dark warm grey and a simple cream five-spoke rim` |
| P1-P2 | `PART_Seat` | `a single car front seat with soft rounded cushions, slate blue fabric, cream piping` |
| P1-P3 | `PART_Engine` | `a compact car engine block, boxy, sage green and slate grey, a few thick pipes, cream filler cap` |
| P1-P4 | `PART_Headlight` | `a car headlight unit, cream housing, soft pale yellow lens` |
| P1-P5 | `PART_Taillight` | `a car tail light unit, cream housing, pale coral lens` |
| P1-P6 | `PART_SideMirror` | `a car side mirror, chunky rounded housing, uniform neutral light grey` |

- 도장 대상(차체 패널, 미러)은 **중성 연회색 단색**으로 받고, 도색은 엔진에서 틴트한다.
- 창유리는 생성하지 않는다. Blender에서 평면으로 만들고 반투명 머티리얼을 쓴다.

### P2 — 분위기·확장
| ID | 이름 | 대상 문장 |
|---|---|---|
| P2-01 | `PROP_Locker` | `a row of three staff lockers, sage green doors with modeled vent slots, blank cream name plates` |
| P2-02 | `PROP_BreakBench` | `a simple break room table with two benches, thick warm wood tops, slate grey legs` |
| P2-03 | `PROP_VendingMachine` | `a boxy retro vending machine, cream body, mustard yellow trim, blank front panel, no logos` |
| P2-04 | `PROP_TimeClock` | `a wall-mounted punch time clock box with a thick card holder, cream and slate grey` |
| P2-05 | `PROP_Barrel` | `an industrial steel drum barrel, slate blue with one cream band, closed lid` |
| P2-06 | `PROP_FireExtinguisher` | `a fire extinguisher, muted brick red body, cream label area, thick short hose` (빨강 예약 규칙: 채도 낮게) |
| P2-07 | `PROP_WallFan` | `a large wall ventilation fan, thick sage green round frame, solid blades behind a simple thick grille` |
| P2-08 | `PROP_PottedPlant` | `a potted plant with round blobby leaf clusters, terracotta pot` (식물은 하드서피스 프리픽스 대신 `Stylized game prop, simple rounded shapes, matte flat colors.`) |
| P2-09 | `PROP_Forklift` | `a small friendly forklift, mustard yellow rounded body, slate grey thick forks, open cab with roof` (보류 기능, 후순위) |
| P2-10 | `PROP_Toolbox` | `a chunky metal toolbox with a thick handle, terracotta orange, slate grey latches` |

제외한 항목
- 케이블 트레이, 클립보드, 렌치: 형태가 단순하거나 얇아서 Blender나 ProBuilder로 만드는 편이 빠르고 품질이 좋다.

---

## 4. 생성 순서 요약
1. **앵커 2종**(`CHAR_Worker`, 단순 `CAR_Body` 1대) 컨셉 → 검수 → 3D → Unity **임시 확인 환경**에서 확인 → 스타일 앵커 확정
   - 셰이더는 마지막 단계가 아니다. 생성 결과를 Unity 기본 Lit으로 보면 실제 분위기와 크게 다르다. Flat Kit은 할인 시 구매로 보류했으므로 그 전까지는 임시 확인 환경으로 판단한다: URP Lit 머티리얼 Metallic 0 / Smoothness 0(무광) + 따뜻한 조명 + 볼륨(채도 소폭 감소, 화이트밸런스 따뜻하게). 선·단계 셰이딩은 구매 후 룩데브에서 본다.
2. **차량 부품 파생**(§3 차량 부품, 개정 3): 외장 패널은 차체에서 Blender 분리, 내부·독립 부품은 확정 차량 컨셉 이미지를 스타일 참조로 생성
3. 앵커를 참조해 P0 소품 10종(분위기 밀도 확인용, AI 성공률이 높아 빠르다)
4. 룩데브(B) → 문제가 있으면 프리픽스 개정
5. 나머지 P1 → P2
- P0/P1/P2는 **게임에 필요한 순서(중요도)**이고, 생성 순서는 위 1~5를 따른다.

---

## 5. 검수 체크리스트

**컨셉 이미지**
- [ ] 오브젝트 1개, 잘린 곳 없음, 배경 단색
- [ ] 선(라인아트)과 강한 음영, 그림자가 없다
- [ ] 얇게 떠 있는 부분(호스, 체인, 와이어)이 없다
- [ ] 스타일 앵커와 나란히 놓았을 때 같은 세트로 보인다

**3D 결과**
- [ ] 면이 평평하고 모서리가 곧다(녹아내린 형태 아님)
- [ ] 볼트와 이음선이 형상으로 있다(텍스처에 그려진 것 아님)
- [ ] 텍스처에 구워진 그림자나 하이라이트가 없다
- [ ] **뒷면과 바닥**이 엉망이 아니다(생성기가 지어내는 부분)
- [ ] 바닥판이 붙지 않았다
- [ ] 팔레트 밖의 색(고채도 빨강, 네온)이 없다
- [ ] 폴리곤 수가 `ART_DIRECTION.md` §5-2 규격 이내다(초과하면 리메시)
- [ ] Unity import 후 피벗과 스케일을 확인하고, 기존 에셋 옆에서 같은 게임처럼 보인다
