using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>Prototype warehouse/fuel desks use the same local interaction rules as assembly posts.</summary>
public sealed class PrototypeServicesInteraction : MonoBehaviour
{
    public FactoryPlayerController player;
    public ProductionProgression progression;
    public SupplyManager supply;
    public FuelStation fuel;
    public Transform warehousePost, fuelPost;
    public float interactionRange = 3.5f;
    public bool PopupIsOpen { get; private set; }
    bool fuelMenu, oldVisible;
    CursorLockMode oldCursor;
    int focused = -1;
    readonly RaycastHit[] hits = new RaycastHit[24];
    bool Settlement => PrototypeDayCycle.Current != null && PrototypeDayCycle.Current.IsSettlement;
    public bool TryHireSupply()
    {
        if(Settlement || supply==null || !supply.CanHire)return false;
        int cost=progression.prototypeSettings.hirePrice;
        if(!EconomyManager.Instance.TrySpend(cost))return false;
        var worker=supply.Hire();
        if(worker==null){EconomyManager.Instance.Earn(cost);return false;}
        var day=PrototypeDayCycle.Current;
        worker.SetDutyStatus(day==null || !day.IsSettlement&&(day.GameHour<20 || day.OvertimeEnabled));
        return true;
    }
    public void OpenService(bool isFuel)
    {
        Transform post=isFuel?fuelPost:warehousePost;
        if(Settlement || player==null || player.MapMode || !player.ControlsActive || post==null ||
            Vector3.Distance(player.transform.position,post.position)>interactionRange)return;
        fuelMenu=isFuel;PopupIsOpen=true;oldCursor=Cursor.lockState;oldVisible=Cursor.visible;
        player.InteractionUIOpen=true;Cursor.lockState=CursorLockMode.None;Cursor.visible=true;
        if(player.interactor!=null)player.interactor.inputEnabled=false;
    }
    public void ClosePopup()
    {
        if(!PopupIsOpen)return;
        PopupIsOpen=false;
        if(player==null)return;
        player.InteractionUIOpen=false;
        Cursor.lockState=player.MapMode?CursorLockMode.None:oldCursor;
        Cursor.visible=player.MapMode||oldVisible;
        if(player.interactor!=null)player.interactor.inputEnabled=player.ControlsActive;
    }
    void Update()
    {
        if(player==null)return;
        var keys=Keyboard.current;
        if(PopupIsOpen)
        {
            Transform post=fuelMenu?fuelPost:warehousePost;
            if(Settlement || player.MapMode || post==null || Vector3.Distance(player.transform.position,post.position)>interactionRange+.25f ||
                keys!=null&&keys.escapeKey.wasPressedThisFrame)ClosePopup();
            return;
        }
        if(Settlement || !player.ControlsActive){focused=-1;return;}
        focused=FindDesk();
        if(focused>=0 && keys!=null && keys.fKey.wasPressedThisFrame){OpenService(focused==1);return;}
        if(fuel==null || keys==null || !keys.eKey.isPressed || player.interactor!=null && (player.interactor.HeldPart!=null || player.interactor.IsAttaching))return;
        if(!fuel.PlayerFueling)fuel.TryBeginPlayerFueling(player);
        if(fuel.PlayerFueling)fuel.AdvancePlayerFueling(player,Time.deltaTime);
    }
    int FindDesk()
    {
        if(player.viewCamera==null)return -1;
        var ray=new Ray(player.viewCamera.transform.position,player.viewCamera.transform.forward);
        int count=Physics.RaycastNonAlloc(ray,hits,Vector3.Distance(ray.origin,player.transform.position)+interactionRange+1f,~(1<<10),QueryTriggerInteraction.Ignore);
        int best=-1;float distance=float.MaxValue;
        for(int i=0;i<count;i++)if(hits[i].distance<distance){best=i;distance=hits[i].distance;}
        if(best<0)return -1;
        var target=hits[best].transform;
        if(warehousePost!=null&&(target==warehousePost||target.IsChildOf(warehousePost))&&Vector3.Distance(player.transform.position,warehousePost.position)<=interactionRange)return 0;
        if(fuelPost!=null&&(target==fuelPost||target.IsChildOf(fuelPost))&&Vector3.Distance(player.transform.position,fuelPost.position)<=interactionRange)return 1;
        return -1;
    }
    void OnGUI()
    {
        if(Settlement || player==null)return;
        if(PopupIsOpen){DrawMenu();return;}
        if(!player.ControlsActive)return;
        string text=focused>=0?(focused==0?"F 창고 · 보급자 고용":"F 주유 · 고용/자동화"):null;
        if(fuel!=null&&fuel.CurrentCar!=null&&Vector3.Distance(player.transform.position+Vector3.up,fuel.CurrentCar.transform.TransformPoint(fuel.carPortLocal))<=interactionRange)
            text=fuel.StatusText+(fuel.PlayerFueling?"":" · 빈손으로 E를 누르고 있어 직접 주유");
        if(text!=null)GUI.Label(new Rect(Screen.width*.5f-240,Screen.height*.76f,480,30),text);
    }
    void DrawMenu()
    {
        var rect=new Rect((Screen.width-380)*.5f,(Screen.height-230)*.5f,380,230);
        GUI.Box(rect,GUIContent.none);
        GUILayout.BeginArea(new Rect(rect.x+18,rect.y+14,344,202));
        GUILayout.Label(fuelMenu?"주유 공정":"창고 · 부품 보급");
        GUILayout.Label("보유 "+EconomyManager.Instance.currentMoney);
        if(fuelMenu)
        {
            GUILayout.Label(fuel.StatusText);
            GUI.enabled=!fuel.AttendantHired&&!fuel.IsAutomated&&fuel.CanChangeStaff&&EconomyManager.Instance.currentMoney>=progression.prototypeSettings.hirePrice;
            if(GUILayout.Button("주유 작업자 고용  "+progression.prototypeSettings.hirePrice,GUILayout.Height(32))){if(fuel.TryHireAttendant())ClosePopup();}
            GUI.enabled=!fuel.IsAutomated&&fuel.CanChangeStaff&&EconomyManager.Instance.currentMoney>=fuel.AutomationCost;
            if(GUILayout.Button("자동 주유 구매  "+fuel.AutomationCost,GUILayout.Height(32))){if(fuel.TryPurchaseAutomation())ClosePopup();}
        }
        else
        {
            GUILayout.Label("보급자 "+supply.WorkerCount+"명 · 창고에서 부족한 재고로 운반");
            GUI.enabled=supply.CanHire&&EconomyManager.Instance.currentMoney>=progression.prototypeSettings.hirePrice;
            if(GUILayout.Button("보급자 1명 고용  "+progression.prototypeSettings.hirePrice,GUILayout.Height(36))){if(TryHireSupply())ClosePopup();}
            GUILayout.Label("정규·특근 급여 적용 / 한 번에 한 종류 운반");
        }
        GUI.enabled=true;GUILayout.FlexibleSpace();
        if(GUILayout.Button("닫기",GUILayout.Height(26)))ClosePopup();
        GUILayout.EndArea();
    }
    void OnDisable(){ClosePopup();}
}
