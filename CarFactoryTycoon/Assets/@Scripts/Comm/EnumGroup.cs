// PartDefinitions.cs
public enum PartGroup
{
    Body, Indoor, Window, Wheel, Outdoor, Parts
}

public enum PartType
{
    None,

    // Body
    Frame_1,

    // Indoor
    Controll_21, Engine_22, EngineCover_23, CarSeat_Right_24, CarSeat_Left_25,

    // Window
    BehindWindow_Left_31, BehindWindow_Right_32, FrontWindow_33, FrontDoor_Left_34, FrontDoor_Right_35,

    // Wheel
    Wheel_FrontRight_41, Wheel_BehindRight_42, Wheel_FrontLeft_43, Wheel_BehindLeft_44,

    // Outdoor
    Bumper_51, Trunk_52, Hood_53,

    // Parts
    BackMirror_Left_61, BackMirror_Right_62, FrontLight_Left_63, FrontLight_Right_64,
    BehindLight_RightBig_65, BehindLight_LeftBig_66, BehindLight_LeftSmall_67, BehindLight_RightSmall_68, Bumper2_69
}