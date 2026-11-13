using Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>Local player; no player singleton. Prototype movement values remain tunable in the balance asset.</summary>
[DefaultExecutionOrder(50)]
[RequireComponent(typeof(CharacterController))]
public sealed class FactoryPlayerController : MonoBehaviour
{
    public PrototypeBalanceSettings settings;
    public Animator bodyAnimator;
    public PlayerAssemblyInteractor interactor;
    public Camera viewCamera;
    public CameraController mapCamera;
    public CinemachineBrain brain;
    public CinemachineVirtualCamera firstPersonCamera, thirdPersonCamera;
    public Transform holdAnchor;
    public bool MapMode { get; private set; }
    public bool FirstPerson { get; private set; }
    public FactoryManagementTablet Tablet { get; private set; }
    public bool TabletOpen => Tablet != null && Tablet.gameObject.activeSelf;
    bool interactionUIOpen;
    public bool InteractionUIOpen { get => interactionUIOpen || TabletOpen; set => interactionUIOpen = value; }
    static Rect TabletButton => new Rect(PrototypeGui.Width - 158f, PrototypeGui.Height - 64f, 146f, 52f);
    public bool ControlsActive => !InteractionUIOpen && !MapMode && PointerInGameView;
    public static bool PointerInGameView => Application.isFocused && Mouse.current != null &&
        Mouse.current.position.ReadValue().x >= 0 && Mouse.current.position.ReadValue().y >= 0 &&
        Mouse.current.position.ReadValue().x < Screen.width && Mouse.current.position.ReadValue().y < Screen.height;
    public Vector3 BeltVelocity { get; private set; }
    public Vector3 SpawnPoint { get; set; }
    CharacterController controller;
    Renderer[] firstPersonHiddenRenderers;
    bool[] firstPersonOriginalForceOffStates;
    float yaw, pitch = 12f, verticalSpeed;
    PrototypeDayHUD dayHud;
    public bool PointerOverInterface
    {
        get
        {
            var mouse=Mouse.current;
            if (FirstProductionLoop.Current != null && TabletButton.Contains(PrototypeGui.PointerPosition)) return true;
            if(PrototypePlayerTestControls.PointerOverAdminPanel(mouse))return true;
            if(UnityEngine.EventSystems.EventSystem.current!=null && UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject())return true;
            if(dayHud==null)dayHud=FindFirstObjectByType<PrototypeDayHUD>();
            if(dayHud==null || mouse==null)return false;
            var point=mouse.position.ReadValue();
            return dayHud.BlocksPointer(new Vector2(point.x,Screen.height-point.y));
        }
    }
    bool grounded;
    [Min(0f)] public float movementTurnSpeed = 720f;
    [Min(0f)] public float lookTurnSpeed = 1440f;
    CarController supportCar;
    int supportVersion;
    Vector3 supportLocalPoint;
    Quaternion supportRotation;
    public Vector3 BodyForward => transform.forward;
    public Vector3 BodyRight => transform.right;
    public Transform CarryAnchor => holdAnchor;
    public CarController SupportCar => supportCar;
    public float ViewYaw => yaw;

    public void RotateView(Vector2 delta)
    {
        yaw += delta.x;
        pitch = Mathf.Clamp(pitch - delta.y, -70f, 75f);
    }
    public Vector3 ResolveMoveDirection(Vector2 input)
    {
        if(input.sqrMagnitude < .0001f)return Vector3.zero;
        var direction=Quaternion.Euler(0,yaw,0)*new Vector3(input.x,0,input.y);
        return Vector3.ClampMagnitude(direction,1f);
    }
    public bool CanReachInteraction(Vector3 point,float range)
    {
        var origin=transform.position+Vector3.up*1.1f;
        var delta=point-origin;
        if(delta.sqrMagnitude>range*range)return false;
        var planar=Vector3.ProjectOnPlane(delta,Vector3.up);
        if(planar.sqrMagnitude>.04f && Vector3.Dot(BodyForward,planar.normalized)<.05f)return false;
        // Static architecture can obstruct a service. Target vehicles are checked by their own interaction.
        return !Physics.Linecast(origin,point,out var obstruction,1<<0,QueryTriggerInteraction.Ignore) ||
            obstruction.collider.GetComponentInParent<CarController>()!=null ||
            (obstruction.collider.ClosestPoint(point)-point).sqrMagnitude<.0025f;
    }
    void ClearSupport(){supportCar=null;supportVersion=0;}
    void ApplySupportMotion()
    {
        if(supportCar==null)return;
        if(!supportCar.gameObject.activeInHierarchy || supportCar.LifeVersion!=supportVersion){ClearSupport();return;}
        var target=supportCar.transform.TransformPoint(supportLocalPoint);
        var delta=target-transform.position;
        if(delta.sqrMagnitude>9f){ClearSupport();return;}
        controller.Move(delta);
        var turn=supportCar.transform.rotation*Quaternion.Inverse(supportRotation);
        transform.rotation=turn*transform.rotation;
        supportRotation=supportCar.transform.rotation;
    }
    void RememberSupport(RaycastHit ground)
    {
        var car=ground.collider!=null?ground.collider.GetComponentInParent<CarController>():null;
        if(car==null){ClearSupport();return;}
        supportCar=car;supportVersion=car.LifeVersion;
        supportLocalPoint=car.transform.InverseTransformPoint(transform.position);
        supportRotation=car.transform.rotation;
    }
    int speedId=Animator.StringToHash("Speed"), motionId=Animator.StringToHash("MotionSpeed");
    int groundId=Animator.StringToHash("Grounded"), jumpId=Animator.StringToHash("Jump"), fallId=Animator.StringToHash("FreeFall");
    static readonly int jumpPhaseId = Animator.StringToHash("JumpPhase");
    int moveXId=Animator.StringToHash("MoveX"), moveZId=Animator.StringToHash("MoveZ");

    void Awake()
    {
        controller = GetComponent<CharacterController>();
        SpawnPoint = transform.position;
        yaw = transform.eulerAngles.y;
        if (bodyAnimator != null && bodyAnimator.isHuman)
        {
            firstPersonHiddenRenderers = bodyAnimator.GetComponentsInChildren<Renderer>(true);
            firstPersonOriginalForceOffStates = new bool[firstPersonHiddenRenderers.Length];
            for (int i = 0; i < firstPersonHiddenRenderers.Length; i++)
            {
                Renderer renderer = firstPersonHiddenRenderers[i];
                firstPersonOriginalForceOffStates[i] = renderer != null && renderer.forceRenderingOff;
            }
        }
        // Keep the test controls self-contained: no scene object or serialized reference is required.
        if (GetComponent<PrototypePlayerTestControls>() == null) gameObject.AddComponent<PrototypePlayerTestControls>();
    }
    void Start()
    {
        brain.m_UpdateMethod = CinemachineBrain.UpdateMethod.ManualUpdate;
        brain.m_DefaultBlend = new CinemachineBlendDefinition(CinemachineBlendDefinition.Style.Cut, 0);
        SetMapMode(false);
        SetFirstPerson(false);
    }
    public void SetFirstPerson(bool value)
    {
        if (TabletOpen) return;
        FirstPerson = value;
        if (firstPersonCamera != null) firstPersonCamera.Priority = value ? 20 : 10;
        if (thirdPersonCamera != null) thirdPersonCamera.Priority = value ? 10 : 20;
        SetFirstPersonBodyHidden(value && !MapMode);
    }

    private void SetFirstPersonBodyHidden(bool hidden)
    {
        if (firstPersonHiddenRenderers == null) return;
        for (int i = 0; i < firstPersonHiddenRenderers.Length; i++)
        {
            Renderer bodyRenderer = firstPersonHiddenRenderers[i];
            if (bodyRenderer == null) continue;
            // forceRenderingOff is per-renderer: other characters keep their normal visibility and shadows.
            bodyRenderer.forceRenderingOff = hidden || firstPersonOriginalForceOffStates[i];
        }
    }
    public void SetMapMode(bool value)
    {
        var loop = FirstProductionLoop.Current;
        if (value && loop != null)
        {
            if (InteractionUIOpen || PrototypeDayCycle.SettlementOpen || viewCamera == null) return;
            if (Tablet == null)
            {
                var device = new GameObject("현장 관리 태블릿");
                device.transform.SetParent(transform, false);
                Tablet = device.AddComponent<FactoryManagementTablet>();
                Tablet.Initialize(this, loop);
            }
            Tablet.Open();
            loop.ShowWashGun(false);
        }
        else if (!value && TabletOpen)
        {
            Tablet.gameObject.SetActive(false);
            if (loop != null) loop.ShowWashGun(loop.WashToolActive);
        }
        MapMode = value;
        if (brain != null) brain.enabled = !value;
        if (mapCamera != null) { mapCamera.enabled=value && !TabletOpen; if(mapCamera.enabled)mapCamera.SetOverview(true); }
        if (!value && viewCamera != null) viewCamera.orthographic=false;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        if(interactor!=null)interactor.inputEnabled=!value && !InteractionUIOpen && (loop == null || !loop.WashToolActive);
        SetFirstPersonBodyHidden(FirstPerson && !value);
    }
    void Update()
    {
        var keyboard=Keyboard.current; var mouse=Mouse.current;
        // Escape always makes the pointer available, including during settlement or while a UI is open.
        if(keyboard!=null && keyboard.escapeKey.wasPressedThisFrame)
        { Cursor.lockState=CursorLockMode.None; Cursor.visible=true; }
        if(PrototypeDayCycle.SettlementOpen)
        {
            if (TabletOpen) SetMapMode(false);
            if(interactor!=null)interactor.inputEnabled=false;
            return;
        }
        bool touch = Touchscreen.current != null && Touchscreen.current.primaryTouch.press.wasReleasedThisFrame;
        bool click = mouse != null && mouse.leftButton.wasReleasedThisFrame;
        if (TabletOpen)
        {
            if (keyboard != null && (keyboard.tabKey.wasPressedThisFrame || keyboard.escapeKey.wasPressedThisFrame))
                SetMapMode(false);
            else if (touch || click)
                Tablet.Tap(touch ? Touchscreen.current.primaryTouch.position.ReadValue() : mouse.position.ReadValue());
        }
        else if (!InteractionUIOpen)
        {
            bool open = keyboard != null && keyboard.tabKey.wasPressedThisFrame;
            if (FirstProductionLoop.Current != null && (touch || click))
            {
                var point = touch ? Touchscreen.current.primaryTouch.position.ReadValue() : mouse.position.ReadValue();
                open |= PrototypeGui.ToScreenRect(TabletButton).Contains(new Vector2(point.x, Screen.height-point.y));
            }
            if (open) SetMapMode(!MapMode);
            else if (!MapMode && keyboard!=null && keyboard.cKey.wasPressedThisFrame)SetFirstPerson(!FirstPerson);
        }
        // Clicking any visible admin control must remain a UI click, never cursor recapture or a hammer hit.
        bool overAdminPanel=PrototypePlayerTestControls.PointerOverAdminPanel(mouse);
        bool overGameUI=PointerOverInterface;
        if(interactor!=null)interactor.inputEnabled=ControlsActive&&!overAdminPanel&&!overGameUI&&(FirstProductionLoop.Current==null||!FirstProductionLoop.Current.WashToolActive);
        bool looking=ControlsActive && !overGameUI && mouse!=null && mouse.rightButton.isPressed;
        if(looking)
        {
            RotateView(mouse.delta.ReadValue()*settings.lookSensitivity);
        }
        Vector2 move=Vector2.zero; bool sprint=false,jump=false;
        if(ControlsActive && keyboard!=null)
        {
            move=new Vector2((keyboard.dKey.isPressed?1:0)-(keyboard.aKey.isPressed?1:0),(keyboard.wKey.isPressed?1:0)-(keyboard.sKey.isPressed?1:0));
            sprint=keyboard.leftShiftKey.isPressed; jump=keyboard.spaceKey.wasPressedThisFrame;
        }
        SimulateMovement(move,sprint,jump,Time.deltaTime,looking);
        if(transform.position.y < -12f)Teleport(SpawnPoint);
    }
    /// <summary>Same path for live input and deterministic movement checks.</summary>
    public void SimulateMovement(Vector2 input, bool sprint, bool jump, float dt, bool looking = false)
    {
        if(controller==null)controller=GetComponent<CharacterController>();
        Physics.SyncTransforms();
        ApplySupportMotion();
        grounded=controller.isGrounded;
        RaycastHit ground;
        bool onSurface=Physics.SphereCast(transform.position+Vector3.up*.3f,.16f,Vector3.down,out ground,.55f,(1<<0)|(1<<11),QueryTriggerInteraction.Ignore) && ground.normal.y>.45f;
        if(onSurface && verticalSpeed<=0)grounded=true;
        BeltVelocity=Vector3.zero;
        if(grounded && onSurface)
        {
            var belt=ground.collider.GetComponent<SplineConveyorDeformer>();
            if(belt!=null)BeltVelocity=belt.GetSurfaceVelocity(ground.point);
        }
        if(grounded && verticalSpeed<0)verticalSpeed=-2f;
        bool tookOff=grounded && jump;
        if(tookOff){verticalSpeed=Mathf.Sqrt(2f*settings.jumpHeight*-settings.playerGravity);grounded=false;BeltVelocity=Vector3.zero;}
        verticalSpeed+=settings.playerGravity*dt;
        var direction=ResolveMoveDirection(input);
        if(direction.sqrMagnitude>.001f || looking)
        {
            var facing=direction.sqrMagnitude>.001f?Quaternion.LookRotation(direction):Quaternion.Euler(0,yaw,0);
            transform.rotation=Quaternion.RotateTowards(transform.rotation,facing,(looking?lookTurnSpeed:movementTurnSpeed)*Mathf.Max(0f,dt));
        }
        if(bodyAnimator!=null && bodyAnimator.transform!=transform)bodyAnimator.transform.localRotation=Quaternion.identity;
        if(holdAnchor!=null && holdAnchor.parent==transform)holdAnchor.localRotation=Quaternion.identity;
        float speed=sprint?settings.playerSprintSpeed:settings.playerWalkSpeed;
        // In map/cursor mode the character stands still relative to the belt, rather than freezing in world space.
        controller.Move((direction*speed+BeltVelocity+Vector3.up*verticalSpeed)*dt);
        if((controller.collisionFlags&CollisionFlags.Above)!=0 && verticalSpeed>0)verticalSpeed=0;
        if(!tookOff)grounded=controller.isGrounded || onSurface && verticalSpeed<=0;
        if(tookOff || !grounded)ClearSupport();
        else if(onSurface)RememberSupport(ground);
        if(bodyAnimator!=null)
        {
            bodyAnimator.SetFloat(speedId,direction.magnitude*speed,.1f,Mathf.Max(.001f,dt));
            bodyAnimator.SetFloat(motionId,1f);
            Vector3 localPlanarVelocity=new Vector3(input.x,0,input.y);
            if(localPlanarVelocity.sqrMagnitude>1f)localPlanarVelocity.Normalize();
            localPlanarVelocity*=speed;
            if (!FirstPerson && bodyAnimator.transform != transform)
            {
                localPlanarVelocity = Vector3.forward * direction.magnitude * speed;
            }
            bodyAnimator.SetFloat(moveXId,localPlanarVelocity.x);
            bodyAnimator.SetFloat(moveZId,localPlanarVelocity.z);
            bodyAnimator.SetBool(groundId,grounded);
            // The run-up and landing are in the same take. Drive the airborne section with the
            // capsule velocity so a ledge, low ceiling or moving car cannot desync the pose.
            float launchSpeed = Mathf.Sqrt(2f * settings.jumpHeight * -settings.playerGravity);
            float flight = Mathf.Clamp01(.5f - verticalSpeed / Mathf.Max(.01f, 2f * launchSpeed));
            bodyAnimator.SetFloat(jumpPhaseId, Mathf.Lerp(.30f, .72f, flight));
            bodyAnimator.SetBool(jumpId,tookOff);
            bodyAnimator.SetBool(fallId,!grounded && verticalSpeed<-3f);
        }
    }
    public void Teleport(Vector3 position)
    { if(controller==null)controller=GetComponent<CharacterController>();ClearSupport();controller.enabled=false;transform.position=position;controller.enabled=true;verticalSpeed=0; }
    void LateUpdate()
    {
        if (TabletOpen) { Tablet.FrameCamera(); return; }
        if(MapMode || viewCamera==null)return;
        var q=Quaternion.Euler(pitch,yaw,0);
        var eye=transform.position+Vector3.up*1.62f;
        firstPersonCamera.transform.SetPositionAndRotation(eye+q*Vector3.forward*.14f,q);
        var shoulder=eye+q*Vector3.right*.38f;
        var offset=-(q*Vector3.forward)*settings.thirdPersonDistance;
        RaycastHit hit;
        if(Physics.SphereCast(shoulder,.16f,offset.normalized,out hit,offset.magnitude,(1<<0)|(1<<11),QueryTriggerInteraction.Ignore))
            offset=offset.normalized*Mathf.Max(.25f,hit.distance-.12f);
        thirdPersonCamera.transform.SetPositionAndRotation(shoulder+offset,q);
        brain.ManualUpdate();
    }
    void OnControllerColliderHit(ControllerColliderHit hit)
    {
        var rb=hit.rigidbody;
        if(rb!=null && !rb.isKinematic && hit.gameObject.layer==9)
            rb.AddForceAtPosition(new Vector3(hit.moveDirection.x,0,hit.moveDirection.z)*2f,hit.point,ForceMode.Impulse);
    }
    // Existing locomotion clips include these events. Prototype audio is a later polish task.
    void OnFootstep(AnimationEvent e) { }
    void OnLand(AnimationEvent e) { }
    void OnDisable()
    {
        if (TabletOpen) SetMapMode(false);
        Cursor.lockState=CursorLockMode.None;Cursor.visible=true;
        ClearSupport();
        SetFirstPersonBodyHidden(false);
    }
    void OnGUI()
    {
        using var gui=PrototypeGui.Scope();
        if (TabletOpen || PrototypeDayCycle.SettlementOpen) return;
        bool hasTablet = FirstProductionLoop.Current != null;
        GUI.Label(new Rect(18,14,800,25),"WASD 이동 · Shift 달리기 · Space 점프 · C 1/3인칭 · Tab " + (hasTablet ? "태블릿" : "지도") + " · 우클릭 드래그 시점");
        if (hasTablet && !InteractionUIOpen) GUI.Box(TabletButton, "관리 태블릿  [Tab]", GUI.skin.button);
        if(!MapMode && !InteractionUIOpen)
            GUI.Label(new Rect(PrototypeGui.Width/2-160,PrototypeGui.Height-36,320,25),"마우스 자유 이동 · 우클릭 드래그로 시점 회전");
    }
}
