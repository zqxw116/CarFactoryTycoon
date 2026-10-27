using UnityEngine;

/// <summary>
/// Small, temporary synthesized work sounds for the prototype. One shared component owns a
/// fixed emitter pool and cached clips, so work pulses never allocate audio objects or clips.
/// </summary>
public sealed class PrototypeWorkAudio : MonoBehaviour
{
    private const int EmitterCount = 6;
    private const int SampleRate = 22050;
    private const float ImpactDuration = .115f;
    private const float CompletionDuration = .24f;

    private static PrototypeWorkAudio instance;

    private readonly AudioSource[] emitters = new AudioSource[EmitterCount];
    private AudioClip impactClip;
    private AudioClip completionClip;
    private AudioClip upgradedImpactClip;
    private AudioClip upgradedCompletionClip;
    private int nextEmitter;
    private uint variationSeed = 0x8F31A2D7u;

    public int PlayedImpactCount { get; private set; }
    public int PlayedCompletionCount { get; private set; }

    public static void PlayWorkPulse(Vector3 position, bool completed, bool upgradedHammer, bool robot)
    {
        var audio = GetOrCreate();
        if (audio == null) return;
        audio.Play(position, completed, upgradedHammer, robot);
    }

    private static PrototypeWorkAudio GetOrCreate()
    {
        if (instance != null) return instance;
        instance = FindFirstObjectByType<PrototypeWorkAudio>();
        if (instance != null) return instance;

        var go = new GameObject("PrototypeWorkAudio");
        instance = go.AddComponent<PrototypeWorkAudio>();
        return instance;
    }

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }

        instance = this;
        DontDestroyOnLoad(gameObject);
        CreateClips();
        CreateEmitters();
    }

    private void CreateClips()
    {
        impactClip = CreateImpactClip("WorkImpact", .15f, 820f, 1830f, .19f, ImpactDuration);
        completionClip = CreateImpactClip("WorkComplete", .17f, 1180f, 2670f, .13f, CompletionDuration);
        upgradedImpactClip = CreateImpactClip("WorkImpactUpgraded", .22f, 510f, 1510f, .25f, ImpactDuration);
        upgradedCompletionClip = CreateImpactClip("WorkCompleteUpgraded", .25f, 980f, 3150f, .19f, CompletionDuration);
    }

    private void CreateEmitters()
    {
        for (int i = 0; i < emitters.Length; i++)
        {
            var sourceObject = new GameObject("WorkAudioEmitter_" + i);
            sourceObject.transform.SetParent(transform, false);
            var source = sourceObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.minDistance = 1f;
            source.maxDistance = 8f;
            source.dopplerLevel = 0f;
            source.priority = 160;
            source.volume = .16f;
            emitters[i] = source;
        }
    }

    private void Play(Vector3 position, bool completed, bool upgradedHammer, bool robot)
    {
        AudioClip clip = completed
            ? (upgradedHammer ? upgradedCompletionClip : completionClip)
            : (upgradedHammer ? upgradedImpactClip : impactClip);
        if (clip == null || emitters[0] == null) return;

        int selected = FindEmitter();
        var source = emitters[selected];
        source.transform.position = position;
        source.clip = clip;
        // The two-hit hammer has a deeper, brighter synthesized waveform and a modest lift.
        source.volume = completed ? (upgradedHammer ? .19f : .16f) : (upgradedHammer ? .19f : .14f);
        if (robot) source.volume *= .82f;
        source.pitch = (upgradedHammer ? .96f : 1f) + NextVariation() * .035f;
        source.Play();

        if (completed) PlayedCompletionCount++;
        else PlayedImpactCount++;
    }

    private int FindEmitter()
    {
        for (int offset = 0; offset < emitters.Length; offset++)
        {
            int index = (nextEmitter + offset) % emitters.Length;
            if (emitters[index] != null && !emitters[index].isPlaying)
            {
                nextEmitter = (index + 1) % emitters.Length;
                return index;
            }
        }

        // Under a burst, recycle the next source instead of growing the pool.
        int recycled = nextEmitter;
        nextEmitter = (nextEmitter + 1) % emitters.Length;
        return recycled;
    }

    private float NextVariation()
    {
        variationSeed = variationSeed * 1664525u + 1013904223u;
        return ((variationSeed >> 8) / 16777215f) * 2f - 1f;
    }

    private static AudioClip CreateImpactClip(string clipName, float amplitude, float baseFrequency,
        float brightFrequency, float noiseAmount, float duration)
    {
        int sampleCount = Mathf.CeilToInt(SampleRate * duration);
        var samples = new float[sampleCount];
        uint noiseState = 0x4C11DB7u;
        for (int i = 0; i < sampleCount; i++)
        {
            float t = i / (float)SampleRate;
            float normalized = t / duration;
            float envelope = Mathf.Pow(1f - normalized, 3.4f);
            // A short resonant body and a crisp click make the cue read as a hammer strike.
            float body = Mathf.Sin(2f * Mathf.PI * baseFrequency * t) * .68f;
            float overtone = Mathf.Sin(2f * Mathf.PI * brightFrequency * t) * .22f;
            noiseState = noiseState * 1664525u + 1013904223u;
            float noise = ((noiseState >> 8) / 16777215f) * 2f - 1f;
            samples[i] = (body + overtone + noise * noiseAmount) * envelope * amplitude;
        }

        var clip = AudioClip.Create(clipName, sampleCount, 1, SampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    private void OnDestroy()
    {
        if (instance == this) instance = null;
        for (int i = 0; i < emitters.Length; i++)
        {
            if (emitters[i] == null) continue;
            emitters[i].Stop();
            emitters[i].clip = null;
        }
        DestroyClip(impactClip);
        DestroyClip(completionClip);
        DestroyClip(upgradedImpactClip);
        DestroyClip(upgradedCompletionClip);
    }

    private static void DestroyClip(AudioClip clip)
    {
        if (clip != null) Destroy(clip);
    }
}
