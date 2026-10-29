using System.Collections.Generic;
using UnityEngine;

/// <summary>Runtime-only smoke, bounded visual shake, and local aim/status hints for the supply tunnel.</summary>
public sealed class PrototypeTunnelFeedback : MonoBehaviour
{
    public Transform tunnel;
    public Transform lever;
    public TextMesh queueLabel;
    public float feedbackHoldSeconds = 1.4f;

    readonly List<Transform> tunnelVisuals = new List<Transform>();
    readonly List<Vector3> visualPositions = new List<Vector3>();
    readonly List<Quaternion> visualRotations = new List<Quaternion>();
    ParticleSystem smoke;
    Transform chimney;
    Material chimneyMaterial;
    Material smokeMaterial;
    Texture2D smokeTexture;
    CarSpawner subscribedSpawner;
    float shakeTime;
    float smokeTime;
    float messageTime;
    string message = "";

    CarSpawner Spawner => PrototypeLineController.Current != null ? PrototypeLineController.Current.spawner : null;

    void OnEnable() { Subscribe(); }

    void Start()
    {
        CacheSafeTunnelVisuals();
        BuildChimneyAndSmoke();
        Subscribe();
    }

    void OnDisable()
    {
        if (subscribedSpawner != null) subscribedSpawner.RequestFeedbackChanged -= OnRequestFeedback;
        subscribedSpawner = null;
        shakeTime = smokeTime = 0f;
        RestoreTunnelVisuals();
        if (smoke != null)
        {
            var emission = smoke.emission;
            emission.rateOverTime = 0f;
            smoke.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }
    }

    void OnDestroy()
    {
        if (chimney != null) Destroy(chimney.gameObject);
        if (chimneyMaterial != null) Destroy(chimneyMaterial);
        if (smokeMaterial != null) Destroy(smokeMaterial);
        if (smokeTexture != null) Destroy(smokeTexture);
    }

    void Subscribe()
    {
        var source = Spawner;
        if (subscribedSpawner == source) return;
        if (subscribedSpawner != null) subscribedSpawner.RequestFeedbackChanged -= OnRequestFeedback;
        subscribedSpawner = source;
        if (subscribedSpawner != null) subscribedSpawner.RequestFeedbackChanged += OnRequestFeedback;
    }

    public void NotifyLeverChanged()
    {
        // Lever feedback belongs to the label; it must never animate the supply tunnel.
        Show("레버 전환");
    }

    void OnRequestFeedback(CarSpawner.RequestFeedback state)
    {
        switch (state)
        {
            case CarSpawner.RequestFeedback.Accepted: Show("투입 준비 중"); shakeTime = .32f; smokeTime = 4.5f; break;
            case CarSpawner.RequestFeedback.Full: Show("대기열 가득 참"); break;
            case CarSpawner.RequestFeedback.Blocked: Show("출발 위치 막힘"); break;
            case CarSpawner.RequestFeedback.Exiting: Show("차량 출발"); break;
        }
    }

    void Show(string value) { message = value; messageTime = feedbackHoldSeconds; }

    void Update()
    {
        if (subscribedSpawner == null && Spawner != null) Subscribe();
        if (messageTime > 0f) messageTime -= Time.deltaTime;
        if (shakeTime > 0f)
        {
            shakeTime -= Time.deltaTime;
            float fade = Mathf.Clamp01(shakeTime / .32f);
            Vector3 offset = new Vector3(Mathf.Sin(Time.time * 47f), Mathf.Sin(Time.time * 61f) * .45f, Mathf.Cos(Time.time * 53f)) * (.018f * fade);
            for (int i = 0; i < tunnelVisuals.Count; i++)
                if (tunnelVisuals[i] != null) tunnelVisuals[i].localPosition = visualPositions[i] + offset;
        }
        else RestoreTunnelVisuals();

        var spawner = Spawner;
        string text = "대기 " + (spawner != null ? spawner.PendingCount : 0) + " / " + (spawner != null ? spawner.queueCapacity : 0);
        if (messageTime > 0f) text += "\n" + message;
        else text += GetAimHint();
        if (queueLabel != null)
        {
            queueLabel.text = text;
            if (Camera.main != null) queueLabel.transform.rotation = Camera.main.transform.rotation;
        }
        UpdateSmoke(spawner);
    }

    void OnGUI()
    {
        var hint = messageTime > 0f ? message : GetAimHint().TrimStart('\n');
        if (string.IsNullOrEmpty(hint)) return;
        const float width = 260f, height = 38f;
        var rect = new Rect(Screen.width - width - 16f, Screen.height - height - 18f, width, height);
        GUI.Label(rect, hint, GUI.skin.box);
    }

    void UpdateSmoke(CarSpawner spawner)
    {
        if (smoke == null) return;
        smokeTime = Mathf.Max(0f, smokeTime - Time.deltaTime);
        bool active = smokeTime > 0f;
        var emission = smoke.emission;
        emission.rateOverTime = active ? 18f : 0f;
        if (active && !smoke.isPlaying) smoke.Play(true);
    }

    string GetAimHint()
    {
        var controller = PrototypeLineController.Current;
        var player = controller != null ? controller.player : null;
        if (player == null || player.MapMode || !player.ControlsActive || Camera.main == null) return "";
        var reach = player.transform.position + Vector3.up * 1.1f;
        float rayLength = 4.5f + Vector3.Distance(Camera.main.transform.position, reach);
        if (!Physics.Raycast(Camera.main.ViewportPointToRay(new Vector3(.5f, .5f)), out var hit, rayLength, ~(1 << 10), QueryTriggerInteraction.Collide) || Vector3.Distance(reach, hit.point) > 3.5f) return "";
        if (tunnel != null && (hit.transform == tunnel || hit.transform.IsChildOf(tunnel))) return "\nE / 클릭 · 차량 투입";
        if (lever != null && (hit.transform == lever || hit.transform.IsChildOf(lever))) return "\nE / 클릭 · 레버 전환";
        return "";
    }

    void CacheSafeTunnelVisuals()
    {
        if (tunnel == null) return;
        foreach (var renderer in tunnel.GetComponentsInChildren<Renderer>(true))
        {
            var t = renderer.transform;
            // Don't animate a collider owner or a parent of colliders; the interaction target stays fixed.
            if (t.GetComponent<Collider>() != null || t.GetComponentInChildren<Collider>(true) != null) continue;
            tunnelVisuals.Add(t); visualPositions.Add(t.localPosition); visualRotations.Add(t.localRotation);
        }
    }

    void RestoreTunnelVisuals()
    {
        for (int i = 0; i < tunnelVisuals.Count; i++)
            if (tunnelVisuals[i] != null) { tunnelVisuals[i].localPosition = visualPositions[i]; tunnelVisuals[i].localRotation = visualRotations[i]; }
    }

    void BuildChimneyAndSmoke()
    {
        if (tunnel == null) return;
        var renderers = tunnel.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return;
        Bounds bounds = renderers[0].bounds;
        foreach (var r in renderers) bounds.Encapsulate(r.bounds);
        chimney = new GameObject("RuntimeTunnelChimney").transform;
        chimney.SetParent(tunnel, true);
        chimney.position = new Vector3(bounds.center.x, bounds.max.y + .18f, bounds.center.z);
        chimney.rotation = tunnel.rotation;
        chimneyMaterial = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
        chimneyMaterial.color = new Color(.26f, .29f, .31f);
        AddChimneyPiece("ChimneyStack", new Vector3(0, .34f, 0), new Vector3(.24f, .35f, .24f), chimneyMaterial);
        AddChimneyPiece("ChimneyRim", new Vector3(0, .68f, 0), new Vector3(.31f, .07f, .31f), chimneyMaterial);
        var puff = new GameObject("TunnelSmoke"); puff.transform.SetParent(chimney, false); puff.transform.localPosition = new Vector3(0, .78f, 0); puff.transform.localRotation = Quaternion.Euler(-90f,0,0);
        smoke = puff.AddComponent<ParticleSystem>();
        var main = smoke.main; main.loop = true; main.startLifetime = new ParticleSystem.MinMaxCurve(5.5f, 7.5f); main.startSpeed = new ParticleSystem.MinMaxCurve(1.8f, 2.8f); main.startSize = new ParticleSystem.MinMaxCurve(.7f, 1.25f);
        main.startColor = new Color(.72f, .75f, .76f, .65f); main.maxParticles = 144; main.simulationSpace = ParticleSystemSimulationSpace.World;
        var emission = smoke.emission; emission.rateOverTime = 0f;
        var shape = smoke.shape; shape.shapeType = ParticleSystemShapeType.Cone; shape.angle = 9f; shape.radius = .22f;
        var size = smoke.sizeOverLifetime; size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, .65f, 1f, 2.8f));
        var color = smoke.colorOverLifetime; color.enabled = true;
        var gradient = new Gradient();
        gradient.SetKeys(new[] { new GradientColorKey(new Color(.62f,.65f,.67f),0), new GradientColorKey(new Color(.48f,.51f,.53f),1) }, new[] { new GradientAlphaKey(.6f,0), new GradientAlphaKey(.05f,1) });
        color.color = gradient;
        CreateSmokeMaterial();
    }

    void CreateSmokeMaterial()
    {
        var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit") ?? Shader.Find("Particles/Standard Unlit");
        if (shader == null) return;
        smokeMaterial = new Material(shader);
        smokeMaterial.name = "RuntimeTunnelSmokeMaterial";
        smokeMaterial.SetFloat("_Surface",1f); smokeMaterial.SetFloat("_ZWrite",0f);
        smokeMaterial.SetFloat("_SrcBlend",(float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        smokeMaterial.SetFloat("_DstBlend",(float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        smokeMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); smokeMaterial.renderQueue=3000;
        smokeMaterial.color = Color.white;
        smokeTexture = new Texture2D(32, 32, TextureFormat.RGBA32, false);
        smokeTexture.name = "RuntimeSoftSmokeSprite";
        smokeTexture.wrapMode = TextureWrapMode.Clamp;
        var pixels = new Color[32 * 32];
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                float dx = (x - 15.5f) / 15.5f, dy = (y - 15.5f) / 15.5f;
                float alpha = Mathf.Pow(Mathf.Clamp01(1f - (dx * dx + dy * dy)), 2.4f);
                pixels[y * 32 + x] = new Color(1f, 1f, 1f, alpha);
            }
        smokeTexture.SetPixels(pixels); smokeTexture.Apply(false, true);
        smokeMaterial.mainTexture = smokeTexture;
        if (smokeMaterial.HasProperty("_BaseMap")) smokeMaterial.SetTexture("_BaseMap", smokeTexture);
        smoke.GetComponent<ParticleSystemRenderer>().sharedMaterial = smokeMaterial;
    }

    void AddChimneyPiece(string name, Vector3 localPosition, Vector3 localScale, Material material)
    {
        var piece = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        piece.name = name; piece.transform.SetParent(chimney, false); piece.transform.localPosition = localPosition; piece.transform.localScale = localScale;
        var collider = piece.GetComponent<Collider>(); if (collider != null) Destroy(collider);
        piece.GetComponent<Renderer>().sharedMaterial = material;
    }
}
