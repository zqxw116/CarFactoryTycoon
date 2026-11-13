using UnityEngine;
using UnityEngine.InputSystem;

public sealed partial class FirstProductionLoop
{
    public bool WashToolActive {get;private set;}=true;
    public bool InvestmentOpen {get;private set;}
    public bool Spraying {get;private set;}
    public float TabletOpenedEnd => FullLine ? 1f : DeliveryEnd(OpenedProcesses);
    public float TabletOutletEnd => OutletPrefix == progression.processes.Length ? 1f : DeliveryEnd(OutletPrefix);
    public void ShowWashGun(bool visible) { if (gun != null) gun.SetActive(visible && (services == null || services.fuel == null || !services.fuel.PlayerFueling)); }
    GameObject gun;
    ParticleSystem water;
    Transform washHand;
    Vector3 palmCenter;
    Quaternion gripInHand;
    Vector3 AimDirection => player.MapMode ? player.transform.forward : player.viewCamera.transform.forward;
    Material gunMaterial,waterMaterial;
    const float InvestmentRange = 3.5f;
    int investmentProcess = -1;
    Rect investmentRect => new Rect(PrototypeGui.Width * .5f - 220, PrototypeGui.Height * .5f - 170, 440, 340);
    public Vector3 InvestmentPosition => PostPosition(0);
    Vector3 PostPosition(int index) => progression.processes[index].hiringPost.position;
    string ProcessName(int index) => index == 0 ? "대시보드" : progression.processes[index].displayName;

    int NearestOpenPost()
    {
        int nearest = -1;
        float minDistance = InvestmentRange * InvestmentRange;
        for (int i = 0; i < OpenedProcesses; i++)
        {
            var post = progression.processes[i].hiringPost;
            if (post == null || !post.gameObject.activeInHierarchy) continue;
            float distance = (post.position - player.transform.position).sqrMagnitude;
            if (distance > minDistance) continue;
            nearest = i;
            minDistance = distance;
        }
        return nearest;
    }

    public string Objective
    {
        get
        {
            foreach(var e in entries)if(!e.returned&&e.car!=null&&e.car.gameObject.activeInHierarchy)
            {
                if (e.contract.isCompleteVehicle && inspection != null && e.car.pathProgress >= inspection.EntryProgress)
                {
                    if (services != null && services.fuel != null && services.fuel.CurrentCar == e.car)
                        return "출고 전 주유 · " + services.fuel.StatusText;
                    if (e.car.pathProgress >= inspection.ScanProgress) return "체결 확인 · 검사 결과에 따라 출고";
                    if (e.car.pathProgress >= inspection.RollerProgress) return "주행 검사 · 바퀴 및 체결 상태 확인 중";
                    return "수밀 검사 · 추가 체결 마감";
                }
                if(!e.wash.IsClean)return "1 분사건 · 차량 외장 전체 세척 "+Mathf.FloorToInt(e.wash.Coverage*100)+"%";
                int delegated = -1;
                for (int i = 0; i < e.contract.processCount; i++)
                {
                    var process = progression.processes[i];
                    foreach (var type in process.parts)
                        foreach (var part in e.parts)
                            if (part.myType == type && !part.IsAssembled)
                            {
                                if (process.hired)
                                {
                                    if (delegated < 0) delegated = i;
                                    continue;
                                }
                                return "2 조립 · " + ProcessName(i) + " · 슬롯에서 E로 집기 → 차량에 E 유지 → 망치 체결";
                            }
                }
                if (delegated >= 0)
                    return ProcessName(delegated) + (progression.processes[delegated].automated ? " · 로봇팔 담당" : " · 작업자 담당") + " · 체결 대기";
                return (e.contract.isCompleteVehicle ? "완성차 검사 진입 대기 · 약정 출고금 " : "대차 인수 대기 · 약정 납품금 ")+e.contract.payout+"원";
            }
            if(SuccessfulDeliveries>=2&&!FirstWorkerHired&&OpenedProcesses==1)return "대시보드 현장 표지에서 F · 작업자 고용 또는 다음 공정 해금";
            return AutomaticInputPurchased ? "자동 투입 가동 · 대기 " + spawner.PendingCount + "/" + spawner.queueCapacity : "현장 투입구를 망치로 쳐서 다음 차를 요청하세요";
        }
    }
    void InitializePlayerTool()
    {
        player.SetMapMode(false);player.SetFirstPerson(true);
        var pose=ProcessAuthoringLayout.FrameAt(progression.mainSpline,WashProgress);
        var position=pose.position+pose.rotation*Vector3.right*3.4f;position.y=player.transform.position.y;
        player.Teleport(position);player.SpawnPoint=position;
        float yaw=Quaternion.LookRotation(pose.position-position).eulerAngles.y;
        player.RotateView(new Vector2(Mathf.DeltaAngle(player.ViewYaw,yaw),0));player.transform.rotation=Quaternion.Euler(0,yaw,0);
        var actor = player.bodyAnimator;
        washHand = actor != null && actor.isHuman ? actor.GetBoneTransform(HumanBodyBones.RightHand) : null;
        if (washHand == null) washHand = player.CarryAnchor;
        var index = actor != null && actor.isHuman ? actor.GetBoneTransform(HumanBodyBones.RightIndexProximal) : null;
        var little = actor != null && actor.isHuman ? actor.GetBoneTransform(HumanBodyBones.RightLittleProximal) : null;
        gun = new GameObject("분사건");
        gun.transform.SetParent(washHand, true); // Preserve metre scale even on an imported, scaled skeleton.
        if (index != null && little != null)
        {
            var fingers = (index.position + little.position) * .5f - washHand.position;
            palmCenter = washHand.InverseTransformVector(fingers * .65f);
            var barrel = index.position - little.position;
            gripInHand = Quaternion.Inverse(washHand.rotation) * Quaternion.LookRotation(barrel, -fingers);
        }
        else gripInHand = Quaternion.Inverse(washHand.rotation) * player.transform.rotation;
        gun.transform.localPosition = palmCenter;
        gun.transform.localRotation = gripInHand;
        gunMaterial=new Material(Shader.Find("Universal Render Pipeline/Lit")){color=new Color(.08f,.28f,.38f)};
        MakeToolPart("건 본체",PrimitiveType.Cube,new Vector3(0,.08f,.04f),new Vector3(.10f,.09f,.24f));
        MakeToolPart("노즐",PrimitiveType.Cylinder,new Vector3(0,.095f,.26f),new Vector3(.025f,.17f,.025f),Quaternion.Euler(90,0,0));
        MakeToolPart("손잡이",PrimitiveType.Cube,Vector3.zero,new Vector3(.065f,.16f,.07f));
        var particles=new GameObject("물 분사");particles.transform.SetParent(gun.transform,false);particles.transform.localPosition=new Vector3(0,.095f,.43f);
        water=particles.AddComponent<ParticleSystem>();water.Stop(true,ParticleSystemStopBehavior.StopEmittingAndClear);
        var main=water.main;main.loop=true;main.playOnAwake=false;main.startLifetime=.28f;main.startSpeed=13;main.startSize=.045f;main.maxParticles=300;main.simulationSpace=ParticleSystemSimulationSpace.World;main.startColor=new Color(.45f,.8f,1,.6f);
        var emission=water.emission;emission.rateOverTime=160;var shape=water.shape;shape.shapeType=ParticleSystemShapeType.Cone;shape.angle=14;shape.radius=.015f;
        var shader=Shader.Find("Universal Render Pipeline/Particles/Unlit");if(shader==null)shader=Shader.Find("Universal Render Pipeline/Unlit");
        waterMaterial=new Material(shader);waterMaterial.SetTexture("_BaseMap",Texture2D.whiteTexture);waterMaterial.SetColor("_BaseColor",new Color(.3f,.75f,1,.35f));
        waterMaterial.SetFloat("_Surface",1);waterMaterial.SetFloat("_SrcBlend",5);waterMaterial.SetFloat("_DstBlend",10);waterMaterial.SetFloat("_ZWrite",0);waterMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");waterMaterial.renderQueue=3000;
        var renderer=particles.GetComponent<ParticleSystemRenderer>();renderer.sharedMaterial=waterMaterial;renderer.renderMode=ParticleSystemRenderMode.Stretch;renderer.lengthScale=1.5f;renderer.velocityScale=.015f;
        SetWashTool(true);
    }
    public bool PoseWashGun(Animator animator)
    {
        if (!WashToolActive || gun == null || animator != player.bodyAnimator) return false;
        Vector3 forward = AimDirection;
        Quaternion aim = Quaternion.LookRotation(forward, Vector3.up);
        var shoulder = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
        if (shoulder == null) return false;
        // Aim relative to the body eye rather than the third-person camera behind the actor.
        var eye = player.transform.position + Vector3.up * 1.62f;
        Vector3 wrist = eye + aim * new Vector3(.18f, -.17f, .46f);
        var elbow = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
        if (elbow != null)
        {
            float reach = Vector3.Distance(shoulder.position, elbow.position) + Vector3.Distance(elbow.position, washHand.position);
            wrist = shoulder.position + Vector3.ClampMagnitude(wrist - shoulder.position, reach * .96f);
        }
        animator.SetIKPositionWeight(AvatarIKGoal.RightHand, 1f);
        animator.SetIKPosition(AvatarIKGoal.RightHand, wrist);
        animator.SetIKRotationWeight(AvatarIKGoal.RightHand, 1f);
        animator.SetIKRotation(AvatarIKGoal.RightHand, aim * Quaternion.Inverse(gripInHand));
        return true;
    }

    void MakeToolPart(string name,PrimitiveType type,Vector3 position,Vector3 scale,Quaternion rotation=default)
    {
        var x=GameObject.CreatePrimitive(type);x.name=name;x.layer=2;x.transform.SetParent(gun.transform,false);x.transform.localPosition=position;x.transform.localScale=scale;
        x.transform.localRotation=rotation==default?Quaternion.identity:rotation;x.GetComponent<Collider>().enabled=false;x.GetComponent<Renderer>().sharedMaterial=gunMaterial;
    }
    void ReleasePlayerTool()
    {
        if(gun!=null)Destroy(gun);
        if(gunMaterial!=null)Destroy(gunMaterial);
        if(waterMaterial!=null)Destroy(waterMaterial);
    }
    public bool SetWashTool(bool wash)
    {
        if(player!=null&&player.interactor!=null&&(player.interactor.HeldPart!=null||player.interactor.IsAttaching))return false;
        WashToolActive=wash;ShowWashGun(wash);
        if(player!=null&&player.interactor!=null){player.interactor.SetHammerVisible(!wash);player.interactor.inputEnabled=!wash&&player.ControlsActive&&!InvestmentOpen;}
        return true;
    }
    public bool TryOpenInvestment()
    {
        if (!ready || player == null || player.MapMode || player.InteractionUIOpen) return false;
        investmentProcess = NearestOpenPost();
        if (investmentProcess < 0) return false;
        InvestmentOpen=true;player.InteractionUIOpen=true;Cursor.lockState=CursorLockMode.None;Cursor.visible=true;return true;
    }
    public void CloseInvestment(){InvestmentOpen=false;investmentProcess=-1;if(player!=null)player.InteractionUIOpen=false;}
    void TickPlayerTool()
    {
        var keyboard=Keyboard.current;
        if(InvestmentOpen)
        {
            if(Vector3.Distance(player.transform.position,PostPosition(investmentProcess))>3.8f||keyboard!=null&&(keyboard.escapeKey.wasPressedThisFrame||keyboard.fKey.wasPressedThisFrame)){CloseInvestment();return;}
            if(keyboard!=null&&keyboard.digit1Key.wasPressedThisFrame)TryStaffProcess(false);
            if(keyboard!=null&&keyboard.digit3Key.wasPressedThisFrame)TryStaffProcess(true);
            if(keyboard!=null&&keyboard.digit2Key.wasPressedThisFrame)TryUnlockNextProcess();
            if(keyboard!=null&&keyboard.digit4Key.wasPressedThisFrame)TryPurchaseAutomaticInput();
        }
        else if(player.ControlsActive&&keyboard!=null)
        {
            if(keyboard.digit1Key.wasPressedThisFrame)SetWashTool(true);
            if(keyboard.digit2Key.wasPressedThisFrame)SetWashTool(false);
            if(keyboard.fKey.wasPressedThisFrame)TryOpenInvestment();
        }
        Spraying=WashToolActive && (services == null || services.fuel == null || !services.fuel.PlayerFueling) && !InvestmentOpen&&player.ControlsActive&&Mouse.current!=null&&Mouse.current.leftButton.isPressed&&!player.PointerOverInterface;
    }

    void LateUpdate()
    {
        if (!ready || !WashToolActive || water == null) return;
        // Use the same visible muzzle and ray after the animator and the camera have moved.
        var view = player.viewCamera.transform;
        float reach = settings.sprayRange;
        var sight = new Ray(view.position, AimDirection);
        float sightDistance = reach + Vector3.Distance(view.position, washHand.position);
        Vector3 target = sight.GetPoint(sightDistance);
        if (!player.MapMode && Physics.Raycast(sight, out var hit, sightDistance, ~((1 << 2) | (1 << 10)), QueryTriggerInteraction.Ignore))
            target = hit.point;
        Vector3 direction = target - gun.transform.position;
        if (direction.sqrMagnitude > .001f)
            gun.transform.rotation = Quaternion.LookRotation(direction, Vector3.up);
        var muzzle = water.transform;
        direction = target - muzzle.position;
        if (direction.sqrMagnitude > .001f && Vector3.Dot(direction, gun.transform.forward) > 0f)
            muzzle.rotation = Quaternion.LookRotation(direction, Vector3.up);
        else muzzle.localRotation = Quaternion.identity;
        bool emit = Spraying && !PrototypeDayCycle.SettlementOpen && !player.MapMode;
        if (emit && !water.isPlaying) water.Play();
        else if (!emit && water.isPlaying) water.Stop();
        if (!emit) return;
        var ray = new Ray(muzzle.position, muzzle.forward);
        foreach(var e in entries)if(!e.returned&&e.car!=null&&e.car.gameObject.activeInHierarchy&&e.version==e.car.LifeVersion)
            if(e.wash.Spray(ray,Time.deltaTime,settings.sprayRadius,settings.sprayRange))break;
    }
    void OnGUI()
    {
        if(!ready||player==null||player.TabletOpen||PrototypeDayCycle.SettlementOpen)return;
        using var scope=PrototypeGui.Scope();
        float width = Mathf.Min(690f, PrototypeDayHUD.HeaderRect.x - 24f);
        GUI.Box(new Rect(12,40,width,118),"첫 납품 작업장 · "+EconomyManager.Instance.currentMoney+"원");
        GUI.Label(new Rect(24,67,width-24,28),Objective);
        GUI.Label(new Rect(24,96,width-24,24),"1 분사건 / 2 조립 · C 시점 · 투입구 망치 접촉 · 현재 "+OpenedProcesses+"공정" + (AutomaticInputPurchased ? " · 자동 투입" : ""));
        GUI.Label(new Rect(24,122,width-24,25),LastResult);
        if(WashToolActive&&!InvestmentOpen&&!player.MapMode)GUI.Label(new Rect(PrototypeGui.Width*.5f-8,PrototypeGui.Height*.5f-12,24,24),"+");
        if (!InvestmentOpen)
        {
            int near = NearestOpenPost();
            if (near >= 0 && !player.InteractionUIOpen)
                GUI.Label(new Rect(PrototypeGui.Width*.5f-190,PrototypeGui.Height-175,420,28),"F " + ProcessName(near) + " · 현장 투자");
            return;
        }
        var process = progression.processes[investmentProcess];
        string name = ProcessName(investmentProcess);
        int money = EconomyManager.Instance.currentMoney;
        int robotPrice = progression.prototypeSettings.robotPrice;
        GUI.Box(investmentRect, name + " · 현장 투자");
        float x=investmentRect.x+16,y=investmentRect.y+35;
        GUI.Label(new Rect(x,y,400,30),"잔액 "+money+"원 · "+(process.automated ? "로봇 가동" : process.hired ? "작업자 근무" : "직접 조립"));
        GUI.enabled=!process.hired && money >= settings.firstHireCost;
        if(GUI.Button(new Rect(x,y+34,400,42),"1 작업자 고용 · "+settings.firstHireCost+"원"))TryStaffProcess(false);
        GUI.enabled=OpenedProcesses<progression.processes.Length;
        string next = GUI.enabled
            ? "2 " + progression.processes[OpenedProcesses].displayName + " 해금 " + settings.UnlockPrice(OpenedProcesses) + "원 · 납품 " + settings.DeliveryPrice(OpenedProcesses + 1) + "원"
            : "모든 공정 해금 완료";
        GUI.enabled = GUI.enabled && money >= settings.UnlockPrice(OpenedProcesses);
        if(GUI.Button(new Rect(x,y+81,400,44),next))TryUnlockNextProcess();
        GUI.enabled = !process.automated && money >= robotPrice;
        if (GUI.Button(new Rect(x,y+131,400,42),"3 로봇팔 "+(process.hired ? "전환" : "구매")+" · "+robotPrice+"원"))
            TryStaffProcess(true);
        GUI.enabled = !AutomaticInputPurchased && money >= settings.automaticInputCost;
        string feedLabel = AutomaticInputPurchased ? "자동 투입 가동 중" : "4 자동 투입 · 공장 전체 · " + settings.automaticInputCost + "원";
        if (GUI.Button(new Rect(x,y+178,400,42),feedLabel)) TryPurchaseAutomaticInput();
        GUI.enabled=true;
        if(GUI.Button(new Rect(x,y+228,400,38),"닫기 (F / Esc)"))CloseInvestment();
    }
}
