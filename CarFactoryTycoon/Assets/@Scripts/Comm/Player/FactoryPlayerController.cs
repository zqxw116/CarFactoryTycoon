using Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>Local player; no player singleton. Prototype movement values remain tunable in the balance asset.</summary>
[DefaultExecutionOrder(-50)]
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
    public bool InteractionUIOpen { get; set; }
    public bool ControlsActive => !InteractionUIOpen && !MapMode && Cursor.lockState == CursorLockMode.Locked;
    public Vector3 BeltVelocity { get; private set; }
    public Vector3 SpawnPoint { get; set; }
    CharacterController controller;
    Renderer[] firstPersonHiddenRenderers;
    bool[] firstPersonOriginalForceOffStates;
    float yaw, pitch = 12f, verticalSpeed;
    bool grounded;
    int speedId=Animator.StringToHash("Speed"), motionId=Animator.StringToHash("MotionSpeed");
    int groundId=Animator.StringToHash("Grounded"), jumpId=Animator.StringToHash("Jump"), fallId=Animator.StringToHash("FreeFall");
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
        MapMode = value;
        if (brain != null) brain.enabled = !value;
        if (mapCamera != null) { mapCamera.enabled=value; if(value)mapCamera.SetOverview(true); }
        if (!value && viewCamera != null) viewCamera.orthographic=false;
        Cursor.lockState = value ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = value;
        if(interactor!=null)interactor.inputEnabled=!value;
        SetFirstPerson(FirstPerson);
    }
    void Update()
    {
        var keyboard=Keyboard.current; var mouse=Mouse.current;
        // Escape always makes the pointer available, including during settlement or while a UI is open.
        if(keyboard!=null && keyboard.escapeKey.wasPressedThisFrame)
        { Cursor.lockState=CursorLockMode.None; Cursor.visible=true; }
        if(PrototypeDayCycle.Current!=null && PrototypeDayCycle.Current.IsSettlement) {if(interactor!=null)interactor.inputEnabled=false;return;}
        if(keyboard!=null && keyboard.tabKey.wasPressedThisFrame) SetMapMode(!MapMode);
        if(!MapMode && keyboard!=null && keyboard.cKey.wasPressedThisFrame)SetFirstPerson(!FirstPerson);
        // Clicking any visible admin control must remain a UI click, never cursor recapture or a hammer hit.
        bool overAdminPanel=PrototypePlayerTestControls.PointerOverAdminPanel(mouse);
        bool overGameUI=UnityEngine.EventSystems.EventSystem.current!=null && UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject();
        bool recaptured=!overAdminPanel&&!overGameUI&&!InteractionUIOpen && !MapMode && Cursor.lockState!=CursorLockMode.Locked && mouse!=null && mouse.leftButton.wasPressedThisFrame;
        if(recaptured){Cursor.lockState=CursorLockMode.Locked;Cursor.visible=false;}
        if(interactor!=null)interactor.inputEnabled=ControlsActive&&!recaptured;
        if(ControlsActive && mouse!=null)
        {
            var look=mouse.delta.ReadValue()*settings.lookSensitivity;
            yaw+=look.x;pitch=Mathf.Clamp(pitch-look.y,-70,75);
            transform.rotation=Quaternion.Euler(0,yaw,0);
        }
        Vector2 move=Vector2.zero; bool sprint=false,jump=false;
        if(ControlsActive && keyboard!=null)
        {
            move=new Vector2((keyboard.dKey.isPressed?1:0)-(keyboard.aKey.isPressed?1:0),(keyboard.wKey.isPressed?1:0)-(keyboard.sKey.isPressed?1:0));
            sprint=keyboard.leftShiftKey.isPressed; jump=keyboard.spaceKey.wasPressedThisFrame;
        }
        SimulateMovement(move,sprint,jump,Time.deltaTime);
        if(transform.position.y < -12f)Teleport(SpawnPoint);
    }
    /// <summary>Same path for live input and deterministic movement checks.</summary>
    public void SimulateMovement(Vector2 input, bool sprint, bool jump, float dt)
    {
        if(controller==null)controller=GetComponent<CharacterController>();
        grounded=controller.isGrounded;
        RaycastHit ground;
        bool onSurface=Physics.SphereCast(transform.position+Vector3.up*.3f,.16f,Vector3.down,out ground,.5f,1<<0,QueryTriggerInteraction.Ignore);
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
        var direction=transform.TransformDirection(new Vector3(input.x,0,input.y));
        if(direction.sqrMagnitude>1)direction.Normalize();
        float speed=sprint?settings.playerSprintSpeed:settings.playerWalkSpeed;
        // In map/cursor mode the character stands still relative to the belt, rather than freezing in world space.
        controller.Move((direction*speed+BeltVelocity+Vector3.up*verticalSpeed)*dt);
        if((controller.collisionFlags&CollisionFlags.Above)!=0 && verticalSpeed>0)verticalSpeed=0;
        if(!tookOff)grounded=controller.isGrounded;
        if(bodyAnimator!=null)
        {
            bodyAnimator.SetFloat(speedId,direction.magnitude*speed,.1f,Mathf.Max(.001f,dt));
            bodyAnimator.SetFloat(motionId,1f);
            Vector3 localPlanarVelocity=new Vector3(input.x,0,input.y);
            if(localPlanarVelocity.sqrMagnitude>1f)localPlanarVelocity.Normalize();
            localPlanarVelocity*=speed;
            bodyAnimator.SetFloat(moveXId,localPlanarVelocity.x);
            bodyAnimator.SetFloat(moveZId,localPlanarVelocity.z);
            bodyAnimator.SetBool(groundId,grounded);
            bodyAnimator.SetBool(jumpId,tookOff);
            bodyAnimator.SetBool(fallId,!grounded && verticalSpeed<-3f);
        }
    }
    public void Teleport(Vector3 position)
    { controller.enabled=false;transform.position=position;controller.enabled=true;verticalSpeed=0; }
    void LateUpdate()
    {
        if(MapMode || viewCamera==null)return;
        var q=Quaternion.Euler(pitch,yaw,0);
        var eye=transform.position+Vector3.up*1.62f;
        firstPersonCamera.transform.SetPositionAndRotation(eye+transform.forward*.14f,q);
        var shoulder=eye+transform.right*.38f;
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
        Cursor.lockState=CursorLockMode.None;Cursor.visible=true;
        SetFirstPersonBodyHidden(false);
    }
    void OnGUI()
    {
        GUI.Label(new Rect(18,14,800,25),"WASD 이동 · Shift 달리기 · Space 점프 · C 1/3인칭 · Tab 지도 · Esc 마우스");
        if(!MapMode && !InteractionUIOpen)
            GUI.Label(new Rect(Screen.width/2-160,Screen.height-36,320,25),ControlsActive?"Esc · 마우스 해제 / 관리자 패널 클릭":"마우스 해제됨 · 클릭은 관리자 패널에서 사용, 바깥 클릭으로 작업 복귀");
    }
}
