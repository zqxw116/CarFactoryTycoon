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
            case PartType.Body:
                return "Prefabs/Parts/Body/Body";

            // ==========================================
            // 2. Indoor
            // ==========================================
            case PartType.Controll:
                return "Prefabs/Parts/Indoor/Controll";
            case PartType.Engine:
                return "Prefabs/Parts/Indoor/Engine";
            case PartType.EngineCover:
                return "Prefabs/Parts/Indoor/EngineCover";
            // 의자 프리팹 재사용 (좌/우 동일)
            case PartType.CarSeat_Left:
            case PartType.CarSeat_Right:
                return "Prefabs/Parts/Indoor/CarSeat";

            // ==========================================
            // 3. Window
            // ==========================================
            case PartType.FrontWindow:
                return "Prefabs/Parts/Window/FrontWindow";
            // 뒷창문 프리팹 재사용
            case PartType.BehindWindow_Left:
            case PartType.BehindWindow_Right:
                return "Prefabs/Parts/Window/BehindWindow";
            // 앞문 프리팹 재사용
            case PartType.FrontDoor_Left:
            case PartType.FrontDoor_Right:
                return "Prefabs/Parts/Window/FrontDoor";

            // ==========================================
            // 4. Outdoor
            // ==========================================
            case PartType.Bumper:
                return "Prefabs/Parts/Outdoor/Bumper";
            case PartType.Trunk:
                return "Prefabs/Parts/Outdoor/Trunk";
            case PartType.Hood:
                return "Prefabs/Parts/Outdoor/Hood";

            // ==========================================
            // 5. Wheel (바퀴 4개 모두 동일한 프리팹 재사용)
            // ==========================================
            case PartType.Wheel_FrontRight:
            case PartType.Wheel_BehindRight:
            case PartType.Wheel_FrontLeft:
            case PartType.Wheel_BehindLeft:
                return "Prefabs/Parts/Wheel/Wheel";

            // ==========================================
            // 6. Parts
            // ==========================================
            // 백미러 재사용
            case PartType.BackMirror_Left:
            case PartType.BackMirror_Right:
                return "Prefabs/Parts/Parts/BackMirror";
            // 앞 라이트 재사용
            case PartType.FrontLight_Left:
            case PartType.FrontLight_Right:
                return "Prefabs/Parts/Parts/FrontLight";
            // 뒤 라이트 재사용 (대/소/좌/우 모두 폴더 내 BehindLight 프리팹 1개로 처리)
            case PartType.BehindLight_RightBig:
            case PartType.BehindLight_LeftBig:
            case PartType.BehindLight_LeftSmall:
            case PartType.BehindLight_RightSmall:
                return "Prefabs/Parts/Parts/BehindLight";
            // 추가 범퍼
            case PartType.Bumper2:
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