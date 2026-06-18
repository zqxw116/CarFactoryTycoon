using UnityEngine;

public static class PartResourceManager
{
    public static string GetPrefabPath(PartType type)
    {
        switch (type)
        {
            // ==========================================
            // 1. Body
            // ==========================================
            case PartType.Frame_1:
                return "Prefabs/Parts/Body/Frame";

            // ==========================================
            // 2. Indoor
            // ==========================================
            case PartType.Controll_21:
                return "Prefabs/Parts/Indoor/Controll";
            case PartType.Engine_22:
                return "Prefabs/Parts/Indoor/Engine";
            case PartType.EngineCover_23:
                return "Prefabs/Parts/Indoor/EngineCover";
            // 의자 프리팹 재사용 (좌/우 동일)
            case PartType.CarSeat_Left_25:
            case PartType.CarSeat_Right_24:
                return "Prefabs/Parts/Indoor/CarSeat";

            // ==========================================
            // 3. Window
            // ==========================================
            case PartType.FrontWindow_33:
                return "Prefabs/Parts/Window/FrontWindow";
            // 뒷창문 프리팹 재사용
            case PartType.BehindWindow_Left_31:
            case PartType.BehindWindow_Right_32:
                return "Prefabs/Parts/Window/BehindWindow";
            // 앞문 프리팹 재사용
            case PartType.FrontDoor_Left_34:
            case PartType.FrontDoor_Right_35:
                return "Prefabs/Parts/Window/FrontDoor";

            // ==========================================
            // 4. Outdoor
            // ==========================================
            case PartType.Bumper_51:
                return "Prefabs/Parts/Outdoor/Bumper";
            case PartType.Trunk_52:
                return "Prefabs/Parts/Outdoor/Trunk";
            case PartType.Hood_53:
                return "Prefabs/Parts/Outdoor/Hood";

            // ==========================================
            // 5. Wheel (바퀴 4개 모두 동일한 프리팹 재사용)
            // ==========================================
            case PartType.Wheel_FrontRight_41:
            case PartType.Wheel_BehindRight_42:
            case PartType.Wheel_FrontLeft_43:
            case PartType.Wheel_BehindLeft_44:
                return "Prefabs/Parts/Wheel/Wheel";

            // ==========================================
            // 6. Parts
            // ==========================================
            // 백미러 재사용
            case PartType.BackMirror_Left_61:
            case PartType.BackMirror_Right_62:
                return "Prefabs/Parts/Parts/BackMirror";
            // 앞 라이트 재사용
            case PartType.FrontLight_Left_63:
            case PartType.FrontLight_Right_64:
                return "Prefabs/Parts/Parts/FrontLight";
            // 뒤 라이트 재사용 (대/소/좌/우 모두 폴더 내 BehindLight 프리팹 1개로 처리)
            case PartType.BehindLight_RightBig_65:
            case PartType.BehindLight_LeftBig_66:
            case PartType.BehindLight_LeftSmall_67:
            case PartType.BehindLight_RightSmall_68:
                return "Prefabs/Parts/Parts/BehindLight";
            // 추가 범퍼
            case PartType.Bumper2_69:
                return "Prefabs/Parts/Parts/Bumper2";

            // ==========================================
            // 예외 처리
            // ==========================================
            case PartType.None:
                return "";
            default:
                Debug.LogError($"[PartResourceManager] 매핑되지 않은 부품 타입입니다: {type}");
                return "";
        }
    }
}