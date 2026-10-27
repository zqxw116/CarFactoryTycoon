using System;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>Local-player interaction. Scene/controller code owns camera mode and cursor capture.</summary>
public sealed class PlayerAssemblyInteractor : MonoBehaviour
{
    public Transform cameraTransform;
    public Camera viewCamera;
    public Transform holdAnchor;
    public Transform hammerAnchor;
    public Animator bodyAnimator;
    public PrototypeBalanceSettings settings;
    public bool inputEnabled = true;
    public float interactionRange = 3.5f;
    public float throwSpeed = 7f; // Provisional feel value.
    public bool showHints = true;
    public PortableAssemblyPart HeldPart { get; private set; }
    public AssemblyPart WorkingPart { get; private set; }
    public bool IsAttaching => attachingPart != null;
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
    private float hammerPulse;
    private ParticleSystem sparks;
    private Material toolMaterial, sparkMaterial;
    private PlayerAssemblyHandIK handIK;
    private Transform runtimeHoldAnchor;
    private Vector3 lastHitPoint;

    private float AttachSeconds => Mathf.Max(.01f, settings != null ? settings.playerAttachSeconds : .6f);
    private float HitInterval => Mathf.Max(.3f, settings != null ? settings.playerHitInterval : .3f);
    private Vector3 ReachOrigin => transform.position + Vector3.up * 1.1f;
    private Transform View => cameraTransform != null ? cameraTransform : (viewCamera != null ? viewCamera.transform : transform);

    private void Start()
    {
        EnsureAnchors();
        CreateHammer();
        if (bodyAnimator != null && bodyAnimator.isHuman)
        {
            handIK = bodyAnimator.GetComponent<PlayerAssemblyHandIK>();
            if (handIK == null) handIK = bodyAnimator.gameObject.AddComponent<PlayerAssemblyHandIK>();
            handIK.interactor = this;
        }
    }

    private void EnsureAnchors()
    {
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
        AdvanceAttachment(Time.deltaTime);
        if (WorkingPart != null && (!WorkingPart.gameObject.activeInHierarchy || !WorkingPart.CanPlayerRepair)) WorkingPart = null;
        if (!inputEnabled || Cursor.lockState != CursorLockMode.Locked)
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
        if (mouse != null && mouse.leftButton.wasPressedThisFrame && focusedPart != null) TryHammer(focusedPart);
        UpdateHint();
    }

    public void SetPlayerHammerHits(int hitsRequired)
    {
        // M2 exposes the first upgrade. Economy/upgrade purchase rules are not implemented here.
        hammerHitsOverride = Mathf.Clamp(hitsRequired, 2, 3);
    }

    public bool TryTake(PartStack stack)
    {
        if (stack == null || !stack.gameObject.activeInHierarchy || HeldPart != null || IsAttaching || !WithinReach(stack)) return false;
        EnsureAnchors();
        if (!stack.TryTakePortable(out var portable)) return false;
        if (!portable.TryHold(this, holdAnchor))
        {
            stack.Add(1);
            Destroy(portable.gameObject);
            return false;
        }
        HeldPart = portable;
        WorkingPart = null;
        return true;
    }

    public bool TryPickUp(PortableAssemblyPart portable)
    {
        if (portable == null || !portable.gameObject.activeInHierarchy || portable.IsHeld || HeldPart != null || IsAttaching ||
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
        if (HeldPart == null || IsAttaching || car == null || !car.gameObject.activeInHierarchy || !car.CanRepair) return false;
        var part = FindPart(car, HeldPart.PartType, true, AimPoint(car));
        if (part == null) return false;
        if (!part.TryBeginPlayerAttachment(this, HeldPart.transform.position, HeldPart.transform.rotation)) return false;
        attachingPart = part;
        WorkingPart = part;
        attachElapsed = 0f;
        HeldPart.Show(false);
        return true;
    }

    public bool TryHammer(AssemblyPart part)
    {
        if (part == null || HeldPart != null || IsAttaching || !part.CanPlayerRepair ||
            Vector3.Distance(ReachOrigin, part.AttachmentWorldPosition) > interactionRange ||
            Time.time - lastHitTime + .00001f < HitInterval) return false;
        var car = part.GetComponentInParent<CarController>();
        if (car == null || !car.gameObject.activeInHierarchy || !car.CanRepair) return false;
        // Detached placeholders require a real carried stock item. Partial NPC work is repairable.
        if (!part.TryPlayerHammer(PlayerHammerHits)) return false;
        WorkingPart = part;
        lastHitTime = Time.time;
        lastHitPoint = part.AttachmentWorldPosition;
        hammerPulse = 1f;
        EmitImpact(lastHitPoint, part.IsAssembled);
        HammerHit?.Invoke(part);
        return true;
    }

    private void AdvanceAttachment(float deltaTime)
    {
        if (attachingPart == null) return;
        var car = attachingPart.GetComponentInParent<CarController>();
        if (HeldPart == null || car == null || !car.gameObject.activeInHierarchy || !car.CanRepair ||
            Vector3.Distance(ReachOrigin, attachingPart.AttachmentWorldPosition) > interactionRange)
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
        Destroy(HeldPart.gameObject);
        HeldPart = null;
        // First hit also waits .3 seconds: .6 attachment + three .3 intervals = 1.5 seconds.
        lastHitTime = Time.time;
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
        var collider = stack.GetComponent<Collider>();
        Vector3 point = collider != null ? collider.ClosestPoint(ReachOrigin) : stack.transform.position;
        return Vector3.Distance(ReachOrigin, point) <= interactionRange;
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
            Vector3 anchor = part.AttachmentWorldPosition;
            if (Vector3.Distance(ReachOrigin, anchor) > interactionRange) continue;
            float distance = (anchor - aimedPoint).sqrMagnitude;
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
        if (IsAttaching) { InteractionHint = "부착 중…"; return; }
        Transform view = View;
        float rayLength = interactionRange + Vector3.Distance(view.position, ReachOrigin) + 1f;
        int count = Physics.RaycastNonAlloc(view.position, view.forward, hits, rayLength, ~0, QueryTriggerInteraction.Collide);
        float closest = float.PositiveInfinity;
        RaycastHit selected = default;
        for (int i = 0; i < count; i++)
        {
            var collider = hits[i].collider;
            if (collider == null || collider.transform.IsChildOf(transform) || hits[i].distance >= closest) continue;
            bool relevant = collider.GetComponentInParent<PartStack>() != null || collider.GetComponentInParent<PortableAssemblyPart>() != null || collider.GetComponentInParent<CarController>() != null;
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
        if (IsAttaching) { InteractionHint = "부착 중…"; return; }
        if (HeldPart != null)
        {
            InteractionHint = focusedPart != null ? $"{PartLabel(HeldPart.PartType)} · E 부착 / G 던지기" : $"{PartLabel(HeldPart.PartType)} · G 던지기";
            return;
        }
        if (focusedPortable != null) { InteractionHint = $"{PartLabel(focusedPortable.PartType)} · E 줍기"; return; }
        if (focusedStack != null) { InteractionHint = $"{PartLabel(focusedStack.partType)} ({focusedStack.Count}) · E 집기"; return; }
        if (focusedPart != null) { InteractionHint = $"{PartLabel(focusedPart.myType)} · 클릭 망치질 ({focusedPart.PlayerHitsRemaining(PlayerHammerHits)}회 남음)"; return; }
        InteractionHint = "E 집기·부착   G 던지기   클릭 망치질";
    }

    private static string PartLabel(PartType type)
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
        hammerVisual.localRotation = Quaternion.Euler(0, 0, 90f);
        hammerRestRotation = hammerVisual.localRotation;
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        if (shader != null) { toolMaterial = new Material(shader); toolMaterial.color = new Color(.25f, .27f, .29f); }
        MakeToolPiece("Handle", PrimitiveType.Cylinder, new Vector3(0, .12f, 0), new Vector3(.035f, .16f, .035f));
        MakeToolPiece("Head", PrimitiveType.Cube, new Vector3(0, .29f, 0), new Vector3(.21f, .10f, .09f));
    }

    private void MakeToolPiece(string label, PrimitiveType shape, Vector3 position, Vector3 scale)
    {
        var piece = GameObject.CreatePrimitive(shape);
        piece.name = label;
        piece.layer = gameObject.layer;
        piece.transform.SetParent(hammerVisual, false);
        piece.transform.localPosition = position; piece.transform.localScale = scale;
        var collider = piece.GetComponent<Collider>(); collider.enabled = false; Destroy(collider);
        if (toolMaterial != null) piece.GetComponent<Renderer>().sharedMaterial = toolMaterial;
    }

    private void LateUpdate()
    {
        hammerPulse = Mathf.MoveTowards(hammerPulse, 0f, Time.deltaTime * 5f);
        if (hammerVisual == null) return;
        hammerVisual.gameObject.SetActive(inputEnabled && HeldPart == null);
        float swing = Mathf.Sin(hammerPulse * Mathf.PI);
        hammerVisual.localRotation = hammerRestRotation * Quaternion.Euler(-70f * swing, 0f, 0f);
        hammerVisual.localScale = Vector3.one * (PlayerHammerHits == 2 ? 1.2f : 1f);
    }

    public void ApplyHandIK(Animator animator)
    {
        if (animator == null || !animator.isHuman) return;
        bool carrying = HeldPart != null && !IsAttaching;
        float workWeight = inputEnabled && (IsAttaching || hammerPulse > 0f) ? .85f : 0f;
        Vector3 target = IsAttaching ? attachingPart.transform.position : lastHitPoint;
        if (carrying)
        {
            float half = Mathf.Clamp(HeldPart.LocalBounds.extents.x, .12f, .45f);
            SetHand(animator, AvatarIKGoal.LeftHand, HeldPart.transform.position - transform.right * half, .85f);
            SetHand(animator, AvatarIKGoal.RightHand, HeldPart.transform.position + transform.right * half, .85f);
        }
        else
        {
            SetHand(animator, AvatarIKGoal.LeftHand, target - transform.right * .15f, IsAttaching ? workWeight : 0f);
            SetHand(animator, AvatarIKGoal.RightHand, target - View.forward * (.15f + Mathf.Sin(hammerPulse * Mathf.PI) * .20f), workWeight);
        }
    }

    private static void SetHand(Animator animator, AvatarIKGoal goal, Vector3 target, float weight)
    {
        animator.SetIKPositionWeight(goal, weight);
        animator.SetIKRotationWeight(goal, 0f);
        if (weight > 0f) animator.SetIKPosition(goal, target);
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
            main.startLifetime = .15f; main.startSpeed = 1.1f; main.startSize = .035f;
            main.maxParticles = 40; main.simulationSpace = ParticleSystemSimulationSpace.World;
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
        sparks.Emit(completed ? 18 : 8);
    }

    private void OnGUI()
    {
        if (!showHints || !inputEnabled || Cursor.lockState != CursorLockMode.Locked) return;
        GUI.Label(new Rect(Screen.width * .5f - 4, Screen.height * .5f - 10, 20, 24), "+");
        var style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 16 };
        GUI.Box(new Rect(Screen.width * .5f - 310, Screen.height - 86, 620, 38), GUIContent.none);
        GUI.Label(new Rect(Screen.width * .5f - 305, Screen.height - 82, 610, 30), InteractionHint, style);
    }

    private void OnDisable()
    {
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
    }
}
