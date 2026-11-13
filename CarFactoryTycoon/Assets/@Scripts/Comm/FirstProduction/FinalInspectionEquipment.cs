using UnityEngine;
using UnityEngine.Splines;

/// <summary>Runtime fixtures for the final conveyor, positioned in world space along its spline.</summary>
public sealed class FinalInspectionEquipment : MonoBehaviour
{
    Material steel, enamel, yellow, water, laser, pass, fail;
    ParticleSystem[] jets;
    Transform rollerBed, scanBeam;
    Renderer lamp;
    bool spraying;
    float lampUntil;

    public void Build(SplineContainer spline, float entry, float wetEnd, float roller, float bedLength, float scan)
    {
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        steel = Surface(shader, "검사대 강재", new Color(.15f, .21f, .24f));
        enamel = Surface(shader, "검사대 프레임", new Color(.13f, .42f, .46f));
        yellow = Surface(shader, "안전 황색", new Color(.93f, .62f, .09f));
        laser = Surface(shader, "스캔광", new Color(.1f, 1f, .75f), true);
        pass = Surface(shader, "검사 합격", new Color(.13f, .86f, .23f), true);
        fail = Surface(shader, "검사 불량", new Color(.96f, .16f, .05f), true);
        var sprayShader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        water = Surface(sprayShader != null ? sprayShader : shader, "수밀 분사", new Color(.58f, .87f, 1f));

        jets = new ParticleSystem[8];
        for (int i = 0; i < 2; i++)
        {
            var gate = Station(spline, Mathf.Lerp(entry, wetEnd, i == 0 ? .15f : .85f), "수밀 분사 아치");
            Arch(gate);
            for (int j = 0; j < 4; j++)
            {
                float side = j % 2 == 0 ? -1f : 1f;
                bool upper = j >= 2;
                var nozzle = new Vector3(side * (upper ? .87f : 1.55f), upper ? 2.66f : 1.38f, 0f);
                var direction = new Vector3(-side * (upper ? .24f : 1f), upper ? -1f : -.38f, .08f).normalized;
                jets[i * 4 + j] = MakeJet(gate, nozzle, direction);
            }
        }

        var test = Station(spline, roller, "주행 검사 벨트");
        rollerBed = new GameObject("승강 롤러 데크").transform;
        rollerBed.SetParent(test, false);
        for (int side = -1; side <= 1; side += 2)
            Box(rollerBed, "측면 지지대", new Vector3(side * 1.54f, -.11f, 0f), new Vector3(.16f, .28f, bedLength), yellow);
        int count = Mathf.Max(2, Mathf.CeilToInt(bedLength / .39f));
        for (int i = 0; i < count; i++)
        {
            var cylinder = Primitive(rollerBed, PrimitiveType.Cylinder, "구동 롤러", i % 2 == 0 ? steel : enamel);
            cylinder.localPosition = new Vector3(0f, -.08f, bedLength * ((i + .5f) / count - .5f));
            cylinder.localRotation = Quaternion.Euler(0f, 0f, 90f);
            cylinder.localScale = new Vector3(.27f, 1.48f, .27f);
        }

        var scanner = Station(spline, scan, "최종 체결 스캐너");
        Arch(scanner);
        scanBeam = Box(scanner, "가로 스캔광", new Vector3(0f, 1f, 0f), new Vector3(3.1f, .035f, .045f), laser);
        scanBeam.gameObject.SetActive(false);
        lamp = Box(scanner, "판정 표시등", new Vector3(-1.68f, 3.04f, 0f),
            new Vector3(.38f, .28f, .32f), enamel).GetComponent<Renderer>();

        // Contiguous player exclusion along the bends; the cars stay spline-driven.
        float length = Mathf.Max(.01f, spline.CalculateLength());
        float end = Mathf.Min(1f, scan + 3f / length);
        int segments = Mathf.Max(1, Mathf.CeilToInt((end - entry) * length / 3f));
        Vector3 a = spline.EvaluatePosition(entry);
        for (int i = 1; i <= segments; i++)
        {
            Vector3 b = spline.EvaluatePosition(Mathf.Lerp(entry, end, (float)i / segments));
            if ((b - a).sqrMagnitude > .001f)
            {
                var guard = new GameObject("검사 안전 구역").transform;
                guard.SetParent(transform, false);
                guard.SetPositionAndRotation((a + b) * .5f, Quaternion.LookRotation(b - a, Vector3.up));
                var blocker = guard.gameObject.AddComponent<BoxCollider>();
                blocker.center = Vector3.up * 1.65f;
                blocker.size = new Vector3(3.5f, 5.4f, (b - a).magnitude + .12f);
                blocker.excludeLayers = ~(1 << 10);
                // Sight lines through the rails let players see each test from the aisle.
                for (int side = -1; side <= 1; side += 2)
                {
                    Box(guard, "안전 난간", new Vector3(side * 1.78f, .95f, 0f),
                        new Vector3(.06f, .08f, blocker.size.z), yellow);
                    Box(guard, "난간 기둥", new Vector3(side * 1.78f, .34f, 0f),
                        new Vector3(.09f, 1.28f, .09f), steel);
                }
            }
            a = b;
        }
    }

    static Material Surface(Shader shader, string label, Color tint, bool glow = false)
    {
        var material = new Material(shader) { name = label, color = tint };
        if (glow && material.HasProperty("_EmissionColor"))
        {
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", tint * 1.8f);
        }
        return material;
    }

    Transform Station(SplineContainer spline, float progress, string label)
    {
        var station = new GameObject(label).transform;
        station.SetParent(transform, false);
        Vector3 tangent = spline.EvaluateTangent(progress);
        station.SetPositionAndRotation(spline.EvaluatePosition(progress), Quaternion.LookRotation(tangent, Vector3.up));
        return station;
    }

    void Arch(Transform gate)
    {
        for (int side = -1; side <= 1; side += 2)
        {
            Box(gate, "분체도장 기둥", new Vector3(side * 1.68f, 1.27f, 0f),
                new Vector3(.21f, 2.92f, .28f), enamel);
            Box(gate, "앵커", new Vector3(side * 1.68f, -.12f, 0f), new Vector3(.4f, .2f, .5f), steel);
        }
        Box(gate, "헤더", Vector3.up * 2.75f, new Vector3(3.59f, .24f, .3f), enamel);
    }

    static Transform Primitive(Transform parent, PrimitiveType shape, string label, Material material)
    {
        var go = GameObject.CreatePrimitive(shape);
        go.name = label;
        go.transform.SetParent(parent, false);
        var collider = go.GetComponent<Collider>();
        collider.enabled = false;
        Destroy(collider);
        go.GetComponent<Renderer>().sharedMaterial = material;
        return go.transform;
    }

    static Transform Box(Transform parent, string label, Vector3 at, Vector3 size, Material material)
    {
        var part = Primitive(parent, PrimitiveType.Cube, label, material);
        part.localPosition = at;
        part.localScale = size;
        return part;
    }

    ParticleSystem MakeJet(Transform gate, Vector3 nozzle, Vector3 direction)
    {
        var head = Box(gate, "수밀 노즐", nozzle, new Vector3(.12f, .12f, .22f), steel);
        head.localRotation = Quaternion.LookRotation(direction);
        var emitter = new GameObject("분사 물줄기");
        emitter.transform.SetParent(gate, false);
        emitter.transform.localPosition = nozzle + direction * .11f;
        emitter.transform.localRotation = head.localRotation;
        var jet = emitter.AddComponent<ParticleSystem>();
        jet.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = jet.main;
        main.playOnAwake = false;
        main.loop = true;
        main.startSpeed = 5.3f;
        main.startLifetime = .42f;
        main.startSize = .032f;
        main.maxParticles = 50;
        main.gravityModifier = .16f;
        var emission = jet.emission;
        emission.rateOverTime = 85f;
        var shape = jet.shape;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.radius = .025f;
        shape.angle = 8f;
        var renderer = jet.GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = water;
        renderer.renderMode = ParticleSystemRenderMode.Stretch;
        renderer.velocityScale = .06f;
        renderer.lengthScale = 2f;
        return jet;
    }

    public void ShowResult(bool accepted)
    {
        lamp.sharedMaterial = accepted ? pass : fail;
        lampUntil = Time.time + 3f;
    }

    public void Animate(bool wet, float lift, bool scanning)
    {
        if (wet != spraying)
        {
            spraying = wet;
            foreach (var jet in jets)
            {
                if (wet) jet.Play();
                else jet.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }
        }
        rollerBed.localPosition = Vector3.up * (.16f * lift);
        if (scanBeam.gameObject.activeSelf != scanning) scanBeam.gameObject.SetActive(scanning);
        if (scanning) scanBeam.localPosition = Vector3.up * (.28f + Mathf.PingPong(Time.time * 2.5f, 1.9f));
        if (lampUntil > 0f && Time.time >= lampUntil)
        {
            lamp.sharedMaterial = enamel;
            lampUntil = 0f;
        }
    }

    void OnDestroy()
    {
        foreach (var material in new[] { steel, enamel, yellow, water, laser, pass, fail })
            if (material != null) Destroy(material);
    }
}
