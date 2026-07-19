/// <summary>
/// LineTrafficManager에 게이트로 등록될 수 있는 공정의 인터페이스.
/// WheelStation, WaterTestStation 등이 구현한다.
/// </summary>
public interface ILineGate
{
    /// <summary>이 게이트의 스플라인 진행도(0~1). 이 앞의 차량은 게이트 지점을 넘지 못한다.</summary>
    float GateProgress { get; }

    /// <summary>게이트가 현재 활성 상태인지. false면 차량이 그냥 통과한다.</summary>
    bool GateEnabled { get; }
}
