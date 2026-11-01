# tripo_gen.py 사용법 (Tripo 3D OpenAPI v3)

> 스크립트: `Tools/Tripo/tripo_gen.py` (Python 3, 표준 라이브러리만 사용)
> 이 문서는 AGENTS.md 규칙에 따라 임시 위치에 작성. 승격 시 `Tools/Tripo/README.md`로 이동 검토.
> 문서 확인일: 2026-10-28 — https://developers.tripo3d.com/en/docs

## API 키

- 환경변수 `TRIPO_API_KEY` → 없으면 `C:\Users\gksmf\.tripo\api_key.txt` (키만 한 줄).
- 스크립트는 키를 출력·저장하지 않음. `--debug` 출력에서도 Authorization 헤더는 마스킹.
- 다운로드(CDN) 요청에는 키를 붙이지 않음.

## 사용 예

```powershell
cd C:\Users\gksmf\Desktop\GitFork\CarFactoryTycoon\Tools\Tripo

# 잔액
python tripo_gen.py balance

# 작업자 멀티뷰 (left 없음) — 먼저 dry-run으로 페이로드·예상 크레딧 확인
$in = "..\..\CarFactoryTycoon\Assets\@Resource\Models\AIGen\_Reference\StyleAnchor_v1_Worker\TripoInput"
python tripo_gen.py multiview --front "$in\GPT_Front_alpha.png" --back "$in\GPT_Back_alpha.png" --right "$in\GPT_Right_alpha.png" `
  --smart-low-poly --face-limit 15000 --name Worker_v1 `
  --out "..\..\CarFactoryTycoon\Assets\@Resource\Models\AIGen\Worker_v1" `
  --convert fbx --convert-texture-format PNG --convert-texture-size 2048 --pivot-bottom --dry-run
# 확인 후 --dry-run 빼고 실행

# 단일 이미지 → 모델 (차량 부품)
python tripo_gen.py image --image door_l.png --smart-low-poly --face-limit 3000 --name Door_L --out out\parts

# 중단된 작업 재개/다운로드 (실행 시 TASK_ID가 먼저 출력됨)
python tripo_gen.py fetch --task-id task_xxx --out out\worker --name Worker_v1

# 배치 (부품 26종 등)
python tripo_gen.py batch --jobs parts.json --dry-run
```

배치 JSON (키는 CLI 옵션명의 `_` 형태, 상대경로는 JSON 파일 기준):

```json
{
  "defaults": {"smart_low_poly": true, "face_limit": 3000, "out": "out/parts", "convert": "fbx"},
  "jobs": [
    {"name": "Door_L", "image": "img/door_l.png"},
    {"name": "Worker", "front": "f.png", "back": "b.png", "right": "r.png", "face_limit": 15000}
  ]
}
```

## 주요 옵션 기본값

| 옵션 | 기본 | 비고 |
|---|---|---|
| `--model-version` | `v3.1-20260211` | H3.1 |
| `--face-limit` | 15000 | `0`이면 생략(adaptive). smart_low_poly 시 500~20000 (quad면 ~10000) |
| `--texture` / `--no-texture` | texture on | |
| `--pbr` | off | API 기본은 true라 항상 `pbr:false`를 명시 전송 |
| `--texture-quality` | (생략=standard) | fast는 `--texture-version v3.5-20260815` 필요 |
| `--params file.json` | | 추가 필드를 페이로드에 병합(덮어씀) |
| `--convert fbx` | | 생성 성공 후 `/v3/models/convert` 후처리 |
| `--poll-interval` / `--timeout` | 3s / 1200s | |

출력: `<name>_model.<ext>`, `<name>_rendered_image.<ext>`, `<name>_task.json`; convert 시 `<name>_fbx_model.fbx` 등 + `<name>_fbx_task.json`.

## 사용 엔드포인트 (Base `https://openapi.tripo3d.com/v3`, `Authorization: Bearer <key>`)

| 용도 | 엔드포인트 | 문서 |
|---|---|---|
| 잔액 | `GET /account/balance` → `data.balance`, `data.frozen` | /en/docs/billing |
| 업로드 | `POST /files` multipart 필드 `file` → `data.file_token` (PNG/JPEG ≤20MB) | /en/docs/files |
| 멀티뷰 | `POST /generation/multiview-to-model` `inputs:[{"front":tok},{"back":tok},...]` | /en/docs/generation-multiview-to-model/standard |
| 단일 이미지 | `POST /generation/image-to-model` `input: tok` | /en/docs/generation-image-to-model/standard |
| 작업 조회 | `GET /tasks/{task_id}` → `status, progress, output.model_url, output.rendered_image_url, credits_consumed, error_code, error_message` | /en/docs/task-query |
| 포맷 변환 | `POST /models/convert` `input: task_id, format: FBX, texture_size, texture_format, pivot_to_center_bottom, fbx_preset, face_limit` | /en/docs/models-convert |

생성 파라미터: `model, texture, pbr, face_limit, smart_low_poly, quad, texture_quality, texture_version, geometry_quality, texture_alignment, orientation, model_seed, texture_seed, export_orientation, auto_size, export_uv, compress` (모델 페이지 /en/models/v3-1).

상태: `queued, running, success, failed, cancelled, banned, expired` (/en/docs/task-lifecycle). 429/5xx는 지수 백오프 재시도.

## 크레딧 (H3.1, /en/models/v3-1 · /en/pricing)

- Image/Multiview: 텍스처 없음 20 / standard 30 / detailed 40 / 8K(extreme) 50
- 추가: detailed geometry +20, quad +5, smart low-poly +10, generate parts +20
- Convert Format: Basic 5 / Advanced 10 (어느 쪽인지 문서 불명) · Auto Rig 25 · Rig Check 무료 · Retarget 10/애니
- 실패·취소 작업은 과금 없음.

## 미구현 / 참고

- 리깅(`POST /v3/animations/rig`, `spec: unity|mixamo…`, `out_format: fbx`, 휴머노이드 권장 `model: v3.0-20260909`)은 문서에 있으나 이 스크립트엔 없음. 사전 `rig-check` 파라미터 확인 후 추가 예정.
