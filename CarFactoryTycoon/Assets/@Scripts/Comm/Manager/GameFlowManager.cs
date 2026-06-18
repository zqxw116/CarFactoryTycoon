using UnityEngine;

/// <summary>
/// 게임 전체 흐름/상태 관리. 진행·일시정지·피버타임 등 상태 전환의 진입점.
/// </summary>
public class GameFlowManager : MonoSingleton<GameFlowManager>
{
    public enum GameState { Ready, Running, Paused }

    [Header("게임 상태")]
    public GameState state = GameState.Running;

    /// <summary>상태 변경 시 호출(UI/연출용).</summary>
    public System.Action<GameState> OnStateChanged;

    public override void Init() { }

    public bool IsRunning => state == GameState.Running;

    public void SetState(GameState next)
    {
        if (state == next) return;
        state = next;
        OnStateChanged?.Invoke(state);
    }

    public void SetRunning() => SetState(GameState.Running);
    public void SetPaused() => SetState(GameState.Paused);
}
