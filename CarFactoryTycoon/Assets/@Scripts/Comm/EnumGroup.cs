// PartDefinitions.cs
public enum PartGroup
{
    Body, Indoor, Window, Wheel, Outdoor, Parts
}

public enum PartType
{
    None,

    // Body
    Frame,

    // Indoor
    Controll, Engine, EngineCover, CarSeat_Right, CarSeat_Left,

    // Window
    BehindWindow_Left, BehindWindow_Right, FrontWindow, FrontDoor_Left, FrontDoor_Right,

    // Wheel
    Wheel_FrontRight, Wheel_BehindRight, Wheel_FrontLeft, Wheel_BehindLeft,

    // Outdoor
    Bumper, Trunk, Hood,

    // Parts
    BackMirror_Left, BackMirror_Right, FrontLight_Left, FrontLight_Right,
    BehindLight_RightBig, BehindLight_LeftBig, BehindLight_LeftSmall, BehindLight_RightSmall, Bumper2
}