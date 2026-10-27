using UnityEngine;

// Sons do player sintetizados por código (o projeto ainda não tem arquivos de áudio):
// "whoosh" quando o golpe sai, impacto quando acerta, um sopro na esquiva e no pulo duplo,
// estrondo do golpe de queda, um "tlim" cristalino ao travar a mira, passos e pouso.
// Para usar sons de verdade, troque os AudioClips gerados em Awake por assets.
[RequireComponent(typeof(AudioSource))]
public class CombatAudio : MonoBehaviour
{
    [Range(0f, 1f)] public float volume = 0.7f;
    [Tooltip("Volume dos passos e do pouso (relativo ao volume geral)")]
    [Range(0f, 1f)] public float footstepVolume = 0.3f;
    public PlayerCombat combat;
    public ThirdPersonController movement;
    public AnimationEventSink animationEvents;
    public TargetLock targetLock;

    private const int SampleRate = 44100;

    private AudioSource source;
    // Fonte separada para passos: o pitch aleatório de cada passo não pode alterar um golpe tocando
    private AudioSource movementSource;
    private AudioClip swing;
    private AudioClip hit;
    private AudioClip heavyHit;
    private AudioClip dodge;
    private AudioClip step;
    private AudioClip land;
    private AudioClip lockOn;
    private AudioClip unlock;

    private void Awake()
    {
        source = GetComponent<AudioSource>();
        source.playOnAwake  = false;
        source.spatialBlend = 0f;
        movementSource = gameObject.AddComponent<AudioSource>();
        movementSource.playOnAwake  = false;
        movementSource.spatialBlend = 0f;

        swing    = CreateWhoosh("Swing", 0.22f, 700f, 3200f, 1);
        dodge    = CreateWhoosh("Dodge", 0.38f, 350f, 1400f, 2);
        hit      = CreateImpact("Hit", 0.16f, 110f, 3);
        heavyHit = CreateImpact("HeavyHit", 0.3f, 70f, 4);
        step     = CreateThump("Step", 0.1f, 900f, 80f, 5);
        land     = CreateThump("Land", 0.22f, 650f, 55f, 6);
        lockOn   = CreateChime("LockOn", 0.28f, 1320f, 1980f);
        unlock   = CreateChime("Unlock", 0.18f, 990f, 660f);

        if (combat == null)
            combat = GetComponent<PlayerCombat>();
        if (movement == null)
            movement = GetComponent<ThirdPersonController>();
        if (animationEvents == null)
            animationEvents = GetComponent<AnimationEventSink>();
        if (targetLock == null)
            targetLock = GetComponent<TargetLock>();
    }

    private void OnEnable()
    {
        if (combat == null)
            combat = GetComponent<PlayerCombat>();
        if (combat != null)
        {
            combat.SwingStarted += OnSwing;
            combat.HitLanded    += OnHit;
            combat.DodgeStarted += OnDodge;
            combat.SlamLanded   += OnSlam;
        }
        if (movement != null)
        {
            movement.Landed       += OnLanded;
            movement.DoubleJumped += OnDoubleJump;
        }
        if (animationEvents != null)
            animationEvents.Footstep += OnFootstep;
        if (targetLock != null)
        {
            targetLock.Locked   += OnLock;
            targetLock.Switched += OnLock;
            targetLock.Released += OnUnlock;
        }
    }

    private void OnDisable()
    {
        if (combat != null)
        {
            combat.SwingStarted -= OnSwing;
            combat.HitLanded    -= OnHit;
            combat.DodgeStarted -= OnDodge;
            combat.SlamLanded   -= OnSlam;
        }
        if (movement != null)
        {
            movement.Landed       -= OnLanded;
            movement.DoubleJumped -= OnDoubleJump;
        }
        if (animationEvents != null)
            animationEvents.Footstep -= OnFootstep;
        if (targetLock != null)
        {
            targetLock.Locked   -= OnLock;
            targetLock.Switched -= OnLock;
            targetLock.Released -= OnUnlock;
        }
    }

    private void OnSwing() => Play(source, swing, 0.45f, Random.Range(0.9f, 1.15f));
    private void OnDodge() => Play(source, dodge, 0.35f, Random.Range(0.95f, 1.05f));
    private void OnHit(DamageInfo info, Health target) => Play(source, info.heavy ? heavyHit : hit, 1f, Random.Range(0.92f, 1.08f));
    private void OnLock(Health target) => Play(source, lockOn, 0.35f, 1f);
    private void OnUnlock() => Play(source, unlock, 0.25f, 1f);
    private void OnSlam(Vector3 point) => Play(source, heavyHit, 1f, 0.7f);
    private void OnDoubleJump() => Play(movementSource, dodge, 0.3f, Random.Range(1.2f, 1.35f));
    private void OnFootstep() => Play(movementSource, step, footstepVolume, Random.Range(0.85f, 1.15f));
    private void OnLanded(float impact) => Play(movementSource, land, footstepVolume * Mathf.Clamp01(impact / 10f) * 2.5f, Random.Range(0.9f, 1.05f));

    private void Play(AudioSource target, AudioClip clip, float clipVolume, float pitch)
    {
        if (target == null || clip == null || volume <= 0f)
            return;
        target.pitch = pitch;
        target.PlayOneShot(clip, clipVolume * volume);
    }

    // Ruído branco filtrado por um passa-baixa cuja frequência sobe e desce: som de lâmina cortando o ar.
    private static AudioClip CreateWhoosh(string clipName, float duration, float lowCutoff, float highCutoff, int seed)
    {
        int count   = Mathf.CeilToInt(duration * SampleRate);
        var data    = new float[count];
        var random  = new System.Random(seed);
        float lp1 = 0f, lp2 = 0f;

        for (int i = 0; i < count; i++)
        {
            float t        = (float)i / count;
            float envelope = Mathf.Sin(Mathf.PI * Mathf.Pow(t, 0.55f));
            envelope      *= envelope;
            float cutoff   = Mathf.Lerp(lowCutoff, highCutoff, Mathf.Sin(Mathf.PI * t));
            float alpha    = 1f - Mathf.Exp(-2f * Mathf.PI * cutoff / SampleRate);
            float noise    = (float)(random.NextDouble() * 2.0 - 1.0);
            lp1 += alpha * (noise - lp1);
            lp2 += alpha * (lp1 - lp2);
            data[i] = lp2 * envelope;
        }
        return CreateClip(clipName, data, 0.8f);
    }

    // Senoide grave com queda de frequência (o "corpo" do impacto) + estalo curto de ruído.
    private static AudioClip CreateImpact(string clipName, float duration, float frequency, int seed)
    {
        int count  = Mathf.CeilToInt(duration * SampleRate);
        var data   = new float[count];
        var random = new System.Random(seed);
        float phase = 0f, lp = 0f;
        float clickAlpha = 1f - Mathf.Exp(-2f * Mathf.PI * 2500f / SampleRate);

        for (int i = 0; i < count; i++)
        {
            float t     = (float)i / SampleRate;
            float freq  = frequency * (1f + 1.5f * Mathf.Exp(-t * 40f));
            phase      += 2f * Mathf.PI * freq / SampleRate;
            float body  = Mathf.Sin(phase) * Mathf.Exp(-t * 16f);
            float noise = (float)(random.NextDouble() * 2.0 - 1.0);
            lp         += clickAlpha * (noise - lp);
            float click = lp * Mathf.Exp(-t * 70f);
            data[i] = body * 0.9f + click * 1.4f;
        }
        return CreateClip(clipName, data, 0.9f);
    }

    // Passo/pouso: baque grave curto (senoide com leve queda de frequência) + ruído abafado do contato com o chão.
    private static AudioClip CreateThump(string clipName, float duration, float cutoff, float frequency, int seed)
    {
        int count  = Mathf.CeilToInt(duration * SampleRate);
        var data   = new float[count];
        var random = new System.Random(seed);
        float phase = 0f, lp1 = 0f, lp2 = 0f;
        float alpha = 1f - Mathf.Exp(-2f * Mathf.PI * cutoff / SampleRate);
        float decay = 5f / duration;

        for (int i = 0; i < count; i++)
        {
            float t        = (float)i / SampleRate;
            float envelope = Mathf.Exp(-t * decay) * Mathf.Clamp01(t * 500f); // ataque de ~2 ms, sem estalo
            phase         += 2f * Mathf.PI * frequency * (1f + Mathf.Exp(-t * 60f)) / SampleRate;
            float noise    = (float)(random.NextDouble() * 2.0 - 1.0);
            lp1 += alpha * (noise - lp1);
            lp2 += alpha * (lp1 - lp2);
            data[i] = (Mathf.Sin(phase) * 0.7f + lp2 * 2.2f) * envelope;
        }
        return CreateClip(clipName, data, 0.8f);
    }

    // Duas notas senoidais curtas com brilho (harmônico) e decaimento rápido: som de mira travando.
    private static AudioClip CreateChime(string clipName, float duration, float first, float second)
    {
        int count = Mathf.CeilToInt(duration * SampleRate);
        var data  = new float[count];
        float phase = 0f;
        for (int i = 0; i < count; i++)
        {
            float t    = (float)i / SampleRate;
            float freq = t < duration * 0.35f ? first : second;
            phase     += 2f * Mathf.PI * freq / SampleRate;
            float local = t < duration * 0.35f ? t : t - duration * 0.35f;
            float envelope = Mathf.Exp(-local * 18f) * Mathf.Clamp01(local * 800f);
            data[i] = (Mathf.Sin(phase) + 0.3f * Mathf.Sin(phase * 2f)) * envelope;
        }
        return CreateClip(clipName, data, 0.6f);
    }

    private static AudioClip CreateClip(string clipName, float[] data, float peak)
    {
        float max = 0.0001f;
        foreach (float sample in data)
            max = Mathf.Max(max, Mathf.Abs(sample));
        float gain = peak / max;
        for (int i = 0; i < data.Length; i++)
            data[i] *= gain;

        AudioClip clip = AudioClip.Create(clipName, data.Length, 1, SampleRate, false);
        clip.SetData(data, 0);
        return clip;
    }
}
