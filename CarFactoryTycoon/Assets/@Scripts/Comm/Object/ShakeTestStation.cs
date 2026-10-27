using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;

/// <summary>Four-post assembly inspection. Passes sealing defects to the separate water test.</summary>
public class ShakeTestStation : MonoBehaviour, ILineGate
{
    public enum TestState { Idle, Raising, Testing, Settling, Breaking, Cooldown }
    public SplineContainer mainLineSpline;
    [Range(0, 1)] public float gateProgress;
    public Transform[] wheelPads;
    public Transform[] pistons;
    public Transform[] rejectLeaves;
    public Renderer indicator;
    public Material warningMaterial;
    public float liftHeight = .5f;
    public float liftDuration = .55f;
    public float testDuration = 2.6f;
    public float settleDuration = .55f;
    public float breakupDuration = 1.55f;
    public TestState currentState;
    [SerializeField] CarController currentCar;
    [SerializeField] List<PartType> missingParts = new List<PartType>();
    public CarController InspectedCar => currentCar;
    public int MissingCount => missingParts.Count;
    public float GateProgress => gateProgress;
    public bool GateEnabled => isActiveAndEnabled;

    class PartPose
    {
        public AssemblyPart part;
        public Vector3 position, scale, burst;
        public Quaternion rotation;
        public bool active, enabled, visible;
    }
    readonly List<PartPose> poses = new List<PartPose>();
    Vector3[] padHome;
    Quaternion[] leafHome;
    Vector3 basePosition, baseScale;
    Quaternion baseRotation;
    CarDolly dolly;
    float elapsed, hatch;
    MaterialPropertyBlock block;
    LineRenderer warning;
    Transform warningParent;
    Vector3 warningLocal;
    Camera warningCamera;

    void OnEnable()
    {
        if (mainLineSpline == null)
        {
            var line = GameObject.Find("MainSpline");
            if (line != null) mainLineSpline = line.GetComponent<SplineContainer>();
        }
        if (mainLineSpline != null)
        {
            SplineUtility.GetNearestPoint(mainLineSpline.Spline, (float3)mainLineSpline.transform.InverseTransformPoint(transform.position), out _, out float t);
            gateProgress = t;
        }
        if (padHome == null)
        {
            padHome = new Vector3[wheelPads != null ? wheelPads.Length : 0];
            for (int i = 0; i < padHome.Length; i++) if (wheelPads[i] != null) padHome[i] = wheelPads[i].localPosition;
            leafHome = new Quaternion[rejectLeaves != null ? rejectLeaves.Length : 0];
            for (int i = 0; i < leafHome.Length; i++) if (rejectLeaves[i] != null) leafHome[i] = rejectLeaves[i].localRotation;
            block = new MaterialPropertyBlock();
        }
        LineTrafficManager.Instance.RegisterGate(this);
        if (warning == null && warningMaterial != null)
        {
            var go = new GameObject("InspectionWarning"); go.transform.SetParent(transform, false);
            warning = go.AddComponent<LineRenderer>(); warning.sharedMaterial = warningMaterial;
            warning.useWorldSpace = true; warning.widthMultiplier = .035f; warning.positionCount = 7;
            warning.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            warning.receiveShadows = false; go.SetActive(false);
        }
    }

    void OnDisable()
    {
        CancelCycle();
        hatch = 0;
        SetHardware(0, 0, 0);
        if (warning != null) warning.gameObject.SetActive(false);
        LineTrafficManager.Instance.UnregisterGate(this);
    }

    void Update()
    {
        bool cycling = currentState != TestState.Idle && currentState != TestState.Cooldown;
        if (cycling && (currentCar == null || !currentCar.gameObject.activeInHierarchy))
        {
            RestoreParts();
            if (dolly != null) dolly.Restore();
            currentCar = null; dolly = null;
            SetState(TestState.Cooldown);
        }
        float raise = 0, pitch = 0, roll = 0;
        elapsed += Time.deltaTime;
        switch (currentState)
        {
            case TestState.Idle:
                Capture();
                break;
            case TestState.Raising:
                raise = liftHeight * Mathf.SmoothStep(0, 1, elapsed / Mathf.Max(.01f, liftDuration));
                PoseCar(raise, 0, 0);
                if (elapsed >= liftDuration) SetState(TestState.Testing);
                break;
            case TestState.Testing:
                float t = Mathf.Clamp01(elapsed / Mathf.Max(.01f, testDuration));
                float envelope = Mathf.Sin(t * Mathf.PI);
                pitch = Mathf.Sin(t * Mathf.PI * 4) * 8f * envelope;
                roll = Mathf.Sin(t * Mathf.PI * 6) * 11f * envelope;
                raise = liftHeight + Mathf.Sin(t * Mathf.PI * 2) * .07f * envelope;
                PoseCar(raise, pitch, roll);
                if (t >= 1) SetState(TestState.Settling);
                break;
            case TestState.Settling:
                raise = liftHeight * (1 - Mathf.SmoothStep(0, 1, elapsed / Mathf.Max(.01f, settleDuration)));
                PoseCar(raise, 0, 0);
                if (elapsed >= settleDuration)
                {
                    PoseCar(0, 0, 0);
                    if (dolly != null) dolly.Restore();
                    if (MissingCount == 0) Pass(); else BeginBreakup();
                }
                break;
            case TestState.Breaking:
                raise = -.55f;
                Breakup();
                break;
            case TestState.Cooldown:
                if (elapsed >= .5f && hatch <= 0) SetState(TestState.Idle);
                break;
        }
        hatch = Mathf.MoveTowards(hatch, currentState == TestState.Breaking ? 1 : 0, Time.deltaTime / .3f);
        SetHardware(raise, pitch, roll);
        SetIndicator();
        SetWarning();
    }

    void Capture()
    {
        if (mainLineSpline == null || currentCar != null) return;
        var car = LineTrafficManager.Instance.GetCarAtGate(gateProgress, .1f);
        if (car == null) return;
        car.CloseRepairWindow(); // Deadline is latched at gate capture, before inspection.
        currentCar = car; car.isMoving = false;
        basePosition = car.transform.position; baseRotation = car.transform.rotation; baseScale = car.transform.localScale;
        var parts = car.GetComponentsInChildren<AssemblyPart>(true);
        VehicleInspectionRules.CountAllMissing(parts, missingParts);
        warningParent = car.transform; warningLocal = Vector3.zero; warningCamera = Camera.main;
        if (MissingCount > 0) foreach (var part in parts) if (part.myType == missingParts[0])
        { warningParent = part.transform.parent; warningLocal = part.assembledPos; break; }
        poses.Clear();
        foreach (var part in parts)
        {
            int key = (int)part.myType;
            Vector3 direction = part.transform.localPosition;
            direction.y = .35f;
            if (direction.sqrMagnitude < .01f) direction = Vector3.up;
            poses.Add(new PartPose {part = part, position = part.transform.localPosition, rotation = part.transform.localRotation,
                scale = part.transform.localScale, active = part.gameObject.activeSelf, enabled = part.enabled,
                visible = part.IsAssembled && part.gameObject.activeInHierarchy,
                burst = direction.normalized * (.45f + key % 4 * .1f) + Vector3.up * .35f});
        }
        dolly = car.Dolly;
        if (dolly != null) dolly.DetachInPlace();
        SetState(TestState.Raising);
    }

    void PoseCar(float raise, float pitch, float roll)
    {
        if (currentCar == null) return;
        currentCar.transform.SetPositionAndRotation(basePosition + Vector3.up * raise, baseRotation * Quaternion.Euler(pitch, 0, roll));
    }

    void Pass()
    {
        RestoreParts();
        currentCar.SetProgress(gateProgress);
        currentCar.isMoving = true;
        currentCar = null; dolly = null;
        SetState(TestState.Cooldown);
    }

    void BeginBreakup()
    {
        currentCar.LeaveLine();
        foreach (var pose in poses)
        {
            if (pose.part == null) continue;
            pose.part.enabled = false;
            if (!pose.visible) pose.part.gameObject.SetActive(false);
        }
        Debug.Log("[ShakeTest] Assembly rejected: " + string.Join(", ", missingParts));
        SetState(TestState.Breaking);
    }

    void Breakup()
    {
        if (currentCar == null) { SetState(TestState.Cooldown); return; }
        float t = Mathf.Clamp01(elapsed / Mathf.Max(.01f, breakupDuration));
        float spread = Mathf.Sin(Mathf.Clamp01(t / .72f) * Mathf.PI);
        float drop = Mathf.Clamp01((t - .25f) / .75f);
        currentCar.transform.position = basePosition + Vector3.down * (6.5f * drop * drop);
        currentCar.transform.rotation = baseRotation;
        foreach (var pose in poses)
        {
            if (pose.part == null || !pose.visible) continue;
            pose.part.transform.localPosition = pose.position + pose.burst * spread;
            int key = (int)pose.part.myType;
            pose.part.transform.localRotation = pose.rotation * Quaternion.Euler((key % 3 - 1) * 55 * spread, (key % 5 - 2) * 32 * spread, 45 * spread);
        }
        if (t < 1) return;
        RestoreParts();
        currentCar.transform.localScale = baseScale;
        if (CarPool.Instance != null) CarPool.Instance.Return(currentCar); else currentCar.gameObject.SetActive(false);
        currentCar = null; dolly = null;
        SetState(TestState.Cooldown);
    }

    void RestoreParts()
    {
        foreach (var pose in poses)
        {
            if (pose.part == null) continue;
            pose.part.transform.localPosition = pose.position;
            pose.part.transform.localRotation = pose.rotation;
            pose.part.transform.localScale = pose.scale;
            pose.part.gameObject.SetActive(pose.active);
            pose.part.enabled = pose.enabled;
        }
        poses.Clear();
    }

    public void CancelCycle()
    {
        RestoreParts();
        if (currentCar != null)
        {
            currentCar.transform.localScale = baseScale;
            currentCar.transform.SetPositionAndRotation(basePosition, baseRotation);
            if (dolly != null) dolly.Restore();
            if (currentCar.gameObject.activeInHierarchy)
            {
                if (currentCar.targetSpline != null)
                {
                    currentCar.SetProgress(gateProgress - .01f / Mathf.Max(.01f, currentCar.SplineLength));
                    currentCar.isMoving = true;
                }
                else if (CarPool.Instance != null) CarPool.Instance.Return(currentCar);
                else currentCar.gameObject.SetActive(false);
            }
        }
        currentCar = null; dolly = null; missingParts.Clear();
        SetState(TestState.Idle);
        hatch = 0;
        SetHardware(0, 0, 0);
        if (warning != null) warning.gameObject.SetActive(false);
    }

    void SetState(TestState state) { currentState = state; elapsed = 0; }

    void SetHardware(float raise, float pitch, float roll)
    {
        if (padHome == null || leafHome == null) return;
        Quaternion tilt = Quaternion.Euler(pitch, 0, roll);
        for (int i = 0; i < padHome.Length; i++)
        {
            if (wheelPads[i] == null) continue;
            var home = padHome[i];
            float y = home.y + raise + (tilt * new Vector3(home.x, 0, home.z)).y;
            wheelPads[i].localPosition = new Vector3(home.x, y, home.z);
            wheelPads[i].localRotation = tilt;
            if (pistons != null && i < pistons.Length && pistons[i] != null)
            {
                float length = Mathf.Max(.02f, y + .405f);
                pistons[i].localPosition = new Vector3(home.x, -.405f + length * .5f, home.z);
                pistons[i].localScale = new Vector3(.13f, length, .13f);
                pistons[i].gameObject.SetActive(currentState != TestState.Breaking);
            }
        }
        for (int i = 0; i < leafHome.Length; i++) if (rejectLeaves[i] != null)
            rejectLeaves[i].localRotation = leafHome[i] * Quaternion.Euler(0, 0, (i == 0 ? -1 : 1) * 105 * Mathf.SmoothStep(0, 1, hatch));
    }

    void SetIndicator()
    {
        if (indicator == null) return;
        Color color = currentState == TestState.Breaking ? Color.red :
            currentState == TestState.Settling || currentState == TestState.Cooldown ? (MissingCount == 0 ? Color.cyan : Color.red) :
            currentState == TestState.Idle ? new Color(.1f, .18f, .2f) : new Color(1, .6f, .1f) * (.6f + .4f * Mathf.Sin(elapsed * 15));
        indicator.GetPropertyBlock(block); block.SetColor("_BaseColor", color); block.SetColor("_EmissionColor", color * 2); indicator.SetPropertyBlock(block);
    }

    void SetWarning()
    {
        if (warning == null) return;
        bool show = currentCar != null && MissingCount > 0 &&
            (currentState == TestState.Settling || currentState == TestState.Breaking && elapsed < .2f);
        warning.gameObject.SetActive(show);
        if (!show) return;
        Vector3 anchor = warningParent != null ? warningParent.TransformPoint(warningLocal) : currentCar.transform.position;
        Vector3 right = warningCamera != null ? warningCamera.transform.right : Vector3.right;
        Vector3 up = warningCamera != null ? warningCamera.transform.up : Vector3.up;
        Vector3 center = anchor + Vector3.up * 1.05f;
        warning.SetPosition(0, anchor); warning.SetPosition(1, center + right * .28f);
        warning.SetPosition(2, center + up * .28f); warning.SetPosition(3, center - right * .28f);
        warning.SetPosition(4, center - up * .28f); warning.SetPosition(5, center + right * .28f); warning.SetPosition(6, anchor);
        Color color = new Color(1, .08f, .025f, .65f + .35f * Mathf.Sin(elapsed * 28));
        warning.startColor = warning.endColor = color;
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireCube(transform.position + Vector3.up * .5f, new Vector3(5.6f, 1, 3.4f));
    }
}
