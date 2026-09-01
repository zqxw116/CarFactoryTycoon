using UnityEngine;

/// <summary>
/// StarterAssets(ThirdPerson) 애니메이션 클립에 박혀 있는 AnimationEvent를 받아 주는 리시버.
///
/// ★ 왜 필요한가
///   작업자(RobotKyle)가 쓰는 클립(Walk_N / Run_N / Run_S / *_Land / Jump)에는
///   StarterAssets가 심어 둔 AnimationEvent가 그대로 남아 있다(FBX .meta의 clipAnimations 안).
///     · OnFootstep — Walk_N, Run_N, Run_S (클립당 2개)
///     · OnLand     — Walk_N_Land, Run_N_Land, Jump
///   원래 리시버인 StarterAssets.ThirdPersonController를 이 프로젝트는 쓰지 않으므로
///   (Worker가 Transform을 직접 움직인다 — WorkerSetupTool이 충돌 컴포넌트로 분류해 제거한다)
///   매 발걸음마다 "AnimationEvent 'OnFootstep' has no receiver!" 경고가 쏟아진다.
///   서드파티 클립에서 이벤트를 지우는 건 재임포트하면 되돌아오므로, 조용한 리시버를 붙이는 게 정석이다.
///
/// ★ 부착 위치
///   반드시 <b>Animator와 같은 GameObject</b>에 붙어야 한다. AnimationEvent는 Animator가 붙은
///   오브젝트의 컴포넌트에서만 함수를 찾는다(자식·부모는 보지 않는다).
///   WorkerSetupTool(Tools ▸ CarFactory ▸ 휴머노이드 작업자 세팅)이 자동으로 붙여 준다.
///
/// 기본 동작은 "아무것도 하지 않음"이다. 나중에 발소리를 넣고 싶으면
/// footstepClips / landingClip 에 AudioClip을 꽂으면 그때부터 소리가 난다(비어 있으면 무음).
/// </summary>
[DisallowMultipleComponent]
public class WorkerFootstepReceiver : MonoBehaviour
{
    [Header("발소리 (비워 두면 무음)")]
    [Tooltip("발걸음마다 랜덤으로 하나 재생. 비어 있으면 소리 없이 이벤트만 소화한다.")]
    [SerializeField] private AudioClip[] footstepClips;

    [Tooltip("착지 시 재생. 비어 있으면 무음.")]
    [SerializeField] private AudioClip landingClip;

    [Range(0f, 1f)]
    [SerializeField] private float volume = 0.4f;

    /// <summary>
    /// 블렌드 트리에서 여러 클립이 동시에 재생될 때 이벤트가 중복으로 들어온다.
    /// StarterAssets 관례대로 가중치가 낮은 클립의 이벤트는 무시한다.
    /// </summary>
    private const float MIN_CLIP_WEIGHT = 0.5f;

    // ─────────────────────────────────────────────
    // AnimationEvent 콜백
    // ─────────────────────────────────────────────
    // Unity는 인자 없는 시그니처도 호출하지만, 클립에 objectReference/float 인자가 들어 있으면
    // AnimationEvent 버전이 우선 선택된다. 양쪽을 모두 두면 어떤 클립이든 안전하게 받는다.

    private void OnFootstep(AnimationEvent e)
    {
        if (e.animatorClipInfo.weight < MIN_CLIP_WEIGHT) return;
        PlayRandom(footstepClips);
    }

    private void OnFootstep() => PlayRandom(footstepClips);

    private void OnLand(AnimationEvent e)
    {
        if (e.animatorClipInfo.weight < MIN_CLIP_WEIGHT) return;
        Play(landingClip);
    }

    private void OnLand() => Play(landingClip);

    // ─────────────────────────────────────────────
    // 재생
    // ─────────────────────────────────────────────

    private void PlayRandom(AudioClip[] clips)
    {
        if (clips == null || clips.Length == 0) return;
        Play(clips[Random.Range(0, clips.Length)]);
    }

    private void Play(AudioClip clip)
    {
        if (clip == null || volume <= 0f) return;
        // 전용 AudioSource를 요구하지 않는다 — 발소리는 겹쳐 나야 자연스럽다.
        AudioSource.PlayClipAtPoint(clip, transform.position, volume);
    }
}
