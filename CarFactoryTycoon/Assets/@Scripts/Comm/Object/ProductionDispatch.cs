using TMPro;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;

/// <summary>Reusable scan/press and covered side outlet for one production generation.</summary>
public sealed class ProductionDispatch : MonoBehaviour
{
    public float gateProgress;
    public SplineContainer mainSpline;
    public bool IsBusy => car != null;
    public bool IsIdle => !IsBusy;

    enum Phase { Scan, Press, Transfer, Close }
    Phase phase;
    CarController car;
    ProductionContract contract;
    Transform laser, shutter, press, marker;
    TextMeshPro label;
    Material shellMaterial, trimMaterial, signalMaterial;
    float elapsed;
    bool valid, leftLine;
    Vector3 capturePosition;
    Quaternion captureRotation;
    const float SideDistance = 5.9f;
    static readonly Color Ready = new Color(0.2f, 0.8f, 1f);
    static readonly Color Pass = new Color(0.15f, 1f, 0.35f);
    static readonly Color Fail = new Color(1f, 0.12f, 0.08f);

    public void Initialize(SplineContainer spline, float progress)
    {
        if (IsBusy) return;
        mainSpline = spline;
        gateProgress = Mathf.Clamp01(progress);
        if (spline != null)
        {
            SplineUtility.Evaluate(spline.Spline, gateProgress, out float3 pos, out float3 tangent, out float3 up);
            var forward = spline.transform.TransformDirection((Vector3)tangent);
            var normal = spline.transform.TransformDirection((Vector3)up);
            transform.SetPositionAndRotation(spline.transform.TransformPoint((Vector3)pos),
                forward.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(forward, normal) : Quaternion.identity);
        }
        if (laser == null) BuildVisuals();
        ResetVisuals();
    }

    public bool TryCapture(CarController candidate)
    {
        if (!isActiveAndEnabled || IsBusy || candidate == null || !candidate.gameObject.activeInHierarchy) return false;
        var promise = candidate.GetComponent<ProductionContract>();
        if (promise == null || promise.dispatch != this || promise.settled || promise.isCompleteVehicle ||
            candidate.pathProgress + 0.0001f < promise.endProgress) return false;
        if (laser == null) BuildVisuals();
        car = candidate;
        contract = promise;
        car.isMoving = false; // Remain a traffic obstacle until the dolly clears the main belt.
        capturePosition = car.transform.position;
        captureRotation = car.transform.rotation;
        valid = contract.Validate(out var missing);
        marker.position = missing != null && missing.transform.parent != null
            ? missing.transform.parent.TransformPoint(missing.assembledPos) + Vector3.up * 0.2f
            : capturePosition + Vector3.up * 1.5f;
        marker.gameObject.SetActive(false);
        shutter.localPosition = new Vector3(3.2f, 5f, 0f);
        phase = Phase.Scan;
        elapsed = 0f;
        leftLine = false;
        SetSignal(Ready, "SCANNING");
        return true;
    }

    void Update()
    {
        if (car == null) return;
        if (!car.gameObject.activeInHierarchy || contract == null || contract.dispatch != this)
        {
            Release(false);
            return;
        }
        elapsed += Time.deltaTime;
        switch (phase)
        {
            case Phase.Scan:
                laser.localPosition = new Vector3(0f, 1.65f, Mathf.Lerp(-2.4f, 2.4f, Mathf.Clamp01(elapsed)));
                if (elapsed < 1f) break;
                laser.gameObject.SetActive(false);
                SetSignal(valid ? Pass : Fail, valid ? "ACCEPTED" : "MISSING PART");
                marker.gameObject.SetActive(!valid);
                phase = valid ? Phase.Transfer : Phase.Press;
                elapsed = 0f;
                break;
            case Phase.Press:
                float compression = Mathf.Clamp01(elapsed / 0.65f);
                contract.SetCompression(compression);
                press.localPosition = new Vector3(0f, Mathf.Lerp(3.25f, 0.85f, compression), 0f);
                if (elapsed < 1f) break;
                marker.gameObject.SetActive(false);
                phase = Phase.Transfer;
                elapsed = 0f;
                break;
            case Phase.Transfer:
                float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / 2f));
                float side = SideDistance * t;
                car.transform.SetPositionAndRotation(capturePosition + transform.right * side, captureRotation);
                if (!leftLine && side > 3.2f) { car.LeaveLine(); leftLine = true; }
                if (!valid)
                    press.localPosition = new Vector3(0f, Mathf.Lerp(0.85f, 3.25f, Mathf.Clamp01(elapsed / 0.5f)), 0f);
                if (elapsed < 2f) break;
                phase = Phase.Close;
                elapsed = 0f;
                break;
            case Phase.Close:
                float close = Mathf.Clamp01(elapsed / 0.5f);
                shutter.localPosition = new Vector3(3.2f, Mathf.Lerp(5f, 1.65f, close), 0f);
                if (close >= 1f) Release(true);
                break;
        }
    }

    void Release(bool completed)
    {
        var outgoing = car;
        var promise = contract;
        car = null;
        contract = null;
        if (outgoing != null)
        {
            if (completed && promise != null && !promise.settled)
            {
                promise.settled = true; // Claim settlement before callbacks or pool reuse.
                if (valid && promise.payout > 0)
                {
                    Vector3 rewardPosition = transform.TransformPoint(new Vector3(3.2f, 3.5f, 0f));
                    EconomyManager.Instance.Earn(promise.payout, rewardPosition);
                    CashPopup.Show(rewardPosition, promise.payout, 1.2f);
                }
            }
            // External pool returns already disabled the vehicle. Never enqueue it twice.
            if (outgoing.gameObject.activeInHierarchy)
            {
                outgoing.LeaveLine();
                if (promise != null) promise.RestorePoses();
                if (outgoing.Dolly != null) outgoing.Dolly.Restore();
                if (CarPool.Instance != null) CarPool.Instance.Return(outgoing);
                else outgoing.gameObject.SetActive(false);
            }
        }
        ResetVisuals();
    }

    void ResetVisuals()
    {
        if (laser == null) return;
        laser.gameObject.SetActive(true);
        laser.localPosition = new Vector3(0f, 1.65f, -2.4f);
        shutter.localPosition = new Vector3(3.2f, 1.65f, 0f);
        press.localPosition = new Vector3(0f, 3.25f, 0f);
        marker.gameObject.SetActive(false);
        SetSignal(Ready, "DELIVERY");
    }

    void SetSignal(Color color, string text)
    {
        if (signalMaterial != null)
        {
            signalMaterial.color = color;
            signalMaterial.SetColor("_BaseColor", color);
            signalMaterial.SetColor("_EmissionColor", color * 2f);
        }
        if (label != null) { label.text = text; label.color = color; }
    }

    Material MakeMaterial(Color color, bool glow = false)
    {
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Standard");
        var material = new Material(shader) { color = color };
        material.SetColor("_BaseColor", color);
        if (glow) { material.EnableKeyword("_EMISSION"); material.SetColor("_EmissionColor", color * 2f); }
        return material;
    }

    Transform Box(string objectName, Vector3 position, Vector3 size, Material material)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = objectName;
        go.transform.SetParent(transform, false);
        go.transform.localPosition = position;
        go.transform.localScale = size;
        var collider = go.GetComponent<Collider>();
        collider.enabled = false;
        Destroy(collider);
        go.GetComponent<Renderer>().sharedMaterial = material;
        return go.transform;
    }

    void BuildVisuals()
    {
        shellMaterial = MakeMaterial(new Color(0.12f, 0.16f, 0.2f));
        trimMaterial = MakeMaterial(new Color(0.38f, 0.43f, 0.46f));
        signalMaterial = MakeMaterial(Ready, true);
        // Most of the enclosure sits beside the belt; its short gate frame leaves the next stage visible.
        Box("SideTunnelRoof", new Vector3(5.7f, 3.4f, 0f), new Vector3(5.3f, 0.25f, 5.8f), shellMaterial);
        Box("SideTunnelNearWall", new Vector3(5.7f, 1.65f, -2.9f), new Vector3(5.3f, 3.5f, 0.25f), shellMaterial);
        Box("SideTunnelFarWall", new Vector3(5.7f, 1.65f, 2.9f), new Vector3(5.3f, 3.5f, 0.25f), shellMaterial);
        Box("SideTunnelBackWall", new Vector3(8.3f, 1.65f, 0f), new Vector3(0.25f, 3.5f, 5.8f), shellMaterial);
        Box("TransferDeck", new Vector3(4.5f, -0.12f, 0f), new Vector3(7.5f, 0.2f, 5.8f), trimMaterial);
        shutter = Box("OpaqueDeliveryShutter", new Vector3(3.2f, 1.65f, 0f), new Vector3(0.22f, 3.5f, 5.8f), shellMaterial);
        Box("ScannerLeft", new Vector3(-1.65f, 1.6f, 0f), new Vector3(0.14f, 3.2f, 0.2f), trimMaterial);
        Box("ScannerRight", new Vector3(1.65f, 1.6f, 0f), new Vector3(0.14f, 3.2f, 0.2f), trimMaterial);
        Box("ScannerHeader", new Vector3(0f, 3.25f, 0f), new Vector3(3.45f, 0.15f, 0.25f), trimMaterial);
        press = Box("RejectPress", new Vector3(0f, 3.25f, 0f), new Vector3(2.8f, 0.2f, 4.7f), trimMaterial);
        laser = Box("ScanLight", new Vector3(0f, 1.65f, -2.4f), new Vector3(2.9f, 0.035f, 0.06f), signalMaterial);
        marker = Box("MissingPartMarker", Vector3.zero, new Vector3(0.22f, 0.4f, 0.22f), signalMaterial);
        var sign = new GameObject("DeliveryStatus");
        sign.transform.SetParent(transform, false);
        sign.transform.localPosition = new Vector3(0f, 3.7f, 0f);
        sign.transform.localRotation = Quaternion.Euler(45f, 0f, 0f);
        label = sign.AddComponent<TextMeshPro>();
        if (TMP_Settings.defaultFontAsset != null) label.font = TMP_Settings.defaultFontAsset;
        label.fontSize = 3f;
        label.alignment = TextAlignmentOptions.Center;
        label.rectTransform.sizeDelta = new Vector2(5f, 0.8f);
        label.raycastTarget = false;
    }

    void OnDisable() { Release(false); }
    void OnDestroy()
    {
        if (shellMaterial != null) Destroy(shellMaterial);
        if (trimMaterial != null) Destroy(trimMaterial);
        if (signalMaterial != null) Destroy(signalMaterial);
    }
}
