using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>Local-player interaction. Scene/controller code owns camera mode and cursor capture.</summary>
[DefaultExecutionOrder(100)]
public sealed class PlayerAssemblyInteractor : MonoBehaviour
{
    public Transform cameraTransform;
    public Camera viewCamera;
    public Transform holdAnchor;
    public Transform hammerAnchor;
    public Animator bodyAnimator;
    public PrototypeBalanceSettings settings;
    public bool inputEnabled = true;
    public Transform ExternalCarryTarget;
    public Vector3 ExternalLeftGrip, ExternalRightGrip;
    FactoryPlayerController player;
    Vector3 BodyForward => player!=null?player.BodyForward:transform.forward;
    Vector3 BodyRight => player!=null?player.BodyRight:transform.right;
    bool InBodyReach(Vector3 point) => player==null || player.CanReachInteraction(point,interactionRange);
    public float SwingProgress => swingActive?Mathf.Clamp01(swingElapsed/WorkerHammerAnimation.Duration):0f;
    public float interactionRange = 3.5f;
    public float throwSpeed = 7f; // Provisional feel value.
    public bool showHints = true;
    public PortableAssemblyPart HeldPart { get; private set; }
    public AssemblyPart WorkingPart { get; private set; }
    public bool IsAttaching => attachingPart != null;
    public AssemblyPart FocusedPart => IsAttaching ? attachingPart : focusedPart;
    public float AttachmentProgress => IsAttaching ? Mathf.Clamp01(attachElapsed / AttachSeconds) : 0f;
    public string InteractionHint { get; private set; } = "";
    public bool HasInteractionTarget => focusedStack != null || focusedPortable != null || focusedPart != null || IsAttaching;
    public int PlayerHammerHits => hammerHitsOverride > 0 ? hammerHitsOverride : Mathf.Max(1, settings != null ? settings.requiredHits : 3);
    public event Action<AssemblyPart> HammerHit;

    private AssemblyPart attachingPart, focusedPart;
    private CarController focusedCar, cachedCar;
    private AssemblyPart[] cachedParts;
    private PartStack focusedStack;
    private PortableAssemblyPart focusedPortable;
    private readonly RaycastHit[] hits = new RaycastHit[48];
    private float attachElapsed, lastHitTime = float.NegativeInfinity;
    private int hammerHitsOverride;
    private Transform hammerVisual;
    private Quaternion hammerRestRotation;
    private Vector3 hammerRestPosition;
    private Quaternion rightHandRestRotation;
    private float rightArmLength = .65f;

    private BoxCollider hammerHead;
    private PrototypeHammerContact hammerContact;
    private bool swingActive, swingContacted;
    private float swingElapsed, swingDepth;
    private Vector3 previousHeadPosition;
    private Quaternion previousHeadRotation;
    public bool IsSwinging => swingActive;
    public Vector3 HammerHeadPosition => hammerHead != null ? hammerHead.transform.position : Vector3.zero;
    public float attachmentPreviewRadius = 4f;
    readonly Collider[] nearbyCarColliders = new Collider[64];
    readonly List<CarController> nearbyCars = new List<CarController>();
    public IReadOnlyList<CarController> NearbyCars => nearbyCars;
    private ParticleSystem sparks;
    private Material toolMaterial, sparkMaterial;
    private PlayerAssemblyHandIK handIK;
    private Transform runtimeHoldAnchor;
    private Vector3 lastHitPoint;
    private readonly Dictionary<AssemblyPart, Bounds> targetBounds = new Dictionary<AssemblyPart, Bounds>();

    private float AttachSeconds => Mathf.Max(.01f, settings != null ? settings.playerAttachSeconds : 2f);
    private float HitInterval => Mathf.Max(.3f, settings != null ? settings.playerHitInterval : .3f);
    private Vector3 ReachOrigin => transform.position + Vector3.up * 1.1f;
    private Transform View => cameraTransform != null ? cameraTransform : (viewCamera != null ? viewCamera.transform : transform);

    private void Start()
    {
        player=GetComponent<FactoryPlayerController>();
        EnsureAnchors();
        CreateHammer();
        if (bodyAnimator != null && bodyAnimator.isHuman)
        {
            var upper = bodyAnimator.GetBoneTransform(HumanBodyBones.RightUpperArm);
            var lower = bodyAnimator.GetBoneTransform(HumanBodyBones.RightLowerArm);
            rightHandRestRotation = Quaternion.Inverse(transform.rotation) * hammerAnchor.rotation;
            if (upper != null && lower != null)
                rightArmLength = Vector3.Distance(upper.position, lower.position) + Vector3.Distance(lower.position, hammerAnchor.position);
        }
        hammerContact = gameObject.AddComponent<PrototypeHammerContact>();
        if (bodyAnimator != null && bodyAnimator.isHuman)
        {
            handIK = bodyAnimator.GetComponent<PlayerAssemblyHandIK>();
            if (handIK == null) handIK = bodyAnimator.gameObject.AddComponent<PlayerAssemblyHandIK>();
            handIK.interactor = this;
        }
    }

    private void EnsureAnchors()
    {
        if(player==null)player=GetComponent<FactoryPlayerController>();
        if(player!=null && player.CarryAnchor!=null)holdAnchor=player.CarryAnchor;
        if (holdAnchor == null)
        {
            var anchor = new GameObject("PortablePartGrip");
            runtimeHoldAnchor = anchor.transform;
            runtimeHoldAnchor.SetParent(transform, false);
            runtimeHoldAnchor.localPosition = new Vector3(0f, 1.15f, .7f);
            holdAnchor = runtimeHoldAnchor;
        }
        if (hammerAnchor == null && bodyAnimator != null && bodyAnimator.isHuman)
            hammerAnchor = bodyAnimator.GetBoneTransform(HumanBodyBones.RightHand);
        if (hammerAnchor == null) hammerAnchor = holdAnchor;
    }

    private void Update()
    {
        if (swingActive) AdvanceHammerSwing(Time.deltaTime);
        RefreshNearbyCars();
        AdvanceHeldAttachment(Keyboard.current != null && Keyboard.current.eKey.isPressed, Time.deltaTime);
        if (WorkingPart != null && (!WorkingPart.gameObject.activeInHierarchy || !WorkingPart.CanPlayerRepair)) WorkingPart = null;
        if (!inputEnabled)
        {
            ClearFocus();
            return;
        }
        UpdateFocus();
        var keyboard = Keyboard.current;
        var mouse = Mouse.current;
        if (keyboard != null && keyboard.eKey.wasPressedThisFrame)
        {
            if (HeldPart != null && focusedCar != null) TryAttach(focusedCar);
            else if (focusedPortable != null) TryPickUp(focusedPortable);
            else if (focusedStack != null) TryTake(focusedStack);
        }
        if (keyboard != null && keyboard.gKey.wasPressedThisFrame) TryThrow();
        if (mouse != null && mouse.leftButton.wasPressedThisFrame &&
            !(UnityEngine.EventSystems.EventSystem.current != null && UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject()) &&
            !PrototypePlayerTestControls.PointerOverAdminPanel(mouse) && (player==null || !player.PointerOverInterface)) TrySwingHammer();
        UpdateHint();
    }

    public void SetPlayerHammerHits(int hitsRequired)
    {
        // M2 exposes the first upgrade. Economy/upgrade purchase rules are not implemented here.
        hammerHitsOverride = Mathf.Clamp(hitsRequired, 2, 3);
    }

    public bool TryTake(PartStack stack)
    {
        if (ExternalCarryTarget!=null || stack == null || !stack.gameObject.activeInHierarchy || HeldPart != null || IsAttaching || !WithinReach(stack)) return false;
        EnsureAnchors();
        if (!stack.IsReadySlotConfigured || !stack.TryTakeReadyPortable(gameObject, holdAnchor, out var portable)) return false;
        if (!portable.TryHold(this, holdAnchor))
        {
            stack.Add(1);
            portable.ReleaseToPool();
            return false;
        }
        HeldPart = portable;
        WorkingPart = null;
        return true;
    }

    public bool TryPickUp(PortableAssemblyPart portable)
    {
        if (ExternalCarryTarget!=null || portable == null || !portable.gameObject.activeInHierarchy || portable.IsHeld || HeldPart != null || IsAttaching ||
            Vector3.Distance(ReachOrigin, portable.transform.position) > interactionRange) return false;
        EnsureAnchors();
        if (!portable.TryHold(this, holdAnchor)) return false;
        HeldPart = portable;
        WorkingPart = null;
        return true;
    }

    public bool TryThrow()
    {
        if (HeldPart == null || IsAttaching) return false;
        Vector3 velocity = View.forward * throwSpeed + Vector3.up * 1.5f;
        if (!HeldPart.Throw(this, velocity)) return false;
        HeldPart = null;
        return true;
    }

    public bool TryAttach(CarController car)
    {
        if (swingActive || HeldPart == null || IsAttaching || car == null || !car.gameObject.activeInHierarchy || !car.CanRepair) return false;
        var part = FindPart(car, HeldPart.PartType, true, AimPoint(car));
        if (part == null || !IsAimingAt(part)) return false;
        if (!part.TryBeginPlayerAttachment(this, HeldPart.transform.position, HeldPart.transform.rotation)) return false;
        attachingPart = part;
        WorkingPart = part;
        attachElapsed = 0f;
        HeldPart.Show(false);
        return true;
    }

    // Compatibility entry point: requests a swing, never awards work on click.
    public bool TryHammer(AssemblyPart part) => TrySwingHammer();

    public bool TrySwingHammer()
    {
        if (!inputEnabled || ExternalCarryTarget!=null || HeldPart != null || IsAttaching) return false;
        // Restart is explicit, including rapid clicks. Never sweep from the old pose to the reset pose.
        if(swingActive)WorkerHammerAnimation.Stop(bodyAnimator);
        EnsureAnchors(); CreateHammer();
        if (hammerContact == null) hammerContact = gameObject.AddComponent<PrototypeHammerContact>();
        lastHitTime = Time.time;
        if (bodyAnimator != null && bodyAnimator.transform != transform)
            bodyAnimator.transform.rotation = transform.rotation;
        hammerVisual.gameObject.SetActive(true);
        if (!WorkerHammerAnimation.Play(bodyAnimator)) return false;
        swingActive = true; swingContacted = false; swingElapsed = 0f;
        Vector3 idleTarget = transform.position + Vector3.up * 1.35f + View.forward * 1.2f;
        swingDepth = focusedPart != null && IsAimingAt(focusedPart)
            ? Vector3.Distance(View.position, InteractionPoint(focusedPart)) : Vector3.Distance(View.position, idleTarget);
        if(focusedPart!=null && hammerContact.TrySurfacePoint(focusedPart,new Ray(View.position,View.forward),ReachOrigin,interactionRange,out var surfacePoint))
            swingDepth=Vector3.Distance(View.position,surfacePoint)+.025f;
        swingDepth = Mathf.Max(.45f, swingDepth);
        bodyAnimator.Update(0f);
        PlaceHammer(0f);
        previousHeadPosition = hammerHead.transform.position; previousHeadRotation = hammerHead.transform.rotation;
        return true;
    }

    public void RefreshNearbyCars()
    {
        nearbyCars.Clear();
        int count = Physics.OverlapSphereNonAlloc(transform.position + Vector3.up * .9f, attachmentPreviewRadius,
            nearbyCarColliders, (1 << 6) | (1 << 11), QueryTriggerInteraction.Collide);
        for (int i = 0; i < count; i++)
        {
            var car = nearbyCarColliders[i].GetComponentInParent<CarController>();
            if (car != null && car.gameObject.activeInHierarchy && car.CanRepair && !nearbyCars.Contains(car)) nearbyCars.Add(car);
        }
    }
    public bool PreviewEligible(AssemblyPart part)
    {
        if (part == null || HeldPart == null || !inputEnabled ||
            part.myType != HeldPart.PartType || !part.CanPlayerRepair || part.IsPlayerAttached || part.IsPlayerAttaching || part.currentWork > 0f) return false;
        return nearbyCars.Contains(part.GetComponentInParent<CarController>());
    }

    Vector3 SwingTarget(float t)
    {
        // The direction is read throughout the swing. Nothing tracks the initially selected part.
        Vector3 shoulder = transform.position + Vector3.up * 1.35f + BodyRight * .22f;
        Vector3 aimed = View.position + View.forward * swingDepth;
        if(Vector3.Dot(Vector3.ProjectOnPlane(aimed-shoulder,Vector3.up),BodyForward)<0f)
            aimed=shoulder+BodyForward*(rightArmLength+.15f);
        aimed = shoulder + Vector3.ClampMagnitude(aimed - shoulder, rightArmLength + .22f);
        return aimed; // The real clip supplies the arc; aim only steers it slightly.
    }
    void PlaceHammer(float t)
    {
        if (hammerHead == null || hammerVisual == null) return;
        // A real grip: only the arm IK moves the tool through space. Never teleport the
        // hammer to the aim point, which detached it from the hand and granted remote hits.
        hammerVisual.localPosition = hammerRestPosition;
        hammerVisual.localRotation = hammerRestRotation;
        hammerVisual.localScale = Vector3.one * (PlayerHammerHits == 2 ? 1.2f : 1f);
    }
    public void AdvanceHammerSwing(float deltaTime)
    {
        if (swingActive) swingElapsed += Mathf.Min(.5f, Mathf.Max(0f, deltaTime));
    }
    void AdvanceHammerStep(float deltaTime)
    {
        if (!swingActive) return;
        if (!inputEnabled || ExternalCarryTarget!=null || HeldPart != null || IsAttaching)
        { swingActive = false; WorkerHammerAnimation.Stop(bodyAnimator); return; }
        float t = Mathf.Clamp01(swingElapsed / WorkerHammerAnimation.Duration);
        PlaceHammer(t);
        if (!swingContacted && t > 0f && t < 1f)
        {
            var line=PrototypeLineController.Current;
            float headRadius=hammerHead!=null?Mathf.Max(.06f,hammerHead.bounds.extents.magnitude):.12f;
            if(line!=null && line.TryHammerContact(previousHeadPosition,hammerHead.transform.position,headRadius))
                swingContacted=true;
            if (!swingContacted && hammerContact.Sweep(hammerHead, previousHeadPosition, previousHeadRotation,
                hammerHead.transform.position, hammerHead.transform.rotation, ReachOrigin, interactionRange, out var part, out var point))
            {
                swingContacted=true;
                if (part != null && InBodyReach(point) && part.CanPlayerRepair && part.GetComponentInParent<CarController>().CanRepair && part.TryPlayerHammer(PlayerHammerHits))
                {
                    swingContacted = true;
                    WorkingPart = part; lastHitPoint = point;
                    EmitImpact(point, part.IsAssembled);
                    PrototypeWorkAudio.PlayWorkPulse(point, part.IsAssembled, PlayerHammerHits == 2, false);
                    HammerHit?.Invoke(part);
                }
            }
        }
        previousHeadPosition = hammerHead.transform.position; previousHeadRotation = hammerHead.transform.rotation;
        if (t >= 1f) { swingActive = false; WorkerHammerAnimation.Stop(bodyAnimator); }
    }

    private void AdvanceAttachment(float deltaTime)
    {
        if (attachingPart == null) return;
        var car = attachingPart.GetComponentInParent<CarController>();
        if (HeldPart == null || car == null || !car.gameObject.activeInHierarchy || !car.CanRepair ||
            Vector3.Distance(ReachOrigin, InteractionPoint(attachingPart)) > interactionRange)
        {
            CancelAttachment();
            return;
        }
        attachElapsed += deltaTime;
        if (!attachingPart.AdvancePlayerAttachment(this, attachElapsed / AttachSeconds)) { CancelAttachment(); return; }
        if (attachElapsed < AttachSeconds) return;
        if (!attachingPart.CompletePlayerAttachment(this)) { CancelAttachment(); return; }
        WorkingPart = attachingPart;
        attachingPart = null;
        HeldPart.ReleaseToPool();
        HeldPart = null;
        // First hit also waits the configured click interval after hold attachment completes.
        lastHitTime = Time.time;
    }

    // Live input and play-mode checks share the same hold, aim, ownership and reach gates.
    public void AdvanceHeldAttachment(bool held, float deltaTime)
    {
        if (!IsAttaching) return;
        if (!held || !inputEnabled || !IsAimingAt(attachingPart))
        { CancelAttachment(); return; }
        AdvanceAttachment(Mathf.Max(0f, deltaTime));
    }

    public Vector3 AttachmentAimPoint(AssemblyPart part)
    {
        return AttachmentBounds(part).center;
    }

    private Bounds LocalTargetBounds(AssemblyPart part)
    {
        if (targetBounds.TryGetValue(part, out var bounds)) return bounds;
        bool found = false;
        // Cache authored geometry in part space; activation and attachment animation never change it.
        foreach (var filter in part.GetComponentsInChildren<MeshFilter>(true))
        {
            if (filter.sharedMesh == null || filter.GetComponentInParent<AssemblyPart>(true) != part) continue;
            var matrix = part.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
            var meshBounds = filter.sharedMesh.bounds;
            for (int corner=0;corner<8;corner++)
            {
                var point = matrix.MultiplyPoint3x4(Corner(meshBounds,corner));
                if (!found) {bounds = new Bounds(point,Vector3.zero);found=true;} else bounds.Encapsulate(point);
            }
        }
        if (!found) bounds = new Bounds(Vector3.zero,Vector3.one*.2f);
        targetBounds.Add(part,bounds);
        return bounds;
    }

    private static Vector3 Corner(Bounds bounds,int corner)
    {
        var e = bounds.extents;
        return bounds.center + new Vector3((corner&1)==0?-e.x:e.x,(corner&2)==0?-e.y:e.y,(corner&4)==0?-e.z:e.z);
    }

    private Bounds TransformBounds(Bounds local,Matrix4x4 matrix)
    {
        var bounds = new Bounds(matrix.MultiplyPoint3x4(Corner(local,0)),Vector3.zero);
        for(int corner=1;corner<8;corner++)bounds.Encapsulate(matrix.MultiplyPoint3x4(Corner(local,corner)));
        return bounds;
    }

    public Bounds AttachmentBounds(AssemblyPart part)
    {
        return TransformBounds(LocalTargetBounds(part),Matrix4x4.TRS(part.AttachmentWorldPosition,part.AttachmentWorldRotation,part.transform.lossyScale));
    }

    public Vector3 InteractionPoint(AssemblyPart part)
    {
        var bounds = AttachmentBounds(part);
        var ray = new Ray(View.position,View.forward);
        if(bounds.IntersectRay(ray,out float distance))return ray.GetPoint(Mathf.Max(0f,distance));
        float along = Mathf.Max(0f,Vector3.Dot(bounds.center-ray.origin,ray.direction));
        return bounds.ClosestPoint(ray.GetPoint(along));
    }

    public bool IsAimingAt(AssemblyPart part)
    {
        if (part == null) return false;
        var bounds = AttachmentBounds(part);
        var size = bounds.size;
        // Small lamps/mirrors get a modest minimum target. Large panels accept their whole surface.
        bounds.size = new Vector3(Mathf.Max(.28f,size.x+.18f),Mathf.Max(.28f,size.y+.18f),Mathf.Max(.28f,size.z+.18f));
        var ray = new Ray(View.position, View.forward);
        if(!bounds.IntersectRay(ray,out float along) || along < 0f)return false;
        var target = InteractionPoint(part);
        if (Vector3.Distance(ReachOrigin, target) > interactionRange || !InBodyReach(target)) return false;
        int count = Physics.RaycastNonAlloc(ray, hits, along, ~(1 << 10), QueryTriggerInteraction.Ignore);
        var car = part.GetComponentInParent<CarController>();
        for (int i=0;i<count;i++)
            if (hits[i].collider != null && hits[i].collider.GetComponentInParent<CarController>() != car &&
                !hits[i].collider.transform.IsChildOf(transform)) return false;
        return true;
    }

    private void CancelAttachment()
    {
        if (attachingPart != null) attachingPart.CancelPlayerAttachment(this);
        attachingPart = null;
        WorkingPart = null;
        if (HeldPart != null) HeldPart.Show(true);
    }

    private bool WithinReach(PartStack stack)
    {
        if (!stack.IsReadySlotConfigured || !stack.HasReadyPart) return false;
        Vector3 point = stack.ReadyBounds.ClosestPoint(ReachOrigin);
        return Vector3.Distance(ReachOrigin, point) <= interactionRange && InBodyReach(point);
    }

    private Vector3 AimPoint(CarController car)
    {
        var ray = new Ray(View.position, View.forward);
        float along = Mathf.Max(0f, Vector3.Dot(car.transform.position - ray.origin, ray.direction));
        return ray.GetPoint(along);
    }

    private AssemblyPart FindPart(CarController car, PartType type, bool needsStock, Vector3 aimedPoint)
    {
        if (car != cachedCar || cachedParts == null)
        {
            cachedCar = car;
            cachedParts = car.GetComponentsInChildren<AssemblyPart>(true);
        }
        AssemblyPart closest = null;
        float best = float.PositiveInfinity;
        foreach (var part in cachedParts)
        {
            if (part == null || !part.CanPlayerRepair || part.IsPlayerAttaching) continue;
            if (type != PartType.None && part.myType != type) continue;
            if (needsStock ? (part.IsPlayerAttached || part.currentWork > 0f) : (!part.IsPlayerAttached && part.currentWork <= 0f)) continue;
            Vector3 anchor = InteractionPoint(part);
            if (Vector3.Distance(ReachOrigin, anchor) > interactionRange) continue;
            float distance = (anchor - aimedPoint).sqrMagnitude;
            if (!IsAimingAt(part)) continue;
            if (distance >= best) continue;
            best = distance;
            closest = part;
        }
        return closest;
    }

    private void ClearFocus()
    {
        focusedStack = null; focusedPortable = null; focusedPart = null; focusedCar = null;
        InteractionHint = "";
    }

    private void UpdateFocus()
    {
        ClearFocus();
        if (IsAttaching) { InteractionHint = "E 유지 · 조준을 유지해 부착 중…"; return; }
        Transform view = View;
        float rayLength = interactionRange + Vector3.Distance(view.position, ReachOrigin) + 1f;
        int count = Physics.RaycastNonAlloc(view.position, view.forward, hits, rayLength, ~0, QueryTriggerInteraction.Collide);
        float closest = float.PositiveInfinity;
        RaycastHit selected = default;
        for (int i = 0; i < count; i++)
        {
            var collider = hits[i].collider;
            if (collider == null || collider.transform.IsChildOf(transform) || hits[i].distance >= closest) continue;
            PartStack hitStack = collider.GetComponentInParent<PartStack>();
            if (hitStack != null && hitStack.IsReadySlotConfigured &&
                (collider != hitStack.ReadyClickCollider || !hitStack.HasReadyPart)) continue;
            bool relevant = hitStack != null || collider.GetComponentInParent<PortableAssemblyPart>() != null || collider.GetComponentInParent<CarController>() != null;
            if (collider.isTrigger && !relevant) continue;
            closest = hits[i].distance;
            selected = hits[i];
        }
        if (selected.collider == null) { UpdateHint(); return; }
        if (HeldPart == null)
        {
            var stack = selected.collider.GetComponentInParent<PartStack>();
            if (stack != null && WithinReach(stack)) focusedStack = stack;
            var portable = selected.collider.GetComponentInParent<PortableAssemblyPart>();
            if (portable != null && !portable.IsHeld && Vector3.Distance(ReachOrigin, portable.transform.position) <= interactionRange) focusedPortable = portable;
        }
        focusedCar = selected.collider.GetComponentInParent<CarController>();
        if (focusedCar != null && focusedCar.CanRepair)
            focusedPart = FindPart(focusedCar, HeldPart != null ? HeldPart.PartType : PartType.None, HeldPart != null, selected.point);
        if (focusedPart == null) focusedCar = null;
        UpdateHint();
    }

    private void UpdateHint()
    {
        if (IsAttaching) { InteractionHint = "E 유지 · 조준을 유지해 부착 중…"; return; }
        if (HeldPart != null)
        {
            InteractionHint = focusedPart != null ? $"{PartLabel(HeldPart.PartType)} · E 꾹 눌러 부착 / G 던지기" : $"{PartLabel(HeldPart.PartType)} · 부착 지점 조준 / G 던지기";
            return;
        }
        if (focusedPortable != null) { InteractionHint = $"{PartLabel(focusedPortable.PartType)} · E 줍기"; return; }
        if (focusedStack != null && focusedStack.HasReadyPart) { InteractionHint = $"{PartLabel(focusedStack.partType)} ({focusedStack.Count}) · 거치대 E 집기"; return; }
        if (focusedPart != null) { InteractionHint = $"{PartLabel(focusedPart.myType)} · 클릭 망치질 ({focusedPart.PlayerHitsRemaining(PlayerHammerHits)}회 남음)"; return; }
        InteractionHint = "E 집기·부착   G 던지기   클릭 망치질";
    }

    public static string PartLabel(PartType type)
    {
        var progression = ProductionProgression.Current;
        if (progression != null && progression.processes != null)
            foreach (var process in progression.processes)
                if (process.parts != null && Array.IndexOf(process.parts, type) >= 0)
                {
                    switch (type)
                    {
                        case PartType.Wheel_FrontRight_41: return "오른쪽 앞바퀴";
                        case PartType.Wheel_BehindRight_42: return "오른쪽 뒷바퀴";
                        case PartType.Wheel_FrontLeft_43: return "왼쪽 앞바퀴";
                        case PartType.Wheel_BehindLeft_44: return "왼쪽 뒷바퀴";
                        default: return process.displayName;
                    }
                }
        return "부품";
    }
    private void CreateHammer()
    {
        if (hammerVisual != null) return;
        var tool = new GameObject("PlayerHammer");
        hammerVisual = tool.transform;
        hammerVisual.SetParent(hammerAnchor, false);
        float shaftWidth = WorkerHammerAnimation.CalibrateGrip(bodyAnimator, hammerVisual);
        hammerRestPosition = hammerVisual.localPosition;
        hammerRestRotation = hammerVisual.localRotation;
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        if (shader != null) { toolMaterial = new Material(shader); toolMaterial.color = new Color(.25f, .27f, .29f); }
        MakeToolPiece("Handle", PrimitiveType.Cylinder, new Vector3(0, .10f, 0), new Vector3(shaftWidth, .15f, shaftWidth));
        MakeToolPiece("Head", PrimitiveType.Cube, new Vector3(0, WorkerHammerAnimation.HeadDistance, 0), new Vector3(.21f, .10f, .09f));
    }

    private void MakeToolPiece(string label, PrimitiveType shape, Vector3 position, Vector3 scale)
    {
        var piece = GameObject.CreatePrimitive(shape);
        piece.name = label;
        piece.layer = gameObject.layer;
        piece.transform.SetParent(hammerVisual, false);
        piece.transform.localPosition = position; piece.transform.localScale = scale;
        var collider = piece.GetComponent<Collider>(); collider.enabled = false;
        if (label == "Head") hammerHead = collider as BoxCollider; else Destroy(collider);
        if (toolMaterial != null) piece.GetComponent<Renderer>().sharedMaterial = toolMaterial;
    }

    private void LateUpdate()
    {
        if (hammerVisual == null) return;
        hammerVisual.gameObject.SetActive(inputEnabled && HeldPart == null && ExternalCarryTarget==null);
        if (swingActive) { AdvanceHammerStep(0f); return; }
        hammerVisual.localPosition = hammerRestPosition;
        hammerVisual.localRotation = hammerRestRotation;
        hammerVisual.localScale = Vector3.one * (PlayerHammerHits == 2 ? 1.2f : 1f);
    }

    public void ApplyHandIK(Animator animator)
    {
        if (animator == null || !animator.isHuman) return;
        bool carrying = HeldPart != null && !IsAttaching;
        if(ExternalCarryTarget!=null && !carrying && !IsAttaching && !swingActive)
        {
            SetHand(animator,AvatarIKGoal.LeftHand,ExternalCarryTarget.TransformPoint(ExternalLeftGrip),.9f);
            SetHand(animator,AvatarIKGoal.RightHand,ExternalCarryTarget.TransformPoint(ExternalRightGrip),.9f);
            return;
        }
        float workWeight = inputEnabled && (IsAttaching || swingActive) ? 1f : 0f;
        Vector3 target = swingActive ? SwingTarget(Mathf.Clamp01(swingElapsed / WorkerHammerAnimation.Duration)) : lastHitPoint;
        if (IsAttaching)
            target = TransformBounds(LocalTargetBounds(attachingPart),attachingPart.transform.localToWorldMatrix).center;
        if (carrying)
        {
            float half = Mathf.Clamp(HeldPart.LocalBounds.extents.x, .12f, .45f);
            SetHand(animator, AvatarIKGoal.LeftHand, HeldPart.transform.position - BodyRight * half, .85f);
            SetHand(animator, AvatarIKGoal.RightHand, HeldPart.transform.position + BodyRight * half, .85f);
        }
        else
        {
            SetHand(animator, AvatarIKGoal.LeftHand, target - BodyRight * .15f, IsAttaching ? workWeight : 0f);
            if (IsAttaching)
                SetHand(animator, AvatarIKGoal.RightHand, target, workWeight);
            else if (swingActive)
            {
                Vector3 gripToHead = hammerHead != null && hammerAnchor != null
                    ? hammerHead.transform.position - hammerAnchor.position : Vector3.zero;
                SetHand(animator, AvatarIKGoal.RightHand,
                    WorkerHammerAnimation.Aim(animator, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm,
                        HumanBodyBones.RightHand, target - gripToHead), .35f);
            }
            else SetHand(animator, AvatarIKGoal.RightHand, Vector3.zero, 0f);
        }
    }

    private static void SetHand(Animator animator, AvatarIKGoal goal, Vector3 target, float weight)
    {
        animator.SetIKPositionWeight(goal, weight);
        animator.SetIKRotationWeight(goal, 0f);
        if (weight > 0f)
        {
            bool left = goal == AvatarIKGoal.LeftHand;
            animator.SetIKPosition(goal, WorkerHammerAnimation.Aim(animator,
                left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm,
                left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm,
                left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand, target, 1f));
        }
    }

    private void EmitImpact(Vector3 position, bool completed)
    {
        if (sparks == null)
        {
            var go = new GameObject("PlayerHammerImpact");
            go.transform.SetParent(transform, false);
            sparks = go.AddComponent<ParticleSystem>();
            sparks.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = sparks.main; main.loop = false; main.playOnAwake = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(.24f, .48f); main.startSpeed = new ParticleSystem.MinMaxCurve(2.8f, 5.2f); main.startSize = new ParticleSystem.MinMaxCurve(.045f, .085f);
            main.maxParticles = 96; main.simulationSpace = ParticleSystemSimulationSpace.World;
            var emission = sparks.emission; emission.enabled = false;
            var shape = sparks.shape; shape.shapeType = ParticleSystemShapeType.Sphere; shape.radius = .015f;
            Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader != null)
            {
                sparkMaterial = new Material(shader);
                sparks.GetComponent<ParticleSystemRenderer>().sharedMaterial = sparkMaterial;
            }
        }
        sparks.transform.position = position;
        var settings = sparks.main;
        settings.startColor = completed ? new Color(.5f, 1f, 1f) : new Color(1f, .7f, .15f);
        sparks.Emit(completed ? 36 : 22);
    }

    private void OnGUI()
    {
        using var gui=PrototypeGui.Scope();
        if (!showHints || !inputEnabled) return;
        GUI.Label(new Rect(PrototypeGui.Width * .5f - 4, PrototypeGui.Height * .5f - 10, 20, 24), "+");
        var style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 16 };
        GUI.Box(new Rect(PrototypeGui.Width * .5f - 310, PrototypeGui.Height - 86, 620, 38), GUIContent.none);
        GUI.Label(new Rect(PrototypeGui.Width * .5f - 305, PrototypeGui.Height - 82, 610, 30), InteractionHint, style);
        if (IsAttaching)
        {
            var bar = new Rect(PrototypeGui.Width * .5f - 110, PrototypeGui.Height * .5f + 30, 220, 12);
            GUI.Box(bar, GUIContent.none);
            GUI.color = new Color(.95f,.72f,.25f);
            GUI.DrawTexture(new Rect(bar.x+2,bar.y+2,(bar.width-4)*AttachmentProgress,bar.height-4),Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(bar.x,bar.y+14,bar.width,25),"부착 " + Mathf.RoundToInt(AttachmentProgress*100) + "% · E 유지",style);
        }
    }

    private void OnDisable()
    {
        swingActive = false; WorkerHammerAnimation.Stop(bodyAnimator);
        CancelAttachment();
        if (HeldPart != null) { HeldPart.Throw(this, Vector3.zero); HeldPart = null; }
        if (hammerVisual != null) hammerVisual.gameObject.SetActive(false);
    }

    private void OnDestroy()
    {
        if (handIK != null && handIK.interactor == this) handIK.interactor = null;
        if (hammerVisual != null) Destroy(hammerVisual.gameObject);
        if (runtimeHoldAnchor != null) Destroy(runtimeHoldAnchor.gameObject);
        if (toolMaterial != null) Destroy(toolMaterial);
        if (sparkMaterial != null) Destroy(sparkMaterial);
        targetBounds.Clear();
    }
}
