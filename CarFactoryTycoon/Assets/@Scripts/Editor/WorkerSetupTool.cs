using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 씬에 배치한 휴머노이드 모델(예: RobotKyle)을 사람 작업자로 한 번에 세팅하는 에디터 툴.
///
/// 기존 작업자는 "빈 GameObject + 기본 Capsule 자식 / BoxCollider size(1,2,1)" 가이드 셋업 기준으로
/// 값이 잡혀 있다(handForward 0.6 / handHeight 0.15 / workStandDistance 0.9). 실제 캐릭터 모델은
/// 신장·몸 두께가 다르므로 그대로 쓰면 부품이 몸에 파묻히거나 머리 위에 뜬다.
/// 이 툴은 <b>모델의 실제 크기를 본(Humanoid)과 메시에서 교차 실측</b>해 그 값들을 모델에 맞게 다시 잡는다.
///
/// ★ 크기 실측이 왜 까다로운가 (예전 버전이 콜라이더를 바닥에 깔아버린 이유)
///   SkinnedMeshRenderer.localBounds / sharedMesh.bounds 는 <b>렌더러 Transform 기준이 아니라
///   루트 본(rootBone) 기준 좌표</b>다. 이걸 렌더러 Transform 로컬로 착각해서 변환하면
///   (RobotKyle처럼 메시 오브젝트가 원점·무회전이면) 바운즈가 그대로 발밑 원점에 놓이고,
///   게다가 Hips 본이 90° 회전해 있어 축이 뒤바뀐 채로 들어온다.
///   → 실제로 RobotKyle에 size(1.89, 1.20, 0.39) / center(0.06, 0.00, -0.02) 가 찍혔다.
///     y 1.2는 신장이 아니라 어깨너비, x 1.89가 진짜 신장이었고 center.y≈0이라 박스가 바닥에 깔렸다.
///   이 툴은 이제 (a) 본 기준 사람 형상 박스를 만들고 (b) 메시 바운즈를 rootBone 행렬로 올바르게
///   변환해 교차검증한 뒤, 둘이 크게 어긋나면 본 기준값을 쓴다.
///
/// 하는 일:
///   1) Animator + isHuman 검증 (Humanoid 리그가 아니면 중단 — 손 본을 못 찾는다)
///   2) Worker / WorkerStatusUI 부착 (이미 있으면 유지하고 값만 보정)
///   3) 오른손 본 아래에 HandPos 빈 자식을 만들어 Worker.handPos에 연결
///   4) 클릭용 BoxCollider를 본+메시 교차실측으로 몸통에 맞춤 (이미 있으면 무조건 덮어씀)
///   5) 신장 기반으로 handHeight / handForward / workStandDistance / WorkerStatusUI.height 보정
///   6) 이동을 방해하는 컴포넌트(ThirdPersonController 등)·부모·레이어 문제를 경고
///
/// 사용법:
///   Tools ▸ CarFactory ▸ 휴머노이드 작업자 세팅 → 하이러키에서 휴머노이드 루트를 선택하고 실행.
///   (여러 개를 한 번에 선택해도 각각 처리된다. 전부 Undo 한 번으로 되돌릴 수 있다)
///   경고가 뜬 이동 충돌 컴포넌트는
///   Tools ▸ CarFactory ▸ 작업자 이동 충돌 컴포넌트 제거 로 지울 수 있다(Undo 가능).
///
/// 범위 밖: 애니메이션 상태 연동(걷기/작업 모션)은 이 툴이 건드리지 않는다.
/// </summary>
public static class WorkerSetupTool
{
    private const string MENU_PATH = "Tools/CarFactory/휴머노이드 작업자 세팅";
    private const string MENU_STRIP_PATH = "Tools/CarFactory/작업자 이동 충돌 컴포넌트 제거";
    private const string HAND_POS_NAME = "HandPos";
    private const string LOG_TAG = "[작업자 세팅]";

    /// <summary>기준 셋업(2m 캡슐 몸통)의 신장. 실측 신장과의 비율로 크기 의존 필드를 스케일한다.</summary>
    private const float REFERENCE_HEIGHT = 2f;

    /// <summary>손에 든 부품이 손바닥 안쪽에 파묻히지 않도록 손 본에서 몸 앞쪽으로 밀어내는 거리(m).</summary>
    private const float HAND_FORWARD_PAD = 0.05f;

    /// <summary>발목 본에서 발바닥까지 내려갈 여유(m). 발 본은 바닥이 아니라 복사뼈 높이에 있다.</summary>
    private const float SOLE_PAD = 0.06f;

    /// <summary>머리 본(두개골 밑동)에서 정수리까지의 높이를 (머리높이-엉덩이높이) 대비 비율로 추정.</summary>
    private const float SKULL_RATIO = 0.45f;

    /// <summary>클릭 박스의 가로/세로 반두께 상한(신장 대비). T포즈로 벌린 팔까지 감싸지 않도록 조인다.</summary>
    private const float HALF_WIDTH_MAX_RATIO = 0.22f;

    /// <summary>메시 실측이 본 실측과 이 배율 범위를 벗어나면 신뢰하지 않고 본 기준값을 쓴다.</summary>
    private const float MESH_TRUST_MIN = 0.7f;
    private const float MESH_TRUST_MAX = 1.4f;

    /// <summary>
    /// Worker가 직접 Transform을 움직이므로, 같은 오브젝트를 스스로 움직이거나 물리로 미는
    /// 컴포넌트가 붙어 있으면 서로 싸워서 떨림이 생긴다. 타입 이름으로 찾는다
    /// (StarterAssets 어셈블리에 컴파일 의존성을 만들지 않기 위해).
    /// </summary>
    private static readonly string[] CONFLICT_TYPE_NAMES =
    {
        "ThirdPersonController",
        "StarterAssetsInputs",
        "PlayerInput",
        "BasicRigidBodyPush",
        "NavMeshAgent",
        "CharacterController",
        "Rigidbody",
    };

    [MenuItem(MENU_PATH, true)]
    private static bool ValidateSetup() => Selection.gameObjects != null && Selection.gameObjects.Length > 0;

    [MenuItem(MENU_PATH)]
    private static void Setup()
    {
        GameObject[] targets = Selection.gameObjects;
        if (targets == null || targets.Length == 0)
        {
            Debug.LogError($"{LOG_TAG} 하이러키에서 휴머노이드 루트를 선택한 뒤 실행하세요.");
            return;
        }

        int ok = 0;
        foreach (GameObject go in targets)
        {
            if (!SetupOne(go)) continue;
            ok++;
            EditorSceneManager.MarkSceneDirty(go.scene);
        }

        Debug.Log($"{LOG_TAG} {ok}/{targets.Length}개 세팅 완료. (Ctrl+Z로 되돌릴 수 있습니다)");
    }

    private static bool SetupOne(GameObject go)
    {
        // 프로젝트 창에서 고른 프리팹 에셋은 건드리지 않는다 — 씬 인스턴스만 대상.
        if (!go.scene.IsValid())
        {
            Debug.LogError($"{LOG_TAG} '{go.name}': 프로젝트의 프리팹 에셋입니다." +
                " 씬에 배치한 인스턴스를 하이러키에서 선택해 실행하세요(프리팹 에셋은 수정하지 않습니다).", go);
            return false;
        }

        // ── 1. Humanoid 검증 ──────────────────────────────────────────────
        // Animator는 자식(모델 루트)에 붙어 있는 경우가 흔하므로 자식까지 훑는다.
        Animator animator = go.GetComponentInChildren<Animator>();
        if (animator == null)
        {
            Debug.LogError($"{LOG_TAG} '{go.name}': Animator가 없습니다. " +
                "휴머노이드 프리팹(예: RobotKyle)을 선택했는지 확인하세요.", go);
            return false;
        }
        if (animator.avatar == null || !animator.avatar.isValid || !animator.isHuman)
        {
            Debug.LogError($"{LOG_TAG} '{go.name}': Humanoid 리그가 아닙니다" +
                $" (avatar={(animator.avatar != null ? animator.avatar.name : "없음")})." +
                " 모델 FBX의 Rig ▸ Animation Type을 Humanoid로 바꾸고 Apply 하세요.", go);
            return false;
        }

        Transform hand = animator.GetBoneTransform(HumanBodyBones.RightHand);
        if (hand == null)
        {
            Debug.LogError($"{LOG_TAG} '{go.name}': 오른손 본(RightHand)을 Avatar에서 찾지 못했습니다." +
                " Avatar Configuration에서 RightHand 매핑을 확인하세요.", go);
            return false;
        }

        var report = new StringBuilder();
        report.AppendLine($"{LOG_TAG} '{go.name}' 세팅 결과");

        // 프리팹 인스턴스면 변경분이 전부 인스턴스 오버라이드로 남는다(프리팹 에셋은 건드리지 않는다).
        bool isPrefabInstance = PrefabUtility.IsPartOfPrefabInstance(go);

        // ── 2. 컴포넌트 부착 ─────────────────────────────────────────────
        if (!go.TryGetComponent(out Worker worker))
        {
            worker = Undo.AddComponent<Worker>(go);
            report.AppendLine("· Worker 컴포넌트 추가");
        }
        else report.AppendLine("· Worker 컴포넌트 기존 것 유지 (값만 보정)");

        if (!go.TryGetComponent(out WorkerStatusUI statusUI))
        {
            statusUI = Undo.AddComponent<WorkerStatusUI>(go);
            report.AppendLine("· WorkerStatusUI 컴포넌트 추가");
        }

        // ── 3. 크기 실측 (본 기준 + 메시 교차검증) ───────────────────────
        if (!TryMeasureBody(go.transform, animator, out Bounds body, out string measureLog))
        {
            Debug.LogError($"{LOG_TAG} '{go.name}': 크기를 잴 수 없습니다(본/렌더러 모두 실패).", go);
            return false;
        }
        report.Append(measureLog);

        float height = body.max.y - Mathf.Min(0f, body.min.y); // 바닥(루트 피벗 y=0)부터 정수리까지
        float halfDepth = Mathf.Max(body.extents.x, body.extents.z);
        float scale = height / REFERENCE_HEIGHT;
        report.AppendLine($"· 최종 신장 {height:F3}m / 몸통 반두께 {halfDepth:F3}m (기준 2m 대비 {scale:P0})");

        // ── 4. 클릭용 BoxCollider ────────────────────────────────────────
        // 이미 붙어 있어도 center/size를 무조건 실측값으로 덮어쓴다(예전 잘못된 값 교정).
        if (!go.TryGetComponent(out BoxCollider box))
        {
            box = Undo.AddComponent<BoxCollider>(go);
            report.AppendLine("· 클릭용 BoxCollider 추가");
        }
        else report.AppendLine("· 기존 BoxCollider 값 덮어쓰기");

        Undo.RecordObject(box, "Setup Worker Collider");
        Vector3 beforeCenter = box.center, beforeSize = box.size;
        box.center = body.center;
        box.size = body.size;
        // 레이어 8(Robot)은 충돌 매트릭스상 Car와 물리 충돌한다 → 솔리드면 작업자가 차를 밀어버린다.
        // Physics.Raycast는 트리거 설정과 무관하게(Queries Hit Triggers 기본 on) 잡히므로 클릭은 그대로 된다.
        box.isTrigger = true;
        EditorUtility.SetDirty(box);
        report.AppendLine($"· BoxCollider center {Fmt(beforeCenter)}→{Fmt(box.center)}" +
            $" / size {Fmt(beforeSize)}→{Fmt(box.size)} / isTrigger=true");
        Debug.Log($"{LOG_TAG} '{go.name}' 콜라이더 실측: 신장 {height:F3}m," +
            $" center {Fmt(box.center)}, size {Fmt(box.size)}" +
            $" (루트 로컬 y {body.min.y:F3} ~ {body.max.y:F3})", go);

        // 자식에 남아 있는 콜라이더는 클릭 레이캐스트를 가로챌 수 있다.
        int disabled = DisableChildColliders(go, box);
        if (disabled > 0)
            report.AppendLine($"· 자식 콜라이더 {disabled}개 비활성화 (클릭·물리 간섭 방지)");

        // ── 5. 손 본 연결 ────────────────────────────────────────────────
        // 손 본에 직접 꽂지 않고 HandPos 빈 자식을 만들어 꽂는다:
        // 손 본의 로컬 축은 모델마다 제각각이라 부품이 뒤집혀 보이는데, 그걸 carryEuler로 매번 맞추는 대신
        // 여기서 한 번 "몸통 정면 기준"으로 회전을 구워 두면 carryOffset/carryEuler를 0으로 시작할 수 있다.
        Transform handPos = hand.Find(HAND_POS_NAME);
        if (handPos == null)
        {
            var handGo = new GameObject(HAND_POS_NAME);
            Undo.RegisterCreatedObjectUndo(handGo, "Create HandPos");
            Undo.SetTransformParent(handGo.transform, hand, "Parent HandPos");
            handPos = handGo.transform;
            report.AppendLine("· 오른손 본 아래 HandPos 생성");
        }
        else report.AppendLine("· 기존 HandPos 재사용 (중복 생성 안 함)");

        Undo.RecordObject(handPos, "Setup HandPos");
        handPos.rotation = go.transform.rotation;
        handPos.position = hand.position + go.transform.forward * HAND_FORWARD_PAD;
        handPos.localScale = Vector3.one;
        EditorUtility.SetDirty(handPos);

        // ── 6. Worker 값 보정 ────────────────────────────────────────────
        Undo.RecordObject(worker, "Setup Worker");

        if (worker.handPos != handPos)
        {
            worker.handPos = handPos;
            report.AppendLine("· Worker.handPos ← HandPos 연결");
        }

        // handPos를 꽂으면 handForward/handHeight는 폴백 값이지만, 나중에 비웠을 때 곧바로
        // 이 모델에 맞는 위치가 나오도록 실측값으로 갱신해 둔다.
        // 손 높이는 바운즈가 아니라 실제 손 본 높이(루트 피벗 기준)라 예전 버그와 무관하지만,
        // 앞쪽 거리(handForward)는 잘못된 바운즈의 반두께를 쓰고 있었으므로 함께 교정된다.
        float newHandHeight = go.transform.InverseTransformPoint(hand.position).y;
        float newHandForward = halfDepth + HAND_FORWARD_PAD * 2f;
        LogChange(report, "handForward", worker.handForward, newHandForward);
        LogChange(report, "handHeight", worker.handHeight, newHandHeight);
        worker.handForward = newHandForward;
        worker.handHeight = newHandHeight;

        // workStandDistance: 작업자가 부착점에서 물러서는 거리. 기준 0.9(2m 몸통)를 신장 비율로 스케일하되,
        // ★ Worker.GetAttachWorldPos 주석의 진동 버그 조건(workStandDistance - handForward <= arriveRadius)에
        //   걸리지 않도록 handForward + arriveRadius보다 충분히 크게 하한을 둔다.
        float scaled = 0.9f * scale;
        float minStand = worker.handForward + worker.arriveRadius + 0.1f;
        float newStand = Mathf.Max(scaled, minStand);
        LogChange(report, "workStandDistance", worker.workStandDistance, newStand);
        worker.workStandDistance = newStand;
        if (newStand > scaled + 0.001f)
            report.AppendLine($"  ↳ 비율 계산값 {scaled:F2}이 하한 {minStand:F2}보다 작아 하한을 적용" +
                " (작업 위치 진동 버그 방지)");

        EditorUtility.SetDirty(worker);

        // ── 7. 상태 표시 높이 ────────────────────────────────────────────
        // 정수리(루트 로컬 y) + 여유. size.y가 아니라 박스 윗면 높이를 써야 머리 위에 정확히 뜬다.
        float newLabelHeight = body.max.y + 0.35f;
        Undo.RecordObject(statusUI, "Setup WorkerStatusUI");
        LogChange(report, "WorkerStatusUI.height", statusUI.height, newLabelHeight);
        statusUI.height = newLabelHeight;
        EditorUtility.SetDirty(statusUI);

        // ── 8. 환경 경고 ─────────────────────────────────────────────────
        WarnEnvironment(go, report);

        if (isPrefabInstance)
            report.AppendLine("· ⚠ 프리팹 인스턴스입니다 — 위 변경은 모두 이 인스턴스의 오버라이드로 남습니다" +
                " (프리팹 에셋은 수정하지 않았습니다). 모든 인스턴스에 적용하려면 인스펙터에서 Overrides ▸ Apply All.");

        Debug.Log(report.ToString(), go);
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────
    // 크기 실측
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 작업자 루트 로컬 공간에서 몸통 박스를 구한다.
    /// 1차: Humanoid 본 위치(발~머리)로 사람 형상 박스 — 리그가 보장되므로 가장 견고하다.
    /// 2차: 메시 바운즈를 <b>rootBone 행렬</b>로 올바르게 변환해 교차검증하고, 신뢰 범위 안이면
    ///      위/아래 끝을 메시값으로 다듬는다(머리카락·헬멧 등 본이 모르는 실루엣 반영).
    /// 가로/세로 두께는 T포즈로 벌린 팔까지 감싸지 않도록 신장 대비 상한으로 조인다.
    /// </summary>
    private static bool TryMeasureBody(Transform root, Animator animator, out Bounds body, out string log)
    {
        var sb = new StringBuilder();
        body = default;

        // ── 본 기준 ──
        Transform hips = animator.GetBoneTransform(HumanBodyBones.Hips);
        Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
        if (hips == null || head == null)
        {
            log = sb.ToString();
            Debug.LogError($"{LOG_TAG} '{root.name}': Hips/Head 본을 찾지 못했습니다.", root);
            return false;
        }

        Vector3 hipsL = root.InverseTransformPoint(hips.position);
        Vector3 headL = root.InverseTransformPoint(head.position);

        float footY = float.MaxValue;
        foreach (HumanBodyBones b in new[]
                 {
                     HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot,
                     HumanBodyBones.LeftToes, HumanBodyBones.RightToes,
                 })
        {
            Transform t = animator.GetBoneTransform(b);
            if (t == null) continue;
            footY = Mathf.Min(footY, root.InverseTransformPoint(t.position).y);
        }
        if (footY == float.MaxValue) footY = 0f; // 발 본이 없으면 루트 피벗을 바닥으로 본다
        float boneBottom = Mathf.Min(0f, footY - SOLE_PAD);
        float boneTop = headL.y + Mathf.Max(0.05f, (headL.y - hipsL.y) * SKULL_RATIO);
        float boneHeight = boneTop - boneBottom;

        sb.AppendLine($"· 본 실측: 발 y {footY:F3} / 엉덩이 y {hipsL.y:F3} / 머리본 y {headL.y:F3}" +
            $" → 바닥 {boneBottom:F3} ~ 정수리(추정) {boneTop:F3} = 신장 {boneHeight:F3}m");

        // ── 메시 기준(교차검증) ──
        float bottom = boneBottom, top = boneTop;
        float halfX, halfZ;
        if (TryMeasureMeshBounds(root, out Bounds mesh))
        {
            float meshHeight = mesh.max.y - Mathf.Min(0f, mesh.min.y);
            float ratio = boneHeight > 0.0001f ? meshHeight / boneHeight : 0f;
            sb.AppendLine($"· 메시 실측(rootBone 기준 변환): y {mesh.min.y:F3} ~ {mesh.max.y:F3}" +
                $" = {meshHeight:F3}m, 가로 {mesh.size.x:F3} 세로 {mesh.size.z:F3} (본 대비 {ratio:P0})");

            if (ratio >= MESH_TRUST_MIN && ratio <= MESH_TRUST_MAX)
            {
                bottom = Mathf.Min(boneBottom, mesh.min.y);
                top = Mathf.Max(boneTop, mesh.max.y);
                halfX = mesh.extents.x;
                halfZ = mesh.extents.z;
                sb.AppendLine("  ↳ 메시값이 본 실측과 일치 → 메시로 위/아래 끝을 다듬어 사용");
            }
            else
            {
                halfX = boneHeight * 0.16f;
                halfZ = boneHeight * 0.12f;
                sb.AppendLine("  ↳ ⚠ 메시값이 본 실측과 크게 어긋남(rootBone 회전/축 문제 가능)" +
                    " → 본 기준값으로 폴백");
            }
        }
        else
        {
            halfX = boneHeight * 0.16f;
            halfZ = boneHeight * 0.12f;
            sb.AppendLine("· 렌더러를 찾지 못해 메시 교차검증 생략 → 본 기준값만 사용");
        }

        float height = top - bottom;
        float maxHalf = height * HALF_WIDTH_MAX_RATIO;
        float clampedX = Mathf.Clamp(halfX, height * 0.08f, maxHalf);
        float clampedZ = Mathf.Clamp(halfZ, height * 0.06f, maxHalf);
        if (!Mathf.Approximately(clampedX, halfX) || !Mathf.Approximately(clampedZ, halfZ))
            sb.AppendLine($"  ↳ 가로/세로 반두께 {halfX:F3}/{halfZ:F3} → {clampedX:F3}/{clampedZ:F3}" +
                $" 로 조정 (T포즈로 벌린 팔 제외, 상한 신장×{HALF_WIDTH_MAX_RATIO:F2})");

        // 가로 중심은 몸 중심(엉덩이 본)에 맞춘다 — 팔이 한쪽으로 치우쳐도 박스가 쏠리지 않는다.
        var center = new Vector3(hipsL.x, (top + bottom) * 0.5f, hipsL.z);
        body = new Bounds(center, new Vector3(clampedX * 2f, height, clampedZ * 2f));

        log = sb.ToString();
        return height > 0.01f;
    }

    /// <summary>
    /// 루트 아래 모든 렌더러의 바운즈를 <b>루트 로컬 공간</b>으로 모아 합친다.
    ///
    /// ★ 핵심: SkinnedMeshRenderer의 바운즈(sharedMesh.bounds == localBounds)는
    ///   렌더러 Transform이 아니라 <b>rootBone</b> 기준 좌표다(Unity가 월드 바운즈를 만들 때
    ///   rootBone.localToWorldMatrix를 곱한다). 그래서 여기서도 rootBone 행렬로 변환한다.
    ///   rootBone이 비어 있을 때만 렌더러 Transform으로 폴백한다.
    ///   또 bounds는 바인드 포즈 기준이라 rootBone(보통 Hips)의 로컬 축이 회전해 있으면
    ///   x/y/z가 뒤바뀐 것처럼 보인다 — 8코너를 전부 변환하므로 회전은 여기서 흡수된다.
    /// </summary>
    private static bool TryMeasureMeshBounds(Transform root, out Bounds local)
    {
        local = default;
        bool has = false;
        Matrix4x4 toLocal = root.worldToLocalMatrix;

        foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
        {
            Bounds b;
            Transform basis;
            if (r is SkinnedMeshRenderer smr)
            {
                if (smr.sharedMesh == null) continue;
                b = smr.sharedMesh.bounds;
                basis = smr.rootBone != null ? smr.rootBone : smr.transform;
            }
            else if (r is MeshRenderer && r.TryGetComponent(out MeshFilter mf) && mf.sharedMesh != null)
            {
                b = mf.sharedMesh.bounds;
                basis = r.transform;
            }
            else continue; // 파티클·라인 등은 몸 크기와 무관

            Matrix4x4 m = toLocal * basis.localToWorldMatrix;
            foreach (Vector3 corner in Corners(b))
            {
                Vector3 p = m.MultiplyPoint3x4(corner);
                if (!has) { local = new Bounds(p, Vector3.zero); has = true; }
                else local.Encapsulate(p);
            }
        }
        return has;
    }

    private static IEnumerable<Vector3> Corners(Bounds b)
    {
        Vector3 c = b.center, e = b.extents;
        for (int i = 0; i < 8; i++)
            yield return c + new Vector3(
                (i & 1) == 0 ? -e.x : e.x,
                (i & 2) == 0 ? -e.y : e.y,
                (i & 4) == 0 ? -e.z : e.z);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 환경 경고
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>부모·레이어·충돌 컴포넌트를 점검하고 경고만 남긴다(자동 제거는 하지 않는다).</summary>
    private static void WarnEnvironment(GameObject go, StringBuilder report)
    {
        // 부모가 있으면 부모가 움직일 때 작업자가 끌려간다(Worker는 자기 Transform을 월드 기준으로 옮긴다).
        if (go.transform.parent != null)
        {
            string path = go.transform.parent.name;
            report.AppendLine($"· ⚠ 부모 '{path}' 아래에 있습니다. 부모가 움직이면 작업자가 끌려다니고," +
                " 작업 위치 계산이 어긋날 수 있습니다. 하이러키 최상위로 빼는 것을 권장합니다.");
            Debug.LogWarning($"{LOG_TAG} '{go.name}': 부모 '{path}' 아래에 배치되어 있습니다." +
                " 작업자는 스스로 월드 좌표로 이동하므로 최상위 배치를 권장합니다.", go);
        }

        // 레이어 8(Robot)은 Car와 물리 충돌한다. 콜라이더를 트리거로 만들어 두었으므로 밀리지는 않지만
        // 트리거 이벤트는 계속 발생하므로 알려 준다.
        if (go.layer == 8)
            report.AppendLine("· ℹ 레이어 8(Robot)입니다 — 충돌 매트릭스상 Car와 충돌합니다." +
                " BoxCollider를 isTrigger로 만들어 차량을 밀지 않도록 했습니다" +
                " (Physics.Raycast 클릭은 그대로 동작).");

        List<Component> conflicts = FindConflictComponents(go);
        if (conflicts.Count == 0) return;

        var names = new StringBuilder();
        foreach (Component c in conflicts) names.Append(c.GetType().Name).Append(", ");
        string list = names.ToString().TrimEnd(',', ' ');

        report.AppendLine($"· ⚠ 이동을 방해하는 컴포넌트 발견: {list}");
        report.AppendLine("  ↳ Worker가 Transform을 직접 움직이는데 이들이 같은 오브젝트를 따로 움직여" +
            $" 떨림이 생깁니다. 메뉴 '{MENU_STRIP_PATH}'로 제거할 수 있습니다(Undo 가능).");
        Debug.LogWarning($"{LOG_TAG} '{go.name}': 이동 충돌 컴포넌트 {conflicts.Count}개 — {list}." +
            $" 자동 제거하지 않았습니다. 제거하려면 메뉴 ▸ {MENU_STRIP_PATH} 를 실행하세요.", go);
    }

    private static List<Component> FindConflictComponents(GameObject go)
    {
        var found = new List<Component>();
        foreach (Component c in go.GetComponents<Component>())
        {
            if (c == null) continue; // 스크립트 미싱
            string n = c.GetType().Name;
            foreach (string bad in CONFLICT_TYPE_NAMES)
            {
                if (n != bad) continue;
                found.Add(c);
                break;
            }
        }
        return found;
    }

    [MenuItem(MENU_STRIP_PATH, true)]
    private static bool ValidateStrip() => Selection.gameObjects != null && Selection.gameObjects.Length > 0;

    /// <summary>
    /// 선택한 작업자에서 이동 충돌 컴포넌트를 제거한다. 파괴적인 동작이라 별도 메뉴로 분리했고,
    /// Undo.DestroyObjectImmediate를 쓰므로 Ctrl+Z로 전부 되돌릴 수 있다.
    /// </summary>
    [MenuItem(MENU_STRIP_PATH)]
    private static void StripConflicts()
    {
        int total = 0;
        foreach (GameObject go in Selection.gameObjects)
        {
            if (!go.scene.IsValid())
            {
                Debug.LogError($"{LOG_TAG} '{go.name}': 프리팹 에셋은 건드리지 않습니다." +
                    " 씬 인스턴스를 선택하세요.", go);
                continue;
            }

            List<Component> conflicts = FindConflictComponents(go);
            if (conflicts.Count == 0)
            {
                Debug.Log($"{LOG_TAG} '{go.name}': 제거할 이동 충돌 컴포넌트가 없습니다.", go);
                continue;
            }

            // 의존 관계(예: ThirdPersonController → CharacterController) 때문에
            // CharacterController/Rigidbody는 마지막에 지운다.
            conflicts.Sort((a, b) => Rank(a).CompareTo(Rank(b)));
            foreach (Component c in conflicts)
            {
                string n = c.GetType().Name;
                Undo.DestroyObjectImmediate(c);
                Debug.LogWarning($"{LOG_TAG} '{go.name}': '{n}' 제거함 (Ctrl+Z로 되돌릴 수 있습니다).", go);
                total++;
            }
            EditorSceneManager.MarkSceneDirty(go.scene);
        }
        Debug.Log($"{LOG_TAG} 이동 충돌 컴포넌트 {total}개 제거 완료.");
    }

    private static int Rank(Component c)
    {
        string n = c.GetType().Name;
        if (n == "CharacterController" || n == "Rigidbody" || n == "NavMeshAgent") return 1;
        return 0;
    }

    /// <summary>루트 BoxCollider 외의 자식 콜라이더를 끈다. 끈 개수를 반환.</summary>
    private static int DisableChildColliders(GameObject root, Collider keep)
    {
        int count = 0;
        foreach (Collider c in root.GetComponentsInChildren<Collider>(true))
        {
            if (c == keep || c.gameObject == root || !c.enabled) continue;
            Undo.RecordObject(c, "Disable Child Collider");
            c.enabled = false;
            EditorUtility.SetDirty(c);
            Debug.LogWarning($"{LOG_TAG} '{root.name}': 자식 콜라이더 '{c.name}'({c.GetType().Name})를" +
                " 비활성화했습니다. 클릭은 루트 BoxCollider 하나로 받습니다.", c);
            count++;
        }
        return count;
    }

    private static void LogChange(StringBuilder sb, string field, float before, float after)
    {
        if (Mathf.Approximately(before, after)) sb.AppendLine($"· {field} {after:F3} (변경 없음)");
        else sb.AppendLine($"· {field} {before:F3} → {after:F3}");
    }

    private static string Fmt(Vector3 v) => $"({v.x:F3}, {v.y:F3}, {v.z:F3})";
}
